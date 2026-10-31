using System.Collections.Concurrent;
using Suni.Core.Abstractions;
using Suni.Core.Domain;
using Suni.Core.Events;
using Suni.Core.Settings;

namespace Suni.Pipeline.Tests;

public class PipelineOrchestratorTests
{
    private static readonly AudioSource Source = new(CaptureMode.ProcessLoopback, "Test", RootPid: 1234, ExeName: "test.exe");

    /// <summary>Capture giả: phát N gói 10 ms khi Start (trên thread khác, như WASAPI).</summary>
    private sealed class FakeCapture(int packets) : IAudioCapture
    {
        public AudioFormat Format => new(48_000, 2);
        public event PcmFramesHandler? FramesAvailable;
        public event Action<CaptureState, ErrorCode?>? StateChanged;
        private Task? _pump;

        public Task StartAsync(CancellationToken ct = default)
        {
            StateChanged?.Invoke(CaptureState.Running, null);
            _pump = Task.Run(() =>
            {
                var block = new float[960];
                for (var i = 0; i < packets; i++)
                {
                    Array.Fill(block, i);
                    FramesAvailable?.Invoke(block, i * 480L, false);
                }
            });
            return Task.CompletedTask;
        }

        public Task StopAsync() => _pump ?? Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeCaptureFactory(int packets) : IAudioCaptureFactory
    {
        public IAudioCapture Create(AudioSource source) => new FakeCapture(packets);
    }

    /// <summary>Detector giả: cứ mỗi <paramref name="every"/> gói thì trả một đoạn lời; Flush trả đoạn cuối.</summary>
    private sealed class FakeDetector(int every) : ISpeechDetector
    {
        private int _blocks;
        private long _ms;
        public bool IsSpeaking => _blocks % every != 0;

        public IReadOnlyList<SpeechChunk> Push(ReadOnlySpan<float> interleaved)
        {
            _blocks++;
            return _blocks % every == 0 ? [Chunk()] : [];
        }

        public IReadOnlyList<SpeechChunk> Flush() => _blocks % every != 0 ? [Chunk()] : [];

        private SpeechChunk Chunk()
        {
            var start = _ms;
            _ms += 1_000;
            return new SpeechChunk(start, _ms, new float[16_000], ForcedCut: false, SpeechRatio: 1f);
        }

        public void Dispose() { }
    }

    private sealed class FakeDetectorFactory(int every) : ISpeechDetectorFactory
    {
        public ISpeechDetector Create(AudioFormat captureFormat, VadSettings settings) => new FakeDetector(every);
    }

    /// <summary>ASR giả: trả text theo thứ tự kịch bản, có thể chậm để tạo tồn đọng.</summary>
    private sealed class FakeAsr(Func<int, string> text, TimeSpan delay, TimeSpan loadDelay = default) : IAsrEngine, IAsrEngineProvider
    {
        private int _calls;
        public int Invalidations;

        public Task<AsrInfo> InitializeAsync(AsrOptions options, CancellationToken ct = default) =>
            Task.FromResult(new AsrInfo("fake", "1", "m", 0));

        public async Task<AsrResult> TranscribeAsync(AsrRequest request, CancellationToken ct = default)
        {
            var n = Interlocked.Increment(ref _calls);
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct);
            return new AsrResult(request.SegmentId, text(n), "en", 1f, [], 0.9f, 0.01f, 5);
        }

        public async Task<IAsrEngine> GetAsync(AsrSettings settings, CancellationToken ct = default)
        {
            await Task.Delay(loadDelay, ct); // nạp model
            return this;
        }
        public void Invalidate() => Interlocked.Increment(ref Invalidations);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Engine dịch giả: trả kết quả theo kịch bản, ghi lại các yêu cầu nhận được.</summary>
    private sealed class FakeTranslator(Func<TranslationRequest, TranslationResult> translate) : ITranslationEngine, ITranslationEngineFactory
    {
        public ConcurrentQueue<TranslationRequest> Requests { get; } = new();

        public Task<TranslationResult> TranslateAsync(TranslationRequest request, CancellationToken ct = default)
        {
            Requests.Enqueue(request);
            return Task.FromResult(translate(request));
        }

        public Task<ITranslationEngine?> CreateAsync(TranslationSettings settings, CancellationToken ct = default) =>
            Task.FromResult<ITranslationEngine?>(this);
    }

    private static (PipelineOrchestrator Orchestrator, ConcurrentQueue<PipelineEvent> Events) Create(
        int packets, int every, FakeAsr asr, ITranslationEngineFactory? translator = null)
    {
        var bus = new InMemoryEventBus();
        var events = new ConcurrentQueue<PipelineEvent>();
        bus.Subscribe<PipelineEvent>(events.Enqueue);
        return (new PipelineOrchestrator(new FakeCaptureFactory(packets), new FakeDetectorFactory(every), asr, bus, translator), events);
    }

    [Fact]
    public async Task Finals_AreTranslatedInOrder_WithPreviousSentencesAsContext()
    {
        var asr = new FakeAsr(n => $"sentence {n}", TimeSpan.Zero);
        var translator = new FakeTranslator(r => new TranslationResult(r.SegmentId, $"câu {r.Text[^1]}", null, 7));
        var (orchestrator, events) = Create(packets: 40, every: 10, asr, translator);

        await orchestrator.StartAsync(Source, new AppSettings());
        await orchestrator.StopAsync();

        var translated = events.OfType<SegmentTranslated>().ToList();
        Assert.Equal(["câu 1", "câu 2", "câu 3", "câu 4"], translated.Select(t => t.Segment.TextTranslated));
        Assert.All(translated, t => Assert.Equal(SegmentStatus.Translated, t.Segment.Status));
        Assert.All(translated, t => Assert.Equal(7, t.Segment.LlmMs));

        // Câu gốc được phát trước bản dịch của nó.
        var order = events.ToList();
        foreach (var t in translated)
            Assert.True(order.FindIndex(e => e is SegmentFinal f && f.Segment.Id == t.Segment.Id) < order.IndexOf(t));

        var requests = Translator(translator);
        Assert.Equal(["sentence 2", "sentence 3"], requests[3].Context); // ContextSegments = 2
        Assert.All(requests, r => Assert.Equal("en", r.SourceLang));
        Assert.All(requests, r => Assert.Equal("vi", r.TargetLang));
        Assert.Contains(events, e => e is OllamaStateChanged { State: OllamaState.Connected });
    }

    private static List<TranslationRequest> Translator(FakeTranslator t) => [.. t.Requests];

    /// <summary>Một đoạn theo kịch bản: phát ra ở gói thứ <c>Block</c>; <c>EndsSpeech</c> = VAD báo hết lời ngay sau đó.</summary>
    private sealed record Scripted(int Block, long Start, long End, bool Forced, bool EndsSpeech);

    /// <summary>Detector theo kịch bản: điều khiển chính xác mảnh ép cắt, thời gian và lúc hết lời.</summary>
    private sealed class ScriptedDetector(Scripted[] script) : ISpeechDetector, ISpeechDetectorFactory
    {
        private int _block;
        private bool _speaking;
        public bool IsSpeaking => _speaking;

        public IReadOnlyList<SpeechChunk> Push(ReadOnlySpan<float> interleaved)
        {
            _block++;
            _speaking = true;
            var due = script.Where(s => s.Block == _block).ToList();
            if (due.Any(s => s.EndsSpeech))
                _speaking = false;
            return [.. due.Select(s => new SpeechChunk(s.Start, s.End, new float[160], s.Forced, 1f))];
        }

        public IReadOnlyList<SpeechChunk> Flush() => [];
        public void Dispose() { }
        public ISpeechDetector Create(AudioFormat captureFormat, VadSettings settings) => this;
    }

    private static (PipelineOrchestrator, ConcurrentQueue<PipelineEvent>, FakeTranslator) CreateScripted(
        int packets, Scripted[] script, Func<int, string> asrText)
    {
        var bus = new InMemoryEventBus();
        var events = new ConcurrentQueue<PipelineEvent>();
        bus.Subscribe<PipelineEvent>(events.Enqueue);
        var translator = new FakeTranslator(r => new TranslationResult(r.SegmentId, $"[{r.Text}]", null, 5));
        var orchestrator = new PipelineOrchestrator(new FakeCaptureFactory(packets), new ScriptedDetector(script),
                                                    new FakeAsr(asrText, TimeSpan.Zero), bus, translator);
        return (orchestrator, events, translator);
    }

    [Fact]
    public async Task ForcedCutFragments_AreMergedIntoOneTranslation()
    {
        // Câu 10 s bị ép cắt 2 lần (chồng 300 ms) rồi kết thúc bình thường; sau đó một câu ngắn riêng.
        var (orchestrator, events, translator) = CreateScripted(40,
        [
            new(5, 0, 4_000, Forced: true, EndsSpeech: false),
            new(10, 3_700, 7_700, Forced: true, EndsSpeech: false),
            new(15, 7_400, 9_800, Forced: false, EndsSpeech: true),
            new(30, 12_000, 13_000, Forced: false, EndsSpeech: true),
        ], n => n switch { 1 => "The translated subtitles", 2 => "will appear sentence by", 3 => "sentence.", _ => "Press start." });

        await orchestrator.StartAsync(Source, new AppSettings());
        await orchestrator.StopAsync();

        Assert.Equal(["The translated subtitles will appear sentence by sentence.", "Press start."], translator.Requests.Select(r => r.Text));
        Assert.Equal(["The translated subtitles will appear sentence by sentence."], translator.Requests.Last().Context);

        var translated = events.OfType<SegmentTranslated>().ToList();
        Assert.Equal(2, translated.Count);
        var finals = events.OfType<SegmentFinal>().Select(f => f.Segment.Id).ToList();
        Assert.Equal(finals.Take(3), translated[0].MergedSegmentIds!);       // gắn đủ 3 mảnh, đúng thứ tự
        Assert.Equal(finals[2], translated[0].Segment.Id);                    // bản dịch gắn vào mảnh cuối
        Assert.Equal("The translated subtitles will appear sentence by sentence.", translated[0].Segment.TextOriginal);
        Assert.Null(translated[1].MergedSegmentIds);
    }

    [Fact]
    public async Task ChainWhoseTailWasDropped_IsTranslatedAtEndOfSpeech()
    {
        // Mảnh cuối chỉ là phần chồng → VAD không gửi; "hết lời" phải đóng câu, không chờ tới câu sau.
        var (orchestrator, events, translator) = CreateScripted(30,
        [
            new(5, 0, 4_000, Forced: true, EndsSpeech: false),
            new(10, 3_700, 7_700, Forced: true, EndsSpeech: true),
            new(25, 10_000, 11_000, Forced: false, EndsSpeech: true),
        ], n => n switch { 1 => "first part", 2 => "second part", _ => "next sentence" });

        await orchestrator.StartAsync(Source, new AppSettings());
        await orchestrator.StopAsync();

        Assert.Equal(["first part second part", "next sentence"], translator.Requests.Select(r => r.Text));
    }

    [Fact]
    public async Task NonOverlappingSegment_StartsNewSentence_EvenWithoutEndOfSpeechMarker()
    {
        var (orchestrator, _, translator) = CreateScripted(30,
        [
            new(5, 0, 4_000, Forced: true, EndsSpeech: false),
            new(10, 6_000, 7_000, Forced: false, EndsSpeech: true), // bắt đầu sau khi mảnh trước kết thúc → câu khác
        ], n => n == 1 ? "a" : "b");

        await orchestrator.StartAsync(Source, new AppSettings());
        await orchestrator.StopAsync();

        Assert.Equal(["a", "b"], translator.Requests.Select(r => r.Text));
    }

    [Fact]
    public async Task PendingFragment_IsTranslatedAfterWaitLimit_WithoutStopping()
    {
        // Không có mảnh tiếp theo, không có dấu hết lời (VAD vẫn "đang nói"): giới hạn chờ = max_chunk + 2 s.
        var (orchestrator, events, translator) = CreateScripted(10,
            [new(5, 0, 100, Forced: true, EndsSpeech: false)], _ => "lonely fragment");
        var defaults = new AppSettings();

        await orchestrator.StartAsync(Source, defaults with { Vad = defaults.Vad with { MaxChunkSec = 0.1 } });
        await Task.Delay(TimeSpan.FromSeconds(3));
        var beforeStop = translator.Requests.Count;
        await orchestrator.StopAsync();

        Assert.Equal(1, beforeStop);
        Assert.Single(events.OfType<SegmentTranslated>());
    }

    [Fact]
    public async Task SourceAlreadyInTargetLanguage_IsNotSentToLlm()
    {
        var asr = new FakeAsr(n => $"câu {n}", TimeSpan.Zero);
        var translator = new FakeTranslator(r => throw new InvalidOperationException("không được gọi"));
        var (orchestrator, events) = Create(packets: 20, every: 10, asr, translator);
        var settings = new AppSettings { Translation = new TranslationSettings { TargetLang = "en" } }; // FakeAsr trả "en"

        await orchestrator.StartAsync(Source, settings);
        await orchestrator.StopAsync();

        Assert.Empty(translator.Requests);
        Assert.Equal(["câu 1", "câu 2"], events.OfType<SegmentTranslated>().Select(t => t.Segment.TextTranslated));
    }

    [Fact]
    public async Task OllamaDown_OpensBreaker_OriginalsStillShown()
    {
        var asr = new FakeAsr(n => $"sentence {n}", TimeSpan.Zero);
        var translator = new FakeTranslator(r => new TranslationResult(r.SegmentId, null, ErrorCode.LlmUnreachable, 3));
        var (orchestrator, events) = Create(packets: 80, every: 10, asr, translator);

        await orchestrator.StartAsync(Source, new AppSettings());
        await orchestrator.StopAsync();

        Assert.Equal(8, events.OfType<SegmentFinal>().Count());                      // câu gốc vẫn đủ
        var failed = events.OfType<SegmentFailed>().Where(f => f.Segment.Status == SegmentStatus.TranslateFailed).ToList();
        Assert.Equal(8, failed.Count);                                                // câu nào cũng có kết cục
        Assert.Equal(3, translator.Requests.Count);                                   // sau 3 lỗi: ngắt mạch, không gọi nữa
        Assert.Single(events.OfType<OllamaStateChanged>(), e => e.State == OllamaState.Disconnected);
    }

    [Fact]
    public async Task BadOutput_DoesNotOpenBreaker()
    {
        var asr = new FakeAsr(n => $"sentence {n}", TimeSpan.Zero);
        var translator = new FakeTranslator(r => new TranslationResult(r.SegmentId, null, ErrorCode.LlmBadOutput, 3));
        var (orchestrator, events) = Create(packets: 50, every: 10, asr, translator);

        await orchestrator.StartAsync(Source, new AppSettings());
        await orchestrator.StopAsync();

        Assert.Equal(5, translator.Requests.Count); // lỗi output không phải lỗi kết nối → vẫn thử từng câu
        Assert.DoesNotContain(events, e => e is OllamaStateChanged { State: OllamaState.Disconnected });
    }

    [Fact]
    public async Task NoTranslatorConfigured_OnlyOriginals()
    {
        var asr = new FakeAsr(n => $"s{n}", TimeSpan.Zero);
        var (orchestrator, events) = Create(packets: 20, every: 10, asr, translator: null);

        await orchestrator.StartAsync(Source, new AppSettings());
        await orchestrator.StopAsync();

        Assert.Equal(2, events.OfType<SegmentFinal>().Count());
        Assert.Empty(events.OfType<SegmentTranslated>());
        Assert.DoesNotContain(events, e => e is SegmentFailed { Segment.Status: SegmentStatus.TranslateFailed });
    }

    [Fact]
    public async Task Chunks_AreTranscribedInOrder_AndHallucinationsDropped()
    {
        // 50 gói, mỗi 10 gói một đoạn → 5 đoạn; đoạn 3 là "bịa" của whisper.
        var asr = new FakeAsr(n => n == 3 ? "[BLANK_AUDIO]" : $"sentence {n}", TimeSpan.Zero);
        var (orchestrator, events) = Create(packets: 50, every: 10, asr);

        var session = await orchestrator.StartAsync(Source, new AppSettings());
        await orchestrator.StopAsync();

        var finals = events.OfType<SegmentFinal>().ToList();
        Assert.Equal(["sentence 1", "sentence 2", "sentence 4", "sentence 5"], finals.Select(f => f.Segment.TextOriginal));
        Assert.All(finals, f => Assert.Equal(SegmentStatus.AsrDone, f.Segment.Status));
        Assert.All(finals, f => Assert.Equal(session.Id, f.Segment.SessionId));
        Assert.Equal([1L, 2, 4, 5], finals.Select(f => f.Segment.Seq));

        var dropped = Assert.Single(events.OfType<SegmentFailed>());
        Assert.Equal(ErrorCode.AsrEmpty, dropped.Error);
        Assert.Equal(SegmentStatus.Dropped, dropped.Segment.Status);

        Assert.Contains(events, e => e is CaptureStateChanged { State: CaptureState.Stopped });
        Assert.Contains(events, e => e is AudioLevel);
        Assert.Null(orchestrator.CurrentSession);
    }

    [Fact]
    public async Task AudioDuringModelLoad_IsNotLost()
    {
        // Capture phát toàn bộ 30 gói ngay khi Start, trong lúc model còn đang nạp 300 ms.
        var asr = new FakeAsr(n => $"s{n}", TimeSpan.Zero, loadDelay: TimeSpan.FromMilliseconds(300));
        var (orchestrator, events) = Create(packets: 30, every: 10, asr);

        await orchestrator.StartAsync(Source, new AppSettings());
        await orchestrator.StopAsync();

        Assert.Equal(["s1", "s2", "s3"], events.OfType<SegmentFinal>().Select(f => f.Segment.TextOriginal));
    }

    [Fact]
    public async Task Stop_FlushesOpenSpeech()
    {
        // 15 gói, đoạn mỗi 10 gói → 1 đoạn trong lúc chạy + 1 đoạn dở được Flush khi dừng.
        var asr = new FakeAsr(n => $"s{n}", TimeSpan.Zero);
        var (orchestrator, events) = Create(packets: 15, every: 10, asr);

        await orchestrator.StartAsync(Source, new AppSettings());
        await orchestrator.StopAsync();

        Assert.Equal(2, events.OfType<SegmentFinal>().Count());
    }

    [Fact]
    public async Task SlowAsr_DropsOldestSpeech_WithPipeOverload()
    {
        // 40 đoạn đổ vào gần như cùng lúc, hàng đợi 2 → phần lớn bị bỏ; ASR 20 ms/đoạn.
        var asr = new FakeAsr(n => $"s{n}", TimeSpan.FromMilliseconds(20));
        var (orchestrator, events) = Create(packets: 40, every: 1, asr);
        var settings = new AppSettings { Pipeline = new PipelineSettings { SpeechQueueMax = 2 } };

        await orchestrator.StartAsync(Source, settings);
        await orchestrator.StopAsync();

        var finals = events.OfType<SegmentFinal>().ToList();
        var overloaded = events.OfType<SegmentFailed>().Where(f => f.Error == ErrorCode.PipeOverload).ToList();
        Assert.NotEmpty(overloaded);
        Assert.Equal(40, finals.Count + overloaded.Count);
        Assert.Equal(40, finals.Max(f => f.Segment.Seq)); // đoạn mới nhất luôn được giữ
        Assert.True(finals.Select(f => f.Segment.Seq).SequenceEqual(finals.Select(f => f.Segment.Seq).Order()));
        Assert.Contains(events, e => e is PipelineHealth { IsLagging: true });
    }

    [Fact]
    public async Task EndOfSpeechMarkers_DoNotEvictSpeech_WhenAsrStartsLate()
    {
        // Hồi quy: dấu "hết câu" từng là phần tử riêng trong hàng đợi giới hạn 8 → khi ASR khởi động chậm (máy bận),
        // 8 đoạn lời + 8 dấu làm rơi mất các đoạn lời cũ nhất. Ở đây model "nạp" 300 ms trong lúc 8 câu đã nói xong.
        var asr = new FakeAsr(n => $"s{n}", TimeSpan.Zero, loadDelay: TimeSpan.FromMilliseconds(300));
        var (orchestrator, events) = Create(packets: 80, every: 10, asr);

        await orchestrator.StartAsync(Source, new AppSettings());
        await orchestrator.StopAsync();

        Assert.Equal(8, events.OfType<SegmentFinal>().Count());
        Assert.DoesNotContain(events, e => e is SegmentFailed { Error: ErrorCode.PipeOverload });
    }

    [Fact]
    public async Task DisposeTwice_DoesNotThrow()
    {
        // App gọi DisposeAsync khi thoát, sau đó DI container dispose singleton lần nữa (từng làm app crash khi đóng).
        var (orchestrator, _) = Create(packets: 5, every: 10, new FakeAsr(n => "x", TimeSpan.Zero));
        await orchestrator.StartAsync(Source, new AppSettings());

        await orchestrator.DisposeAsync();
        await orchestrator.DisposeAsync();
    }

    [Fact]
    public async Task StartingAgain_StopsPreviousSession()
    {
        var asr = new FakeAsr(n => $"s{n}", TimeSpan.Zero);
        var (orchestrator, events) = Create(packets: 5, every: 10, asr);

        var first = await orchestrator.StartAsync(Source, new AppSettings());
        var second = await orchestrator.StartAsync(Source, new AppSettings());
        await orchestrator.StopAsync();

        Assert.NotEqual(first.Id, second.Id);
        Assert.Contains(events, e => e is CaptureStateChanged { State: CaptureState.Stopped } s && s.SessionId == first.Id);
        Assert.Contains(events, e => e is CaptureStateChanged { State: CaptureState.Stopped } s && s.SessionId == second.Id);
    }
}
