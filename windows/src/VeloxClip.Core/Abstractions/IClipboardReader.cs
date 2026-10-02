using VeloxClip.Core.Models;

namespace VeloxClip.Core.Abstractions;

/// <summary>Reads the current clipboard contents into a <see cref="ClipboardCapture"/>.</summary>
public interface IClipboardReader
{
    /// <summary>
    /// Reads the clipboard. Returns null when the clipboard is empty, holds only
    /// unsupported formats, or cannot be opened.
    /// </summary>
    ClipboardCapture? TryRead();
}
