using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using VeloxClip.Core.Abstractions;
using VeloxClip.Core.Models;

namespace VeloxClip.Platform.Clipboard;

/// <summary>
/// Win32 <see cref="IClipboardReader"/>. Opens the clipboard, picks the highest-
/// priority available format (file &gt; rtf &gt; text &gt; image), and returns a
/// <see cref="ClipboardCapture"/>. Images are normalized to PNG bytes.
/// </summary>
public sealed class Win32ClipboardReader : IClipboardReader
{
    private const uint CF_BITMAP = 2;
    private const uint CF_DIB = 8;
    private const uint CF_UNICODETEXT = 13;
    private const uint CF_HDROP = 15;
    private const int OpenRetryCount = 5;
    private const int OpenRetryDelayMs = 20;

    private static readonly uint CfRtf = RegisterClipboardFormatW("Rich Text Format");
    private static readonly uint CfPng = RegisterClipboardFormatW("PNG");

    public ClipboardCapture? TryRead()
    {
        if (!TryOpenClipboard())
        {
            return null;
        }

        try
        {
            // Priority: file > rtf > text > image.
            if (IsClipboardFormatAvailable(CF_HDROP))
            {
                var files = ReadFileList();
                if (files is not null)
                {
                    return new ClipboardCapture(ClipboardKind.File, files, null);
                }
            }

            if (CfRtf != 0 && IsClipboardFormatAvailable(CfRtf))
            {
                var rtf = ReadAnsiText(CfRtf);
                if (rtf is not null)
                {
                    return new ClipboardCapture(ClipboardKind.Rtf, rtf, null);
                }
            }

            if (IsClipboardFormatAvailable(CF_UNICODETEXT))
            {
                var text = ReadUnicodeText();
                if (text is not null)
                {
                    return new ClipboardCapture(ClipboardKind.Text, text, null);
                }
            }

            var image = ReadImageAsPng();
            if (image is not null)
            {
                return new ClipboardCapture(ClipboardKind.Image, null, image);
            }

            return null;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static bool TryOpenClipboard()
    {
        for (var attempt = 0; attempt < OpenRetryCount; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                return true;
            }

            System.Threading.Thread.Sleep(OpenRetryDelayMs);
        }

        return false;
    }

    private static string? ReadUnicodeText()
    {
        var handle = GetClipboardData(CF_UNICODETEXT);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var ptr = GlobalLock(handle);
        if (ptr == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var text = Marshal.PtrToStringUni(ptr);
            return string.IsNullOrEmpty(text) ? null : text;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static string? ReadAnsiText(uint format)
    {
        var handle = GetClipboardData(format);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var ptr = GlobalLock(handle);
        if (ptr == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var size = (int)GlobalSize(handle);
            if (size <= 0)
            {
                return null;
            }

            var bytes = new byte[size];
            Marshal.Copy(ptr, bytes, 0, size);
            // RTF is 7-bit ASCII; trim a trailing NUL if present.
            var text = Encoding.ASCII.GetString(bytes).TrimEnd('\0');
            return string.IsNullOrEmpty(text) ? null : text;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static string? ReadFileList()
    {
        var handle = GetClipboardData(CF_HDROP);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var count = DragQueryFileW(handle, 0xFFFFFFFF, null, 0);
        if (count == 0)
        {
            return null;
        }

        var paths = new string[count];
        for (uint i = 0; i < count; i++)
        {
            var length = DragQueryFileW(handle, i, null, 0);
            var buffer = new char[length + 1];
            var copied = DragQueryFileW(handle, i, buffer, (uint)buffer.Length);
            paths[i] = new string(buffer, 0, (int)copied);
        }

        return string.Join("\n", paths);
    }

    private static byte[]? ReadImageAsPng()
    {
        // Prefer an app-provided PNG: no re-encoding needed.
        if (CfPng != 0 && IsClipboardFormatAvailable(CfPng))
        {
            var pngHandle = GetClipboardData(CfPng);
            var pngBytes = CopyGlobalBytes(pngHandle);
            if (pngBytes is not null)
            {
                return pngBytes;
            }
        }

        // Fall back to CF_DIB: prepend a BITMAPFILEHEADER to make a .bmp stream.
        if (IsClipboardFormatAvailable(CF_DIB))
        {
            var dibHandle = GetClipboardData(CF_DIB);
            var dib = CopyGlobalBytes(dibHandle);
            if (dib is not null)
            {
                return DibToPng(dib);
            }
        }

        // Last resort: CF_BITMAP (an HBITMAP).
        if (IsClipboardFormatAvailable(CF_BITMAP))
        {
            var hBitmap = GetClipboardData(CF_BITMAP);
            if (hBitmap != IntPtr.Zero)
            {
                using var bitmap = Image.FromHbitmap(hBitmap);
                return EncodePng(bitmap);
            }
        }

        return null;
    }

    private static byte[]? CopyGlobalBytes(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var ptr = GlobalLock(handle);
        if (ptr == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var size = (int)GlobalSize(handle);
            if (size <= 0)
            {
                return null;
            }

            var bytes = new byte[size];
            Marshal.Copy(ptr, bytes, 0, size);
            return bytes;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static byte[] DibToPng(byte[] dib)
    {
        // A CF_DIB is a .bmp file missing its 14-byte BITMAPFILEHEADER.
        // The pixel data offset = 14 + the DIB header size (first 4 LE bytes of the DIB).
        const int fileHeaderSize = 14;
        var dibHeaderSize = BitConverter.ToInt32(dib, 0);
        var pixelOffset = fileHeaderSize + dibHeaderSize;

        var bmp = new byte[fileHeaderSize + dib.Length];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);   // total file size
        BitConverter.GetBytes(pixelOffset).CopyTo(bmp, 10); // offset to pixel data
        dib.CopyTo(bmp, fileHeaderSize);

        using var bmpStream = new MemoryStream(bmp);
        using var bitmap = new Bitmap(bmpStream);
        return EncodePng(bitmap);
    }

    private static byte[] EncodePng(Image image)
    {
        using var pngStream = new MemoryStream();
        image.Save(pngStream, ImageFormat.Png);
        return pngStream.ToArray();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormatW(string lpszFormat);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFileW(IntPtr hDrop, uint iFile, char[]? lpszFile, uint cch);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern UIntPtr GlobalSize(IntPtr hMem);
}
