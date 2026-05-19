using System;
using Microsoft.Extensions.DependencyInjection;
using VeloxClip.Core.Abstractions;
using VeloxClip.Core.Capture;
using VeloxClip.Core.Persistence;
using VeloxClip.Platform.Clipboard;

namespace VeloxClip.Platform;

/// <summary>
/// Registers Windows-specific service implementations and the P1 clipboard-core
/// pipeline with the App-layer DI container.
/// </summary>
public static class PlatformServiceCollectionExtensions
{
    public static IServiceCollection AddVeloxClipPlatform(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Shared infrastructure.
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<VeloxClipDatabase>();

        // Persistence (Core).
        services.AddSingleton<IClipboardStore, SqliteClipboardStore>();
        services.AddSingleton<IAppSettingsStore, SqliteAppSettingsStore>();
        services.AddSingleton<IBlobStore, FileBlobStore>();

        // Capture logic (Core).
        services.AddSingleton<Blacklist>();
        services.AddSingleton<OrphanBlobReconciler>();
        services.AddSingleton<ClipboardCaptureService>();

        // Win32 implementations (Platform).
        services.AddSingleton<IClipboardChangeSource, Win32ClipboardChangeSource>();
        services.AddSingleton<IClipboardReader, Win32ClipboardReader>();
        services.AddSingleton<IForegroundAppProvider, Win32ForegroundAppProvider>();

        // The monitor itself.
        services.AddHostedService<ClipboardMonitorHostedService>();

        return services;
    }
}
