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

        // The schema must still be intact after the second (idempotent) call.
        ScalarText(conn2, "SELECT name FROM sqlite_master WHERE type='table' AND name='clipboard_entries';")
            .Should().Be("clipboard_entries");
        ScalarText(conn2, "SELECT name FROM sqlite_master WHERE type='table' AND name='app_settings';")
            .Should().Be("app_settings");
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
