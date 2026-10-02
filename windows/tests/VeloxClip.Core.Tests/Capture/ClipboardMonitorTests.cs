using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using VeloxClip.Core.Abstractions;
using VeloxClip.Core.Capture;
using VeloxClip.Core.Models;
using VeloxClip.Platform;
using Xunit;

namespace VeloxClip.Core.Tests.Capture;

public class ClipboardMonitorTests
{
    [Fact]
    public async Task StopAsync_DrainsSerializedCaptures_AndIgnoresLaterChanges()
    {
        var source = new ChangeSource();
        using var release = new ManualResetEventSlim();
        var reader = new Reader(release);
        var store = new Store();
        var blobs = new Blobs();
        var capture = new ClipboardCaptureService(reader, new Foreground(), new Blacklist(),
            store, blobs, new Settings(), TimeProvider.System,
            NullLogger<ClipboardCaptureService>.Instance);
        var monitor = new ClipboardMonitorHostedService(source, capture,
            new OrphanBlobReconciler(store, blobs),
            NullLogger<ClipboardMonitorHostedService>.Instance);

        await monitor.StartAsync(CancellationToken.None);
        source.Raise();
        try
        {
            await reader.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var i = 0; i < 20; i++)
            {
                source.Raise();
            }

            var stopping = monitor.StopAsync(CancellationToken.None);
            stopping.IsCompleted.Should().BeFalse();
            reader.Count.Should().Be(1);
            release.Set();
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));

            reader.Count.Should().Be(21);
            reader.Overlapped.Should().BeFalse();
            source.Stopped.Should().BeTrue();
            source.Raise();
            reader.Count.Should().Be(21);
        }
        finally
        {
            release.Set();
            await monitor.StopAsync(CancellationToken.None);
        }
    }

    private sealed class ChangeSource : IClipboardChangeSource
    {
        public event EventHandler? ClipboardChanged;
        public bool Stopped { get; private set; }
        public void Start() { }
        public void Stop() => Stopped = true;
        public void Raise() => ClipboardChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class Reader(ManualResetEventSlim release) : IClipboardReader
    {
        private int _active;
        private int _count;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Count => Volatile.Read(ref _count);
        public bool Overlapped { get; private set; }
        public ClipboardCapture? TryRead()
        {
            if (Interlocked.Increment(ref _active) != 1)
            {
                Overlapped = true;
            }

            Interlocked.Increment(ref _count);
            Entered.TrySetResult();
            release.Wait(TimeSpan.FromSeconds(10));
            Interlocked.Decrement(ref _active);
            return null;
        }
    }

    private sealed class Foreground : IForegroundAppProvider
    {
        public string? GetForegroundProcessName() => "notepad";
    }

    private sealed class Settings : IAppSettingsStore
    {
        public int GetHistoryLimit() => 100;
        public void SetHistoryLimit(int limit) { }
    }

    private sealed class Store : IClipboardStore
    {
        public AddResult Add(ClipboardEntry entry, int historyLimit) => new(false, Array.Empty<string>());
        public ClipboardEntry? GetMostRecent() => null;
        public IReadOnlyList<ClipboardEntry> GetRecent(int count) => Array.Empty<ClipboardEntry>();
        public IReadOnlyList<ImageBlobReference> GetImageBlobReferences() => Array.Empty<ImageBlobReference>();
        public void DeleteById(Guid id) { }
    }

    private sealed class Blobs : IBlobStore
    {
        public string Save(byte[] pngBytes) => "blobs/test.png";
        public void Delete(string relativePath) { }
        public bool Exists(string relativePath) => false;
        public void ReconcileOrphans(IReadOnlySet<string> knownRelativePaths) { }
    }
}
