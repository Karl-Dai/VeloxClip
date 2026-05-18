using System;
using System.Collections.Generic;
using VeloxClip.Core.Models;

namespace VeloxClip.Core.Abstractions;

/// <summary>Outcome of <see cref="IClipboardStore.Add"/>.</summary>
/// <param name="WasDeduplicated">
/// True if an existing entry with the same hash was moved to the top instead of
/// inserting a new row.
/// </param>
/// <param name="OrphanedBlobPaths">
/// Relative blob paths whose backing files the caller must delete: either the
/// incoming image (when deduplicated) or images trimmed by the history cap.
/// </param>
public sealed record AddResult(bool WasDeduplicated, IReadOnlyList<string> OrphanedBlobPaths);

/// <summary>An image entry's id paired with its blob path.</summary>
public sealed record ImageBlobReference(Guid Id, string BlobPath);

/// <summary>Persistence for clipboard history entries.</summary>
public interface IClipboardStore
{
    /// <summary>
    /// Persists <paramref name="entry"/>. If an entry with the same
    /// <see cref="ClipboardEntry.ContentHash"/> already exists, that row's timestamp
    /// is bumped to the entry's <see cref="ClipboardEntry.CreatedAt"/> (Tier-2 move
    /// to top) and no new row is inserted. After an insert, rows beyond
    /// <paramref name="historyLimit"/> (oldest first) are deleted.
    /// </summary>
    AddResult Add(ClipboardEntry entry, int historyLimit);

    /// <summary>The most recent entry, or null if the store is empty.</summary>
    ClipboardEntry? GetMostRecent();

    /// <summary>Up to <paramref name="count"/> most recent entries, newest first.</summary>
    IReadOnlyList<ClipboardEntry> GetRecent(int count);

    /// <summary>Every image entry's id and blob path.</summary>
    IReadOnlyList<ImageBlobReference> GetImageBlobReferences();

    /// <summary>Deletes the entry with the given id, if present.</summary>
    void DeleteById(Guid id);
}
