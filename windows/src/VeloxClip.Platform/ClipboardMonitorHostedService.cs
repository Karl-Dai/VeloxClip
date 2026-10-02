using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VeloxClip.Core.Abstractions;
using VeloxClip.Core.Capture;

namespace VeloxClip.Platform;

/// <summary>
/// Hosted service that owns the clipboard monitor lifecycle: runs orphan-blob
/// reconciliation at startup, registers the Win32 clipboard listener, and routes
/// each change event through <see cref="ClipboardCaptureService"/> on a background
/// thread so the UI thread is never blocked.
/// </summary>
public sealed class ClipboardMonitorHostedService : IHostedService
{
    private readonly object _captureGate = new();
    private Task _pendingCapture = Task.CompletedTask;
    private bool _running;

    private readonly IClipboardChangeSource _changeSource;
    private readonly ClipboardCaptureService _captureService;
    private readonly OrphanBlobReconciler _reconciler;
    private readonly ILogger<ClipboardMonitorHostedService> _logger;

    public ClipboardMonitorHostedService(
        IClipboardChangeSource changeSource,
        ClipboardCaptureService captureService,
        OrphanBlobReconciler reconciler,
        ILogger<ClipboardMonitorHostedService> logger)
    {
        _changeSource = changeSource ?? throw new ArgumentNullException(nameof(changeSource));
        _captureService = captureService ?? throw new ArgumentNullException(nameof(captureService));
        _reconciler = reconciler ?? throw new ArgumentNullException(nameof(reconciler));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _reconciler.Reconcile();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Orphan-blob reconciliation failed at startup.");
        }

        lock (_captureGate)
        {
            _running = true;
        }

        _changeSource.ClipboardChanged += OnClipboardChanged;
        _changeSource.Start();
        _logger.LogInformation("Clipboard monitor started.");
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task pending;
        lock (_captureGate)
        {
            _running = false;
            pending = _pendingCapture;
        }

        _changeSource.ClipboardChanged -= OnClipboardChanged;
        _changeSource.Stop();
        await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Clipboard monitor stopped.");
    }

    private void OnClipboardChanged(object? sender, EventArgs e)
    {
        lock (_captureGate)
        {
            if (!_running)
            {
                return;
            }

            // Each read/dedup/write pipeline finishes before the next one starts.
            // Track the chain so shutdown cannot race unfinished database writes.
            _pendingCapture = _pendingCapture.ContinueWith(
                _ => _captureService.Capture(), CancellationToken.None,
                TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }
}
