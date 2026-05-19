# VeloxClip Windows Port — P1 Clipboard Core Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the Windows clipboard-capture core — a background service that detects clipboard changes via a Win32 listener, captures text/RTF/image/file/color content, tracks the source app, deduplicates, and persists to SQLite with an external blob store and a history cap. No user-facing UI.

**Architecture:** All capture/persistence logic lives in `VeloxClip.Core` (pure `net8.0`, unit-testable on macOS/Linux) behind interfaces. `VeloxClip.Platform` provides thin Win32 implementations (clipboard listener, clipboard reader, foreground-app probe) and an `IHostedService` that wires them together. The orchestrator `ClipboardCaptureService` depends only on Core interfaces, so the whole pipeline is testable with fakes.

**Tech Stack:** C# 12, .NET 8, `Microsoft.Data.Sqlite` (cross-platform SQLite), `System.Drawing.Common` (Windows-only, DIB→PNG), Win32 P/Invoke (`AddClipboardFormatListener`, `GetClipboardData`, `GetForegroundWindow`), xUnit + FluentAssertions.

**Execution environment note:** `VeloxClip.Core` and its tests build and run on macOS/Linux via `dotnet test` — `Microsoft.Data.Sqlite` is cross-platform, so even the SQLite tasks (7–11) are locally verifiable. `VeloxClip.Platform` tasks (13–16) depend on Win32 and only build on Windows / the GitHub Actions `windows-2022` runner — CI is the source of truth for "did it build", and Task 17 covers the Windows smoke test. Each task states its verification path. If `dotnet` is unavailable on the dev machine, write the TDD tests as specified and rely on CI; do not skip writing them.

**Deliberate divergence from the macOS app — clipboard format priority:** the macOS `ClipboardMonitor` checks plain text *before* RTF, so copying from Word (which puts both on the pasteboard) is recorded as `text`, and `rtf` is almost never captured. That is a latent macOS bug. This plan's `Win32ClipboardReader` uses the priority **file > rtf > text > image** (Task 15), so Word copies are correctly recorded as `rtf` — which is what the spec's Definition of Done (§11: "复制 RTF(从 Word)… kind 正确") requires. The spec's §6 does not pin reader priority, so this resolves an ambiguity rather than contradicting the spec.

---

## File Map

**Modified P0 files (Task 1):**
- `windows/src/VeloxClip.Core/Environment/IAppPaths.cs` — add `Blobs` property
- `windows/src/VeloxClip.Core/Environment/AppPaths.cs` — implement `Blobs`
- `windows/src/VeloxClip.Core/Environment/AppEnvironmentBootstrapper.cs` — create `blobs/` dir
- `windows/tests/VeloxClip.Core.Tests/Environment/AppPathsTests.cs` — assert `Blobs`
- `windows/tests/VeloxClip.Core.Tests/Environment/AppEnvironmentBootstrapperTests.cs` — assert `blobs/`

**Build config (Task 2, 7):**
- `windows/Directory.Packages.props` — add `Microsoft.Data.Sqlite`, `System.Drawing.Common`
- `windows/src/VeloxClip.Core/VeloxClip.Core.csproj` — reference `Microsoft.Data.Sqlite`
- `windows/src/VeloxClip.Platform/VeloxClip.Platform.csproj` — reference `System.Drawing.Common`
- `windows/tests/VeloxClip.Core.Tests/VeloxClip.Core.Tests.csproj` — reference `Microsoft.Data.Sqlite`

**`VeloxClip.Core` — new files:**
- `Models/ClipboardKind.cs` — `enum ClipboardKind` + `ClipboardKindExtensions` (DB string mapping)
- `Models/ClipboardEntry.cs` — the persisted record
- `Models/ClipboardCapture.cs` — the reader-output DTO
- `Capture/ContentHasher.cs` — SHA-256 hex helper
- `Capture/ColorDetector.cs` — hex/rgb regex classifier
- `Capture/Blacklist.cs` — hardcoded password-manager process names
- `Capture/ClipboardDeduplicator.cs` — 5-second window pure function
- `Capture/ClipboardCaptureService.cs` — the pipeline orchestrator
- `Capture/OrphanBlobReconciler.cs` — startup blob/row reconciliation
- `Abstractions/IClipboardStore.cs` — store interface + `AddResult` + `ImageBlobReference`
- `Abstractions/IAppSettingsStore.cs` — settings interface
- `Abstractions/IBlobStore.cs` — blob interface
- `Abstractions/IClipboardChangeSource.cs` — change-source interface
- `Abstractions/IClipboardReader.cs` — reader interface
- `Abstractions/IForegroundAppProvider.cs` — foreground-app interface
- `Persistence/ClipboardSchema.cs` — DDL + `PRAGMA user_version`
- `Persistence/VeloxClipDatabase.cs` — connection factory + schema bootstrap
- `Persistence/SqliteClipboardStore.cs` — `IClipboardStore` impl
- `Persistence/SqliteAppSettingsStore.cs` — `IAppSettingsStore` impl
- `Persistence/FileBlobStore.cs` — `IBlobStore` impl

**`VeloxClip.Platform` — new files:**
- `Clipboard/Win32ForegroundAppProvider.cs` — `IForegroundAppProvider` impl
- `Clipboard/Win32ClipboardChangeSource.cs` — `IClipboardChangeSource` impl (message-only window)
- `Clipboard/Win32ClipboardReader.cs` — `IClipboardReader` impl
- `ClipboardMonitorHostedService.cs` — `IHostedService` that runs the monitor
- `PlatformServiceCollectionExtensions.cs` — *modified* to register all P1 services

**`VeloxClip.Core.Tests` — new test files:** one per Core unit (Tasks 3–12).

**Repo docs (Task 17):**
- `CHANGELOG.md` — add P1 entry under `[Unreleased]`

---

## Task 1: Add `Blobs` path to P0 `AppPaths`

P1 stores clipboard images as files under `%LOCALAPPDATA%\VeloxClip\blobs\`. P0's `IAppPaths` exposes `Root`/`Database`/`Cache`/`Logs`/`SettingsFile` but not `Blobs`. Add it and have the bootstrapper create the directory.

**Files:**
- Modify: `windows/src/VeloxClip.Core/Environment/IAppPaths.cs`
- Modify: `windows/src/VeloxClip.Core/Environment/AppPaths.cs`
- Modify: `windows/src/VeloxClip.Core/Environment/AppEnvironmentBootstrapper.cs`
- Modify: `windows/tests/VeloxClip.Core.Tests/Environment/AppPathsTests.cs`
- Modify: `windows/tests/VeloxClip.Core.Tests/Environment/AppEnvironmentBootstrapperTests.cs`

- [ ] **Step 1: Extend the `AppPathsTests.Subdirectories_AreRootedUnderRoot` test**

In `windows/tests/VeloxClip.Core.Tests/Environment/AppPathsTests.cs`, add one assertion line inside `Subdirectories_AreRootedUnderRoot`, after the `Logs` assertion:

```csharp
        paths.Blobs.Should().Be(Path.Combine(paths.Root, "blobs"));
```

- [ ] **Step 2: Extend the `AppEnvironmentBootstrapperTests.Ensure_CreatesAllSubdirectories` test**

In `windows/tests/VeloxClip.Core.Tests/Environment/AppEnvironmentBootstrapperTests.cs`, add one assertion line inside `Ensure_CreatesAllSubdirectories`, after the `Logs` assertion:

```csharp
        Directory.Exists(_paths.Blobs).Should().BeTrue();
```

- [ ] **Step 3: Run the tests, expect compile failure**

Run: `cd windows && dotnet test tests/VeloxClip.Core.Tests --nologo`
Expected: build fails — `IAppPaths` has no `Blobs` member. (Skip if `dotnet` unavailable; CI verifies.)

- [ ] **Step 4: Add `Blobs` to the `IAppPaths` interface**

In `windows/src/VeloxClip.Core/Environment/IAppPaths.cs`, add this property after `Logs`:

```csharp
    /// <summary>Directory holding externally-stored clipboard image blobs.</summary>
    string Blobs { get; }
```

- [ ] **Step 5: Implement `Blobs` in `AppPaths`**

In `windows/src/VeloxClip.Core/Environment/AppPaths.cs`:

Add the property assignment inside the constructor, after the `Logs` line:

```csharp
        Blobs = Path.Combine(Root, "blobs");
```

Add the property declaration after the `Logs` property:

```csharp
    public string Blobs { get; }
```

- [ ] **Step 6: Create the `blobs/` directory in the bootstrapper**

In `windows/src/VeloxClip.Core/Environment/AppEnvironmentBootstrapper.cs`, add this line inside `Ensure`, after `Directory.CreateDirectory(paths.Logs);`:

```csharp
        Directory.CreateDirectory(paths.Blobs);
```

- [ ] **Step 7: Run the tests, expect pass**

Run: `cd windows && dotnet test tests/VeloxClip.Core.Tests --nologo`
Expected: all existing tests pass (8 from P0, now with the extra assertions). (Skip if `dotnet` unavailable.)

- [ ] **Step 8: Commit**

```bash
git add windows/src/VeloxClip.Core/Environment windows/tests/VeloxClip.Core.Tests/Environment
git commit -m "feat(windows/core): add Blobs path for P1 clipboard image storage"
```

---

## Task 2: Add CPM packages and Core models

Add the two NuGet packages P1 needs, then create the three model types: the `ClipboardKind` enum (with DB-string mapping), the `ClipboardEntry` record, and the `ClipboardCapture` DTO.

**Files:**
- Modify: `windows/Directory.Packages.props`
- Modify: `windows/src/VeloxClip.Core/VeloxClip.Core.csproj`
- Create: `windows/src/VeloxClip.Core/Models/ClipboardKind.cs`
- Create: `windows/src/VeloxClip.Core/Models/ClipboardEntry.cs`
- Create: `windows/src/VeloxClip.Core/Models/ClipboardCapture.cs`
- Create: `windows/tests/VeloxClip.Core.Tests/Models/ClipboardKindTests.cs`

- [ ] **Step 1: Add packages to Central Package Management**

In `windows/Directory.Packages.props`, add these two lines inside the `<ItemGroup>` (after the `coverlet.collector` line):

```xml
    <PackageVersion Include="Microsoft.Data.Sqlite" Version="8.0.10" />
    <PackageVersion Include="System.Drawing.Common" Version="8.0.10" />
    <PackageVersion Include="Microsoft.Extensions.Hosting.Abstractions" Version="8.0.1" />
```

- [ ] **Step 2: Reference `Microsoft.Data.Sqlite` from the Core project**

In `windows/src/VeloxClip.Core/VeloxClip.Core.csproj`, add this line inside the `<ItemGroup>` that already has `CommunityToolkit.Mvvm` and `Microsoft.Extensions.Logging`:

```xml
    <PackageReference Include="Microsoft.Data.Sqlite" />
```

- [ ] **Step 3: Write the failing `ClipboardKind` mapping test**

Create `windows/tests/VeloxClip.Core.Tests/Models/ClipboardKindTests.cs`:

```csharp
using System;
using FluentAssertions;
using VeloxClip.Core.Models;
using Xunit;

namespace VeloxClip.Core.Tests.Models;

public class ClipboardKindTests
{
    [Theory]
    [InlineData(ClipboardKind.Text, "text")]
    [InlineData(ClipboardKind.Rtf, "rtf")]
    [InlineData(ClipboardKind.Image, "image")]
    [InlineData(ClipboardKind.File, "file")]
    [InlineData(ClipboardKind.Color, "color")]
    public void ToDbString_And_Parse_RoundTrip(ClipboardKind kind, string dbValue)
    {
        kind.ToDbString().Should().Be(dbValue);
        ClipboardKindExtensions.ParseClipboardKind(dbValue).Should().Be(kind);
    }

    [Fact]
    public void ParseClipboardKind_RejectsUnknownValue()
    {
        Action act = () => ClipboardKindExtensions.ParseClipboardKind("video");
        act.Should().Throw<ArgumentException>();
    }
}
```

- [ ] **Step 4: Run the test, expect compile failure**

Run: `cd windows && dotnet test tests/VeloxClip.Core.Tests --nologo`
Expected: build fails — `ClipboardKind` does not exist. (Skip if `dotnet` unavailable.)

- [ ] **Step 5: Create `Models/ClipboardKind.cs`**

```csharp
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
```

- [ ] **Step 6: Create `Models/ClipboardEntry.cs`**

```csharp
using System;

namespace VeloxClip.Core.Models;

/// <summary>
/// One persisted clipboard history entry.
/// Text/RTF/File/Color payloads live in <see cref="Content"/>; images live as an
/// external PNG file referenced by <see cref="BlobPath"/>.
/// </summary>
public sealed record ClipboardEntry(
    Guid Id,
    DateTimeOffset CreatedAt,
    ClipboardKind Kind,
    string? Content,
    string? BlobPath,
    string ContentHash,
    string? SourceApp);
```

- [ ] **Step 7: Create `Models/ClipboardCapture.cs`**

```csharp
namespace VeloxClip.Core.Models;

/// <summary>
/// Raw result of reading the current clipboard, produced by an
/// <c>IClipboardReader</c> before classification and persistence.
/// For <see cref="ClipboardKind.Image"/>, <see cref="ImageBytes"/> holds PNG bytes
/// and <see cref="Text"/> is null. For all other kinds, <see cref="Text"/> holds the
/// payload and <see cref="ImageBytes"/> is null.
/// </summary>
public sealed record ClipboardCapture(ClipboardKind Kind, string? Text, byte[]? ImageBytes);
```

- [ ] **Step 8: Run the test, expect pass**

Run: `cd windows && dotnet test tests/VeloxClip.Core.Tests --nologo`
Expected: `ClipboardKindTests` passes (6 theory cases + 1 fact). (Skip if `dotnet` unavailable.)

- [ ] **Step 9: Commit**

```bash
git add windows/Directory.Packages.props windows/src/VeloxClip.Core/VeloxClip.Core.csproj windows/src/VeloxClip.Core/Models windows/tests/VeloxClip.Core.Tests/Models
git commit -m "feat(windows/core): add clipboard models + SQLite/Drawing packages"
```

---

## Task 3: `ContentHasher`

A SHA-256 hex helper used to compute the dedup key for every captured payload.

**Files:**
- Create: `windows/src/VeloxClip.Core/Capture/ContentHasher.cs`
- Create: `windows/tests/VeloxClip.Core.Tests/Capture/ContentHasherTests.cs`

- [ ] **Step 1: Write the failing test**

Create `windows/tests/VeloxClip.Core.Tests/Capture/ContentHasherTests.cs`:

```csharp
using FluentAssertions;
using VeloxClip.Core.Capture;
using Xunit;

namespace VeloxClip.Core.Tests.Capture;

public class ContentHasherTests
{
    [Fact]
    public void Hash_String_MatchesKnownSha256Vector()
    {
        // SHA-256("abc") — standard test vector, lowercase hex.
        ContentHasher.Hash("abc").Should()
            .Be("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    }

    [Fact]
    public void Hash_EmptyString_MatchesKnownSha256Vector()
    {
        // SHA-256("") — standard test vector.
        ContentHasher.Hash("").Should()
            .Be("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855");
    }

    [Fact]
    public void Hash_Bytes_IsLowercaseHexAndStable()
    {
        var hash = ContentHasher.Hash(new byte[] { 1, 2, 3 });
        hash.Should().HaveLength(64);
        hash.Should().Be(hash.ToLowerInvariant());
        hash.Should().Be(ContentHasher.Hash(new byte[] { 1, 2, 3 }));
    }
}
```

- [ ] **Step 2: Run the test, expect compile failure**

Run: `cd windows && dotnet test tests/VeloxClip.Core.Tests --nologo`
Expected: build fails — `ContentHasher` does not exist. (Skip if `dotnet` unavailable.)

- [ ] **Step 3: Create `Capture/ContentHasher.cs`**

```csharp
using System;
using System.Security.Cryptography;
using System.Text;

namespace VeloxClip.Core.Capture;

/// <summary>Computes the SHA-256 hex digest used as a clipboard entry's dedup key.</summary>
public static class ContentHasher
{
    /// <summary>Hashes raw bytes; returns a lowercase 64-char hex string.</summary>
    public static string Hash(ReadOnlySpan<byte> payload)
        => Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    /// <summary>Hashes a string via its UTF-8 encoding.</summary>
    public static string Hash(string text)
        => Hash(Encoding.UTF8.GetBytes(text));
}
```

- [ ] **Step 4: Run the test, expect pass**

Run: `cd windows && dotnet test tests/VeloxClip.Core.Tests --nologo`
Expected: `ContentHasherTests` passes (3 tests). (Skip if `dotnet` unavailable.)

- [ ] **Step 5: Commit**

```bash
git add windows/src/VeloxClip.Core/Capture/ContentHasher.cs windows/tests/VeloxClip.Core.Tests/Capture/ContentHasherTests.cs
git commit -m "feat(windows/core): add ContentHasher (SHA-256 dedup key)"
```

---

## Task 4: `ColorDetector`

Classifies a trimmed text string as a CSS-style color (hex or rgb/rgba). Port of the macOS `isColor` regex pair.

**Files:**
- Create: `windows/src/VeloxClip.Core/Capture/ColorDetector.cs`
- Create: `windows/tests/VeloxClip.Core.Tests/Capture/ColorDetectorTests.cs`

- [ ] **Step 1: Write the failing test**

Create `windows/tests/VeloxClip.Core.Tests/Capture/ColorDetectorTests.cs`:

```csharp
using FluentAssertions;
using VeloxClip.Core.Capture;
using Xunit;

namespace VeloxClip.Core.Tests.Capture;

public class ColorDetectorTests
{
    [Theory]
    [InlineData("#FF5733")]      // 6-digit hex
    [InlineData("#abc")]         // 3-digit hex
    [InlineData("#11223344")]    // 8-digit hex (with alpha)
    [InlineData("rgb(255, 0, 0)")]
    [InlineData("rgb(1,2,3)")]
    [InlineData("rgba(10, 20, 30, 0.5)")]
    [InlineData("  #FF5733  ")]  // surrounding whitespace tolerated
    public void IsColor_ReturnsTrue_ForColorStrings(string input)
        => ColorDetector.IsColor(input).Should().BeTrue();

    [Theory]
    [InlineData("hello world")]
    [InlineData("#GG5733")]      // non-hex digits
    [InlineData("#FF57")]        // wrong hex length
    [InlineData("rgb(1,2)")]     // too few components
    [InlineData("")]
    [InlineData("FF5733")]       // missing leading '#'
    public void IsColor_ReturnsFalse_ForNonColorStrings(string input)
        => ColorDetector.IsColor(input).Should().BeFalse();
}
```

- [ ] **Step 2: Run the test, expect compile failure**

Run: `cd windows && dotnet test tests/VeloxClip.Core.Tests --nologo`
Expected: build fails — `ColorDetector` does not exist. (Skip if `dotnet` unavailable.)

- [ ] **Step 3: Create `Capture/ColorDetector.cs`**

```csharp
using System.Text.RegularExpressions;

namespace VeloxClip.Core.Capture;

/// <summary>
/// Detects whether a string is a CSS-style color literal (hex or rgb/rgba).
/// Mirrors the macOS app's <c>isColor</c> classifier.
/// </summary>
public static partial class ColorDetector
{
    [GeneratedRegex("^#([A-Fa-f0-9]{6}|[A-Fa-f0-9]{3}|[A-Fa-f0-9]{8})$")]
    private static partial Regex HexRegex();

    [GeneratedRegex(@"^rgba?\((\d+),\s*(\d+),\s*(\d+)(?:,\s*([\d.]+))?\)$")]
    private static partial Regex RgbRegex();

    /// <summary>True if <paramref name="text"/> (after trimming) is a color literal.</summary>
    public static bool IsColor(string text)
    {
        var trimmed = text.Trim();
        return HexRegex().IsMatch(trimmed) || RgbRegex().IsMatch(trimmed);
    }
}
```

- [ ] **Step 4: Run the test, expect pass**

Run: `cd windows && dotnet test tests/VeloxClip.Core.Tests --nologo`
Expected: `ColorDetectorTests` passes (13 theory cases). (Skip if `dotnet` unavailable.)

- [ ] **Step 5: Commit**

```bash
git add windows/src/VeloxClip.Core/Capture/ColorDetector.cs windows/tests/VeloxClip.Core.Tests/Capture/ColorDetectorTests.cs
git commit -m "feat(windows/core): add ColorDetector (hex/rgb classifier)"
```

---

## Task 5: `Blacklist`

Holds the hardcoded set of password-manager process names; copies originating from those apps are never captured.

**Files:**
- Create: `windows/src/VeloxClip.Core/Capture/Blacklist.cs`
- Create: `windows/tests/VeloxClip.Core.Tests/Capture/BlacklistTests.cs`

- [ ] **Step 1: Write the failing test**

Create `windows/tests/VeloxClip.Core.Tests/Capture/BlacklistTests.cs`:

```csharp
using FluentAssertions;
using VeloxClip.Core.Capture;
using Xunit;

namespace VeloxClip.Core.Tests.Capture;

public class BlacklistTests
{
    private readonly Blacklist _blacklist = new();

    [Theory]
    [InlineData("Bitwarden.exe")]   // with .exe suffix
    [InlineData("bitwarden")]       // without suffix
    [InlineData("BITWARDEN")]       // case-insensitive
    [InlineData("1Password.exe")]
    [InlineData("KeePassXC")]
    [InlineData("  lastpass  ")]    // surrounding whitespace
    public void ShouldIgnore_ReturnsTrue_ForBlacklistedProcesses(string processName)
        => _blacklist.ShouldIgnore(processName).Should().BeTrue();

    [Theory]
    [InlineData("notepad")]
    [InlineData("chrome.exe")]
    [InlineData("explorer")]
    public void ShouldIgnore_ReturnsFalse_ForOtherProcesses(string processName)
        => _blacklist.ShouldIgnore(processName).Should().BeFalse();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ShouldIgnore_ReturnsFalse_ForMissingProcessName(string? processName)
        => _blacklist.ShouldIgnore(processName).Should().BeFalse();
}
```

- [ ] **Step 2: Run the test, expect compile failure**

Run: `cd windows && dotnet test tests/VeloxClip.Core.Tests --nologo`
Expected: build fails — `Blacklist` does not exist. (Skip if `dotnet` unavailable.)

- [ ] **Step 3: Create `Capture/Blacklist.cs`**

```csharp
using System;
using System.Collections.Generic;

namespace VeloxClip.Core.Capture;

/// <summary>
/// The set of source applications whose clipboard activity is never captured.
/// P1 ships a hardcoded list of common password managers; P7 will add a settings UI.
/// </summary>
public sealed class Blacklist
{
    private static readonly string[] DefaultProcessNames =
    {
        "1password", "bitwarden", "keepass", "keepassxc",
        "lastpass", "dashlane", "enpass", "roboform",
    };

    private readonly HashSet<string> _names;

    public Blacklist()
        : this(DefaultProcessNames)
    {
    }

    /// <summary>Test seam: construct with an explicit name set.</summary>
    internal Blacklist(IEnumerable<string> processNames)
        => _names = new HashSet<string>(processNames, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// True if a copy from <paramref name="processName"/> must be ignored.
    /// The name is trimmed and a trailing <c>.exe</c> is stripped before comparison.
    /// A null/blank name is treated as unknown and allowed (returns false).
    /// </summary>
    public bool ShouldIgnore(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        var stem = processName.Trim();
        if (stem.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            stem = stem[..^4];
        }

        return _names.Contains(stem);
    }
}
```

- [ ] **Step 4: Run the test, expect pass**

Run: `cd windows && dotnet test tests/VeloxClip.Core.Tests --nologo`
Expected: `BlacklistTests` passes (12 cases). (Skip if `dotnet` unavailable.)

- [ ] **Step 5: Commit**

```bash
git add windows/src/VeloxClip.Core/Capture/Blacklist.cs windows/tests/VeloxClip.Core.Tests/Capture/BlacklistTests.cs
git commit -m "feat(windows/core): add Blacklist (password-manager process filter)"
```

---

## Task 6: `ClipboardDeduplicator`

The Tier-1 dedup decision: a pure function answering "is this capture a repeat of the most recent entry within the 5-second window?"

**Files:**
- Create: `windows/src/VeloxClip.Core/Capture/ClipboardDeduplicator.cs`
- Create: `windows/tests/VeloxClip.Core.Tests/Capture/ClipboardDeduplicatorTests.cs`

- [ ] **Step 1: Write the failing test**

Create `windows/tests/VeloxClip.Core.Tests/Capture/ClipboardDeduplicatorTests.cs`:

```csharp
using System;
using FluentAssertions;
using VeloxClip.Core.Capture;
using Xunit;

namespace VeloxClip.Core.Tests.Capture;

public class ClipboardDeduplicatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 5, 18, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void IsWithinDedupWindow_True_WhenSameHashAndUnder5Seconds()
    {
        var recent = Now - TimeSpan.FromSeconds(4.9);
        ClipboardDeduplicator.IsWithinDedupWindow("hashA", "hashA", recent, Now)
            .Should().BeTrue();
    }

    [Fact]
    public void IsWithinDedupWindow_False_WhenSameHashButOver5Seconds()
    {
        var recent = Now - TimeSpan.FromSeconds(5.1);
        ClipboardDeduplicator.IsWithinDedupWindow("hashA", "hashA", recent, Now)
            .Should().BeFalse();
    }

    [Fact]
    public void IsWithinDedupWindow_False_AtExactly5Seconds()
    {
        var recent = Now - TimeSpan.FromSeconds(5.0);
        ClipboardDeduplicator.IsWithinDedupWindow("hashA", "hashA", recent, Now)
            .Should().BeFalse();
    }

    [Fact]
    public void IsWithinDedupWindow_False_WhenHashDiffers()
    {
        var recent = Now - TimeSpan.FromSeconds(1);
        ClipboardDeduplicator.IsWithinDedupWindow("hashA", "hashB", recent, Now)
            .Should().BeFalse();
    }

    [Fact]
    public void IsWithinDedupWindow_False_WhenNoRecentEntry()
    {
        ClipboardDeduplicator.IsWithinDedupWindow("hashA", null, Now, Now)
            .Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run the test, expect compile failure**

Run: `cd windows && dotnet test tests/VeloxClip.Core.Tests --nologo`
Expected: build fails — `ClipboardDeduplicator` does not exist. (Skip if `dotnet` unavailable.)

- [ ] **Step 3: Create `Capture/ClipboardDeduplicator.cs`**

```csharp
using System;

namespace VeloxClip.Core.Capture;

/// <summary>
/// Tier-1 deduplication: discard a capture that repeats the most recent entry
/// within a short window. (Tier-2 "move to top" lives in the clipboard store.)
/// </summary>
public static class ClipboardDeduplicator
{
    /// <summary>The rapid-repeat suppression window.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(5);

    /// <summary>
    /// True if the new capture (<paramref name="newHash"/>) should be discarded
    /// because the most recent stored entry has the same hash and was created
    /// strictly less than <see cref="Window"/> ago.
    /// </summary>
    public static bool IsWithinDedupWindow(
        string newHash,
        string? mostRecentHash,
        DateTimeOffset mostRecentCreatedAt,
        DateTimeOffset now)
    {
        if (mostRecentHash is null
            || !string.Equals(newHash, mostRecentHash, StringComparison.Ordinal))
        {
            return false;
        }

        return now - mostRecentCreatedAt < Window;
    }
}
```

- [ ] **Step 4: Run the test, expect pass**

Run: `cd windows && dotnet test tests/VeloxClip.Core.Tests --nologo`
Expected: `ClipboardDeduplicatorTests` passes (5 tests). (Skip if `dotnet` unavailable.)

- [ ] **Step 5: Commit**

```bash
git add windows/src/VeloxClip.Core/Capture/ClipboardDeduplicator.cs windows/tests/VeloxClip.Core.Tests/Capture/ClipboardDeduplicatorTests.cs
git commit -m "feat(windows/core): add ClipboardDeduplicator (5-second window)"
```

---

## Task 7: `ClipboardSchema` + `VeloxClipDatabase`

`ClipboardSchema` creates the two tables and indexes and stamps `PRAGMA user_version = 1`. `VeloxClipDatabase` is the shared connection factory: it builds the connection string, opens connections with a busy-timeout, and runs the schema once.

**Files:**
- Modify: `windows/tests/VeloxClip.Core.Tests/VeloxClip.Core.Tests.csproj`
- Create: `windows/src/VeloxClip.Core/Persistence/ClipboardSchema.cs`
- Create: `windows/src/VeloxClip.Core/Persistence/VeloxClipDatabase.cs`
- Create: `windows/tests/VeloxClip.Core.Tests/Persistence/ClipboardSchemaTests.cs`

- [ ] **Step 1: Reference `Microsoft.Data.Sqlite` from the test project**

In `windows/tests/VeloxClip.Core.Tests/VeloxClip.Core.Tests.csproj`, add this line inside the first `<ItemGroup>` (the one with `xunit`):

```xml
    <PackageReference Include="Microsoft.Data.Sqlite" />
```

- [ ] **Step 2: Write the failing schema test**

Create `windows/tests/VeloxClip.Core.Tests/Persistence/ClipboardSchemaTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using VeloxClip.Core.Persistence;
using Xunit;

namespace VeloxClip.Core.Tests.Persistence;

public class ClipboardSchemaTests : IDisposable
{
    private readonly string _dbFile;

    public ClipboardSchemaTests()
        => _dbFile = Path.Combine(Path.GetTempPath(), "veloxclip-schema-" + Path.GetRandomFileName() + ".db");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbFile))
        {
            File.Delete(_dbFile);
        }

        GC.SuppressFinalize(this);
    }

    private SqliteConnection OpenRaw()
    {
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _dbFile }.ToString());
        conn.Open();
        return conn;
    }

    [Fact]
    public void EnsureCreated_CreatesTablesIndexesAndStampsUserVersion()
    {
        using (var conn = OpenRaw())
        {
            ClipboardSchema.EnsureCreated(conn);
        }

        using var check = OpenRaw();

        ScalarText(check, "SELECT name FROM sqlite_master WHERE type='table' AND name='clipboard_entries';")
            .Should().Be("clipboard_entries");
        ScalarText(check, "SELECT name FROM sqlite_master WHERE type='table' AND name='app_settings';")
            .Should().Be("app_settings");

        var indexes = QueryTextColumn(check,
            "SELECT name FROM sqlite_master WHERE type='index' AND tbl_name='clipboard_entries';");
        indexes.Should().Contain("ix_entries_created_at");
        indexes.Should().Contain("ix_entries_hash");

        using var pragma = check.CreateCommand();
        pragma.CommandText = "PRAGMA user_version;";
        Convert.ToInt32(pragma.ExecuteScalar()).Should().Be(ClipboardSchema.CurrentVersion);
    }

    [Fact]
    public void EnsureCreated_IsIdempotent()
    {
        using (var conn = OpenRaw())
        {
            ClipboardSchema.EnsureCreated(conn);
        }

        using var conn2 = OpenRaw();
        Action act = () => ClipboardSchema.EnsureCreated(conn2);
        act.Should().NotThrow();
    }

    private static string? ScalarText(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar() as string;
    }

    private static List<string> QueryTextColumn(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var result = new List<string>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }
}
```

- [ ] **Step 3: Run the test, expect compile failure**

Run: `cd windows && dotnet test tests/VeloxClip.Core.Tests --nologo`
Expected: build fails — `ClipboardSchema` does not exist. (Skip if `dotnet` unavailable.)

- [ ] **Step 4: Create `Persistence/ClipboardSchema.cs`**

```csharp
using System;
using Microsoft.Data.Sqlite;

namespace VeloxClip.Core.Persistence;

/// <summary>
/// Owns the SQLite schema for the clipboard database. The schema version is
/// tracked via <c>PRAGMA user_version</c>; P3+ migrations bump it.
/// </summary>
public static class ClipboardSchema
{
    /// <summary>The schema version this build produces.</summary>
    public const int CurrentVersion = 1;

    private const string CreateSql = """
        CREATE TABLE IF NOT EXISTS clipboard_entries (
            id            TEXT    PRIMARY KEY NOT NULL,
            created_at    INTEGER NOT NULL,
            kind          TEXT    NOT NULL,
            content       TEXT,
            blob_path     TEXT,
            content_hash  TEXT    NOT NULL,
            source_app    TEXT
        );
        CREATE INDEX IF NOT EXISTS ix_entries_created_at ON clipboard_entries (created_at DESC);
        CREATE INDEX IF NOT EXISTS ix_entries_hash       ON clipboard_entries (content_hash);
        CREATE TABLE IF NOT EXISTS app_settings (
            key   TEXT PRIMARY KEY NOT NULL,
            value TEXT NOT NULL
        );
        """;

    /// <summary>
    /// Creates the tables and indexes if the database is below
    /// <see cref="CurrentVersion"/>. Idempotent and safe to call on every startup.
    /// </summary>
    public static void EnsureCreated(SqliteConnection openConnection)
    {
        ArgumentNullException.ThrowIfNull(openConnection);

        long version;
        using (var read = openConnection.CreateCommand())
        {
            read.CommandText = "PRAGMA user_version;";
            version = Convert.ToInt64(read.ExecuteScalar());
        }

        if (version >= CurrentVersion)
        {
            return;
        }

        using var tx = openConnection.BeginTransaction();
        using (var create = openConnection.CreateCommand())
        {
            create.Transaction = tx;
            create.CommandText = CreateSql;
            create.ExecuteNonQuery();
        }

        using (var stamp = openConnection.CreateCommand())
        {
            stamp.Transaction = tx;
            // PRAGMA does not accept parameters; CurrentVersion is a trusted constant.
            stamp.CommandText = $"PRAGMA user_version = {CurrentVersion};";
            stamp.ExecuteNonQuery();
        }

        tx.Commit();
    }
}
```

- [ ] **Step 5: Create `Persistence/VeloxClipDatabase.cs`**

```csharp
using System;
using System.IO;
using Microsoft.Data.Sqlite;
using VeloxClip.Core.Environment;

namespace VeloxClip.Core.Persistence;

/// <summary>
/// Shared SQLite connection factory for the clipboard database. Builds the
/// connection string once, runs <see cref="ClipboardSchema"/> at construction,
/// and hands out freshly-opened connections with a busy timeout applied.
/// </summary>
public sealed class VeloxClipDatabase
{
    private readonly string _connectionString;

    /// <summary>Production constructor: database lives at <c>{paths.Database}/veloxclip.db</c>.</summary>
    public VeloxClipDatabase(IAppPaths paths)
        : this(Path.Combine((paths ?? throw new ArgumentNullException(nameof(paths))).Database, "veloxclip.db"))
    {
    }

    /// <summary>Test seam: database at an explicit file path.</summary>
    public VeloxClipDatabase(string databaseFilePath)
    {
        if (string.IsNullOrWhiteSpace(databaseFilePath))
        {
            throw new ArgumentException("Database file path must be non-empty.", nameof(databaseFilePath));
        }

        _connectionString = new SqliteConnectionStringBuilder { DataSource = databaseFilePath }.ToString();

        using var conn = Open();
        ClipboardSchema.EnsureCreated(conn);
    }

    /// <summary>Opens a new connection with a 3-second busy timeout applied.</summary>
    public SqliteConnection Open()
    {
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        using var pragma = conn.CreateCommand();
        pragma.CommandText = "PRAGMA busy_timeout = 3000;";
        pragma.ExecuteNonQuery();
        return conn;
    }
}
```

- [ ] **Step 6: Run the test, expect pass**

Run: `cd windows && dotnet test tests/VeloxClip.Core.Tests --nologo`
Expected: `ClipboardSchemaTests` passes (2 tests). (Skip if `dotnet` unavailable.)

- [ ] **Step 7: Commit**

```bash
git add windows/tests/VeloxClip.Core.Tests/VeloxClip.Core.Tests.csproj windows/src/VeloxClip.Core/Persistence windows/tests/VeloxClip.Core.Tests/Persistence
git commit -m "feat(windows/core): add SQLite schema + connection factory"
```

---

## Task 8: `IClipboardStore` + `SqliteClipboardStore`

The clipboard store: insert with Tier-2 "move to top" dedup and history-cap trimming, plus read queries used by the pipeline and the orphan reconciler.

**Files:**
- Create: `windows/src/VeloxClip.Core/Abstractions/IClipboardStore.cs`
- Create: `windows/src/VeloxClip.Core/Persistence/SqliteClipboardStore.cs`
- Create: `windows/tests/VeloxClip.Core.Tests/Persistence/SqliteClipboardStoreTests.cs`

- [ ] **Step 1: Create the `IClipboardStore` interface**

Create `windows/src/VeloxClip.Core/Abstractions/IClipboardStore.cs`:

```csharp
using System;
using System.Collections.Generic;
using VeloxClip.Core.Models;

namespace VeloxClip.Core.Abstractions;

/// <summary>Outcome of <see cref="IClipboardStore.Add"/>.</summary>
/// <param name="WasDeduplicated">
/// True if an existing entry with the same hash was moved to the top instead of
/// inserting a new row.
/// </param>
/// <param name="OrphanedBlobPaths">
/// Relative blob paths whose backing files the caller must delete: either the
/// incoming image (when deduplicated) or images trimmed by the history cap.
/// </param>
public sealed record AddResult(bool WasDeduplicated, IReadOnlyList<string> OrphanedBlobPaths);

/// <summary>An image entry's id paired with its blob path.</summary>
public sealed record ImageBlobReference(Guid Id, string BlobPath);

/// <summary>Persistence for clipboard history entries.</summary>
public interface IClipboardStore
{
    /// <summary>
    /// Persists <paramref name="entry"/>. If an entry with the same
    /// <see cref="ClipboardEntry.ContentHash"/> already exists, that row's timestamp
    /// is bumped to the entry's <see cref="ClipboardEntry.CreatedAt"/> (Tier-2 move
    /// to top) and no new row is inserted. After an insert, rows beyond
    /// <paramref name="historyLimit"/> (oldest first) are deleted.
    /// </summary>
    AddResult Add(ClipboardEntry entry, int historyLimit);

    /// <summary>The most recent entry, or null if the store is empty.</summary>
    ClipboardEntry? GetMostRecent();

    /// <summary>Up to <paramref name="count"/> most recent entries, newest first.</summary>
    IReadOnlyList<ClipboardEntry> GetRecent(int count);

    /// <summary>Every image entry's id and blob path.</summary>
    IReadOnlyList<ImageBlobReference> GetImageBlobReferences();

    /// <summary>Deletes the entry with the given id, if present.</summary>
    void DeleteById(Guid id);
}
```

- [ ] **Step 2: Write the failing store test**

Create `windows/tests/VeloxClip.Core.Tests/Persistence/SqliteClipboardStoreTests.cs`:

```csharp
using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using VeloxClip.Core.Models;
using VeloxClip.Core.Persistence;
using Xunit;

namespace VeloxClip.Core.Tests.Persistence;

public class SqliteClipboardStoreTests : IDisposable
{
    private readonly string _dbFile;
    private readonly SqliteClipboardStore _store;

    public SqliteClipboardStoreTests()
    {
        _dbFile = Path.Combine(Path.GetTempPath(), "veloxclip-store-" + Path.GetRandomFileName() + ".db");
        _store = new SqliteClipboardStore(new VeloxClipDatabase(_dbFile));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbFile))
        {
            File.Delete(_dbFile);
        }

        GC.SuppressFinalize(this);
    }

    private static ClipboardEntry TextEntry(string text, DateTimeOffset createdAt)
        => new(Guid.NewGuid(), createdAt, ClipboardKind.Text, text, null, "hash-" + text, "notepad");

    [Fact]
    public void Add_ThenGetMostRecent_ReturnsTheEntry()
    {
        var entry = TextEntry("hello", DateTimeOffset.UtcNow);

        var result = _store.Add(entry, historyLimit: 100);

        result.WasDeduplicated.Should().BeFalse();
        var recent = _store.GetMostRecent();
        recent.Should().NotBeNull();
        recent!.Content.Should().Be("hello");
        recent.Kind.Should().Be(ClipboardKind.Text);
    }

    [Fact]
    public void GetRecent_ReturnsNewestFirst()
    {
        var t0 = new DateTimeOffset(2026, 5, 18, 10, 0, 0, TimeSpan.Zero);
        _store.Add(TextEntry("oldest", t0), 100);
        _store.Add(TextEntry("middle", t0.AddSeconds(10)), 100);
        _store.Add(TextEntry("newest", t0.AddSeconds(20)), 100);

        var recent = _store.GetRecent(10);

        recent.Select(e => e.Content).Should().ContainInOrder("newest", "middle", "oldest");
    }

    [Fact]
    public void Add_SameHash_MovesExistingToTopWithoutNewRow()
    {
        var t0 = new DateTimeOffset(2026, 5, 18, 10, 0, 0, TimeSpan.Zero);
        var first = new ClipboardEntry(Guid.NewGuid(), t0, ClipboardKind.Text, "A", null, "hash-A", "notepad");
        _store.Add(first, 100);
        _store.Add(TextEntry("B", t0.AddSeconds(10)), 100);

        // Re-copy "A": same hash, later timestamp.
        var reA = new ClipboardEntry(Guid.NewGuid(), t0.AddSeconds(20), ClipboardKind.Text, "A", null, "hash-A", "notepad");
        var result = _store.Add(reA, 100);

        result.WasDeduplicated.Should().BeTrue();
        var recent = _store.GetRecent(10);
        recent.Should().HaveCount(2);                          // not 3
        recent[0].Content.Should().Be("A");                    // A moved to top
        recent[0].Id.Should().Be(first.Id);                    // original row kept
    }

    [Fact]
    public void Add_DeduplicatedImage_ReportsIncomingBlobAsOrphan()
    {
        var t0 = new DateTimeOffset(2026, 5, 18, 10, 0, 0, TimeSpan.Zero);
        var img1 = new ClipboardEntry(Guid.NewGuid(), t0, ClipboardKind.Image, null, "blobs/a.png", "hash-img", "snip");
        _store.Add(img1, 100);

        var img2 = new ClipboardEntry(Guid.NewGuid(), t0.AddSeconds(30), ClipboardKind.Image, null, "blobs/b.png", "hash-img", "snip");
        var result = _store.Add(img2, 100);

        result.WasDeduplicated.Should().BeTrue();
        result.OrphanedBlobPaths.Should().ContainSingle().Which.Should().Be("blobs/b.png");
    }

    [Fact]
    public void Add_BeyondHistoryLimit_TrimsOldestAndReportsTrimmedImageBlobs()
    {
        var t0 = new DateTimeOffset(2026, 5, 18, 10, 0, 0, TimeSpan.Zero);
        // Oldest entry is an image; it should be trimmed and its blob reported.
        var oldImage = new ClipboardEntry(Guid.NewGuid(), t0, ClipboardKind.Image, null, "blobs/old.png", "hash-old", "snip");
        _store.Add(oldImage, historyLimit: 3);
        _store.Add(TextEntry("two", t0.AddSeconds(1)), 3);
        _store.Add(TextEntry("three", t0.AddSeconds(2)), 3);

        // Fourth insert exceeds the limit of 3 → oldest (the image) is trimmed.
        var result = _store.Add(TextEntry("four", t0.AddSeconds(3)), historyLimit: 3);

        result.OrphanedBlobPaths.Should().ContainSingle().Which.Should().Be("blobs/old.png");
        _store.GetRecent(10).Should().HaveCount(3);
        _store.GetRecent(10).Select(e => e.Content).Should().NotContain((string?)null!);
    }

    [Fact]
    public void GetImageBlobReferences_ReturnsOnlyImageRows()
    {
        var t0 = new DateTimeOffset(2026, 5, 18, 10, 0, 0, TimeSpan.Zero);
        var img = new ClipboardEntry(Guid.NewGuid(), t0, ClipboardKind.Image, null, "blobs/x.png", "hash-x", "snip");
        _store.Add(img, 100);
        _store.Add(TextEntry("plain", t0.AddSeconds(1)), 100);

        var refs = _store.GetImageBlobReferences();

        refs.Should().ContainSingle();
        refs[0].Id.Should().Be(img.Id);
        refs[0].BlobPath.Should().Be("blobs/x.png");
    }

    [Fact]
    public void DeleteById_RemovesTheRow()
    {
        var entry = TextEntry("doomed", DateTimeOffset.UtcNow);
        _store.Add(entry, 100);

        _store.DeleteById(entry.Id);

        _store.GetMostRecent().Should().BeNull();
    }
}
```

- [ ] **Step 3: Run the test, expect compile failure**

Run: `cd windows && dotnet test tests/VeloxClip.Core.Tests --nologo`
Expected: build fails — `SqliteClipboardStore` does not exist. (Skip if `dotnet` unavailable.)

- [ ] **Step 4: Create `Persistence/SqliteClipboardStore.cs`**

```csharp
using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using VeloxClip.Core.Abstractions;
using VeloxClip.Core.Models;

namespace VeloxClip.Core.Persistence;

/// <summary>SQLite-backed <see cref="IClipboardStore"/>.</summary>
public sealed class SqliteClipboardStore : IClipboardStore
{
    private const string SelectColumns =
        "id, created_at, kind, content, blob_path, content_hash, source_app";

    private readonly VeloxClipDatabase _database;

    public SqliteClipboardStore(VeloxClipDatabase database)
        => _database = database ?? throw new ArgumentNullException(nameof(database));

    public AddResult Add(ClipboardEntry entry, int historyLimit)
    {
        ArgumentNullException.ThrowIfNull(entry);

        using var conn = _database.Open();
        using var tx = conn.BeginTransaction();

        var existingId = FindIdByHash(conn, tx, entry.ContentHash);
        if (existingId is not null)
        {
            BumpTimestamp(conn, tx, existingId, entry.CreatedAt);
            tx.Commit();

            var dedupOrphans = entry.BlobPath is null
                ? Array.Empty<string>()
                : new[] { entry.BlobPath };
            return new AddResult(WasDeduplicated: true, dedupOrphans);
        }

        Insert(conn, tx, entry);
        var trimmedBlobs = TrimToHistoryLimit(conn, tx, historyLimit);
        tx.Commit();

        return new AddResult(WasDeduplicated: false, trimmedBlobs);
    }

    public ClipboardEntry? GetMostRecent()
    {
        using var conn = _database.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT {SelectColumns} FROM clipboard_entries ORDER BY created_at DESC LIMIT 1;";
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? ReadEntry(reader) : null;
    }

    public IReadOnlyList<ClipboardEntry> GetRecent(int count)
    {
        using var conn = _database.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"SELECT {SelectColumns} FROM clipboard_entries ORDER BY created_at DESC LIMIT $n;";
        cmd.Parameters.AddWithValue("$n", count);

        var result = new List<ClipboardEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(ReadEntry(reader));
        }

        return result;
    }

    public IReadOnlyList<ImageBlobReference> GetImageBlobReferences()
    {
        using var conn = _database.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT id, blob_path FROM clipboard_entries WHERE kind = 'image' AND blob_path IS NOT NULL;";

        var result = new List<ImageBlobReference>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new ImageBlobReference(Guid.Parse(reader.GetString(0)), reader.GetString(1)));
        }

        return result;
    }

    public void DeleteById(Guid id)
    {
        using var conn = _database.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM clipboard_entries WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id.ToString());
        cmd.ExecuteNonQuery();
    }

    private static string? FindIdByHash(SqliteConnection conn, SqliteTransaction tx, string hash)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT id FROM clipboard_entries WHERE content_hash = $h LIMIT 1;";
        cmd.Parameters.AddWithValue("$h", hash);
        return cmd.ExecuteScalar() as string;
    }

    private static void BumpTimestamp(
        SqliteConnection conn, SqliteTransaction tx, string id, DateTimeOffset createdAt)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE clipboard_entries SET created_at = $t WHERE id = $id;";
        cmd.Parameters.AddWithValue("$t", createdAt.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private static void Insert(SqliteConnection conn, SqliteTransaction tx, ClipboardEntry entry)
    {
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO clipboard_entries
                (id, created_at, kind, content, blob_path, content_hash, source_app)
            VALUES ($id, $ca, $k, $c, $bp, $h, $sa);
            """;
        cmd.Parameters.AddWithValue("$id", entry.Id.ToString());
        cmd.Parameters.AddWithValue("$ca", entry.CreatedAt.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$k", entry.Kind.ToDbString());
        cmd.Parameters.AddWithValue("$c", (object?)entry.Content ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$bp", (object?)entry.BlobPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$h", entry.ContentHash);
        cmd.Parameters.AddWithValue("$sa", (object?)entry.SourceApp ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Deletes rows beyond <paramref name="historyLimit"/>; returns trimmed image blob paths.</summary>
    private static IReadOnlyList<string> TrimToHistoryLimit(
        SqliteConnection conn, SqliteTransaction tx, int historyLimit)
    {
        long total;
        using (var count = conn.CreateCommand())
        {
            count.Transaction = tx;
            count.CommandText = "SELECT COUNT(*) FROM clipboard_entries;";
            total = Convert.ToInt64(count.ExecuteScalar());
        }

        if (total <= historyLimit)
        {
            return Array.Empty<string>();
        }

        var excess = total - historyLimit;
        var idsToDelete = new List<string>();
        var trimmedBlobs = new List<string>();

        using (var select = conn.CreateCommand())
        {
            select.Transaction = tx;
            select.CommandText =
                "SELECT id, blob_path FROM clipboard_entries ORDER BY created_at ASC LIMIT $n;";
            select.Parameters.AddWithValue("$n", excess);
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                idsToDelete.Add(reader.GetString(0));
                if (!reader.IsDBNull(1))
                {
                    trimmedBlobs.Add(reader.GetString(1));
                }
            }
        }

        foreach (var id in idsToDelete)
        {
            using var delete = conn.CreateCommand();
            delete.Transaction = tx;
            delete.CommandText = "DELETE FROM clipboard_entries WHERE id = $id;";
            delete.Parameters.AddWithValue("$id", id);
            delete.ExecuteNonQuery();
        }

        return trimmedBlobs;
    }

    private static ClipboardEntry ReadEntry(SqliteDataReader reader) => new(
        Id: Guid.Parse(reader.GetString(0)),
        CreatedAt: DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
        Kind: ClipboardKindExtensions.ParseClipboardKind(reader.GetString(2)),
        Content: reader.IsDBNull(3) ? null : reader.GetString(3),
        BlobPath: reader.IsDBNull(4) ? null : reader.GetString(4),
        ContentHash: reader.GetString(5),
        SourceApp: reader.IsDBNull(6) ? null : reader.GetString(6));
}
```

- [ ] **Step 5: Run the test, expect pass**

Run: `cd windows && dotnet test tests/VeloxClip.Core.Tests --nologo`
Expected: `SqliteClipboardStoreTests` passes (7 tests). (Skip if `dotnet` unavailable.)

- [ ] **Step 6: Commit**

```bash
git add windows/src/VeloxClip.Core/Abstractions/IClipboardStore.cs windows/src/VeloxClip.Core/Persistence/SqliteClipboardStore.cs windows/tests/VeloxClip.Core.Tests/Persistence/SqliteClipboardStoreTests.cs
git commit -m "feat(windows/core): add SqliteClipboardStore (dedup + history cap)"
```

---

## Task 9: `IAppSettingsStore` + `SqliteAppSettingsStore`

The key/value settings store. P1 needs exactly one setting — `history_limit` — with a default of 100.

**Files:**
- Create: `windows/src/VeloxClip.Core/Abstractions/IAppSettingsStore.cs`
- Create: `windows/src/VeloxClip.Core/Persistence/SqliteAppSettingsStore.cs`
- Create: `windows/tests/VeloxClip.Core.Tests/Persistence/SqliteAppSettingsStoreTests.cs`

- [ ] **Step 1: Create the `IAppSettingsStore` interface**

Create `windows/src/VeloxClip.Core/Abstractions/IAppSettingsStore.cs`:

```csharp
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
```

- [ ] **Step 2: Write the failing test**

Create `windows/tests/VeloxClip.Core.Tests/Persistence/SqliteAppSettingsStoreTests.cs`:

```csharp
using System;
using System.IO;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using VeloxClip.Core.Persistence;
using Xunit;

namespace VeloxClip.Core.Tests.Persistence;

public class SqliteAppSettingsStoreTests : IDisposable
{
    private readonly string _dbFile;
    private readonly SqliteAppSettingsStore _store;

    public SqliteAppSettingsStoreTests()
    {
        _dbFile = Path.Combine(Path.GetTempPath(), "veloxclip-settings-" + Path.GetRandomFileName() + ".db");
        _store = new SqliteAppSettingsStore(new VeloxClipDatabase(_dbFile));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (File.Exists(_dbFile))
        {
            File.Delete(_dbFile);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void GetHistoryLimit_ReturnsDefault100_WhenUnset()
        => _store.GetHistoryLimit().Should().Be(100);

    [Fact]
    public void SetHistoryLimit_ThenGet_ReturnsStoredValue()
    {
        _store.SetHistoryLimit(25);
        _store.GetHistoryLimit().Should().Be(25);
    }

    [Fact]
    public void SetHistoryLimit_Twice_OverwritesPreviousValue()
    {
        _store.SetHistoryLimit(25);
        _store.SetHistoryLimit(7);
        _store.GetHistoryLimit().Should().Be(7);
    }
}
```

- [ ] **Step 3: Run the test, expect compile failure**

Run: `cd windows && dotnet test tests/VeloxClip.Core.Tests --nologo`
Expected: build fails — `SqliteAppSettingsStore` does not exist. (Skip if `dotnet` unavailable.)

- [ ] **Step 4: Create `Persistence/SqliteAppSettingsStore.cs`**

```csharp
using System;
using System.Globalization;
using VeloxClip.Core.Abstractions;

namespace VeloxClip.Core.Persistence;

/// <summary>SQLite-backed <see cref="IAppSettingsStore"/> over the <c>app_settings</c> table.</summary>
public sealed class SqliteAppSettingsStore : IAppSettingsStore
{
    private const string HistoryLimitKey = "history_limit";
    private const int DefaultHistoryLimit = 100;

    private readonly VeloxClipDatabase _database;

    public SqliteAppSettingsStore(VeloxClipDatabase database)
        => _database = database ?? throw new ArgumentNullException(nameof(database));

    public int GetHistoryLimit()
    {
        var raw = Get(HistoryLimitKey);
        if (raw is not null && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        SetHistoryLimit(DefaultHistoryLimit);
        return DefaultHistoryLimit;
    }

    public void SetHistoryLimit(int limit)
        => Set(HistoryLimitKey, limit.ToString(CultureInfo.InvariantCulture));

    private string? Get(string key)
    {
        using var conn = _database.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT value FROM app_settings WHERE key = $k;";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string;
    }

    private void Set(string key, string value)
    {
        using var conn = _database.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO app_settings (key, value) VALUES ($k, $v)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }
}
```

- [ ] **Step 5: Run the test, expect pass**

Run: `cd windows && dotnet test tests/VeloxClip.Core.Tests --nologo`
Expected: `SqliteAppSettingsStoreTests` passes (3 tests). (Skip if `dotnet` unavailable.)

- [ ] **Step 6: Commit**

```bash
git add windows/src/VeloxClip.Core/Abstractions/IAppSettingsStore.cs windows/src/VeloxClip.Core/Persistence/SqliteAppSettingsStore.cs windows/tests/VeloxClip.Core.Tests/Persistence/SqliteAppSettingsStoreTests.cs
git commit -m "feat(windows/core): add SqliteAppSettingsStore (history_limit)"
```

---

## Task 10: `IBlobStore` + `FileBlobStore`

External storage for clipboard image bytes: one PNG file per image under `blobs/`, referenced from the DB by a relative path.

**Files:**
- Create: `windows/src/VeloxClip.Core/Abstractions/IBlobStore.cs`
- Create: `windows/src/VeloxClip.Core/Persistence/FileBlobStore.cs`
- Create: `windows/tests/VeloxClip.Core.Tests/Persistence/FileBlobStoreTests.cs`

- [ ] **Step 1: Create the `IBlobStore` interface**

Create `windows/src/VeloxClip.Core/Abstractions/IBlobStore.cs`:

```csharp
using System.Collections.Generic;

namespace VeloxClip.Core.Abstractions;

/// <summary>Stores clipboard image payloads as files outside the SQLite database.</summary>
public interface IBlobStore
{
    /// <summary>
    /// Writes <paramref name="pngBytes"/> to a new file and returns its path relative
    /// to the app root (e.g. <c>blobs/3f2a….png</c>, always forward-slash separated).
    /// </summary>
    string Save(byte[] pngBytes);

    /// <summary>True if a blob file exists for the given relative path.</summary>
    bool Exists(string relativePath);

    /// <summary>Deletes the blob file at the given relative path; no-op if absent.</summary>
    void Delete(string relativePath);

    /// <summary>
    /// Deletes every file in the blob directory whose relative path is not in
    /// <paramref name="knownRelativePaths"/> (orphan-file cleanup).
    /// </summary>
    void ReconcileOrphans(IReadOnlySet<string> knownRelativePaths);
}
```

- [ ] **Step 2: Write the failing test**

Create `windows/tests/VeloxClip.Core.Tests/Persistence/FileBlobStoreTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using VeloxClip.Core.Environment;
using VeloxClip.Core.Persistence;
using Xunit;

namespace VeloxClip.Core.Tests.Persistence;

public class FileBlobStoreTests : IDisposable
{
    private readonly string _tempBase;
    private readonly AppPaths _paths;
    private readonly FileBlobStore _store;

    public FileBlobStoreTests()
    {
        _tempBase = Path.Combine(Path.GetTempPath(), "veloxclip-blob-" + Path.GetRandomFileName());
        Directory.CreateDirectory(_tempBase);
        _paths = new AppPaths(_tempBase);
        AppEnvironmentBootstrapper.Ensure(_paths);
        _store = new FileBlobStore(_paths);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempBase))
        {
            Directory.Delete(_tempBase, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Save_ReturnsForwardSlashRelativePath_AndWritesTheFile()
    {
        var path = _store.Save(new byte[] { 9, 8, 7 });

        path.Should().StartWith("blobs/");
        path.Should().EndWith(".png");
        path.Should().NotContain("\\");
        _store.Exists(path).Should().BeTrue();
    }

    [Fact]
    public void Save_WritesExactBytes()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var path = _store.Save(payload);

        var onDisk = File.ReadAllBytes(Path.Combine(_paths.Root, path));
        onDisk.Should().Equal(payload);
    }

    [Fact]
    public void Delete_RemovesTheFile()
    {
        var path = _store.Save(new byte[] { 1 });
        _store.Delete(path);
        _store.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void Delete_IsNoOp_WhenFileMissing()
    {
        Action act = () => _store.Delete("blobs/does-not-exist.png");
        act.Should().NotThrow();
    }

    [Fact]
    public void ReconcileOrphans_DeletesFilesNotInKnownSet()
    {
        var keep = _store.Save(new byte[] { 1 });
        var orphan = _store.Save(new byte[] { 2 });

        _store.ReconcileOrphans(new HashSet<string> { keep });

        _store.Exists(keep).Should().BeTrue();
        _store.Exists(orphan).Should().BeFalse();
    }
}
```

- [ ] **Step 3: Run the test, expect compile failure**

Run: `cd windows && dotnet test tests/VeloxClip.Core.Tests --nologo`
Expected: build fails — `FileBlobStore` does not exist. (Skip if `dotnet` unavailable.)

- [ ] **Step 4: Create `Persistence/FileBlobStore.cs`**

```csharp
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
```

- [ ] **Step 5: Run the test, expect pass**

Run: `cd windows && dotnet test tests/VeloxClip.Core.Tests --nologo`
Expected: `FileBlobStoreTests` passes (5 tests). (Skip if `dotnet` unavailable.)

- [ ] **Step 6: Commit**

```bash
git add windows/src/VeloxClip.Core/Abstractions/IBlobStore.cs windows/src/VeloxClip.Core/Persistence/FileBlobStore.cs windows/tests/VeloxClip.Core.Tests/Persistence/FileBlobStoreTests.cs
git commit -m "feat(windows/core): add FileBlobStore (external image storage)"
```

---

## Task 11: `OrphanBlobReconciler`

Startup reconciliation: delete blob files with no DB row, and delete image rows whose blob file is missing.

**Files:**
- Create: `windows/src/VeloxClip.Core/Capture/OrphanBlobReconciler.cs`
- Create: `windows/tests/VeloxClip.Core.Tests/Capture/OrphanBlobReconcilerTests.cs`

- [ ] **Step 1: Write the failing test**

Create `windows/tests/VeloxClip.Core.Tests/Capture/OrphanBlobReconcilerTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using VeloxClip.Core.Abstractions;
using VeloxClip.Core.Capture;
using VeloxClip.Core.Models;
using Xunit;

namespace VeloxClip.Core.Tests.Capture;

public class OrphanBlobReconcilerTests
{
    [Fact]
    public void Reconcile_DeletesOrphanFiles_AndRowsWithMissingFiles()
    {
        // Row "good" has a file; row "ghost" has no file.
        var good = new ImageBlobReference(Guid.NewGuid(), "blobs/good.png");
        var ghost = new ImageBlobReference(Guid.NewGuid(), "blobs/ghost.png");
        var store = new FakeStore(new[] { good, ghost });
        var blobStore = new FakeBlobStore(existing: new[] { "blobs/good.png", "blobs/loose.png" });

        new OrphanBlobReconciler(store, blobStore).Reconcile();

        // Orphan file "blobs/loose.png" deleted (no row references it).
        blobStore.ReconciledKnownPaths.Should().BeEquivalentTo(new[] { "blobs/good.png", "blobs/ghost.png" });
        // Row "ghost" deleted (its file is missing).
        store.DeletedIds.Should().ContainSingle().Which.Should().Be(ghost.Id);
        store.DeletedIds.Should().NotContain(good.Id);
    }

    private sealed class FakeStore : IClipboardStore
    {
        private readonly IReadOnlyList<ImageBlobReference> _refs;
        public List<Guid> DeletedIds { get; } = new();

        public FakeStore(IReadOnlyList<ImageBlobReference> refs) => _refs = refs;

        public IReadOnlyList<ImageBlobReference> GetImageBlobReferences() => _refs;
        public void DeleteById(Guid id) => DeletedIds.Add(id);

        public AddResult Add(ClipboardEntry entry, int historyLimit) => throw new NotSupportedException();
        public ClipboardEntry? GetMostRecent() => throw new NotSupportedException();
        public IReadOnlyList<ClipboardEntry> GetRecent(int count) => throw new NotSupportedException();
    }

    private sealed class FakeBlobStore : IBlobStore
    {
        private readonly HashSet<string> _existing;
        public IReadOnlySet<string>? ReconciledKnownPaths { get; private set; }

        public FakeBlobStore(IEnumerable<string> existing)
            => _existing = new HashSet<string>(existing, StringComparer.OrdinalIgnoreCase);

        public bool Exists(string relativePath) => _existing.Contains(relativePath);
        public void ReconcileOrphans(IReadOnlySet<string> knownRelativePaths)
            => ReconciledKnownPaths = knownRelativePaths;

        public string Save(byte[] pngBytes) => throw new NotSupportedException();
        public void Delete(string relativePath) => throw new NotSupportedException();
    }
}
```

- [ ] **Step 2: Run the test, expect compile failure**

Run: `cd windows && dotnet test tests/VeloxClip.Core.Tests --nologo`
Expected: build fails — `OrphanBlobReconciler` does not exist. (Skip if `dotnet` unavailable.)

- [ ] **Step 3: Create `Capture/OrphanBlobReconciler.cs`**

```csharp
using System;
using System.Collections.Generic;
using VeloxClip.Core.Abstractions;

namespace VeloxClip.Core.Capture;

/// <summary>
/// Startup reconciliation between the clipboard store and the blob store:
/// deletes blob files no row references, and deletes image rows whose blob file
/// is missing.
/// </summary>
public sealed class OrphanBlobReconciler
{
    private readonly IClipboardStore _store;
    private readonly IBlobStore _blobStore;

    public OrphanBlobReconciler(IClipboardStore store, IBlobStore blobStore)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _blobStore = blobStore ?? throw new ArgumentNullException(nameof(blobStore));
    }

    /// <summary>Runs the two-way reconciliation. Safe to call once at startup.</summary>
    public void Reconcile()
    {
        var references = _store.GetImageBlobReferences();

        var knownPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in references)
        {
            knownPaths.Add(reference.BlobPath);
        }

        // File → no row: delete blob files not referenced by any row.
        _blobStore.ReconcileOrphans(knownPaths);

        // Row → no file: delete image rows whose blob file is gone.
        foreach (var reference in references)
        {
            if (!_blobStore.Exists(reference.BlobPath))
            {
                _store.DeleteById(reference.Id);
            }
        }
    }
}
```

- [ ] **Step 4: Run the test, expect pass**

Run: `cd windows && dotnet test tests/VeloxClip.Core.Tests --nologo`
Expected: `OrphanBlobReconcilerTests` passes (1 test). (Skip if `dotnet` unavailable.)

- [ ] **Step 5: Commit**

```bash
git add windows/src/VeloxClip.Core/Capture/OrphanBlobReconciler.cs windows/tests/VeloxClip.Core.Tests/Capture/OrphanBlobReconcilerTests.cs
git commit -m "feat(windows/core): add OrphanBlobReconciler (startup blob/row cleanup)"
```

---

## Task 12: `ClipboardCaptureService` + input interfaces

The pipeline orchestrator and the three "input" interfaces the Platform layer implements. This is the heart of P1 — fully testable with fakes.

**Files:**
- Create: `windows/src/VeloxClip.Core/Abstractions/IClipboardChangeSource.cs`
- Create: `windows/src/VeloxClip.Core/Abstractions/IClipboardReader.cs`
- Create: `windows/src/VeloxClip.Core/Abstractions/IForegroundAppProvider.cs`
- Create: `windows/src/VeloxClip.Core/Capture/ClipboardCaptureService.cs`
- Create: `windows/tests/VeloxClip.Core.Tests/Capture/ClipboardCaptureServiceTests.cs`

- [ ] **Step 1: Create `Abstractions/IClipboardChangeSource.cs`**

```csharp
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
```

- [ ] **Step 2: Create `Abstractions/IClipboardReader.cs`**

```csharp
using VeloxClip.Core.Models;

namespace VeloxClip.Core.Abstractions;

/// <summary>Reads the current clipboard contents into a <see cref="ClipboardCapture"/>.</summary>
public interface IClipboardReader
{
    /// <summary>
    /// Reads the clipboard. Returns null when the clipboard is empty, holds only
    /// unsupported formats, or cannot be opened.
    /// </summary>
    ClipboardCapture? TryRead();
}
```

- [ ] **Step 3: Create `Abstractions/IForegroundAppProvider.cs`**

```csharp
namespace VeloxClip.Core.Abstractions;

/// <summary>Provides the process name of the current foreground application.</summary>
public interface IForegroundAppProvider
{
    /// <summary>The foreground process name (without <c>.exe</c>), or null if unknown.</summary>
    string? GetForegroundProcessName();
}
```

- [ ] **Step 4: Write the failing pipeline test**

Create `windows/tests/VeloxClip.Core.Tests/Capture/ClipboardCaptureServiceTests.cs`:

```csharp
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
```

- [ ] **Step 5: Run the test, expect compile failure**

Run: `cd windows && dotnet test tests/VeloxClip.Core.Tests --nologo`
Expected: build fails — `ClipboardCaptureService` does not exist. (Skip if `dotnet` unavailable.)

- [ ] **Step 6: Create `Capture/ClipboardCaptureService.cs`**

```csharp
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
```

- [ ] **Step 7: Run the test, expect pass**

Run: `cd windows && dotnet test tests/VeloxClip.Core.Tests --nologo`
Expected: `ClipboardCaptureServiceTests` passes (8 tests). (Skip if `dotnet` unavailable.)

- [ ] **Step 8: Commit**

```bash
git add windows/src/VeloxClip.Core/Abstractions windows/src/VeloxClip.Core/Capture/ClipboardCaptureService.cs windows/tests/VeloxClip.Core.Tests/Capture/ClipboardCaptureServiceTests.cs
git commit -m "feat(windows/core): add ClipboardCaptureService pipeline orchestrator"
```

---

## Task 13: `Win32ForegroundAppProvider`

The simplest Win32 implementation: the foreground window's process name. Verified via CI build + Task 17 smoke test (no macOS unit test — Windows-only API).

**Files:**
- Create: `windows/src/VeloxClip.Platform/Clipboard/Win32ForegroundAppProvider.cs`

- [ ] **Step 1: Create `Clipboard/Win32ForegroundAppProvider.cs`**

```csharp
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using VeloxClip.Core.Abstractions;

namespace VeloxClip.Platform.Clipboard;

/// <summary>
/// Win32 <see cref="IForegroundAppProvider"/>: resolves the foreground window's
/// owning process name.
/// </summary>
public sealed partial class Win32ForegroundAppProvider : IForegroundAppProvider
{
    [LibraryImport("user32.dll")]
    private static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    public string? GetForegroundProcessName()
    {
        var hWnd = GetForegroundWindow();
        if (hWnd == IntPtr.Zero)
        {
            return null;
        }

        _ = GetWindowThreadProcessId(hWnd, out var processId);
        if (processId == 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName; // already without the ".exe" suffix
        }
        catch (ArgumentException)
        {
            return null; // process exited between the calls
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
```

- [ ] **Step 2: Verify (CI)**

This file only builds on Windows. Verification is the CI `build-windows.yml` run after a later task pushes; no local test. Proceed.

- [ ] **Step 3: Commit**

```bash
git add windows/src/VeloxClip.Platform/Clipboard/Win32ForegroundAppProvider.cs
git commit -m "feat(windows/platform): add Win32ForegroundAppProvider"
```

---

## Task 14: `Win32ClipboardChangeSource`

A message-only window that registers as a clipboard format listener and raises `ClipboardChanged` on `WM_CLIPBOARDUPDATE`. Created on the caller's thread (the WinUI UI thread, which has a message pump). Windows-only; verified via CI + smoke test.

**Files:**
- Create: `windows/src/VeloxClip.Platform/Clipboard/Win32ClipboardChangeSource.cs`

- [ ] **Step 1: Create `Clipboard/Win32ClipboardChangeSource.cs`**

```csharp
using System;
using System.Runtime.InteropServices;
using VeloxClip.Core.Abstractions;

namespace VeloxClip.Platform.Clipboard;

/// <summary>
/// Win32 <see cref="IClipboardChangeSource"/>. Creates a message-only window and
/// uses <c>AddClipboardFormatListener</c> so the OS posts <c>WM_CLIPBOARDUPDATE</c>
/// whenever the clipboard changes — reliable even while the app runs in the
/// background. <see cref="Start"/> must be called on a thread with a message pump
/// (the WinUI UI thread).
/// </summary>
public sealed class Win32ClipboardChangeSource : IClipboardChangeSource
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private WndProc? _wndProc;       // kept alive for the lifetime of the window
    private IntPtr _hWnd;
    private ushort _classAtom;
    private string? _className;

    public event EventHandler? ClipboardChanged;

    public void Start()
    {
        if (_hWnd != IntPtr.Zero)
        {
            return;
        }

        _wndProc = WindowProc;
        _className = "VeloxClipClipboardListener_" + Guid.NewGuid().ToString("n");

        var wndClass = new WNDCLASS
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = _className,
        };

        _classAtom = RegisterClassW(ref wndClass);
        if (_classAtom == 0)
        {
            throw new InvalidOperationException(
                $"RegisterClassW failed (error {Marshal.GetLastWin32Error()}).");
        }

        _hWnd = CreateWindowExW(
            0, _className, "VeloxClip Clipboard Listener", 0,
            0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, wndClass.hInstance, IntPtr.Zero);
        if (_hWnd == IntPtr.Zero)
        {
            throw new InvalidOperationException(
                $"CreateWindowExW failed (error {Marshal.GetLastWin32Error()}).");
        }

        if (!AddClipboardFormatListener(_hWnd))
        {
            throw new InvalidOperationException(
                $"AddClipboardFormatListener failed (error {Marshal.GetLastWin32Error()}).");
        }
    }

    public void Stop()
    {
        if (_hWnd != IntPtr.Zero)
        {
            RemoveClipboardFormatListener(_hWnd);
            DestroyWindow(_hWnd);
            _hWnd = IntPtr.Zero;
        }

        if (_classAtom != 0 && _className is not null)
        {
            UnregisterClassW(_className, GetModuleHandle(null));
            _classAtom = 0;
        }

        _wndProc = null;
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_CLIPBOARDUPDATE)
        {
            ClipboardChanged?.Invoke(this, EventArgs.Empty);
            return IntPtr.Zero;
        }

        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string? lpszClassName;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassW(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int width, int height,
        IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hWnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
```

> Note: `RegisterClassW` / `CreateWindowExW` etc. use classic `[DllImport]` rather than `[LibraryImport]` because they pass a struct containing a delegate-derived function pointer and string fields; `DllImport` marshals this layout without extra ceremony.

- [ ] **Step 2: Verify (CI)**

Windows-only; CI build is the check. Proceed.

- [ ] **Step 3: Commit**

```bash
git add windows/src/VeloxClip.Platform/Clipboard/Win32ClipboardChangeSource.cs
git commit -m "feat(windows/platform): add Win32ClipboardChangeSource (format listener)"
```

---

## Task 15: `Win32ClipboardReader`

Reads the clipboard via Win32 and produces a `ClipboardCapture`. Format priority: **file > rtf > text > image** (see the plan header's "Deliberate divergence" note). Images are normalized to PNG. Windows-only; verified via CI + smoke test.

**Files:**
- Modify: `windows/src/VeloxClip.Platform/VeloxClip.Platform.csproj`
- Create: `windows/src/VeloxClip.Platform/Clipboard/Win32ClipboardReader.cs`

- [ ] **Step 1: Reference `System.Drawing.Common` from the Platform project**

In `windows/src/VeloxClip.Platform/VeloxClip.Platform.csproj`, add this line inside the `<ItemGroup>` that has the `Microsoft.Extensions.DependencyInjection.Abstractions` reference:

```xml
    <PackageReference Include="System.Drawing.Common" />
```

- [ ] **Step 2: Create `Clipboard/Win32ClipboardReader.cs`**

```csharp
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using VeloxClip.Core.Abstractions;
using VeloxClip.Core.Models;

namespace VeloxClip.Platform.Clipboard;

/// <summary>
/// Win32 <see cref="IClipboardReader"/>. Opens the clipboard, picks the highest-
/// priority available format (file &gt; rtf &gt; text &gt; image), and returns a
/// <see cref="ClipboardCapture"/>. Images are normalized to PNG bytes.
/// </summary>
public sealed class Win32ClipboardReader : IClipboardReader
{
    private const uint CF_BITMAP = 2;
    private const uint CF_DIB = 8;
    private const uint CF_UNICODETEXT = 13;
    private const uint CF_HDROP = 15;
    private const int OpenRetryCount = 5;
    private const int OpenRetryDelayMs = 20;

    private static readonly uint CfRtf = RegisterClipboardFormatW("Rich Text Format");
    private static readonly uint CfPng = RegisterClipboardFormatW("PNG");

    public ClipboardCapture? TryRead()
    {
        if (!TryOpenClipboard())
        {
            return null;
        }

        try
        {
            // Priority: file > rtf > text > image.
            if (IsClipboardFormatAvailable(CF_HDROP))
            {
                var files = ReadFileList();
                if (files is not null)
                {
                    return new ClipboardCapture(ClipboardKind.File, files, null);
                }
            }

            if (CfRtf != 0 && IsClipboardFormatAvailable(CfRtf))
            {
                var rtf = ReadAnsiText(CfRtf);
                if (rtf is not null)
                {
                    return new ClipboardCapture(ClipboardKind.Rtf, rtf, null);
                }
            }

            if (IsClipboardFormatAvailable(CF_UNICODETEXT))
            {
                var text = ReadUnicodeText();
                if (text is not null)
                {
                    return new ClipboardCapture(ClipboardKind.Text, text, null);
                }
            }

            var image = ReadImageAsPng();
            if (image is not null)
            {
                return new ClipboardCapture(ClipboardKind.Image, null, image);
            }

            return null;
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static bool TryOpenClipboard()
    {
        for (var attempt = 0; attempt < OpenRetryCount; attempt++)
        {
            if (OpenClipboard(IntPtr.Zero))
            {
                return true;
            }

            System.Threading.Thread.Sleep(OpenRetryDelayMs);
        }

        return false;
    }

    private static string? ReadUnicodeText()
    {
        var handle = GetClipboardData(CF_UNICODETEXT);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var ptr = GlobalLock(handle);
        if (ptr == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var text = Marshal.PtrToStringUni(ptr);
            return string.IsNullOrEmpty(text) ? null : text;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static string? ReadAnsiText(uint format)
    {
        var handle = GetClipboardData(format);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var ptr = GlobalLock(handle);
        if (ptr == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var size = (int)GlobalSize(handle);
            if (size <= 0)
            {
                return null;
            }

            var bytes = new byte[size];
            Marshal.Copy(ptr, bytes, 0, size);
            // RTF is 7-bit ASCII; trim a trailing NUL if present.
            var text = Encoding.ASCII.GetString(bytes).TrimEnd('\0');
            return string.IsNullOrEmpty(text) ? null : text;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static string? ReadFileList()
    {
        var handle = GetClipboardData(CF_HDROP);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var count = DragQueryFileW(handle, 0xFFFFFFFF, null, 0);
        if (count == 0)
        {
            return null;
        }

        var paths = new string[count];
        for (uint i = 0; i < count; i++)
        {
            var length = DragQueryFileW(handle, i, null, 0);
            var buffer = new StringBuilder((int)length + 1);
            DragQueryFileW(handle, i, buffer, (uint)buffer.Capacity);
            paths[i] = buffer.ToString();
        }

        return string.Join("\n", paths);
    }

    private static byte[]? ReadImageAsPng()
    {
        // Prefer an app-provided PNG: no re-encoding needed.
        if (CfPng != 0 && IsClipboardFormatAvailable(CfPng))
        {
            var pngHandle = GetClipboardData(CfPng);
            var pngBytes = CopyGlobalBytes(pngHandle);
            if (pngBytes is not null)
            {
                return pngBytes;
            }
        }

        // Fall back to CF_DIB: prepend a BITMAPFILEHEADER to make a .bmp stream.
        if (IsClipboardFormatAvailable(CF_DIB))
        {
            var dibHandle = GetClipboardData(CF_DIB);
            var dib = CopyGlobalBytes(dibHandle);
            if (dib is not null)
            {
                return DibToPng(dib);
            }
        }

        // Last resort: CF_BITMAP (an HBITMAP).
        if (IsClipboardFormatAvailable(CF_BITMAP))
        {
            var hBitmap = GetClipboardData(CF_BITMAP);
            if (hBitmap != IntPtr.Zero)
            {
                using var bitmap = Image.FromHbitmap(hBitmap);
                return EncodePng(bitmap);
            }
        }

        return null;
    }

    private static byte[]? CopyGlobalBytes(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var ptr = GlobalLock(handle);
        if (ptr == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var size = (int)GlobalSize(handle);
            if (size <= 0)
            {
                return null;
            }

            var bytes = new byte[size];
            Marshal.Copy(ptr, bytes, 0, size);
            return bytes;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    private static byte[] DibToPng(byte[] dib)
    {
        // A CF_DIB is a .bmp file missing its 14-byte BITMAPFILEHEADER.
        // The pixel data offset = 14 + the DIB header size (first 4 LE bytes of the DIB).
        const int fileHeaderSize = 14;
        var dibHeaderSize = BitConverter.ToInt32(dib, 0);
        var pixelOffset = fileHeaderSize + dibHeaderSize;

        var bmp = new byte[fileHeaderSize + dib.Length];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);   // total file size
        BitConverter.GetBytes(pixelOffset).CopyTo(bmp, 10); // offset to pixel data
        dib.CopyTo(bmp, fileHeaderSize);

        using var bmpStream = new MemoryStream(bmp);
        using var bitmap = new Bitmap(bmpStream);
        return EncodePng(bitmap);
    }

    private static byte[] EncodePng(Image image)
    {
        using var pngStream = new MemoryStream();
        image.Save(pngStream, ImageFormat.Png);
        return pngStream.ToArray();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormatW(string lpszFormat);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFileW(IntPtr hDrop, uint iFile, StringBuilder? lpszFile, uint cch);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern UIntPtr GlobalSize(IntPtr hMem);
}
```

- [ ] **Step 3: Verify (CI)**

Windows-only; CI build is the check. Proceed.

- [ ] **Step 4: Commit**

```bash
git add windows/src/VeloxClip.Platform/VeloxClip.Platform.csproj windows/src/VeloxClip.Platform/Clipboard/Win32ClipboardReader.cs
git commit -m "feat(windows/platform): add Win32ClipboardReader (text/rtf/image/file)"
```

---

## Task 16: `ClipboardMonitorHostedService` + DI wiring

The `IHostedService` that runs the monitor — startup reconciliation, register the listener, route each change event through the capture pipeline — plus the DI registration of every P1 service.

**Files:**
- Modify: `windows/src/VeloxClip.Platform/VeloxClip.Platform.csproj`
- Create: `windows/src/VeloxClip.Platform/ClipboardMonitorHostedService.cs`
- Modify: `windows/src/VeloxClip.Platform/PlatformServiceCollectionExtensions.cs`

- [ ] **Step 1: Reference `Microsoft.Extensions.Hosting.Abstractions` from the Platform project**

`IHostedService` and the `AddHostedService` extension live in `Microsoft.Extensions.Hosting.Abstractions`, which the Platform project does not get transitively (Platform → Core → `Microsoft.Extensions.Logging` only). In `windows/src/VeloxClip.Platform/VeloxClip.Platform.csproj`, add this line inside the `<ItemGroup>` that has the `Microsoft.Extensions.DependencyInjection.Abstractions` reference:

```xml
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" />
```

- [ ] **Step 2: Create `ClipboardMonitorHostedService.cs`**

```csharp
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

        _changeSource.ClipboardChanged += OnClipboardChanged;
        _changeSource.Start();
        _logger.LogInformation("Clipboard monitor started.");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _changeSource.ClipboardChanged -= OnClipboardChanged;
        _changeSource.Stop();
        _logger.LogInformation("Clipboard monitor stopped.");
        return Task.CompletedTask;
    }

    private void OnClipboardChanged(object? sender, EventArgs e)
        => Task.Run(_captureService.Capture);
}
```

- [ ] **Step 3: Replace `PlatformServiceCollectionExtensions.cs`**

Replace the entire contents of `windows/src/VeloxClip.Platform/PlatformServiceCollectionExtensions.cs` with:

```csharp
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
```

> `IAppPaths` is already registered as a singleton by the App layer (`HostBuilderExtensions` from P0), so `VeloxClipDatabase` and `FileBlobStore` resolve their `IAppPaths` dependency from there.

- [ ] **Step 4: Verify (CI)**

Windows-only; CI build is the check. Proceed.

- [ ] **Step 5: Commit**

```bash
git add windows/src/VeloxClip.Platform/VeloxClip.Platform.csproj windows/src/VeloxClip.Platform/ClipboardMonitorHostedService.cs windows/src/VeloxClip.Platform/PlatformServiceCollectionExtensions.cs
git commit -m "feat(windows/platform): wire clipboard monitor hosted service + DI"
```

---

## Task 17: CHANGELOG + verification

Record P1 in the changelog, then verify the whole feature via CI and a Windows smoke test.

**Files:**
- Modify: `CHANGELOG.md`

- [ ] **Step 1: Update `CHANGELOG.md`**

In `CHANGELOG.md`, under the existing `## [Unreleased]` → `### Added` section, add this bullet after the P0 entry:

```markdown
- **Windows port — P1 clipboard core**: background clipboard monitor (Win32 format listener) capturing text / RTF / image / file / color, SQLite persistence (`clipboard_entries` + `app_settings`), external PNG blob store with a 16 MB cap, two-tier deduplication, source-app tracking, a configurable history limit (default 100), a hardcoded password-manager blacklist, and startup orphan-blob reconciliation. No user-facing UI yet.
```

- [ ] **Step 2: Commit**

```bash
git add CHANGELOG.md
git commit -m "docs(changelog): record P1 clipboard core"
```

- [ ] **Step 3: Push and verify CI**

```bash
git push
gh run watch
```

Expected on the `build-windows.yml` run:
- `Build` green — all of `VeloxClip.Core`, `VeloxClip.Platform`, `VeloxClip.App` compile with 0 warnings (`TreatWarningsAsErrors` is on).
- `Test` green — every `VeloxClip.Core.Tests` test passes, including the ~40 new P1 tests from Tasks 1–12.

If `Build` fails on a Win32 P/Invoke signature, fix the signature and re-push. If `Test` fails, the failure names the test — fix the corresponding Core class.

- [ ] **Step 4: Windows smoke test (requires a Windows 11 22H2 host or VM)**

This is the only verification that needs a Windows machine. Build and run:

```pwsh
cd windows
dotnet run --project src/VeloxClip.App -c Debug
```

The app launches the placeholder window + tray icon (no clipboard UI — P1 has none). With a SQLite browser open on `%LOCALAPPDATA%\VeloxClip\db\veloxclip.db`, verify each Definition-of-Done item from the spec §11:

1. Copy plain text, RTF (from Word), an image (a screenshot), files (select files in Explorer, Ctrl+C), and a color string (`#FF5733`) — one each. `clipboard_entries` gains 5 rows with correct `kind` (`text`/`rtf`/`image`/`file`/`color`), `content`, `blob_path`, `source_app`.
2. The `image` row's `blob_path` resolves to a real PNG file under `%LOCALAPPDATA%\VeloxClip\blobs\`.
3. Copy the same text twice within 3 seconds → only 1 row (Tier-1 dedup).
4. Copy A, copy B, copy A again → A's row has a refreshed `created_at` and is newest; 2 rows total, not 3 (Tier-2 move-to-top).
5. Set `history_limit` to 5 (via the SQLite browser: `INSERT OR REPLACE INTO app_settings VALUES('history_limit','5')`), copy 7 distinct things → DB keeps 5 rows; the 2 oldest are gone and any trimmed image's blob file is deleted.
6. With a password manager installed (e.g. Bitwarden), copy from it → no new row.
7. Copy an unsupported format / trigger a clipboard lock → `%LOCALAPPDATA%\VeloxClip\logs\` shows a warning, the app keeps running, the next normal copy is captured.
8. Put a stray file in `%LOCALAPPDATA%\VeloxClip\blobs\`, restart the app → the stray file is deleted by startup reconciliation.

- [ ] **Step 5: Fix any smoke-test failures, re-push, confirm CI green**

Iterate until CI is green and all 8 smoke-test checks pass. File each failure against the task that owns the relevant class (e.g. Tier-2 failing → Task 8; listener not firing → Task 14).

---

## P1 Definition of Done — final checklist (from spec §11)

- [ ] `VeloxClip.Core.Tests` all green — new test classes from Tasks 1–12 pass (macOS local + CI)
- [ ] CI `build-windows.yml` green on `windows-2022`, 0 warnings
- [ ] Windows smoke test: text / RTF / image / file / color each produce a correct row (smoke step 1)
- [ ] Image row's `blob_path` points to a real PNG file (smoke step 2)
- [ ] Tier-1 dedup: same text twice in 3 s → 1 row (smoke step 3)
- [ ] Tier-2 move-to-top: A, B, A → 2 rows, A newest (smoke step 4)
- [ ] History cap: limit 5, copy 7 → 5 rows, oldest 2 + blobs trimmed (smoke step 5)
- [ ] Blacklist: copy from a password manager → no row (smoke step 6)
- [ ] Resilience: clipboard lock / unsupported format logged, monitor survives (smoke step 7)
- [ ] Startup orphan reconciliation removes a stray blob file (smoke step 8)
