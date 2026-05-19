using System;
using System.Collections.Generic;
using VeloxClip.Core.Abstractions;

namespace VeloxClip.Core.Capture;

/// <summary>
/// Startup reconciliation between the clipboard store and the blob store:
/// deletes blob files no row references, and deletes image rows whose blob file
/// is missing.
/// </summary>
public sealed class OrphanBlobReconciler
{
    private readonly IClipboardStore _store;
    private readonly IBlobStore _blobStore;

    public OrphanBlobReconciler(IClipboardStore store, IBlobStore blobStore)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
    }

    /// <summary>Runs the two-way reconciliation. Safe to call once at startup.</summary>
    public void Reconcile()
    {
        var references = _store.GetImageBlobReferences();

        var knownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in references)
        {
            knownPaths.Add(reference.BlobPath);
        }

        // File → no row: delete blob files not referenced by any row.
        _blobStore.ReconcileOrphans(knownPaths);

        // Row → no file: delete image rows whose blob file is gone.
        foreach (var reference in references)
        {
            if (!_blobStore.Exists(reference.BlobPath))
            {
                _store.DeleteById(reference.Id);
            }
        }
    }
}
