using System;
using FluentAssertions;
using VeloxClip.Core.Models;
using Xunit;

namespace VeloxClip.Core.Tests.Models;

public class ClipboardKindTests
{
    [Theory]
    [InlineData(ClipboardKind.Text, "text")]
    [InlineData(ClipboardKind.Rtf, "rtf")]
    [InlineData(ClipboardKind.Image, "image")]
    [InlineData(ClipboardKind.File, "file")]
    [InlineData(ClipboardKind.Color, "color")]
    public void ToDbString_And_Parse_RoundTrip(ClipboardKind kind, string dbValue)
    {
        kind.ToDbString().Should().Be(dbValue);
        ClipboardKindExtensions.ParseClipboardKind(dbValue).Should().Be(kind);
    }

    [Fact]
    public void ParseClipboardKind_RejectsUnknownValue()
    {
        Action act = () => ClipboardKindExtensions.ParseClipboardKind("video");
        act.Should().Throw<ArgumentException>();
    }
}
