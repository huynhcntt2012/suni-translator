using Dapper;
using Microsoft.Data.Sqlite;

namespace Suni.Storage.Tests;

public class SuniDatabaseTests
{
    private static async Task<SqliteConnection> OpenMemoryAsync()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        await conn.OpenAsync();
        await conn.ExecuteAsync("PRAGMA foreign_keys=ON;");
        return conn;
    }

    [Fact]
    public async Task Migrate_CreatesSchema_AndIsIdempotent()
    {
        await using var conn = await OpenMemoryAsync();
        await SuniDatabase.MigrateAsync(conn);
        await SuniDatabase.MigrateAsync(conn);

        var tables = (await conn.QueryAsync<string>("SELECT name FROM sqlite_master WHERE type='table'")).ToHashSet();
        Assert.Superset(new HashSet<string> { "sessions", "segments", "jobs", "glossary", "settings", "segments_fts" }, tables);
        Assert.Equal(2, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM schema_version")); // 001_initial, 002_merged
    }

    [Fact]
    public async Task Segments_UpsertBySessionAndSeq_DoesNotDuplicate_AndFtsFindsCjk()
    {
        await using var conn = await OpenMemoryAsync();
        await SuniDatabase.MigrateAsync(conn);
        await conn.ExecuteAsync("""
            INSERT INTO sessions(id, started_at, capture_mode, target_lang, status)
            VALUES ('s1', 0, 'ProcessLoopback', 'vi', 'Running');
            """);

        const string upsert = """
            INSERT INTO segments(id, session_id, seq, t_start_ms, t_end_ms, text_original, text_translated, is_final, status)
            VALUES (@id, 's1', 1, 0, 1000, 'この機能の使い方', @tr, 1, @st)
            ON CONFLICT(session_id, seq) DO UPDATE SET text_translated = excluded.text_translated, status = excluded.status;
            """;
        await conn.ExecuteAsync(upsert, new { id = "seg_a", tr = (string?)null, st = "TranslatePending" });
        await conn.ExecuteAsync(upsert, new { id = "seg_b", tr = "cách sử dụng tính năng", st = "Translated" });

        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM segments"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM segments_fts WHERE segments_fts MATCH '使い方'"));
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM segments_fts WHERE segments_fts MATCH 'tính năng'"));
    }

    [Fact]
    public async Task DeletingSession_CascadesToSegments()
    {
        await using var conn = await OpenMemoryAsync();
        await SuniDatabase.MigrateAsync(conn);
        await conn.ExecuteAsync("""
            INSERT INTO sessions(id, started_at, capture_mode, target_lang, status) VALUES ('s1', 0, 'ProcessLoopback', 'vi', 'Stopped');
            INSERT INTO segments(id, session_id, seq, t_start_ms, t_end_ms, is_final, status) VALUES ('seg', 's1', 1, 0, 1, 1, 'Displayed');
            DELETE FROM sessions WHERE id = 's1';
            """);

        Assert.Equal(0, await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM segments"));
    }
}
