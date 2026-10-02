using System;
using System.Collections.Generic;
using System.Globalization;
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
            total = Convert.ToInt64(count.ExecuteScalar(), CultureInfo.InvariantCulture);
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
