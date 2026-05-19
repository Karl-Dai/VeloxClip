using System;
using System.Runtime.InteropServices;
using VeloxClip.Core.Abstractions;

namespace VeloxClip.Platform.Clipboard;

/// <summary>
/// Win32 <see cref="IClipboardChangeSource"/>. Creates a message-only window and
/// uses <c>AddClipboardFormatListener</c> so the OS posts <c>WM_CLIPBOARDUPDATE</c>
/// whenever the clipboard changes — reliable even while the app runs in the
/// background. <see cref="Start"/> must be called on a thread with a message pump
/// (the WinUI UI thread).
/// </summary>
public sealed class Win32ClipboardChangeSource : IClipboardChangeSource
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private WndProc? _wndProc;       // kept alive for the lifetime of the window
    private IntPtr _hWnd;
    private ushort _classAtom;
    private string? _className;

    public event EventHandler? ClipboardChanged;

    public void Start()
    {
        if (_hWnd != IntPtr.Zero)
        {
            return;
        }

        _wndProc = WindowProc;
        _className = "VeloxClipClipboardListener_" + Guid.NewGuid().ToString("n");

        var wndClass = new WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = _className,
        };

        _classAtom = RegisterClassW(ref wndClass);
        if (_classAtom == 0)
        {
            throw new InvalidOperationException(
                $"RegisterClassW failed (error {Marshal.GetLastWin32Error()}).");
        }

        _hWnd = CreateWindowExW(
            0, _className, "VeloxClip Clipboard Listener", 0,
            0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, wndClass.hInstance, IntPtr.Zero);
        if (_hWnd == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"CreateWindowExW failed (error {Marshal.GetLastWin32Error()}).");
        }

        if (!AddClipboardFormatListener(_hWnd))
        {
            throw new InvalidOperationException(
                $"AddClipboardFormatListener failed (error {Marshal.GetLastWin32Error()}).");
        }
    }

    public void Stop()
    {
        if (_hWnd != IntPtr.Zero)
        {
            RemoveClipboardFormatListener(_hWnd);
            DestroyWindow(_hWnd);
            _hWnd = IntPtr.Zero;
        }

        if (_classAtom != 0 && _className is not null)
        {
            UnregisterClassW(_className, GetModuleHandle(null));
            _classAtom = 0;
        }

        _wndProc = null;
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            ClipboardChanged?.Invoke(this, EventArgs.Empty);
            return IntPtr.Zero;
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string? lpszClassName;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassW(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int width, int height,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hWnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
