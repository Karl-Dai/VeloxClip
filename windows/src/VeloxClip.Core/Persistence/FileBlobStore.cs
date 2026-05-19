using System;
using System.Collections.Generic;
using System.IO;
using VeloxClip.Core.Abstractions;
using VeloxClip.Core.Environment;

namespace VeloxClip.Core.Persistence;

/// <summary>
/// File-backed <see cref="IBlobStore"/>. Each blob is a PNG file under
/// <see cref="IAppPaths.Blobs"/>; relative paths are always of the form
/// <c>blobs/{guid}.png</c> with a forward slash, so they round-trip across platforms.
/// </summary>
public sealed class FileBlobStore : IBlobStore
{
    private const string RelativePrefix = "blobs/";

    private readonly IAppPaths _paths;

    public FileBlobStore(IAppPaths paths)
        => _paths = paths ?? throw new ArgumentNullException(nameof(paths));

    public string Save(byte[] pngBytes)
    {
        ArgumentNullException.ThrowIfNull(pngBytes);

        Directory.CreateDirectory(_paths.Blobs);
        var relativePath = RelativePrefix + Guid.NewGuid().ToString("n") + ".png";
        File.WriteAllBytes(ResolveAbsolute(relativePath), pngBytes);
        return relativePath;
    }

    public bool Exists(string relativePath)
        => File.Exists(ResolveAbsolute(relativePath));

    public void Delete(string relativePath)
    {
        var absolute = ResolveAbsolute(relativePath);
        if (File.Exists(absolute))
        {
            File.Delete(absolute);
        }
    }

    public void ReconcileOrphans(IReadOnlySet<string> knownRelativePaths)
    {
        ArgumentNullException.ThrowIfNull(knownRelativePaths);

        if (!Directory.Exists(_paths.Blobs))
        {
            return;
        }

        foreach (var absolute in Directory.EnumerateFiles(_paths.Blobs))
        {
            var relativePath = RelativePrefix + Path.GetFileName(absolute);
            if (!knownRelativePaths.Contains(relativePath))
            {
                File.Delete(absolute);
            }
        }
    }

    private string ResolveAbsolute(string relativePath)
    {
        // relativePath is "blobs/<name>"; resolve under the app root.
        var fileName = Path.GetFileName(relativePath);
        return Path.Combine(_paths.Blobs, fileName);
    }
}
