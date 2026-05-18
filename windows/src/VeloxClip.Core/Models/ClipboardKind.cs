using System;

namespace VeloxClip.Core.Models;

/// <summary>The kind of payload a clipboard entry holds.</summary>
public enum ClipboardKind
{
    Text,
    Rtf,
    Image,
    File,
    Color,
}

/// <summary>Maps <see cref="ClipboardKind"/> to and from its lowercase DB string form.</summary>
public static class ClipboardKindExtensions
{
    public static string ToDbString(this ClipboardKind kind) => kind switch
    {
        ClipboardKind.Text => "text",
        ClipboardKind.Rtf => "rtf",
        ClipboardKind.Image => "image",
        ClipboardKind.File => "file",
        ClipboardKind.Color => "color",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown clipboard kind."),
    };

    public static ClipboardKind ParseClipboardKind(string dbValue) => dbValue switch
    {
        "text" => ClipboardKind.Text,
        "rtf" => ClipboardKind.Rtf,
        "image" => ClipboardKind.Image,
        "file" => ClipboardKind.File,
        "color" => ClipboardKind.Color,
        _ => throw new ArgumentException($"Unknown clipboard kind '{dbValue}'.", nameof(dbValue)),
    };
}
