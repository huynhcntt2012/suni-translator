using System.Data;
using Dapper;
using Microsoft.Data.Sqlite;
using Suni.Core.Abstractions;
using Suni.Core.Domain;

namespace Suni.Storage;

/// <summary>
/// Lịch sử phiên/câu trong SQLite (SDD 7). Mỗi thao tác mở một kết nối (có pool); WAL cho phép đọc trong lúc
/// <see cref="HistoryRecorder"/> ghi. Ghi theo lô dùng <see cref="WriteBatchAsync"/> trong một transaction.
/// </summary>
public sealed partial class HistoryStore(SuniDatabase db) : ISessionRepository, ISegmentRepository
{
    private int _migrated;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        await SuniDatabase.MigrateAsync(conn, ct).ConfigureAwait(false);
        Volatile.Write(ref _migrated, 1);
    }

    // ---------------------------------------------------------------- ghi

    public Task UpsertAsync(Session session, CancellationToken ct = default) =>
        WriteBatchAsync([HistoryWrite.Session(session)], ct);

    public Task UpsertAsync(Segment segment, CancellationToken ct = default) =>
        WriteBatchAsync([HistoryWrite.Segment(segment)], ct);

    public Task MarkMergedAsync(IReadOnlyList<string> segmentIds, string mergedIntoId, CancellationToken ct = default) =>
        WriteBatchAsync([HistoryWrite.Merged(segmentIds, mergedIntoId)], ct);

    /// <summary>Áp dụng nhiều thao tác ghi trong một transaction (một lần fsync cho cả lô).</summary>
    public async Task WriteBatchAsync(IReadOnlyList<HistoryWrite> writes, CancellationToken ct = default)
    {
        if (writes.Count == 0)
            return;
        await EnsureMigratedAsync(ct).ConfigureAwait(false);
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await conn.BeginTransactionAsync(ct).ConfigureAwait(false);
        foreach (var w in writes)
        {
            switch (w)
            {
                case HistoryWrite.SessionWrite s:
                    await conn.ExecuteAsync(UpsertSessionSql, SessionRow.From(s.Value), tx).ConfigureAwait(false);
                    break;
                case HistoryWrite.SegmentWrite s:
                    await conn.ExecuteAsync(UpsertSegmentSql, SegmentRow.From(s.Value), tx).ConfigureAwait(false);
                    break;
                case HistoryWrite.MergedWrite m:
                    await conn.ExecuteAsync(
                        "UPDATE segments SET status = 'Merged', merged_into = @into WHERE id IN @ids AND id <> @into;",
                        new { ids = m.SegmentIds, into = m.MergedIntoId }, tx).ConfigureAwait(false);
                    break;
            }
        }
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<Segment?> SetEditedTranslationAsync(string segmentId, string? text, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct).ConfigureAwait(false);
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        var edited = string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        // Sửa thành đúng bản máy = không còn là bản sửa (để "đã sửa" chỉ hiện khi thật sự khác).
        await conn.ExecuteAsync("""
            UPDATE segments
            SET text_translated_edited = CASE WHEN @edited IS NULL OR @edited = text_translated THEN NULL ELSE @edited END
            WHERE id = @segmentId;
            """, new { segmentId, edited }).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<SegmentRow>($"{SelectSegment} WHERE id = @segmentId;", new { segmentId })
            .ConfigureAwait(false);
        return row?.ToSegment();
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct).ConfigureAwait(false);
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync("DELETE FROM sessions WHERE id = @id;", new { id }).ConfigureAwait(false);
    }

    public async Task DeleteAllAsync(CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct).ConfigureAwait(false);
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        await conn.ExecuteAsync("DELETE FROM sessions; DELETE FROM segments; INSERT INTO segments_fts(segments_fts) VALUES('rebuild');")
            .ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- đọc

    public async Task<Session?> GetAsync(string id, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct).ConfigureAwait(false);
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        var row = await conn.QuerySingleOrDefaultAsync<SessionRow>($"{SelectSession} WHERE id = @id;", new { id }).ConfigureAwait(false);
        return row?.ToSession();
    }

    public async Task<IReadOnlyList<SessionSummary>> ListAsync(DateTimeOffset? from = null, DateTimeOffset? to = null,
                                                              string? sourceApp = null, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct).ConfigureAwait(false);
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<SessionSummaryRow>($"""
            SELECT s.*, (SELECT COUNT(*) FROM segments g WHERE g.session_id = s.id AND {VisibleSegment}) AS segment_count
            FROM ({SelectSession}) s
            WHERE (@from IS NULL OR s.started_at >= @from)
              AND (@to IS NULL OR s.started_at < @to)
              AND (@app IS NULL OR s.source_app = @app)
            ORDER BY s.started_at DESC;
            """, new { from = from?.ToUnixTimeMilliseconds(), to = to?.ToUnixTimeMilliseconds(), app = sourceApp }).ConfigureAwait(false);
        return [.. rows.Select(r => new SessionSummary(r.ToSession(), (int)r.segment_count))];
    }

    public async Task<IReadOnlyList<Segment>> ListBySessionAsync(string sessionId, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct).ConfigureAwait(false);
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<SegmentRow>(
            $"{SelectSegment} WHERE session_id = @sessionId AND {VisibleSegment} ORDER BY seq;", new { sessionId }).ConfigureAwait(false);
        return [.. rows.Select(r => r.ToSegment())];
    }

    public async Task<IReadOnlyList<Segment>> ListPendingTranslationAsync(string sessionId, CancellationToken ct = default)
    {
        await EnsureMigratedAsync(ct).ConfigureAwait(false);
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);
        var rows = await conn.QueryAsync<SegmentRow>(
            $"{SelectSegment} WHERE session_id = @sessionId AND status IN ('TranslatePending', 'TranslateFailed') ORDER BY seq;",
            new { sessionId }).ConfigureAwait(false);
        return [.. rows.Select(r => r.ToSegment())];
    }

    public async Task<IReadOnlyList<Segment>> SearchAsync(string query, int limit = 200, CancellationToken ct = default)
    {
        query = query.Trim();
        if (query.Length == 0)
            return [];
        await EnsureMigratedAsync(ct).ConfigureAwait(false);
        await using var conn = await db.OpenAsync(ct).ConfigureAwait(false);

        // FTS5 trigram cần ≥ 3 ký tự mỗi từ; truy vấn ngắn hơn (vd. "日本" hay "ok") dùng LIKE.
        IEnumerable<SegmentRow> rows;
        if (query.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(w => w.Length >= 3))
        {
            var phrase = "\"" + query.Replace("\"", "\"\"") + "\"";
            rows = await conn.QueryAsync<SegmentRow>($"""
                {SelectSegment}
                WHERE segments.rowid IN (SELECT rowid FROM segments_fts WHERE segments_fts MATCH @phrase) AND {VisibleSegment}
                ORDER BY session_id DESC, seq LIMIT @limit;
                """, new { phrase, limit }).ConfigureAwait(false);
        }
        else
        {
            var like = "%" + query.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            rows = await conn.QueryAsync<SegmentRow>($"""
                {SelectSegment}
                WHERE (text_original LIKE @like ESCAPE '\' OR text_translated LIKE @like ESCAPE '\' OR text_translated_edited LIKE @like ESCAPE '\')
                  AND {VisibleSegment}
                ORDER BY session_id DESC, seq LIMIT @limit;
                """, new { like, limit }).ConfigureAwait(false);
        }
        return [.. rows.Select(r => r.ToSegment())];
    }

    private async Task EnsureMigratedAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _migrated) == 0)
            await InitializeAsync(ct).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- SQL & ánh xạ

    /// <summary>Câu hiển thị/xuất: có câu gốc, không phải mảnh đã gộp hay đoạn bị loại.</summary>
    private const string VisibleSegment = "status NOT IN ('Merged', 'Dropped') AND text_original IS NOT NULL";

    private const string SelectSession = """
        SELECT id, started_at, ended_at, source_app, source_pid, capture_mode, source_lang, target_lang,
               asr_engine, asr_model, ollama_url_kind, ollama_model, status, title
        FROM sessions
        """;

    private const string SelectSegment = """
        SELECT segments.id, segments.session_id, segments.seq, segments.t_start_ms, segments.t_end_ms,
               segments.text_original, segments.text_translated, segments.text_translated_edited,
               segments.lang_detected, segments.lang_prob, segments.is_final, segments.status,
               segments.asr_ms, segments.llm_ms, segments.error_code
        FROM segments
        """;

    private const string UpsertSessionSql = """
        INSERT INTO sessions(id, started_at, ended_at, source_app, source_pid, capture_mode, source_lang, target_lang,
                             asr_engine, asr_model, ollama_url_kind, ollama_model, status, title)
        VALUES (@id, @started_at, @ended_at, @source_app, @source_pid, @capture_mode, @source_lang, @target_lang,
                @asr_engine, @asr_model, @ollama_url_kind, @ollama_model, @status, @title)
        ON CONFLICT(id) DO UPDATE SET
            ended_at = excluded.ended_at, status = excluded.status, title = COALESCE(excluded.title, sessions.title);
        """;

    // Không hạ trạng thái: bản dịch về trước/sau sự kiện khác vẫn giữ dữ liệu đầy đủ nhất; không ghi đè 'Merged'.
    private const string UpsertSegmentSql = """
        INSERT INTO segments(id, session_id, seq, t_start_ms, t_end_ms, text_original, text_translated, text_translated_edited,
                             lang_detected, lang_prob, is_final, status, asr_ms, llm_ms, error_code)
        VALUES (@id, @session_id, @seq, @t_start_ms, @t_end_ms, @text_original, @text_translated, @text_translated_edited,
                @lang_detected, @lang_prob, @is_final, @status, @asr_ms, @llm_ms, @error_code)
        ON CONFLICT(session_id, seq) DO UPDATE SET
            t_start_ms = excluded.t_start_ms,
            text_original = COALESCE(excluded.text_original, segments.text_original),
            text_translated = COALESCE(excluded.text_translated, segments.text_translated),
            lang_detected = COALESCE(excluded.lang_detected, segments.lang_detected),
            status = CASE WHEN segments.status = 'Merged' THEN 'Merged' ELSE excluded.status END,
            asr_ms = COALESCE(excluded.asr_ms, segments.asr_ms),
            llm_ms = COALESCE(excluded.llm_ms, segments.llm_ms),
            error_code = excluded.error_code;
        """;

    // Tên thuộc tính = tên cột để Dapper ánh xạ trực tiếp.
#pragma warning disable IDE1006
    private record SessionRow(string id, long started_at, long? ended_at, string? source_app, long? source_pid,
                              string capture_mode, string? source_lang, string target_lang, string? asr_engine,
                              string? asr_model, string? ollama_url_kind, string? ollama_model, string status, string? title)
    {
        public SessionRow() : this("", 0, null, null, null, "", null, "", null, null, null, null, "", null) { }

        public static SessionRow From(Session s) => new(
            s.Id, s.StartedAt.ToUnixTimeMilliseconds(), s.EndedAt?.ToUnixTimeMilliseconds(), s.SourceApp, s.SourcePid,
            s.CaptureMode.ToString(), s.SourceLang, s.TargetLang, s.AsrEngine, s.AsrModel, s.OllamaEndpointKind?.ToString(),
            s.OllamaModel, s.Status.ToString(), s.Title);

        public Session ToSession() => new()
        {
            Id = id,
            StartedAt = DateTimeOffset.FromUnixTimeMilliseconds(started_at).ToLocalTime(),
            EndedAt = ended_at is { } e ? DateTimeOffset.FromUnixTimeMilliseconds(e).ToLocalTime() : null,
            SourceApp = source_app,
            SourcePid = (int?)source_pid,
            CaptureMode = Enum.Parse<CaptureMode>(capture_mode),
            SourceLang = source_lang,
            TargetLang = target_lang,
            AsrEngine = asr_engine,
            AsrModel = asr_model,
            OllamaEndpointKind = Enum.TryParse<OllamaEndpointKind>(ollama_url_kind, out var k) ? k : null,
            OllamaModel = ollama_model,
            Status = Enum.Parse<SessionStatus>(status),
            Title = title,
        };
    }

    private sealed record SessionSummaryRow : SessionRow
    {
        public long segment_count { get; init; }
    }

    private sealed record SegmentRow(string id, string session_id, long seq, long t_start_ms, long t_end_ms,
                                     string? text_original, string? text_translated, string? text_translated_edited,
                                     string? lang_detected, double? lang_prob, long is_final, string status,
                                     long? asr_ms, long? llm_ms, string? error_code)
    {
        public SegmentRow() : this("", "", 0, 0, 0, null, null, null, null, null, 1, "", null, null, null) { }

        public static SegmentRow From(Segment s) => new(
            s.Id, s.SessionId, s.Seq, s.StartMs, s.EndMs, s.TextOriginal, s.TextTranslated, s.TextTranslatedEdited,
            s.LangDetected, s.LangProbability, s.IsFinal ? 1 : 0, s.Status.ToString(), s.AsrMs, s.LlmMs, s.Error?.ToString());

        public Segment ToSegment() => new()
        {
            Id = id,
            SessionId = session_id,
            Seq = seq,
            StartMs = t_start_ms,
            EndMs = t_end_ms,
            TextOriginal = text_original,
            TextTranslated = text_translated,
            TextTranslatedEdited = text_translated_edited,
            LangDetected = lang_detected,
            LangProbability = (float?)lang_prob,
            IsFinal = is_final != 0,
            Status = Enum.Parse<SegmentStatus>(status),
            AsrMs = (int?)asr_ms,
            LlmMs = (int?)llm_ms,
            Error = Enum.TryParse<ErrorCode>(error_code, out var e) ? e : null,
        };
    }
#pragma warning restore IDE1006
}

/// <summary>Một thao tác ghi lịch sử (để gom thành lô).</summary>
public abstract record HistoryWrite
{
    public static HistoryWrite Session(Session s) => new SessionWrite(s);
    public static HistoryWrite Segment(Segment s) => new SegmentWrite(s);
    public static HistoryWrite Merged(IReadOnlyList<string> ids, string into) => new MergedWrite(ids, into);

    public sealed record SessionWrite(Session Value) : HistoryWrite;
    public sealed record SegmentWrite(Segment Value) : HistoryWrite;
    public sealed record MergedWrite(IReadOnlyList<string> SegmentIds, string MergedIntoId) : HistoryWrite;
}
