using FluentAssertions;
using VeloxClip.Core.Capture;
using Xunit;

namespace VeloxClip.Core.Tests.Capture;

public class BlacklistTests
{
    private readonly Blacklist _blacklist = new();

    [Theory]
    [InlineData("Bitwarden.exe")]   // with .exe suffix
    [InlineData("bitwarden")]       // without suffix
    [InlineData("BITWARDEN")]       // case-insensitive
    [InlineData("1Password.exe")]
    [InlineData("KeePassXC")]
    [InlineData("  lastpass  ")]    // surrounding whitespace
    public void ShouldIgnore_ReturnsTrue_ForBlacklistedProcesses(string processName)
        => _blacklist.ShouldIgnore(processName).Should().BeTrue();

    [Theory]
    [InlineData("notepad")]
    [InlineData("chrome.exe")]
    [InlineData("explorer")]
    public void ShouldIgnore_ReturnsFalse_ForOtherProcesses(string processName)
        => _blacklist.ShouldIgnore(processName).Should().BeFalse();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ShouldIgnore_ReturnsFalse_ForMissingProcessName(string? processName)
        => _blacklist.ShouldIgnore(processName).Should().BeFalse();
}
