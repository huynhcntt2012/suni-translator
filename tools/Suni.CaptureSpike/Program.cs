using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Suni.Audio;
using Suni.Audio.Processing;

// Công cụ spike P0: kiểm chứng WASAPI process loopback viết bằng C#.
//   list                                     liệt kê ứng dụng đang có audio session
//   capture --pid N [--exclude] [--seconds S] [--out f.wav]
//   capture --system | --endpoint [--seconds S] [--out f.wav]
//   tone --freq Hz [--seconds S] [--amp A] --out f.wav   (PCM16 để SoundPlayer phát được)
//   analyze f.wav --freqs 440,1000           mức (dBFS) tại từng tần số (Goertzel)
//   vad f.wav [--dump dir]                   chạy Downsampler → Silero → Segmenter trên file, đo tốc độ
//   vad --pid N | --system [--seconds S] [--dump dir]   như trên nhưng thu trực tiếp
//   live --pid N | --system [--seconds S] [--model large-v3-turbo-q5_0] [--cpu]
//        [--ollama http://127.0.0.1:11434 --llm qwen3.5:9b] [--target vi]
//                                            chạy đúng PipelineOrchestrator của app (thu → VAD → whisper → Ollama)

Console.OutputEncoding = Encoding.UTF8;
var json = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

try
{
    return args.FirstOrDefault() switch
    {
        "list" => await ListAsync(),
        "capture" => await CaptureAsync(),
        "tone" => Tone(),
        "analyze" => Analyze(),
        "vad" => await VadAsync(),
        "live" => await LiveAsync(),
        _ => Usage(),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"LỖI: {ex.GetType().Name}: {ex.Message}");
    return 1;
}

async Task<int> ListAsync()
{
    var sources = await new AudioSourceEnumerator().GetSourcesAsync();
    foreach (var s in sources)
        Console.WriteLine($"{(s.IsActive ? "●" : "○")} {s.RootPid,7}  {s.ExeName,-24} {s.DisplayName}  [{s.Mode}, sessions={s.SessionCount}, capture pid={s.CapturePid}]");
    return 0;
}

async Task<int> CaptureAsync()
{
    var seconds = double.Parse(Opt("--seconds") ?? "5");
    var output = Opt("--out") ?? "capture.wav";

    WasapiLoopbackCapture capture;
    string mode;
    if (Opt("--pid") is { } pidText)
    {
        var include = !Flag("--exclude");
        capture = WasapiLoopbackCapture.ForProcess(int.Parse(pidText), include);
        mode = include ? $"process-include pid={pidText}" : $"process-exclude pid={pidText}";
    }
    else if (Flag("--endpoint"))
    {
        capture = WasapiLoopbackCapture.ForEndpoint();
        mode = "endpoint-default";
    }
    else
    {
        capture = WasapiLoopbackCapture.ForSystemExcludingSelf();
        mode = $"system-exclude-self pid={Environment.ProcessId}";
    }

    var states = new List<string>();
    var callbackTicks = 0L;
    var callbacks = 0L;
    var peak = 0f;
    double sumSquares = 0;
    long sampleCount = 0;

    await using (capture)
    using (var wav = new WavWriter(output, capture.Format.SampleRate, capture.Format.Channels))
    {
        capture.StateChanged += (s, e) => { lock (states) states.Add($"{DateTime.Now:HH:mm:ss.fff} {s} {e}"); };
        capture.FramesAvailable += (frames, _, _) =>
        {
            var t0 = Stopwatch.GetTimestamp();
            wav.Write(frames);
            foreach (var x in frames)
            {
                var a = MathF.Abs(x);
                if (a > peak) peak = a;
                sumSquares += x * x;
            }
            sampleCount += frames.Length;
            callbackTicks += Stopwatch.GetTimestamp() - t0;
            callbacks++;
        };

        var sw = Stopwatch.StartNew();
        await capture.StartAsync();
        var startMs = sw.Elapsed.TotalMilliseconds;
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        await capture.StopAsync();
        var wall = sw.Elapsed.TotalSeconds - startMs / 1000;

        var stats = capture.Stats;
        var report = new
        {
            mode,
            output = Path.GetFullPath(output),
            format = $"{capture.Format.SampleRate} Hz, {capture.Format.Channels} ch, float32",
            start_ms = Math.Round(startMs, 1),
            wall_seconds = Math.Round(wall, 3),
            captured_seconds = Math.Round((double)stats.Frames / capture.Format.SampleRate, 3),
            stats.Packets,
            stats.SilentPackets,
            stats.Discontinuities,
            stats.TimestampErrors,
            first_packet_ms = stats.FirstPacketAfterStartMs is { } f ? Math.Round(f, 1) : (double?)null,
            max_packet_gap_ms = Math.Round(stats.MaxPacketGapMs, 1),
            avg_callback_us = callbacks == 0 ? 0 : Math.Round(Stopwatch.GetElapsedTime(0, callbackTicks).TotalMicroseconds / callbacks, 1),
            peak_dbfs = Math.Round(LevelMeter.ToDb(peak), 1),
            rms_dbfs = Math.Round(LevelMeter.ToDb(sampleCount == 0 ? 0 : (float)Math.Sqrt(sumSquares / sampleCount)), 1),
            states,
        };
        Console.WriteLine(JsonSerializer.Serialize(report, json));
    }
    return 0;
}

int Tone()
{
    var freq = double.Parse(Opt("--freq") ?? "440");
    var seconds = double.Parse(Opt("--seconds") ?? "5");
    var amp = double.Parse(Opt("--amp") ?? "0.1");
    var output = Opt("--out") ?? $"tone_{freq}.wav";
    const int rate = 48_000;
    var frames = (int)(rate * seconds);

    using var w = new BinaryWriter(File.Create(output));
    w.Write("RIFF"u8); w.Write(36 + frames * 4); w.Write("WAVE"u8);
    w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)16);
    w.Write("data"u8); w.Write(frames * 4);
    for (var i = 0; i < frames; i++)
    {
        var fade = Math.Min(1, Math.Min(i, frames - i) / (rate * 0.02)); // 20 ms fade chống click
        var v = (short)(amp * fade * short.MaxValue * Math.Sin(2 * Math.PI * freq * i / rate));
        w.Write(v); w.Write(v);
    }
    Console.WriteLine(Path.GetFullPath(output));
    return 0;
}

int Analyze()
{
    var path = args.ElementAtOrDefault(1) ?? throw new ArgumentException("Thiếu đường dẫn WAV.");
    var freqs = (Opt("--freqs") ?? "440,1000").Split(',').Select(double.Parse).ToArray();
    var wav = WavReader.Read(path);

    var mono = new float[wav.Samples.Length / wav.Channels];
    for (var i = 0; i < mono.Length; i++)
    {
        float sum = 0;
        for (var c = 0; c < wav.Channels; c++)
            sum += wav.Samples[i * wav.Channels + c];
        mono[i] = sum / wav.Channels;
    }

    var (peak, rms) = LevelMeter.Measure(mono);
    var report = new
    {
        file = Path.GetFullPath(path),
        duration_seconds = Math.Round(wav.DurationSeconds, 3),
        peak_dbfs = Math.Round(peak, 1),
        rms_dbfs = Math.Round(rms, 1),
        tones = freqs.ToDictionary(f => $"{f} Hz", f => Math.Round(LevelMeter.ToDb((float)Goertzel(mono, wav.SampleRate, f)), 1)),
    };
    Console.WriteLine(JsonSerializer.Serialize(report, json));
    return 0;
}

async Task<int> VadAsync()
{
    var dump = Opt("--dump");
    if (dump is not null)
        Directory.CreateDirectory(dump);

    using var model = new SileroVad();
    var settings = new Suni.Core.Settings.VadSettings();
    var segmenter = new VadSegmenter(model, settings);
    var vadTicks = 0L;
    var index = 0;

    void Report(IReadOnlyList<Suni.Core.Abstractions.SpeechChunk> chunks, double? nowMs)
    {
        foreach (var c in chunks)
        {
            index++;
            // Độ trễ phát hiện = thời điểm đoạn được phát ra so với thời điểm kết thúc đoạn (theo sample clock).
            var lag = nowMs is { } n ? $"  trễ {n - c.EndMs,5:F0} ms" : "";
            Console.WriteLine($"#{index,-3} {c.StartMs,7}–{c.EndMs,7} ms  dài {c.DurationMs,5}  ratio {c.SpeechRatio:F2}" +
                              $"{(c.ForcedCut ? "  [cắt ép]" : "")}{(c.DropReason != Suni.Core.Abstractions.SpeechDropReason.None ? $"  [{c.DropReason}]" : "")}{lag}");
            if (dump is not null)
            {
                using var w = new WavWriter(Path.Combine(dump, $"chunk_{index:000}_{c.StartMs}.wav"), 16_000, 1);
                w.Write(c.Pcm16kMono);
            }
        }
    }

    IReadOnlyList<Suni.Core.Abstractions.SpeechChunk> Feed(Downsampler down, ReadOnlySpan<float> block, float[] mono)
    {
        var n = down.Process(block, mono);
        var t0 = Stopwatch.GetTimestamp();
        var result = segmenter.Push(mono.AsSpan(0, n));
        vadTicks += Stopwatch.GetTimestamp() - t0;
        return result;
    }

    double audioSeconds;
    if (args.ElementAtOrDefault(1) is { } file && !file.StartsWith("--"))
    {
        var wav = WavReader.Read(file);
        var down = new Downsampler(wav.SampleRate, wav.Channels);
        var mono = new float[down.MaxOutputLength(wav.Samples.Length)];
        var block = wav.SampleRate / 100 * wav.Channels; // gói 10 ms
        for (var pos = 0; pos < wav.Samples.Length; pos += block)
            Report(Feed(down, wav.Samples.AsSpan(pos, Math.Min(block, wav.Samples.Length - pos)), mono), null);
        Report(segmenter.Flush(), null);
        audioSeconds = wav.DurationSeconds;
    }
    else
    {
        var seconds = double.Parse(Opt("--seconds") ?? "30");
        await using var capture = Opt("--pid") is { } pid
            ? WasapiLoopbackCapture.ForProcess(int.Parse(pid), includeTree: true)
            : WasapiLoopbackCapture.ForSystemExcludingSelf();
        var down = new Downsampler(capture.Format.SampleRate, capture.Format.Channels);
        var ring = new SpscRingBuffer<float>(capture.Format.SampleRate * capture.Format.Channels * 10);
        capture.FramesAvailable += (frames, _, _) => ring.Write(frames); // thread capture: chỉ copy
        capture.StateChanged += (s, e) => Console.WriteLine($"[capture] {s} {e}");

        await capture.StartAsync();
        Console.WriteLine($"Đang nghe {seconds} s... (Ctrl+C để dừng)");
        var block = new float[capture.Format.SampleRate / 50 * capture.Format.Channels];
        var mono = new float[down.MaxOutputLength(block.Length)];
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            var n = ring.Read(block);
            if (n == 0)
            {
                await Task.Delay(10);
                continue;
            }
            Report(Feed(down, block.AsSpan(0, n), mono), segmenter.ProcessedSamples * 1000.0 / 16_000);
        }
        await capture.StopAsync();
        Report(segmenter.Flush(), null);
        audioSeconds = segmenter.ProcessedSamples / 16_000.0;
    }

    var vadSeconds = Stopwatch.GetElapsedTime(0, vadTicks).TotalSeconds;
    Console.WriteLine($"Âm thanh {audioSeconds:F1} s, thời gian VAD {vadSeconds * 1000:F0} ms, " +
                      $"RTF {vadSeconds / audioSeconds:F4} ({audioSeconds / Math.Max(vadSeconds, 1e-9):F0}× thời gian thực)");
    return 0;
}

async Task<int> LiveAsync()
{
    var root = Directory.GetParent(AppContext.BaseDirectory)!;
    while (root is not null && !File.Exists(Path.Combine(root.FullName, "SuniTranslator.slnx")))
        root = root.Parent;
    if (root is null)
        throw new DirectoryNotFoundException("Không tìm thấy thư mục gốc repo.");

    var seconds = double.Parse(Opt("--seconds") ?? "30");
    var source = Opt("--pid") is { } pid
        ? new Suni.Core.Abstractions.AudioSource(Suni.Core.Domain.CaptureMode.ProcessLoopback, $"PID {pid}", int.Parse(pid))
        : new Suni.Core.Abstractions.AudioSource(Suni.Core.Domain.CaptureMode.SystemExcludeSelf, "System");
    var defaults = new Suni.Core.Settings.AppSettings();
    var settings = defaults with
    {
        Asr = defaults.Asr with { Model = Opt("--model") ?? "large-v3-turbo-q5_0", Device = Flag("--cpu") ? "cpu" : "vulkan" },
        Translation = defaults.Translation with
        {
            OllamaUrl = Opt("--ollama") ?? "",
            Model = Opt("--llm") ?? "",
            TargetLang = Opt("--target") ?? "vi",
        },
    };
    using var http = new HttpClient(new Suni.Translation.OfflineStrictHandler { InnerHandler = new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(3) } })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    var bus = new Suni.Pipeline.InMemoryEventBus();
    await using var asr = new Suni.Pipeline.WhisperWorkerProvider(
        () => Path.Combine(root.FullName, "src", "Suni.AsrWorker", "bin", "Debug", "net10.0", "Suni.AsrWorker.exe"),
        () => Path.Combine(root.FullName, "models", "asr", "whisper.cpp"));
    await using var pipeline = new Suni.Pipeline.PipelineOrchestrator(new AudioCaptureFactory(), new SileroSpeechDetectorFactory(), asr, bus,
        new Suni.Translation.OllamaTranslationEngineFactory(http));

    var clock = Stopwatch.StartNew();
    bus.Subscribe<Suni.Core.Events.SegmentFinal>(e => Console.WriteLine(
        $"[{clock.Elapsed.TotalSeconds,6:F1}s] #{e.Segment.Seq,-3} {e.Segment.StartMs,6}–{e.Segment.EndMs,6} ms  {e.Segment.LangDetected}  asr {e.Segment.AsrMs} ms  {e.Segment.TextOriginal}"));
    bus.Subscribe<Suni.Core.Events.SegmentTranslated>(e => Console.WriteLine(
        $"[{clock.Elapsed.TotalSeconds,6:F1}s] #{e.Segment.Seq,-3} → {settings.Translation.TargetLang}  llm {e.Segment.LlmMs} ms  {e.Segment.TextTranslated}"));
    bus.Subscribe<Suni.Core.Events.SegmentFailed>(e => Console.WriteLine(
        $"[{clock.Elapsed.TotalSeconds,6:F1}s] #{e.Segment.Seq,-3} {e.Segment.Status}: {e.Error}"));
    bus.Subscribe<Suni.Core.Events.OllamaStateChanged>(e => Console.WriteLine(
        $"[{clock.Elapsed.TotalSeconds,6:F1}s] [ollama] {e.State} {e.Detail}"));
    bus.Subscribe<Suni.Core.Events.CaptureStateChanged>(e => Console.WriteLine(
        $"[{clock.Elapsed.TotalSeconds,6:F1}s] [capture] {e.State} {e.Error} {e.Detail}"));

    // --db: ghi lịch sử như app, sau đó đọc lại và xuất SRT để kiểm tra.
    Suni.Storage.HistoryStore? store = null;
    Suni.Storage.HistoryRecorder? recorder = null;
    if (Opt("--db") is { } dbPath)
    {
        store = new Suni.Storage.HistoryStore(Suni.Storage.SuniDatabase.ForFile(Path.GetFullPath(dbPath)));
        await store.InitializeAsync();
        recorder = new Suni.Storage.HistoryRecorder(store, bus);
    }

    var session = await pipeline.StartAsync(source, settings);
    Console.WriteLine($"Model {settings.Asr.Model} · runtime {asr.Runtime} · nghe {seconds} s");
    await Task.Delay(TimeSpan.FromSeconds(seconds));
    await pipeline.StopAsync();

    if (store is not null && recorder is not null)
    {
        await recorder.FlushAsync();
        var saved = (await store.ListAsync()).First(s => s.Session.Id == session.Id);
        Console.WriteLine($"\n[db] phiên {saved.Session.Id} · {saved.Session.SourceApp} · {saved.SegmentCount} câu · " +
                          $"{saved.Session.StartedAt:HH:mm:ss}–{saved.Session.EndedAt:HH:mm:ss} · {saved.Session.Status}");
        var segments = await store.ListBySessionAsync(session.Id);
        Console.WriteLine(Suni.Storage.Exporters.SrtExporter.Build(segments, Suni.Core.Abstractions.ExportContent.Bilingual));
        await recorder.DisposeAsync();
    }
    return 0;
}

// Biên độ hình sin ước lượng tại tần số f.
static double Goertzel(float[] x, int rate, double f)
{
    if (x.Length == 0) return 0;
    var w = 2 * Math.PI * f / rate;
    var coeff = 2 * Math.Cos(w);
    double s1 = 0, s2 = 0;
    foreach (var v in x)
    {
        var s0 = v + coeff * s1 - s2;
        s2 = s1;
        s1 = s0;
    }
    var power = s1 * s1 + s2 * s2 - coeff * s1 * s2;
    return 2 * Math.Sqrt(Math.Max(0, power)) / x.Length;
}

string? Opt(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

bool Flag(string name) => args.Contains(name);

int Usage()
{
    Console.WriteLine("""
        Suni capture spike
          list
          capture --pid N [--exclude] [--seconds S] [--out f.wav]
          capture --system | --endpoint [--seconds S] [--out f.wav]
          tone --freq Hz [--seconds S] [--amp A] --out f.wav
          analyze f.wav [--freqs 440,1000]
          vad f.wav [--dump dir]
          vad --pid N | --system [--seconds S] [--dump dir]
        """);
    return 2;
}
