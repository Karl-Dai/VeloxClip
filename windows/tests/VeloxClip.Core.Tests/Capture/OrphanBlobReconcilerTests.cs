using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using VeloxClip.Core.Abstractions;
using VeloxClip.Core.Capture;
using VeloxClip.Core.Models;
using Xunit;

namespace VeloxClip.Core.Tests.Capture;

public class OrphanBlobReconcilerTests
{
    [Fact]
    public void Reconcile_DeletesOrphanFiles_AndRowsWithMissingFiles()
    {
        // Row "good" has a file; row "ghost" has no file.
        var good = new ImageBlobReference(Guid.NewGuid(), "blobs/good.png");
        var ghost = new ImageBlobReference(Guid.NewGuid(), "blobs/ghost.png");
        var store = new FakeStore(new[] { good, ghost });
        var blobStore = new FakeBlobStore(existing: new[] { "blobs/good.png", "blobs/loose.png" });

        new OrphanBlobReconciler(store, blobStore).Reconcile();

        // Orphan file "blobs/loose.png" deleted (no row references it).
        blobStore.ReconciledKnownPaths.Should().BeEquivalentTo(new[] { "blobs/good.png", "blobs/ghost.png" });
        // Row "ghost" deleted (its file is missing).
        store.DeletedIds.Should().ContainSingle().Which.Should().Be(ghost.Id);
        store.DeletedIds.Should().NotContain(good.Id);
    }

    private sealed class FakeStore : IClipboardStore
    {
        private readonly IReadOnlyList<ImageBlobReference> _refs;
        public List<Guid> DeletedIds { get; } = new();

        public FakeStore(IReadOnlyList<ImageBlobReference> refs) => _refs = refs;

        public IReadOnlyList<ImageBlobReference> GetImageBlobReferences() => _refs;
        public void DeleteById(Guid id) => DeletedIds.Add(id);

        public AddResult Add(ClipboardEntry entry, int historyLimit) => throw new NotSupportedException();
        public ClipboardEntry? GetMostRecent() => throw new NotSupportedException();
        public IReadOnlyList<ClipboardEntry> GetRecent(int count) => throw new NotSupportedException();
    }

    private sealed class FakeBlobStore : IBlobStore
    {
        private readonly HashSet<string> _existing;
        public IReadOnlySet<string>? ReconciledKnownPaths { get; private set; }

        public FakeBlobStore(IEnumerable<string> existing)
            => _existing = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);

        public bool Exists(string relativePath) => _existing.Contains(relativePath);
        public void ReconcileOrphans(IReadOnlySet<string> knownRelativePaths)
            => ReconciledKnownPaths = knownRelativePaths;

        public string Save(byte[] pngBytes) => throw new NotSupportedException();
        public void Delete(string relativePath) => throw new NotSupportedException();
    }
}
