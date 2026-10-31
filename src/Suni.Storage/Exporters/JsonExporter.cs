using System.Text.Encodings.Web;
using System.Text.Json;
using Suni.Core.Abstractions;
using Suni.Core.Domain;

namespace Suni.Storage.Exporters;

/// <summary>Xuất JSON UTF-8, mỗi segment theo schema sự kiện SRS 6.3.</summary>
public sealed class JsonExporter : ISubtitleExporter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // giữ nguyên chữ Việt/CJK cho dễ đọc
    };

    public ExportFormat Format => ExportFormat.Json;

    public Task ExportAsync(Session session, IReadOnlyList<Segment> segments, ExportContent content,
                            Stream output, CancellationToken ct = default)
    {
        var doc = new
        {
            session = new
            {
                id = session.Id,
                started_at = session.StartedAt,
                ended_at = session.EndedAt,
                source_app = session.SourceApp,
                source_lang = session.SourceLang ?? "auto",
                target_lang = session.TargetLang,
                asr_model = session.AsrModel,
                ollama_model = session.OllamaModel,
            },
            segments = segments.OrderBy(s => s.Seq).Select(s => new
            {
                segment_id = s.Id,
                seq = s.Seq,
                start_ms = s.StartMs,
                end_ms = s.EndMs,
                source_lang = s.LangDetected,
                target_lang = session.TargetLang,
                original = content == ExportContent.TranslationOnly ? null : s.TextOriginal,
                translation = content == ExportContent.OriginalOnly ? null : s.DisplayTranslation,
                status = s.Status.ToString(),
                asr_latency_ms = s.AsrMs,
                translate_latency_ms = s.LlmMs,
            }),
        };
        return JsonSerializer.SerializeAsync(output, doc, Options, ct);
    }
}
