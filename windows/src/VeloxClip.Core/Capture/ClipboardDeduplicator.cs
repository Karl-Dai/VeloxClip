using System;

namespace VeloxClip.Core.Capture;

/// <summary>
/// Tier-1 deduplication: discard a capture that repeats the most recent entry
/// within a short window. (Tier-2 "move to top" lives in the clipboard store.)
/// </summary>
public static class ClipboardDeduplicator
{
    /// <summary>The rapid-repeat suppression window.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    /// <summary>
    /// True if the new capture (<paramref name="newHash"/>) should be discarded
    /// because the most recent stored entry has the same hash and was created
    /// strictly less than <see cref="Window"/> ago.
    /// </summary>
    public static bool IsWithinDedupWindow(
        string newHash,
        string? mostRecentHash,
        DateTimeOffset mostRecentCreatedAt,
        DateTimeOffset now)
    {
        if (mostRecentHash is null
            || !string.Equals(newHash, mostRecentHash, StringComparison.Ordinal))
        {
            return false;
        }

        return now - mostRecentCreatedAt < Window;
    }
}
