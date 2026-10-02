namespace VeloxClip.Core.Abstractions;

/// <summary>Persistent application settings (key/value).</summary>
public interface IAppSettingsStore
{
    /// <summary>
    /// The maximum number of clipboard entries to retain. Returns the stored value,
    /// or 100 (and persists it) if unset.
    /// </summary>
    int GetHistoryLimit();

    /// <summary>Sets the history limit.</summary>
    void SetHistoryLimit(int limit);
}
