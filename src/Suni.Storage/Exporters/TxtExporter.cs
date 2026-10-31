using System.Text;
using Suni.Core.Abstractions;
using Suni.Core.Domain;

namespace Suni.Storage.Exporters;

public sealed class TxtExporter : ISubtitleExporter
{
    public ExportFormat Format => ExportFormat.Txt;

    public async Task ExportAsync(Session session, IReadOnlyList<Segment> segments, ExportContent content,
                                  Stream output, CancellationToken ct = default)
    {
        await using var writer = new StreamWriter(output, new UTF8Encoding(true), leaveOpen: true) { NewLine = "\r\n" };
        foreach (var s in segments.OrderBy(s => s.Seq))
        {
            var stamp = $"[{TimeSpan.FromMilliseconds(s.StartMs):hh\\:mm\\:ss}] ";
            var pad = new string(' ', stamp.Length);
            string?[] lines = content switch
            {
                ExportContent.OriginalOnly => [s.TextOriginal],
                ExportContent.TranslationOnly => [s.DisplayTranslation],
                _ => [s.TextOriginal, s.DisplayTranslation],
            };
            var first = true;
            foreach (var line in lines.Where(l => !string.IsNullOrWhiteSpace(l)))
            {
                await writer.WriteLineAsync((first ? stamp : pad) + line!.Trim()).ConfigureAwait(false);
                first = false;
            }
        }
    }
}
