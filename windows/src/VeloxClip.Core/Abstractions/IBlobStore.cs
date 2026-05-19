using System.Collections.Generic;

namespace VeloxClip.Core.Abstractions;

/// <summary>Stores clipboard image payloads as files outside the SQLite database.</summary>
public interface IBlobStore
{
    /// <summary>
    /// Writes <paramref name="pngBytes"/> to a new file and returns its path relative
    /// to the app root (e.g. <c>blobs/3f2a….png</c>, always forward-slash separated).
    /// </summary>
    string Save(byte[] pngBytes);

    /// <summary>True if a blob file exists for the given relative path.</summary>
    bool Exists(string relativePath);

    /// <summary>Deletes the blob file at the given relative path; no-op if absent.</summary>
    void Delete(string relativePath);

    /// <summary>
    /// Deletes every file in the blob directory whose relative path is not in
    /// <paramref name="knownRelativePaths"/> (orphan-file cleanup).
    /// </summary>
    void ReconcileOrphans(IReadOnlySet<string> knownRelativePaths);
}
