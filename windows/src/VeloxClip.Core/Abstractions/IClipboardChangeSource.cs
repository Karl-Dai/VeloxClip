using System;

namespace VeloxClip.Core.Abstractions;

/// <summary>Raises an event whenever the system clipboard changes.</summary>
public interface IClipboardChangeSource
{
    /// <summary>Fired after the clipboard contents change.</summary>
    event EventHandler? ClipboardChanged;

    /// <summary>Begins listening for clipboard changes.</summary>
    void Start();

    /// <summary>Stops listening and releases any OS resources.</summary>
    void Stop();
}
