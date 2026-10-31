using Suni.Core.Domain;

namespace Suni.Core.Abstractions;

public enum TranslationStyle { Natural, Literal }

public sealed record GlossaryEntry(string SourceTerm, string TargetTerm, bool CaseSensitive = false,
                                   string? SourceLang = null, string? TargetLang = null);

public sealed record TranslationRequest(
    string SegmentId,
    string Text,
    string SourceLang,
    string TargetLang,
    TranslationStyle Style,
    IReadOnlyList<string> Context,
    IReadOnlyList<GlossaryEntry> Glossary);

public sealed record TranslationResult(string SegmentId, string? Translation, ErrorCode? Error, int ElapsedMs)
{
    public bool IsSuccess => Translation is not null && Error is null;
}

public interface ITranslationEngine
{
    Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct = default);
}

public interface ITranslationEngineFactory
{
    /// <summary>
    /// Tạo engine cho cấu hình hiện tại; null nếu chưa cấu hình (chỉ hiện câu gốc).
    /// Không ném lỗi khi server chưa sẵn sàng — lỗi kết nối được báo theo từng yêu cầu dịch.
    /// </summary>
    Task<ITranslationEngine?> CreateAsync(Settings.TranslationSettings settings, CancellationToken ct = default);
}
