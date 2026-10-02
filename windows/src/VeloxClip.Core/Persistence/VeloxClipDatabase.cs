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
