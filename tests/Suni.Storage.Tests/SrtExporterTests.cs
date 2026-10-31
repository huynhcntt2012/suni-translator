using System.Text;
using Suni.Core.Abstractions;
using Suni.Core.Domain;
using Suni.Storage.Exporters;

namespace Suni.Storage.Tests;

public class SrtExporterTests
{
    private static Segment Seg(long seq, long start, long end, string? original, string? translated) => new()
    {
        Id = $"seg_{seq}", SessionId = "sess", Seq = seq, StartMs = start, EndMs = end,
        TextOriginal = original, TextTranslated = translated, Status = SegmentStatus.Displayed,
    };

    [Theory]
    [InlineData(0, "00:00:00,000")]
    [InlineData(12_500, "00:00:12,500")]
    [InlineData(3_723_004, "01:02:03,004")]
    [InlineData(-5, "00:00:00,000")]
    public void Timecode_Format(long ms, string expected) => Assert.Equal(expected, SrtExporter.Timecode(ms));

    [Fact]
    public void Bilingual_JapaneseVietnamese()
    {
        var srt = SrtExporter.Build([Seg(1, 12_500, 15_600, "こんにちは", "Xin chào")], ExportContent.Bilingual);

        Assert.Equal("1\r\n00:00:12,500 --> 00:00:15,600\r\nこんにちは\r\nXin chào\r\n\r\n", srt);
    }

    [Fact]
    public void Cues_AreRenumberedInSeqOrder_AndEmptyOnesSkipped()
    {
        var srt = SrtExporter.Build(
        [
            Seg(3, 6_000, 7_000, "c", "C"),
            Seg(1, 1_000, 2_000, "a", "A"),
            Seg(2, 3_000, 4_000, null, null),
        ], ExportContent.Bilingual);

        var lines = srt.Split("\r\n");
        Assert.Equal("1", lines[0]);
        Assert.Equal("a", lines[2]);
        Assert.Equal("2", lines[5]);
        Assert.Equal("c", lines[7]);
        Assert.DoesNotContain("\r\n3\r\n", srt);
    }

    [Fact]
    public void OverlappingSegments_AreClampedToIncreasingTimecodes()
    {
        var srt = SrtExporter.Build(
        [
            Seg(1, 1_000, 4_300, "a", null),   // overlap 300 ms với đoạn sau
            Seg(2, 4_000, 6_000, "b", null),
        ], ExportContent.OriginalOnly);

        Assert.Contains("00:00:01,000 --> 00:00:04,000", srt);
        Assert.Contains("00:00:04,000 --> 00:00:06,000", srt);
    }

    [Fact]
    public void TranslationOnly_PrefersEditedText_AndFlattensNewlines()
    {
        var seg = Seg(1, 0, 1_000, "x", "máy") with { TextTranslatedEdited = "dòng 1\n\ndòng 2" };
        var srt = SrtExporter.Build([seg], ExportContent.TranslationOnly);

        Assert.Contains("dòng 1 dòng 2\r\n", srt);
        Assert.DoesNotContain("máy", srt);
    }

    [Fact]
    public async Task ExportAsync_WritesUtf8WithoutBomByDefault()
    {
        var session = new Session
        {
            Id = "sess", StartedAt = DateTimeOffset.UnixEpoch, CaptureMode = CaptureMode.ProcessLoopback, TargetLang = "vi",
        };
        using var ms = new MemoryStream();
        await new SrtExporter().ExportAsync(session, [Seg(1, 0, 1_000, "日本語", "Tiếng Việt")], ExportContent.Bilingual, ms);

        var bytes = ms.ToArray();
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Contains("日本語\r\nTiếng Việt", Encoding.UTF8.GetString(bytes));
    }
}
