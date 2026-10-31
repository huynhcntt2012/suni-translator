using Suni.Core.Abstractions;
using Suni.Core.Domain;
using Suni.Core.Events;
using Suni.Core.Settings;
using Suni.Pipeline;

namespace Suni.Storage.Tests;

public class HistoryStoreTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"suni_hist_{Guid.NewGuid():N}");
    private HistoryStore _store = null!;

    public async Task InitializeAsync()
    {
        _store = new HistoryStore(SuniDatabase.ForFile(Path.Combine(_dir, "suni.db")));
        await _store.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
        return Task.CompletedTask;
    }

    private static Session NewSession(string app = "chrome.exe", DateTimeOffset? at = null) => new()
    {
        Id = Ids.NewSessionId(),
        StartedAt = at ?? DateTimeOffset.Now,
        CaptureMode = CaptureMode.ProcessLoopback,
        SourceApp = app,
        SourcePid = 1234,
        TargetLang = "vi",
        AsrModel = "large-v3-turbo-q5_0",
        OllamaModel = "qwen3.5:9b",
    };

    private static Segment Seg(Session s, long seq, string? original, string? translated = null,
                               SegmentStatus status = SegmentStatus.AsrDone, string? id = null) => new()
    {
        Id = id ?? Ids.NewSegmentId(),
        SessionId = s.Id,
        Seq = seq,
        StartMs = seq * 1_000,
        EndMs = seq * 1_000 + 900,
        TextOriginal = original,
        TextTranslated = translated,
        LangDetected = "en",
        Status = status,
        AsrMs = 400,
    };

    [Fact]
    public async Task Session_And_Segments_RoundTrip()
    {
        var s = NewSession();
        await _store.UpsertAsync(s);
        await _store.UpsertAsync(Seg(s, 2, "Second.", "Thứ hai.", SegmentStatus.Translated));
        await _store.UpsertAsync(Seg(s, 1, "First.", "Thứ nhất.", SegmentStatus.Translated));

        var loaded = await _store.GetAsync(s.Id);
        Assert.Equal(s.SourceApp, loaded!.SourceApp);
        Assert.Equal(s.StartedAt.ToUnixTimeMilliseconds(), loaded.StartedAt.ToUnixTimeMilliseconds());
        Assert.Equal(CaptureMode.ProcessLoopback, loaded.CaptureMode);

        var segs = await _store.ListBySessionAsync(s.Id);
        Assert.Equal(["First.", "Second."], segs.Select(x => x.TextOriginal));
        Assert.Equal("Thứ nhất.", segs[0].TextTranslated);
        Assert.Equal(SegmentStatus.Translated, segs[0].Status);
        Assert.Equal(400, segs[0].AsrMs);
    }

    [Fact]
    public async Task LaterUpsert_FillsTranslation_WithoutLosingOriginal()
    {
        var s = NewSession();
        await _store.UpsertAsync(s);
        var id = Ids.NewSegmentId();
        await _store.UpsertAsync(Seg(s, 1, "Hello.", id: id));                                  // segment.final
        await _store.UpsertAsync(Seg(s, 1, null, "Xin chào.", SegmentStatus.Translated, id) with { LlmMs = 300 }); // segment.translated

        var seg = Assert.Single(await _store.ListBySessionAsync(s.Id));
        Assert.Equal("Hello.", seg.TextOriginal);
        Assert.Equal("Xin chào.", seg.TextTranslated);
        Assert.Equal(300, seg.LlmMs);
        Assert.Equal(400, seg.AsrMs);
    }

    [Fact]
    public async Task MergedFragments_AreHidden_AndLastCarriesWholeSentence()
    {
        var s = NewSession();
        await _store.UpsertAsync(s);
        var a = Seg(s, 1, "The translated subtitles");
        var b = Seg(s, 2, "will appear sentence by");
        var c = Seg(s, 3, "sentence.");
        foreach (var x in new[] { a, b, c })
            await _store.UpsertAsync(x);

        await _store.WriteBatchAsync(
        [
            HistoryWrite.Segment(c with
            {
                TextOriginal = "The translated subtitles will appear sentence by sentence.",
                TextTranslated = "Phụ đề dịch sẽ xuất hiện theo từng câu.",
                StartMs = a.StartMs,
                Status = SegmentStatus.Translated,
            }),
            HistoryWrite.Merged([a.Id, b.Id, c.Id], c.Id),
        ]);
        await _store.UpsertAsync(b with { Status = SegmentStatus.TranslateFailed }); // sự kiện tới muộn không "hồi sinh" mảnh đã gộp

        var seg = Assert.Single(await _store.ListBySessionAsync(s.Id));
        Assert.Equal(c.Id, seg.Id);
        Assert.Equal(a.StartMs, seg.StartMs);
        Assert.Equal("Phụ đề dịch sẽ xuất hiện theo từng câu.", seg.TextTranslated);
        Assert.Equal(1, (await _store.ListAsync()).Single().SegmentCount);
    }

    [Fact]
    public async Task EditedTranslation_IsKeptSeparately_AndUsedByExportAndSearch()
    {
        var s = NewSession();
        await _store.UpsertAsync(s);
        var seg = Seg(s, 1, "Press start.", "Bấm Bắt đầu.", SegmentStatus.Translated);
        await _store.UpsertAsync(seg);

        var edited = await _store.SetEditedTranslationAsync(seg.Id, "  Nhấn nút Start.  ");

        Assert.Equal("Nhấn nút Start.", edited!.TextTranslatedEdited);
        Assert.Equal("Bấm Bắt đầu.", edited.TextTranslated);           // bản máy giữ nguyên
        Assert.Equal("Nhấn nút Start.", edited.DisplayTranslation);
        var listed = Assert.Single(await _store.ListBySessionAsync(s.Id));
        Assert.Contains("Nhấn nút Start.", Exporters.SrtExporter.Build([listed], ExportContent.TranslationOnly));
        Assert.Single(await _store.SearchAsync("nút Start"));

        // Sự kiện pipeline tới muộn (upsert lại) không được ghi đè bản sửa.
        await _store.UpsertAsync(seg with { TextTranslated = "Bấm Bắt đầu!", LlmMs = 9 });
        Assert.Equal("Nhấn nút Start.", (await _store.ListBySessionAsync(s.Id))[0].TextTranslatedEdited);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Bấm Bắt đầu.")] // trùng bản máy
    public async Task EditedTranslation_RevertsWhenEmptyOrSameAsMachine(string? text)
    {
        var s = NewSession();
        await _store.UpsertAsync(s);
        var seg = Seg(s, 1, "Press start.", "Bấm Bắt đầu.", SegmentStatus.Translated);
        await _store.UpsertAsync(seg);
        await _store.SetEditedTranslationAsync(seg.Id, "Nhấn Start.");

        var reverted = await _store.SetEditedTranslationAsync(seg.Id, text);

        Assert.Null(reverted!.TextTranslatedEdited);
        Assert.Equal("Bấm Bắt đầu.", reverted.DisplayTranslation);
    }

    [Fact]
    public async Task EditedTranslation_CanFillFailedTranslation()
    {
        var s = NewSession();
        await _store.UpsertAsync(s);
        var seg = Seg(s, 1, "Press start.", null, SegmentStatus.TranslateFailed);
        await _store.UpsertAsync(seg);

        var edited = await _store.SetEditedTranslationAsync(seg.Id, "Bấm Bắt đầu.");

        Assert.Equal("Bấm Bắt đầu.", edited!.DisplayTranslation);
        Assert.Null(await _store.SetEditedTranslationAsync("seg_khong_ton_tai", "x"));
    }

    [Fact]
    public async Task List_NewestFirst_WithFilters_AndCounts()
    {
        var old = NewSession("discord.exe", DateTimeOffset.Now.AddDays(-3));
        var recent = NewSession("chrome.exe");
        await _store.UpsertAsync(old);
        await _store.UpsertAsync(recent);
        await _store.UpsertAsync(Seg(recent, 1, "a"));
        await _store.UpsertAsync(Seg(recent, 2, "b"));
        await _store.UpsertAsync(Seg(recent, 3, null, status: SegmentStatus.Dropped));

        var all = await _store.ListAsync();
        Assert.Equal([recent.Id, old.Id], all.Select(x => x.Session.Id));
        Assert.Equal(2, all[0].SegmentCount);

        Assert.Equal([old.Id], (await _store.ListAsync(sourceApp: "discord.exe")).Select(x => x.Session.Id));
        Assert.Equal([recent.Id], (await _store.ListAsync(from: DateTimeOffset.Now.AddDays(-1))).Select(x => x.Session.Id));
    }

    [Theory]
    [InlineData("tính năng")]   // tiếng Việt, FTS trigram
    [InlineData("使い方")]       // tiếng Nhật, 3 ký tự
    [InlineData("日本")]         // 2 ký tự → LIKE
    [InlineData("feature")]
    [InlineData("FEATURE")]
    public async Task Search_FindsVietnameseCjkAndEnglish(string query)
    {
        var s = NewSession();
        await _store.UpsertAsync(s);
        await _store.UpsertAsync(Seg(s, 1, "この機能の使い方を日本語で説明します。", "Tôi sẽ giải thích cách dùng tính năng này."));
        await _store.UpsertAsync(Seg(s, 2, "Explain this feature.", "Giải thích."));
        await _store.UpsertAsync(Seg(s, 3, "Unrelated.", "Không liên quan."));

        var hits = await _store.SearchAsync(query);

        Assert.NotEmpty(hits);
        Assert.DoesNotContain(hits, h => h.TextOriginal == "Unrelated.");
    }

    [Fact]
    public async Task Search_HandlesQuotesAndWildcards() =>
        Assert.Empty(await _store.SearchAsync("\"%_\\"));

    [Fact]
    public async Task Delete_RemovesSessionAndItsSegments()
    {
        var s = NewSession();
        var keep = NewSession();
        await _store.UpsertAsync(s);
        await _store.UpsertAsync(keep);
        await _store.UpsertAsync(Seg(s, 1, "gone"));
        await _store.UpsertAsync(Seg(keep, 1, "stays"));

        await _store.DeleteAsync(s.Id);

        Assert.Null(await _store.GetAsync(s.Id));
        Assert.Empty(await _store.SearchAsync("gone"));
        Assert.Single(await _store.SearchAsync("stays"));

        await _store.DeleteAllAsync();
        Assert.Empty(await _store.ListAsync());
        Assert.Empty(await _store.SearchAsync("stays"));
    }

    [Fact]
    public async Task Recorder_PersistsSessionFromEvents_IncludingMergedSentences()
    {
        var bus = new InMemoryEventBus();
        await using var recorder = new HistoryRecorder(_store, bus);
        var s = NewSession();
        var a = Seg(s, 1, "first part") with { Status = SegmentStatus.AsrDone };
        var b = Seg(s, 2, "second part") with { Status = SegmentStatus.AsrDone };

        bus.Publish(new SessionStarted(s, new AppSettings()));
        bus.Publish(new SegmentFinal(a, "vi"));
        bus.Publish(new SegmentFinal(b, "vi"));
        bus.Publish(new SegmentTranslated(b with
        {
            TextOriginal = "first part second part", TextTranslated = "phần một phần hai",
            StartMs = a.StartMs, Status = SegmentStatus.Translated,
        }, [a.Id, b.Id]));
        bus.Publish(new SessionEnded(s with { EndedAt = DateTimeOffset.Now, Status = SessionStatus.Stopped }));
        await recorder.FlushAsync();

        var saved = await _store.GetAsync(s.Id);
        Assert.Equal(SessionStatus.Stopped, saved!.Status);
        Assert.NotNull(saved.EndedAt);
        var seg = Assert.Single(await _store.ListBySessionAsync(s.Id));
        Assert.Equal("first part second part", seg.TextOriginal);
        Assert.Equal("phần một phần hai", seg.TextTranslated);
    }

    [Fact]
    public async Task Recorder_RespectsSaveTranscriptOff()
    {
        var bus = new InMemoryEventBus();
        await using var recorder = new HistoryRecorder(_store, bus);
        var s = NewSession();
        var settings = new AppSettings { Privacy = new PrivacySettings { SaveTranscript = false } };

        bus.Publish(new SessionStarted(s, settings));
        bus.Publish(new SegmentFinal(Seg(s, 1, "secret"), "vi"));
        bus.Publish(new SessionEnded(s));
        await recorder.FlushAsync();

        Assert.Empty(await _store.ListAsync());
        Assert.Empty(await _store.SearchAsync("secret"));
    }
}
