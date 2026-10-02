using System;
using System.Buffers.Binary;
using System.IO;

namespace VeloxClip.Platform.Clipboard;

/// <summary>Wraps a packed clipboard DIB with a BMP file header.</summary>
internal static class DibBitmap
{
    public static byte[] ToBitmapFile(byte[] dib)
    {
        ArgumentNullException.ThrowIfNull(dib);
        if (dib.Length < 40)
        {
            throw new InvalidDataException("The DIB header is truncated.");
        }

        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(dib);
        if (headerSize < 40 || headerSize > dib.Length)
        {
            throw new InvalidDataException("Unsupported or truncated DIB header.");
        }

        var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14));
        var compression = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(16));
        var colors = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(32));
        if (colors == 0 && bitCount is 1 or 4 or 8)
        {
            colors = 1u << bitCount;
        }

        // BITMAPINFOHEADER stores bitfield masks after the header. V4/V5
        // headers already include them. Indexed images also carry RGBQUADs.
        var masks = headerSize == 40 ? compression switch
        {
            3 => 12u, // BI_BITFIELDS
            6 => 16u, // BI_ALPHABITFIELDS
            _ => 0u,
        } : 0u;
        var pixelOffset = checked(14u + headerSize + masks + colors * 4u);
        if (pixelOffset >= (ulong)dib.Length + 14u)
        {
            throw new InvalidDataException("The DIB palette, masks or pixels are truncated.");
        }

        var bmp = new byte[checked(14 + dib.Length)];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(2), (uint)bmp.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(10), pixelOffset);
        dib.CopyTo(bmp, 14);
        return bmp;
    }
}
