using Suni.Core.Domain;

namespace Suni.Core.Abstractions;

/// <summary>Một dòng trong danh sách lịch sử: phiên kèm số câu đã lưu.</summary>
public sealed record SessionSummary(Session Session, int SegmentCount);

public interface ISessionRepository
{
    Task UpsertAsync(Session session, CancellationToken ct = default);
    Task<Session?> GetAsync(string id, CancellationToken ct = default);

    /// <summary>Phiên mới nhất trước; lọc theo khoảng ngày và ứng dụng nguồn (FR-18, SRS 3.2 History).</summary>
    Task<IReadOnlyList<SessionSummary>> ListAsync(DateTimeOffset? from = null, DateTimeOffset? to = null,
                                                  string? sourceApp = null, CancellationToken ct = default);
    Task DeleteAsync(string id, CancellationToken ct = default);
    Task DeleteAllAsync(CancellationToken ct = default);
}

public interface ISegmentRepository
{
    /// <summary>Insert hoặc cập nhật theo (session_id, seq) — không nhân bản khi recover.</summary>
    Task UpsertAsync(Segment segment, CancellationToken ct = default);

    /// <summary>
    /// Lưu bản dịch người dùng sửa tay (FR-13); bản máy dịch giữ nguyên. Null, rỗng hoặc trùng bản máy = bỏ bản sửa.
    /// Trả về segment sau khi cập nhật, null nếu không tìm thấy.
    /// </summary>
    Task<Segment?> SetEditedTranslationAsync(string segmentId, string? text, CancellationToken ct = default);

    /// <summary>Đánh dấu các mảnh đã gộp vào mảnh cuối của câu (không còn xuất riêng).</summary>
    Task MarkMergedAsync(IReadOnlyList<string> segmentIds, string mergedIntoId, CancellationToken ct = default);

    /// <summary>Các câu để hiển thị/xuất: theo seq, bỏ mảnh đã gộp và đoạn bị loại.</summary>
    Task<IReadOnlyList<Segment>> ListBySessionAsync(string sessionId, CancellationToken ct = default);
    Task<IReadOnlyList<Segment>> ListPendingTranslationAsync(string sessionId, CancellationToken ct = default);

    /// <summary>Tìm trong câu gốc và bản dịch (Việt/CJK) của mọi phiên, mới nhất trước.</summary>
    Task<IReadOnlyList<Segment>> SearchAsync(string query, int limit = 200, CancellationToken ct = default);
}

public interface IGlossaryRepository
{
    Task<IReadOnlyList<GlossaryEntry>> GetAsync(string sourceLang, string targetLang, CancellationToken ct = default);
}

public enum ExportFormat { Txt, Srt, Json }

public enum ExportContent { Bilingual, OriginalOnly, TranslationOnly }

public interface ISubtitleExporter
{
    ExportFormat Format { get; }

    Task ExportAsync(Session session, IReadOnlyList<Segment> segments, ExportContent content,
                     Stream output, CancellationToken ct = default);
}
