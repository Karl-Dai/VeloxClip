using System;
using System.Security.Cryptography;
using System.Text;

namespace VeloxClip.Core.Capture;

/// <summary>Computes the SHA-256 hex digest used as a clipboard entry's dedup key.</summary>
public static class ContentHasher
{
    /// <summary>Hashes raw bytes; returns a lowercase 64-char hex string.</summary>
    public static string Hash(ReadOnlySpan<byte> payload)
        => Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    /// <summary>Hashes a string via its UTF-8 encoding.</summary>
    public static string Hash(string text)
        => Hash(Encoding.UTF8.GetBytes(text));
}
