using System.Text;
using Suni.Core.Abstractions;
using Suni.Core.Domain;

namespace Suni.Storage.Exporters;

/// <summary>
/// Xuất SRT (FR-19, TC-09): đánh số liên tục theo seq, timecode tăng dần, không chồng lấn, UTF-8.
/// </summary>
public sealed class SrtExporter(bool writeBom = false) : ISubtitleExporter
{
    public ExportFormat Format => ExportFormat.Srt;

    public async Task ExportAsync(Session session, IReadOnlyList<Segment> segments, ExportContent content,
                                  Stream output, CancellationToken ct = default)
    {
        await using var writer = new StreamWriter(output, new UTF8Encoding(writeBom), leaveOpen: true) { NewLine = "\r\n" };
        await writer.WriteAsync(Build(segments, content).AsMemory(), ct).ConfigureAwait(false);
    }

    public static string Build(IReadOnlyList<Segment> segments, ExportContent content)
    {
        var cues = segments
            .Select(s => (Seg: s, Text: CueText(s, content)))
            .Where(c => c.Text.Length > 0)
            .OrderBy(c => c.Seg.Seq)
            .ToList();

        var sb = new StringBuilder();
        long previousEnd = 0;
        for (var i = 0; i < cues.Count; i++)
        {
            var seg = cues[i].Seg;
            var start = Math.Max(seg.StartMs, previousEnd);
            var end = seg.EndMs;
            if (i + 1 < cues.Count)
                end = Math.Min(end, cues[i + 1].Seg.StartMs);
            if (end <= start)
                end = start + 1;
            previousEnd = end;

            sb.Append(i + 1).Append("\r\n")
              .Append(Timecode(start)).Append(" --> ").Append(Timecode(end)).Append("\r\n")
              .Append(cues[i].Text).Append("\r\n\r\n");
        }
        return sb.ToString();
    }

    public static string Timecode(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        return $"{(int)t.TotalHours:00}:{t.Minutes:00}:{t.Seconds:00},{t.Milliseconds:000}";
    }

    private static string CueText(Segment s, ExportContent content)
    {
        var original = Normalize(s.TextOriginal);
        var translated = Normalize(s.DisplayTranslation);
        return content switch
        {
            ExportContent.OriginalOnly => original,
            ExportContent.TranslationOnly => translated,
            _ => string.Join("\r\n", new[] { original, translated }.Where(x => x.Length > 0)),
        };
    }

    // Dòng trống bên trong cue sẽ làm vỡ cấu trúc SRT.
    private static string Normalize(string? text) =>
        string.Join(" ", (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
