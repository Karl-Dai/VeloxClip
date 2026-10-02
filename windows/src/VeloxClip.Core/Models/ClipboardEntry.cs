using System;

namespace VeloxClip.Core.Models;

/// <summary>
/// One persisted clipboard history entry.
/// Text/RTF/File/Color payloads live in <see cref="Content"/>; images live as an
/// external PNG file referenced by <see cref="BlobPath"/>.
/// </summary>
public sealed record ClipboardEntry(
    Guid Id,
    DateTimeOffset CreatedAt,
    ClipboardKind Kind,
    string? Content,
    string? BlobPath,
    string ContentHash,
    string? SourceApp);
