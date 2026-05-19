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
