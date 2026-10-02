using System;
using System.Collections.Generic;

namespace VeloxClip.Core.Capture;

/// <summary>
/// The set of source applications whose clipboard activity is never captured.
/// P1 ships a hardcoded list of common password managers; P7 will add a settings UI.
/// </summary>
public sealed class Blacklist
{
    private static readonly string[] DefaultProcessNames =
    {
        "1password", "bitwarden", "keepass", "keepassxc",
        "lastpass", "dashlane", "enpass", "roboform",
    };

    private readonly HashSet<string> _names;

    public Blacklist()
        : this(DefaultProcessNames)
    {
    }

    /// <summary>Test seam: construct with an explicit name set.</summary>
    internal Blacklist(IEnumerable<string> processNames)
        => _names = new HashSet<string>(processNames, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True if a copy from <paramref name="processName"/> must be ignored.
    /// The name is trimmed and a trailing <c>.exe</c> is stripped before comparison.
    /// A null/blank name is treated as unknown and allowed (returns false).
    /// </summary>
    public bool ShouldIgnore(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        var stem = processName.Trim();
        if (stem.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^4];
        }

        return _names.Contains(stem);
    }
}
