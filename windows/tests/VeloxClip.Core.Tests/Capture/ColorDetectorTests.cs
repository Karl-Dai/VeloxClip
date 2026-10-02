using FluentAssertions;
using VeloxClip.Core.Capture;
using Xunit;

namespace VeloxClip.Core.Tests.Capture;

public class ColorDetectorTests
{
    [Theory]
    [InlineData("#FF5733")]      // 6-digit hex
    [InlineData("#abc")]         // 3-digit hex
    [InlineData("#11223344")]    // 8-digit hex (with alpha)
    [InlineData("rgb(255, 0, 0)")]
    [InlineData("rgb(1,2,3)")]
    [InlineData("rgba(10, 20, 30, 0.5)")]
    [InlineData("  #FF5733  ")]  // surrounding whitespace tolerated
    public void IsColor_ReturnsTrue_ForColorStrings(string input)
        => ColorDetector.IsColor(input).Should().BeTrue();

    [Theory]
    [InlineData("hello world")]
    [InlineData("#GG5733")]      // non-hex digits
    [InlineData("#FF57")]        // wrong hex length
    [InlineData("rgb(1,2)")]     // too few components
    [InlineData("")]
    [InlineData("FF5733")]       // missing leading '#'
    public void IsColor_ReturnsFalse_ForNonColorStrings(string input)
        => ColorDetector.IsColor(input).Should().BeFalse();
}
