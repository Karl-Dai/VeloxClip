using FluentAssertions;
using VeloxClip.Core.Capture;
using Xunit;

namespace VeloxClip.Core.Tests.Capture;

public class ContentHasherTests
{
    [Fact]
    public void Hash_String_MatchesKnownSha256Vector()
    {
        // SHA-256("abc") — standard test vector, lowercase hex.
        ContentHasher.Hash("abc").Should()
            .Be("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    }

    [Fact]
    public void Hash_EmptyString_MatchesKnownSha256Vector()
    {
        // SHA-256("") — standard test vector.
        ContentHasher.Hash("").Should()
            .Be("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
    }

    [Fact]
    public void Hash_Bytes_IsLowercaseHexAndStable()
    {
        var hash = ContentHasher.Hash(new byte[] { 1, 2, 3 });
        hash.Should().HaveLength(64);
        hash.Should().Be(hash.ToLowerInvariant());
        hash.Should().Be(ContentHasher.Hash(new byte[] { 1, 2, 3 }));
    }
}
