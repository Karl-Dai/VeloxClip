using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using VeloxClip.Core.Abstractions;

namespace VeloxClip.Platform.Clipboard;

/// <summary>
/// Win32 <see cref="IForegroundAppProvider"/>: resolves the foreground window's
/// owning process name.
/// </summary>
public sealed class Win32ForegroundAppProvider : IForegroundAppProvider
{
    // Classic [DllImport] (not [LibraryImport]) keeps this consistent with the
    // other Win32 wrappers and avoids requiring <AllowUnsafeBlocks>.
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    public string? GetForegroundProcessName()
    {
        var hWnd = GetForegroundWindow();
        if (hWnd == IntPtr.Zero)
        {
            return null;
        }

        _ = GetWindowThreadProcessId(hWnd, out var processId);
        if (processId == 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName; // already without the ".exe" suffix
        }
        catch (ArgumentException)
        {
            return null; // process exited between the calls
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
