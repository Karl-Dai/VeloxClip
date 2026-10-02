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
