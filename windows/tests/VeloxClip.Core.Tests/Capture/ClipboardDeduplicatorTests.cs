using System;
using FluentAssertions;
using VeloxClip.Core.Capture;
using Xunit;

namespace VeloxClip.Core.Tests.Capture;

public class ClipboardDeduplicatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IsWithinDedupWindow_True_WhenSameHashAndUnder5Seconds()
    {
        var recent = Now - TimeSpan.FromSeconds(4.9);
        ClipboardDeduplicator.IsWithinDedupWindow("hashA", "hashA", recent, Now)
            .Should().BeTrue();
    }

    [Fact]
    public void IsWithinDedupWindow_False_WhenSameHashButOver5Seconds()
    {
        var recent = Now - TimeSpan.FromSeconds(5.1);
        ClipboardDeduplicator.IsWithinDedupWindow("hashA", "hashA", recent, Now)
            .Should().BeFalse();
    }

    [Fact]
    public void IsWithinDedupWindow_False_AtExactly5Seconds()
    {
        var recent = Now - TimeSpan.FromSeconds(5.0);
        ClipboardDeduplicator.IsWithinDedupWindow("hashA", "hashA", recent, Now)
            .Should().BeFalse();
    }

    [Fact]
    public void IsWithinDedupWindow_False_WhenHashDiffers()
    {
        var recent = Now - TimeSpan.FromSeconds(1);
        ClipboardDeduplicator.IsWithinDedupWindow("hashA", "hashB", recent, Now)
            .Should().BeFalse();
    }

    [Fact]
    public void IsWithinDedupWindow_False_WhenNoRecentEntry()
    {
        ClipboardDeduplicator.IsWithinDedupWindow("hashA", null, Now, Now)
            .Should().BeFalse();
    }
}
