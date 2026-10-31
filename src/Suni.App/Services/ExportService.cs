using System.IO;
using System.Windows;
using Microsoft.Win32;
using Suni.Core.Abstractions;
using Suni.Core.Domain;

namespace Suni.App.Services;

/// <summary>Xuất một phiên ra TXT / SRT / JSON (FR-19) qua hộp thoại lưu file.</summary>
public sealed class ExportService(IEnumerable<ISubtitleExporter> exporters)
{
    /// <returns>Đường dẫn đã lưu, hoặc null nếu người dùng huỷ.</returns>
    public async Task<string?> ExportAsync(Session session, IReadOnlyList<Segment> segments, ExportFormat format,
                                           ExportContent content, Window? owner)
    {
        var exporter = exporters.First(e => e.Format == format);
        var ext = format.ToString().ToLowerInvariant();
        var app = Path.GetFileNameWithoutExtension(session.SourceApp ?? "system");
        var dialog = new SaveFileDialog
        {
            FileName = $"suni_{session.StartedAt:yyyyMMdd_HHmm}_{app}.{ext}",
            DefaultExt = "." + ext,
            Filter = format switch
            {
                ExportFormat.Srt => "Phụ đề SRT (*.srt)|*.srt",
                ExportFormat.Json => "JSON (*.json)|*.json",
                _ => "Văn bản (*.txt)|*.txt",
            },
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(owner) != true)
            return null;

        await using (var stream = File.Create(dialog.FileName))
            await exporter.ExportAsync(session, segments, content, stream);
        return dialog.FileName;
    }
}
