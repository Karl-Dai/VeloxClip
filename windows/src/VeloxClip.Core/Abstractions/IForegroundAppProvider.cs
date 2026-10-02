namespace VeloxClip.Core.Abstractions;

/// <summary>Provides the process name of the current foreground application.</summary>
public interface IForegroundAppProvider
{
    /// <summary>The foreground process name (without <c>.exe</c>), or null if unknown.</summary>
    string? GetForegroundProcessName();
}
