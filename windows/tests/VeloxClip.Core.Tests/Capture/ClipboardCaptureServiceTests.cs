using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging.Abstractions;
using FluentAssertions;
using VeloxClip.Core.Abstractions;
using VeloxClip.Core.Capture;
using VeloxClip.Core.Models;
using Xunit;

namespace VeloxClip.Core.Tests.Capture;

public class ClipboardCaptureServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Capture_InsertsTextEntry_ForPlainText()
    {
        var harness = new Harness { Capture = new ClipboardCapture(ClipboardKind.Text, "hello world", null) };

        harness.Service.Capture();

        harness.Store.Added.Should().ContainSingle();
        harness.Store.Added[0].Entry.Kind.Should().Be(ClipboardKind.Text);
        harness.Store.Added[0].Entry.Content.Should().Be("hello world");
        harness.Store.Added[0].Entry.SourceApp.Should().Be("notepad");
    }

    [Fact]
    public void Capture_ReclassifiesTextAsColor_WhenTextIsAColor()
    {
        var harness = new Harness { Capture = new ClipboardCapture(ClipboardKind.Text, "#FF5733", null) };

        harness.Service.Capture();

        harness.Store.Added.Should().ContainSingle();
        harness.Store.Added[0].Entry.Kind.Should().Be(ClipboardKind.Color);
    }

    [Fact]
    public void Capture_DoesNothing_WhenSourceAppIsBlacklisted()
    {
        var harness = new Harness
        {
            Capture = new ClipboardCapture(ClipboardKind.Text, "secret", null),
            ForegroundProcess = "bitwarden",
        };

        harness.Service.Capture();

        harness.Store.Added.Should().BeEmpty();
        harness.Reader.ReadCount.Should().Be(0); // short-circuited before reading
    }

    [Fact]
    public void Capture_DoesNothing_WhenReaderReturnsNull()
    {
        var harness = new Harness { Capture = null };

        harness.Service.Capture();

        harness.Store.Added.Should().BeEmpty();
    }

    [Fact]
    public void Capture_DiscardsImage_LargerThan16Mb()
    {
        var harness = new Harness
        {
            Capture = new ClipboardCapture(ClipboardKind.Image, null, new byte[(16 * 1024 * 1024) + 1]),
        };

        harness.Service.Capture();

        harness.Store.Added.Should().BeEmpty();
        harness.BlobStore.Saved.Should().BeEmpty();
    }

    [Fact]
    public void Capture_SavesBlob_ForImageEntry()
    {
        var harness = new Harness
        {
            Capture = new ClipboardCapture(ClipboardKind.Image, null, new byte[] { 1, 2, 3 }),
        };

        harness.Service.Capture();

        harness.BlobStore.Saved.Should().ContainSingle();
        harness.Store.Added.Should().ContainSingle();
        harness.Store.Added[0].Entry.Kind.Should().Be(ClipboardKind.Image);
        harness.Store.Added[0].Entry.BlobPath.Should().NotBeNull();
        harness.Store.Added[0].Entry.Content.Should().BeNull();
    }

    [Fact]
    public void Capture_SkipsInsert_WhenWithinTier1DedupWindow()
    {
        var harness = new Harness { Capture = new ClipboardCapture(ClipboardKind.Text, "dup", null) };
        // Most-recent stored entry has the same hash, 2 seconds ago.
        harness.Store.MostRecent = new ClipboardEntry(
            Guid.NewGuid(), Now - TimeSpan.FromSeconds(2), ClipboardKind.Text, "dup", null,
            ContentHasher.Hash("dup"), "notepad");

        harness.Service.Capture();

        harness.Store.Added.Should().BeEmpty();
    }

    [Fact]
    public void Capture_DeletesOrphanBlob_WhenStoreReportsDeduplication()
    {
        var harness = new Harness
        {
            Capture = new ClipboardCapture(ClipboardKind.Image, null, new byte[] { 7 }),
        };
        harness.Store.NextAddResult = entry =>
            new AddResult(WasDeduplicated: true, new[] { entry.BlobPath! });

        harness.Service.Capture();

        harness.BlobStore.Deleted.Should().ContainSingle();
    }

    // ---- test harness ----

    private sealed class Harness
    {
        public ClipboardCapture? Capture { get; set; }
        public string? ForegroundProcess { get; set; } = "notepad";
        public FakeReader Reader { get; }
        public FakeStore Store { get; } = new();
        public FakeBlobStore BlobStore { get; } = new();
        public ClipboardCaptureService Service { get; }

        public Harness()
        {
            Reader = new FakeReader(() => Capture);
            Service = new ClipboardCaptureService(
                Reader,
                new FakeForegroundApp(() => ForegroundProcess),
                new Blacklist(),
                Store,
                BlobStore,
                new FakeSettings(),
                new FixedTimeProvider(Now),
                NullLogger<ClipboardCaptureService>.Instance);
        }
    }

    private sealed class FakeReader : IClipboardReader
    {
        private readonly Func<ClipboardCapture?> _capture;
        public int ReadCount { get; private set; }
        public FakeReader(Func<ClipboardCapture?> capture) => _capture = capture;
        public ClipboardCapture? TryRead()
        {
            ReadCount++;
            return _capture();
        }
    }

    private sealed class FakeForegroundApp : IForegroundAppProvider
    {
        private readonly Func<string?> _process;
        public FakeForegroundApp(Func<string?> process) => _process = process;
        public string? GetForegroundProcessName() => _process();
    }

    private sealed class FakeStore : IClipboardStore
    {
        public List<(ClipboardEntry Entry, int Limit)> Added { get; } = new();
        public ClipboardEntry? MostRecent { get; set; }
        public Func<ClipboardEntry, AddResult>? NextAddResult { get; set; }

        public AddResult Add(ClipboardEntry entry, int historyLimit)
        {
            Added.Add((entry, historyLimit));
            return NextAddResult?.Invoke(entry)
                ?? new AddResult(WasDeduplicated: false, Array.Empty<string>());
        }

        public ClipboardEntry? GetMostRecent() => MostRecent;
        public IReadOnlyList<ClipboardEntry> GetRecent(int count) => Array.Empty<ClipboardEntry>();
        public IReadOnlyList<ImageBlobReference> GetImageBlobReferences() => Array.Empty<ImageBlobReference>();
        public void DeleteById(Guid id) { }
    }

    private sealed class FakeBlobStore : IBlobStore
    {
        public List<byte[]> Saved { get; } = new();
        public List<string> Deleted { get; } = new();

        public string Save(byte[] pngBytes)
        {
            Saved.Add(pngBytes);
            return "blobs/" + Guid.NewGuid().ToString("n") + ".png";
        }

        public void Delete(string relativePath) => Deleted.Add(relativePath);
        public bool Exists(string relativePath) => true;
        public void ReconcileOrphans(IReadOnlySet<string> knownRelativePaths) { }
    }

    private sealed class FakeSettings : IAppSettingsStore
    {
        public int GetHistoryLimit() => 100;
        public void SetHistoryLimit(int limit) { }
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
