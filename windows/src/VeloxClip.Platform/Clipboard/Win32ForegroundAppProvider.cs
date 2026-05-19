using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using VeloxClip.Core.Abstractions;

namespace VeloxClip.Platform.Clipboard;

/// <summary>
/// Win32 <see cref="IForegroundAppProvider"/>: resolves the foreground window's
/// owning process name.
/// </summary>
public sealed partial class Win32ForegroundAppProvider : IForegroundAppProvider
{
    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

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
