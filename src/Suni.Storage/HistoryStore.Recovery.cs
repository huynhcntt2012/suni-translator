using System.Diagnostics;
using Dapper;
using Microsoft.Data.Sqlite;

namespace Suni.Storage;

/// <summary>Kết quả khôi phục DB lịch sử bị hỏng.</summary>
/// <param name="CorruptCopyPath">File hỏng được giữ lại (đổi tên), cùng -wal/-shm nếu có.</param>
public sealed record RecoveryReport(int SessionsRecovered, int SegmentsRecovered, int OrphanSessionsCreated, string CorruptCopyPath);

public sealed partial class HistoryStore
{
    private static readonly TimeSpan SalvageBudget = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Kiểm tra DB lúc khởi động; nếu hỏng (mất điện, ổ đĩa lỗi, ghi chéo từ tiến trình khác…):
    /// cứu mọi dòng còn đọc được sang một DB mới sạch, đổi tên file hỏng thành <c>suni.db.corrupt-&lt;thời gian&gt;</c>
    /// (giữ lại, không xoá). Trả về null nếu DB lành hoặc chưa tồn tại.
    /// </summary>
    public static async Task<RecoveryReport?> RecoverIfCorruptAsync(string dbPath, CancellationToken ct = default)
    {
        if (!File.Exists(dbPath) || await IsHealthyAsync(dbPath).ConfigureAwait(false))
            return null;

        // Chỉ giữ dòng chuyển được thành phiên/câu hợp lệ: dòng rác (sai kiểu, enum lạ) sẽ làm app lỗi khi đọc về sau.
        var sessions = Salvage<SessionRow>(dbPath, "sessions", SessionColumns, r => { _ = r.ToSession(); return r.id.Length > 0; });
        var segments = Salvage<SegmentRow>(dbPath, "segments", SegmentColumns, r => { _ = r.ToSegment(); return r.session_id.Length > 0; });
        SqliteConnection.ClearAllPools(); // nhả file trước khi đổi tên

        var corrupt = $"{dbPath}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
        File.Move(dbPath, corrupt);
        foreach (var suffix in new[] { "-wal", "-shm" })
            if (File.Exists(dbPath + suffix))
                File.Move(dbPath + suffix, corrupt + suffix);

        var store = new HistoryStore(SuniDatabase.ForFile(dbPath));
        await store.InitializeAsync(ct).ConfigureAwait(false);

        var sessionRows = sessions.GroupBy(s => s.id).Select(g => g.First()).ToList();
        var known = sessionRows.Select(s => s.id).ToHashSet();
        // Câu còn đọc được nhưng mất dòng phiên → tạo phiên thay thế để không vứt bỏ câu.
        var fallbackTime = new DateTimeOffset(File.GetLastWriteTime(corrupt)).ToUnixTimeMilliseconds();
        var orphans = segments.Select(s => s.session_id).Where(id => !known.Contains(id)).Distinct()
            .Select(id => new SessionRow(id, fallbackTime, null, null, null, "ProcessLoopback", null, "?", null, null, null, null,
                                         "Stopped", "Khôi phục (mất thông tin phiên)"))
            .ToList();

        await using (var conn = await store.Database.OpenAsync(ct).ConfigureAwait(false))
        await using (var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false))
        {
            foreach (var s in sessionRows.Concat(orphans))
                await conn.ExecuteAsync(UpsertSessionSql, s, tx).ConfigureAwait(false);
            foreach (var g in segments.GroupBy(s => (s.session_id, s.seq)).Select(g => g.First()))
                await conn.ExecuteAsync(UpsertSegmentSql, g, tx).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        var segmentCount = segments.Select(s => (s.session_id, s.seq)).Distinct().Count();
        return new RecoveryReport(sessionRows.Count, segmentCount, orphans.Count, corrupt);
    }

    internal SuniDatabase Database => db;

    private static async Task<bool> IsHealthyAsync(string dbPath)
    {
        try
        {
            await using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Pooling = false }.ToString());
            await conn.OpenAsync().ConfigureAwait(false);
            var result = await conn.QueryAsync<string>("PRAGMA quick_check;").ConfigureAwait(false);
            return result.FirstOrDefault() == "ok";
        }
        catch (SqliteException)
        {
            return false; // "file is not a database", "malformed"…
        }
    }

    /// <summary>
    /// Đọc mọi dòng còn đọc được: quét cả bảng trước; gặp trang hỏng giữa chừng thì đọc từng rowid
    /// để chỉ mất các dòng nằm trên trang hỏng.
    /// </summary>
    private static List<T> Salvage<T>(string dbPath, string table, string columns, Func<T, bool> isValid)
    {
        // Một dòng hỏng (giá trị rác, sai kiểu) → bỏ dòng đó, không bỏ cả bảng.
        bool TryParse(Func<System.Data.IDataReader, T> parse, System.Data.IDataReader reader, out T row)
        {
            row = default!;
            try
            {
                row = parse(reader);
                return isValid(row);
            }
            catch (Exception ex) when (ex is not SqliteException)
            {
                return false;
            }
        }

        var rows = new Dictionary<long, T>();
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false,
        }.ToString());
        try
        {
            conn.Open();
        }
        catch (SqliteException)
        {
            return [];
        }

        var complete = false;
        try
        {
            using var reader = conn.ExecuteReader($"SELECT rowid, {columns} FROM {table};");
            var parse = reader.GetRowParser<T>(startIndex: 1);
            while (reader.Read())
                if (TryParse(parse, reader, out var row))
                    rows[reader.GetInt64(0)] = row;
            complete = true;
        }
        catch (SqliteException)
        {
            // trang hỏng: bổ sung bằng cách đọc từng rowid
        }

        if (!complete)
        {
            long max;
            try
            {
                max = conn.ExecuteScalar<long?>($"SELECT max(rowid) FROM {table};") ?? 0;
            }
            catch (SqliteException)
            {
                max = Math.Max(rows.Keys.DefaultIfEmpty(0).Max() * 4, 50_000);
            }

            var sw = Stopwatch.StartNew();
            for (var r = 1L; r <= Math.Min(max, 2_000_000) && sw.Elapsed < SalvageBudget; r++)
            {
                if (rows.ContainsKey(r))
                    continue;
                try
                {
                    using var reader = conn.ExecuteReader($"SELECT rowid, {columns} FROM {table} WHERE rowid = @r;", new { r });
                    if (reader.Read() && TryParse(reader.GetRowParser<T>(startIndex: 1), reader, out var row))
                        rows[r] = row;
                }
                catch (SqliteException)
                {
                    // dòng nằm trên trang hỏng
                }
            }
        }
        return [.. rows.OrderBy(kv => kv.Key).Select(kv => kv.Value)];
    }

    private const string SessionColumns =
        "id, started_at, ended_at, source_app, source_pid, capture_mode, source_lang, target_lang, asr_engine, asr_model, " +
        "ollama_url_kind, ollama_model, status, title";

    private const string SegmentColumns =
        "id, session_id, seq, t_start_ms, t_end_ms, text_original, text_translated, text_translated_edited, lang_detected, " +
        "lang_prob, is_final, status, asr_ms, llm_ms, error_code";
}
