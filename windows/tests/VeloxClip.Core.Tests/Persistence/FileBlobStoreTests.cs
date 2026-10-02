using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using VeloxClip.Core.Environment;
using VeloxClip.Core.Persistence;
using Xunit;

namespace VeloxClip.Core.Tests.Persistence;

public class FileBlobStoreTests : IDisposable
{
    private readonly string _tempBase;
    private readonly AppPaths _paths;
    private readonly FileBlobStore _store;

    public FileBlobStoreTests()
    {
        _tempBase = Path.Combine(Path.GetTempPath(), "veloxclip-blob-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempBase);
        _paths = new AppPaths(_tempBase);
        AppEnvironmentBootstrapper.Ensure(_paths);
        _store = new FileBlobStore(_paths);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempBase))
        {
            Directory.Delete(_tempBase, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Save_ReturnsForwardSlashRelativePath_AndWritesTheFile()
    {
        var path = _store.Save(new byte[] { 9, 8, 7 });

        path.Should().StartWith("blobs/");
        path.Should().EndWith(".png");
        path.Should().NotContain("\\");
        _store.Exists(path).Should().BeTrue();
    }

    [Fact]
    public void Save_WritesExactBytes()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var path = _store.Save(payload);

        var onDisk = File.ReadAllBytes(Path.Combine(_paths.Root, path));
        onDisk.Should().Equal(payload);
    }

    [Fact]
    public void Delete_RemovesTheFile()
    {
        var path = _store.Save(new byte[] { 1 });
        _store.Delete(path);
        _store.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void Delete_IsNoOp_WhenFileMissing()
    {
        Action act = () => _store.Delete("blobs/does-not-exist.png");
        act.Should().NotThrow();
    }

    [Fact]
    public void ReconcileOrphans_DeletesFilesNotInKnownSet()
    {
        var keep = _store.Save(new byte[] { 1 });
        var orphan = _store.Save(new byte[] { 2 });

        _store.ReconcileOrphans(new HashSet<string> { keep });

        _store.Exists(keep).Should().BeTrue();
        _store.Exists(orphan).Should().BeFalse();
    }
}
