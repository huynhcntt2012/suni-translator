using System.Buffers;
using System.Diagnostics;
using System.Threading.Channels;
using Suni.Asr;
using Suni.Core.Abstractions;
using Suni.Core.Domain;
using Suni.Core.Events;
using Suni.Core.Settings;

namespace Suni.Pipeline;

/// <summary>
/// Điều phối một phiên (SDD 6): capture → (channel PCM) → thread tiền xử lý/VAD → (channel đoạn lời, bounded,
/// bỏ đoạn cũ nhất khi quá tải) → ASR tuần tự → (channel câu gốc) → dịch Ollama có ngắt mạch → sự kiện cho UI.
/// Câu gốc được phát ngay sau ASR; bản dịch điền vào sau. Lưu SQLite sẽ nối tiếp sau.
/// </summary>
public sealed class PipelineOrchestrator(
    IAudioCaptureFactory captureFactory,
    ISpeechDetectorFactory detectorFactory,
    IAsrEngineProvider asrProvider,
    IEventBus bus,
    ITranslationEngineFactory? translationFactory = null) : IPipelineOrchestrator, IAsyncDisposable
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Số lỗi kết nối liên tiếp trước khi ngắt mạch; khi ngắt chỉ thử lại sau <see cref="ProbeInterval"/>.</summary>
    private const int BreakerThreshold = 3;
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(5);
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private Run? _run;

    public Session? CurrentSession => _run?.Session;

    public async Task<Session> StartAsync(AudioSource source, AppSettings settings, CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);

            var session = new Session
            {
                Id = Ids.NewSessionId(),
                StartedAt = DateTimeOffset.Now,
                CaptureMode = source.Mode,
                SourceApp = source.ExeName ?? source.DisplayName,
                SourcePid = source.RootPid,
                SourceLang = settings.Asr.SourceLang,
                TargetLang = settings.Translation.TargetLang,
                AsrEngine = settings.Asr.Engine,
                AsrModel = settings.Asr.Model,
                OllamaModel = string.IsNullOrEmpty(settings.Translation.Model) ? null : settings.Translation.Model,
            };
            bus.Publish(new CaptureStateChanged(session.Id, CaptureState.Starting, Detail: "Đang nạp model ASR…"));

            var capture = captureFactory.Create(source);
            var detector = detectorFactory.Create(capture.Format, settings.Vad);
            var run = new Run(session, settings, capture, detector);
            run.DroppedSpeech = dropped => bus.Publish(new SegmentFailed(
                dropped.Fail(SegmentStatus.Dropped, ErrorCode.PipeOverload), ErrorCode.PipeOverload));
            run.DroppedTranslation = dropped => bus.Publish(new SegmentFailed(
                dropped.Fail(SegmentStatus.TranslateFailed, ErrorCode.PipeOverload), ErrorCode.PipeOverload));

            capture.FramesAvailable += run.OnFrames;
            capture.StateChanged += (state, error) =>
            {
                if (state != CaptureState.Stopped) // "Stopped" của phiên do orchestrator phát
                    bus.Publish(new CaptureStateChanged(session.Id, state, error));
            };

            // Chuẩn bị engine dịch song song (hỏi capabilities của model, nạp sẵn model vào Ollama).
            var translatorTask = CreateTranslatorAsync(settings.Translation, ct);

            // Thu trước, nạp model sau: lời nói trong lúc nạp (1–4 s) nằm chờ trong channel PCM thay vì bị mất.
            try
            {
                await capture.StartAsync(ct).ConfigureAwait(false);
                run.Asr = await asrProvider.GetAsync(settings.Asr, ct).ConfigureAwait(false);
                run.Translator = await translatorTask.ConfigureAwait(false);
            }
            catch
            {
                await capture.StopAsync().ConfigureAwait(false);
                await capture.DisposeAsync().ConfigureAwait(false);
                detector.Dispose();
                throw;
            }

            run.ProcessTask = Task.Factory.StartNew(() => ProcessLoop(run), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            run.AsrTask = Task.Run(() => AsrLoopAsync(run));
            run.TranslateTask = Task.Run(() => TranslateLoopAsync(run));
            _run = run;
            bus.Publish(new SessionStarted(session, settings));
            return session;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public Task PauseAsync()
    {
        if (_run is { } run)
        {
            run.Paused = true;
            bus.Publish(new CaptureStateChanged(run.Session.Id, CaptureState.Paused));
        }
        return Task.CompletedTask;
    }

    public Task ResumeAsync()
    {
        if (_run is { } run)
        {
            run.Paused = false;
            bus.Publish(new CaptureStateChanged(run.Session.Id, CaptureState.Running));
        }
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Gọi nhiều lần vẫn an toàn (app tự dispose khi thoát, rồi DI container dispose lần nữa).</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        await StopAsync().ConfigureAwait(false);
        _lifecycle.Dispose();
    }

    private int _disposed;

    private async Task StopCoreAsync()
    {
        if (_run is not { } run)
            return;
        _run = null;

        await run.Capture.StopAsync().ConfigureAwait(false);
        run.Pcm.Writer.TryComplete();                       // thread VAD flush đoạn cuối rồi đóng channel đoạn lời
        await run.ProcessTask!.ConfigureAwait(false);

        // Cho ASR rồi dịch xử lý nốt hàng đợi; quá hạn thì huỷ (ASR: bỏ engine vì giao thức pipe dở dang).
        var drained = Task.WhenAll(run.AsrTask!, run.TranslateTask!);
        if (await Task.WhenAny(drained, Task.Delay(DrainTimeout)).ConfigureAwait(false) != drained)
        {
            await run.Cancel.CancelAsync().ConfigureAwait(false);
            if (!run.AsrTask!.IsCompleted)
                asrProvider.Invalidate();
            await drained.ConfigureAwait(false);
        }

        await run.Capture.DisposeAsync().ConfigureAwait(false);
        run.Detector.Dispose();
        run.Cancel.Dispose();
        bus.Publish(new SessionEnded(run.Session with { EndedAt = DateTimeOffset.Now, Status = SessionStatus.Stopped }));
        bus.Publish(new CaptureStateChanged(run.Session.Id, CaptureState.Stopped));
    }

    /// <summary>Thread riêng: đo mức tín hiệu, chạy VAD, đẩy đoạn lời vào hàng đợi ASR.</summary>
    private void ProcessLoop(Run run)
    {
        var levelEvery = Stopwatch.Frequency / 20; // 20 Hz
        var lastLevel = 0L;
        float peak = 0;
        double sumSquares = 0;
        long count = 0;
        var wasSpeaking = false;
        var wasPaused = false;
        var reader = run.Pcm.Reader;

        try
        {
            while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
            {
                while (reader.TryRead(out var block))
                {
                    try
                    {
                        var samples = block.Buffer.AsSpan(0, block.Length);
                        foreach (var s in samples)
                        {
                            var a = Math.Abs(s);
                            if (a > peak) peak = a;
                            sumSquares += s * s;
                        }
                        count += samples.Length;

                        IReadOnlyList<SpeechChunk> chunks;
                        if (run.Paused)
                        {
                            chunks = wasPaused ? [] : run.Detector.Flush(); // không bỏ dở câu đang nói
                            wasPaused = true;
                        }
                        else
                        {
                            wasPaused = false;
                            chunks = run.Detector.Push(samples);
                        }

                        // "Hết câu" (VAD hết lời) đi kèm đoạn lời cuối cùng để không chiếm chỗ trong hàng đợi có giới hạn.
                        var speaking = run.Detector.IsSpeaking;
                        Enqueue(run, chunks, endsSpeech: wasSpeaking && !speaking);
                        if (speaking != wasSpeaking)
                        {
                            wasSpeaking = speaking;
                            bus.Publish(new VoiceActivity(run.Session.Id, wasSpeaking));
                        }
                    }
                    finally
                    {
                        ArrayPool<float>.Shared.Return(block.Buffer);
                    }

                    var now = Stopwatch.GetTimestamp();
                    if (now - lastLevel >= levelEvery && count > 0)
                    {
                        var rms = (float)Math.Sqrt(sumSquares / count);
                        bus.Publish(new AudioLevel(ToDb(peak), ToDb(rms), rms < 0.001f));
                        peak = 0;
                        sumSquares = 0;
                        count = 0;
                        lastLevel = now;
                    }
                }
            }
            // Dừng phiên: đóng câu đang nói dở và báo hết câu.
            Enqueue(run, run.Detector.Flush(), endsSpeech: wasSpeaking);
            if (wasSpeaking)
            {
                wasSpeaking = false;
                bus.Publish(new VoiceActivity(run.Session.Id, false));
            }
        }
        catch (Exception ex)
        {
            bus.Publish(new CaptureStateChanged(run.Session.Id, CaptureState.Error, Detail: ex.Message));
        }
        finally
        {
            if (wasSpeaking)
                bus.Publish(new VoiceActivity(run.Session.Id, false));
            run.Speech.Writer.TryComplete();
        }
    }

    /// <param name="endsSpeech">
    /// VAD vừa hết lời: cờ được gắn vào đoạn cuối cùng; nếu không có đoạn nào (đuôi câu quá ngắn nên VAD bỏ)
    /// thì mới gửi một dấu riêng — dấu này chỉ xuất hiện một lần mỗi câu nên gần như không chiếm chỗ hàng đợi.
    /// </param>
    private void Enqueue(Run run, IReadOnlyList<SpeechChunk> chunks, bool endsSpeech)
    {
        var kept = chunks.Where(c => c.DropReason == SpeechDropReason.None).ToList();
        for (var i = 0; i < kept.Count; i++)
        {
            var chunk = kept[i];
            var segment = new Segment
            {
                Id = Ids.NewSegmentId(),
                SessionId = run.Session.Id,
                Seq = ++run.LastSeq,
                StartMs = chunk.StartMs,
                EndMs = chunk.EndMs,
            }.MoveTo(SegmentStatus.VoiceDetected).MoveTo(SegmentStatus.AsrPending);

            // Channel DropOldest: khi quá tải, đoạn cũ nhất bị bỏ qua callback itemDropped.
            run.Speech.Writer.TryWrite(new PendingSpeech(segment, chunk, Stopwatch.GetTimestamp(),
                                                         EndsSpeech: endsSpeech && i == kept.Count - 1));
            PublishHealth(run);
        }
        if (endsSpeech && kept.Count == 0)
            run.Speech.Writer.TryWrite(PendingSpeech.EndOfSpeech);
    }

    private async Task AsrLoopAsync(Run run)
    {
        string? previousText = null;
        var previousForced = false;
        try
        {
            await foreach (var item in run.Speech.Reader.ReadAllAsync(run.Cancel.Token).ConfigureAwait(false))
            {
                if (item.Segment is not { } segment || item.Chunk is not { } chunk)
                {
                    previousForced = false;
                    if (run.Translator is not null)
                        run.Transcribed.Writer.TryWrite(TranscribedItem.EndOfSpeech);
                    continue;
                }

                Interlocked.Increment(ref run.InFlight);
                PublishHealth(run);
                try
                {
                    var result = await run.Asr.TranscribeAsync(
                        new AsrRequest(segment.Id, chunk.Pcm16kMono, run.Settings.Asr.SourceLang, null),
                        run.Cancel.Token).ConfigureAwait(false);

                    var text = AsrResultFilter.Clean(result);
                    if (text is not null && previousForced && previousText is not null)
                        text = OverlapDeduper.TrimOverlap(previousText, text, Languages.IsCjk(result.Language));

                    if (string.IsNullOrWhiteSpace(text))
                    {
                        bus.Publish(new SegmentFailed(segment.Fail(SegmentStatus.Dropped, ErrorCode.AsrEmpty), ErrorCode.AsrEmpty));
                        continue;
                    }

                    previousText = text;
                    previousForced = chunk.ForcedCut;
                    var done = segment.MoveTo(SegmentStatus.AsrDone) with
                    {
                        TextOriginal = text,
                        LangDetected = string.IsNullOrEmpty(result.Language) ? null : result.Language,
                        AsrMs = result.ElapsedMs,
                    };
                    run.RecordLatency(Stopwatch.GetElapsedTime(item.QueuedAt));
                    bus.Publish(new SegmentFinal(done, run.Settings.Translation.TargetLang));
                    if (run.Translator is not null)
                        run.Transcribed.Writer.TryWrite(new TranscribedItem(done.MoveTo(SegmentStatus.TranslatePending), chunk.ForcedCut));
                }
                catch (OperationCanceledException) when (run.Cancel.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    asrProvider.Invalidate(); // worker có thể đã chết; lần Start sau sẽ tạo lại
                    bus.Publish(new SegmentFailed(segment.Fail(SegmentStatus.AsrFailed, ErrorCode.AsrWorkerCrashed), ErrorCode.AsrWorkerCrashed));
                    bus.Publish(new CaptureStateChanged(run.Session.Id, CaptureState.Error, ErrorCode.AsrWorkerCrashed, ex.Message));
                    break;
                }
                finally
                {
                    Interlocked.Decrement(ref run.InFlight);
                    PublishHealth(run);
                    // Câu kết thúc ở đoạn này (kể cả khi đoạn bị loại vì rỗng): báo cho khâu gộp mảnh.
                    if (item.EndsSpeech)
                    {
                        previousForced = false;
                        if (run.Translator is not null)
                            run.Transcribed.Writer.TryWrite(TranscribedItem.EndOfSpeech);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // dừng cưỡng bức
        }
        finally
        {
            run.Transcribed.Writer.TryComplete();
        }
    }

    private async Task<ITranslationEngine?> CreateTranslatorAsync(TranslationSettings settings, CancellationToken ct)
    {
        if (translationFactory is null)
            return null;
        try
        {
            return await translationFactory.CreateAsync(settings, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            bus.Publish(new OllamaStateChanged(OllamaState.Disconnected, null, ex.Message));
            return null;
        }
    }

    /// <summary>
    /// Dịch tuần tự theo thứ tự câu.
    /// <para>
    /// Gộp mảnh: câu dài bị VAD ép cắt (mỗi mảnh ≤ max_chunk) được giữ lại cho tới khi có mảnh kết thúc câu, rồi dịch
    /// cả câu một lần — dịch từng mảnh làm mảnh sau mất nghĩa. Câu được coi là kết thúc khi: gặp mảnh không bị ép cắt,
    /// gặp dấu "hết câu" (VAD hết lời), mảnh mới không chồng thời gian với mảnh trước (câu khác), hoặc chờ quá lâu.
    /// </para>
    /// <para>
    /// Ngắt mạch (SDD 5.4): sau <see cref="BreakerThreshold"/> lỗi kết nối liên tiếp, các câu tiếp theo được đánh dấu
    /// TRANSLATE_FAILED ngay (vẫn hiện câu gốc) và chỉ thử lại sau mỗi <see cref="ProbeInterval"/>.
    /// </para>
    /// </summary>
    private async Task TranslateLoopAsync(Run run)
    {
        var translator = run.Translator;
        var settings = run.Settings.Translation;
        var reader = run.Transcribed.Reader;
        var context = new Queue<string>();
        var pending = new List<Segment>();
        var consecutiveFailures = 0;
        var openUntil = 0L;
        var state = OllamaState.Unknown;
        // Mảnh tiếp theo của câu tới chậm nhất sau một max_chunk nữa (+ thời gian ASR); quá thì dịch phần đã có.
        var mergeWait = TimeSpan.FromSeconds(run.Settings.Vad.MaxChunkSec + 2);

        void SetState(OllamaState next, string? detail = null)
        {
            if (next == state)
                return;
            state = next;
            bus.Publish(new OllamaStateChanged(next, null, detail));
        }

        async Task FlushAsync()
        {
            if (pending.Count == 0)
                return;
            var fragments = pending.ToList();
            pending.Clear();
            await TranslateSentenceAsync(fragments).ConfigureAwait(false);
        }

        async Task TranslateSentenceAsync(List<Segment> fragments)
        {
            var last = fragments[^1];
            var text = string.Join(' ', fragments.Select(f => f.TextOriginal!.Trim()));
            var sentence = last with { TextOriginal = text, StartMs = fragments[0].StartMs };
            IReadOnlyList<string>? mergedIds = fragments.Count > 1 ? [.. fragments.Select(f => f.Id)] : null;
            var sourceLang = fragments.Select(f => f.LangDetected).LastOrDefault(l => l is not null)
                             ?? run.Settings.Asr.SourceLang ?? "auto";

            if (string.Equals(sourceLang, settings.TargetLang, StringComparison.OrdinalIgnoreCase))
            {
                // Đã đúng ngôn ngữ đích: không gọi LLM.
                bus.Publish(new SegmentTranslated(sentence.MoveTo(SegmentStatus.Translated) with { TextTranslated = text, LlmMs = 0 }, mergedIds));
            }
            else if (consecutiveFailures >= BreakerThreshold && Stopwatch.GetTimestamp() < openUntil)
            {
                foreach (var f in fragments)
                    bus.Publish(new SegmentFailed(f.Fail(SegmentStatus.TranslateFailed, ErrorCode.LlmUnreachable), ErrorCode.LlmUnreachable));
            }
            else
            {
                var result = await translator!.TranslateAsync(new TranslationRequest(
                    last.Id, text, sourceLang, settings.TargetLang, settings.Style,
                    [.. context], []), run.Cancel.Token).ConfigureAwait(false);

                if (result.IsSuccess)
                {
                    consecutiveFailures = 0;
                    SetState(OllamaState.Connected);
                    bus.Publish(new SegmentTranslated(sentence.MoveTo(SegmentStatus.Translated) with
                    {
                        TextTranslated = result.Translation,
                        LlmMs = result.ElapsedMs,
                    }, mergedIds));
                }
                else
                {
                    var error = result.Error ?? ErrorCode.LlmBadOutput;
                    if (error.IsTransient())
                    {
                        consecutiveFailures++;
                        if (consecutiveFailures >= BreakerThreshold)
                        {
                            openUntil = Stopwatch.GetTimestamp() + (long)(ProbeInterval.TotalSeconds * Stopwatch.Frequency);
                            SetState(OllamaState.Disconnected, "Mất kết nối Ollama — vẫn hiện câu gốc, sẽ thử lại.");
                        }
                    }
                    else if (error == ErrorCode.LlmModelNotFound)
                    {
                        SetState(OllamaState.ModelMissing, $"Ollama không có model {settings.Model}.");
                    }
                    foreach (var f in fragments)
                        bus.Publish(new SegmentFailed(f.Fail(SegmentStatus.TranslateFailed, error) with { LlmMs = result.ElapsedMs }, error));
                }
            }

            context.Enqueue(text);
            while (context.Count > settings.ContextSegments)
                context.Dequeue();
        }

        try
        {
            while (true)
            {
                // Đang giữ mảnh dở: chỉ chờ mảnh tiếp theo trong giới hạn, quá hạn thì dịch phần đã có.
                bool more;
                if (pending.Count > 0)
                {
                    using var wait = CancellationTokenSource.CreateLinkedTokenSource(run.Cancel.Token);
                    wait.CancelAfter(mergeWait);
                    try
                    {
                        more = await reader.WaitToReadAsync(wait.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!run.Cancel.IsCancellationRequested)
                    {
                        await FlushAsync().ConfigureAwait(false);
                        continue;
                    }
                }
                else
                {
                    more = await reader.WaitToReadAsync(run.Cancel.Token).ConfigureAwait(false);
                }

                if (!more)
                {
                    await FlushAsync().ConfigureAwait(false); // dừng phiên: dịch nốt câu dở
                    break;
                }

                while (reader.TryRead(out var item))
                {
                    if (translator is null)
                        continue;
                    if (item.Segment is not { } segment)
                    {
                        await FlushAsync().ConfigureAwait(false); // hết câu
                        continue;
                    }

                    // Mảnh nối tiếp luôn bắt đầu trước khi mảnh trước kết thúc (chồng overlap_ms); không chồng → câu khác.
                    if (pending.Count > 0 && segment.StartMs >= pending[^1].EndMs)
                        await FlushAsync().ConfigureAwait(false);

                    pending.Add(segment);
                    if (!item.ForcedCut)
                        await FlushAsync().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // dừng cưỡng bức
        }
    }

    private void PublishHealth(Run run)
    {
        var queued = run.Speech.Reader.Count + Volatile.Read(ref run.InFlight);
        bus.Publish(new PipelineHealth(queued, run.Transcribed.Reader.Count, IsLagging: queued >= Math.Max(2, run.Settings.Pipeline.SpeechQueueMax / 2),
                                       run.LatencyPercentile(0.5), run.LatencyPercentile(0.95)));
    }

    private static float ToDb(float linear) => linear <= 0 ? -90f : Math.Max(-90f, 20f * MathF.Log10(linear));

    private readonly record struct PcmBlock(float[] Buffer, int Length);

    /// <summary>
    /// Đoạn lời chờ ASR (<see cref="EndsSpeech"/> = câu kết thúc ở đoạn này), hoặc dấu "hết câu" riêng (Segment = null)
    /// khi VAD hết lời mà không còn đoạn nào để gắn cờ.
    /// </summary>
    private sealed record PendingSpeech(Segment? Segment, SpeechChunk? Chunk, long QueuedAt, bool EndsSpeech = false)
    {
        public static readonly PendingSpeech EndOfSpeech = new(null, null, 0, EndsSpeech: true);
    }

    /// <summary>Câu gốc chờ dịch (kèm cờ bị ép cắt để gộp mảnh), hoặc dấu "hết câu" (Segment = null).</summary>
    private sealed record TranscribedItem(Segment? Segment, bool ForcedCut)
    {
        public static readonly TranscribedItem EndOfSpeech = new(null, false);
    }

    private sealed class Run
    {
        private readonly Queue<int> _latencies = new();

        public Run(Session session, AppSettings settings, IAudioCapture capture, ISpeechDetector detector)
        {
            Session = session;
            Settings = settings;
            Capture = capture;
            Detector = detector;
            // ~10 s âm thanh (gói 10 ms); quá thì bỏ gói cũ — chỉ xảy ra khi thread VAD bị treo.
            Pcm = Channel.CreateBounded<PcmBlock>(
                new BoundedChannelOptions(1_000) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest },
                dropped => ArrayPool<float>.Shared.Return(dropped.Buffer));
            Speech = Channel.CreateBounded<PendingSpeech>(
                new BoundedChannelOptions(Math.Max(1, settings.Pipeline.SpeechQueueMax)) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest },
                dropped => { if (dropped.Segment is { } s) DroppedSpeech?.Invoke(s); });
            // Câu gốc chờ dịch; quá tải thì câu cũ nhất mất bản dịch (câu gốc đã hiện).
            Transcribed = Channel.CreateBounded<TranscribedItem>(
                new BoundedChannelOptions(Math.Max(1, settings.Pipeline.QueueMax)) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest },
                dropped => { if (dropped.Segment is { } s) DroppedTranslation?.Invoke(s); });
        }

        public Session Session { get; }
        public AppSettings Settings { get; }
        public IAudioCapture Capture { get; }
        public ISpeechDetector Detector { get; }
        public IAsrEngine Asr { get; set; } = null!; // gán ngay sau khi nạp model, trước khi chạy vòng ASR
        public ITranslationEngine? Translator { get; set; }
        public Channel<PcmBlock> Pcm { get; }
        public Channel<PendingSpeech> Speech { get; }
        public Channel<TranscribedItem> Transcribed { get; }
        public CancellationTokenSource Cancel { get; } = new();
        public Task? ProcessTask { get; set; }
        public Task? AsrTask { get; set; }
        public Task? TranslateTask { get; set; }
        public Action<Segment>? DroppedTranslation;
        public volatile bool Paused;
        public long LastSeq;
        public int InFlight;
        public Action<Segment>? DroppedSpeech;

        /// <summary>Thread capture: chỉ copy vào buffer thuê từ pool.</summary>
        public void OnFrames(ReadOnlySpan<float> frames, long position, bool discontinuity)
        {
            var buffer = ArrayPool<float>.Shared.Rent(frames.Length);
            frames.CopyTo(buffer);
            if (!Pcm.Writer.TryWrite(new PcmBlock(buffer, frames.Length)))
                ArrayPool<float>.Shared.Return(buffer);
        }

        public void RecordLatency(TimeSpan latency)
        {
            lock (_latencies)
            {
                _latencies.Enqueue((int)latency.TotalMilliseconds);
                if (_latencies.Count > 50)
                    _latencies.Dequeue();
            }
        }

        public int? LatencyPercentile(double p)
        {
            lock (_latencies)
            {
                if (_latencies.Count == 0)
                    return null;
                var sorted = _latencies.Order().ToArray();
                return sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
            }
        }
    }
}
