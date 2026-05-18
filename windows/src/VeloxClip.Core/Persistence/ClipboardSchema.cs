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
