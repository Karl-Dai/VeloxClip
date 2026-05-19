using System;
using Microsoft.Extensions.Logging;
using VeloxClip.Core.Abstractions;
using VeloxClip.Core.Models;

namespace VeloxClip.Core.Capture;

/// <summary>
/// Orchestrates one clipboard capture: read → blacklist → classify → size-check →
/// hash → Tier-1 dedup → blob save → persist → orphan cleanup. Every call is wrapped
/// so a single failure logs and returns rather than killing the monitor.
/// </summary>
public sealed class ClipboardCaptureService
{
    /// <summary>Captured images larger than this (PNG bytes) are discarded.</summary>
    public const int MaxImagePngBytes = 16 * 1024 * 1024;

    private readonly IClipboardReader _reader;
    private readonly IForegroundAppProvider _foregroundApp;
    private readonly Blacklist _blacklist;
    private readonly IClipboardStore _store;
    private readonly IBlobStore _blobStore;
    private readonly IAppSettingsStore _settings;
    private readonly TimeProvider _clock;
    private readonly ILogger<ClipboardCaptureService> _logger;

    public ClipboardCaptureService(
        IClipboardReader reader,
        IForegroundAppProvider foregroundApp,
        Blacklist blacklist,
        IClipboardStore store,
        IBlobStore blobStore,
        IAppSettingsStore settings,
        TimeProvider clock,
        ILogger<ClipboardCaptureService> logger)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _foregroundApp = foregroundApp ?? throw new ArgumentNullException(nameof(foregroundApp));
        _blacklist = blacklist ?? throw new ArgumentNullException(nameof(blacklist));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Runs the capture pipeline once. Never throws.</summary>
    public void Capture()
    {
        try
        {
            CaptureCore();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Clipboard capture failed; monitor continues.");
        }
    }

    private void CaptureCore()
    {
        var sourceApp = _foregroundApp.GetForegroundProcessName();
        if (_blacklist.ShouldIgnore(sourceApp))
        {
            _logger.LogDebug("Skipping capture: source app '{App}' is blacklisted.", sourceApp);
            return;
        }

        var capture = _reader.TryRead();
        if (capture is null)
        {
            return;
        }

        var kind = capture.Kind;
        var content = capture.Text;
        var imageBytes = capture.ImageBytes;

        if (kind == ClipboardKind.Text && content is not null && ColorDetector.IsColor(content))
        {
            kind = ClipboardKind.Color;
        }

        if (kind == ClipboardKind.Image)
        {
            if (imageBytes is null)
            {
                return;
            }

            if (imageBytes.Length > MaxImagePngBytes)
            {
                _logger.LogWarning(
                    "Discarding clipboard image: {Size} bytes exceeds the {Limit}-byte cap.",
                    imageBytes.Length, MaxImagePngBytes);
                return;
            }
        }

        var hash = kind == ClipboardKind.Image
            ? ContentHasher.Hash(imageBytes!)
            : ContentHasher.Hash(content ?? string.Empty);

        var now = _clock.GetUtcNow();
        var mostRecent = _store.GetMostRecent();
        if (mostRecent is not null
            && ClipboardDeduplicator.IsWithinDedupWindow(hash, mostRecent.ContentHash, mostRecent.CreatedAt, now))
        {
            _logger.LogDebug("Skipping capture: repeat within the {Window} dedup window.",
                ClipboardDeduplicator.Window);
            return;
        }

        string? blobPath = null;
        if (kind == ClipboardKind.Image)
        {
            blobPath = _blobStore.Save(imageBytes!);
        }

        var entry = new ClipboardEntry(
            Id: Guid.NewGuid(),
            CreatedAt: now,
            Kind: kind,
            Content: kind == ClipboardKind.Image ? null : content,
            BlobPath: blobPath,
            ContentHash: hash,
            SourceApp: sourceApp);

        var historyLimit = _settings.GetHistoryLimit();
        var result = _store.Add(entry, historyLimit);

        foreach (var orphan in result.OrphanedBlobPaths)
        {
            _blobStore.Delete(orphan);
        }

        _logger.LogInformation(
            "Captured {Kind} entry from '{App}' (deduplicated={Dedup}).",
            kind, sourceApp ?? "unknown", result.WasDeduplicated);
    }
}
