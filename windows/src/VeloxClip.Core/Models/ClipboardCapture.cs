namespace VeloxClip.Core.Models;

/// <summary>
/// Raw result of reading the current clipboard, produced by an
/// <c>IClipboardReader</c> before classification and persistence.
/// For <see cref="ClipboardKind.Image"/>, <see cref="ImageBytes"/> holds PNG bytes
/// and <see cref="Text"/> is null. For all other kinds, <see cref="Text"/> holds the
/// payload and <see cref="ImageBytes"/> is null.
/// </summary>
public sealed record ClipboardCapture(ClipboardKind Kind, string? Text, byte[]? ImageBytes);
