using System;
using System.Text.RegularExpressions;

namespace VeloxClip.Core.Capture;

/// <summary>
/// Detects whether a string is a CSS-style color literal (hex or rgb/rgba).
/// Mirrors the macOS app's <c>isColor</c> classifier.
/// </summary>
public static partial class ColorDetector
{
    [GeneratedRegex("^#([A-Fa-f0-9]{6}|[A-Fa-f0-9]{3}|[A-Fa-f0-9]{8})$")]
    private static partial Regex HexRegex();

    [GeneratedRegex(@"^rgba?\((\d+),\s*(\d+),\s*(\d+)(?:,\s*([\d.]+))?\)$")]
    private static partial Regex RgbRegex();

    /// <summary>True if <paramref name="text"/> (after trimming) is a color literal.</summary>
    public static bool IsColor(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var trimmed = text.Trim();
        return HexRegex().IsMatch(trimmed) || RgbRegex().IsMatch(trimmed);
    }
}
