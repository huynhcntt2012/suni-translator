using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Suni.Asr;
using Suni.Audio;
using Suni.Audio.Processing;
using Suni.Core.Abstractions;
using Suni.Core.Settings;

// Benchmark ASR (spike P0 phần b). Đi đúng đường của app: đoạn do VAD cắt → AsrWorkerClient → pipe → Suni.AsrWorker.
//   dotnet run --project tools/Suni.AsrBench -- [--models a,b] [--threads 2,4,8] [--devices cpu,vulkan] [--lang auto|en]
// Mặc định chạy ma trận trong docs/spikes/P0-asr.md. Kết quả: artifacts/bench/asr-<thời gian>.{md,json}

Console.OutputEncoding = Encoding.UTF8;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

var root = FindRepoRoot();
var workerPath = Path.Combine(root, "src", "Suni.AsrWorker", "bin", "Debug", "net10.0", "Suni.AsrWorker.exe");
var modelDir = Path.Combine(root, "models", "asr", "whisper.cpp");
var fixtureDir = Path.Combine(root, "fixtures", "audio", "tts");

var references = new Dictionary<string, string>
{
    ["en_1"] = "Today I will explain how to use this feature.",
    ["en_2"] = "First, open the settings window and choose the audio source.",
    ["en_3"] = "Press start.",
    ["en_4"] = "The translated subtitles will appear on top of the video, sentence by sentence, while the original audio keeps playing.",
};

var configs = BuildMatrix();
var chunks = BuildChunks();
Console.WriteLine($"Máy: {Environment.ProcessorCount} luồng logic | worker: {workerPath}");
Console.WriteLine($"{chunks.Count(c => !c.IsProbe)} đoạn lời ({chunks.Where(c => !c.IsProbe).Sum(c => c.Seconds):F1} s) + {chunks.Count(c => c.IsProbe)} đoạn thử ảo giác\n");

var results = new List<ConfigResult>();
foreach (var cfg in configs)
{
    Console.Write($"{cfg.Label,-36} ");
    try
    {
        var r = await RunAsync(cfg);
        results.Add(r);
        var leaked = r.Chunks.Count(x => x.IsProbe && x.Filtered is not null);
        Console.WriteLine($"load {r.LoadMs,5} ms | RTF {r.Rtf:F3} | p50 {r.P50Ms,5} ms | p95 {r.P95Ms,5} ms | WER {r.Wer:P1} | RAM {r.PeakMb,5} MB | {r.Runtime} | ảo giác lọt {leaked}/3");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"LỖI: {ex.Message}");
        results.Add(new ConfigResult(cfg, 0, "", 0, 0, 0, 0, double.NaN, 0, [], [], ex.Message));
    }
}

WriteReport(results);
return 0;

// ---------------------------------------------------------------------------------------------

List<BenchConfig> BuildMatrix()
{
    var models = Opt("--models")?.Split(',');
    var threads = Opt("--threads")?.Split(',').Select(int.Parse).ToArray();
    var devices = Opt("--devices")?.Split(',');
    var lang = Opt("--lang") is { } l ? (l == "auto" ? null : l) : null;
    var ctx = int.Parse(Opt("--ctx") ?? "0");

    if (models is not null || threads is not null || devices is not null)
        return [.. from m in models ?? ["small-q5_1"]
                   from d in devices ?? ["cpu"]
                   from t in threads ?? [4]
                   select new BenchConfig(m, d, t, lang, ctx)];

    // Ma trận mặc định: CPU với số luồng giới hạn để ước lượng máy yếu; GPU (Vulkan) cho model lớn.
    return
    [
        new("tiny-q5_1", "cpu", 2, null), new("tiny-q5_1", "cpu", 4, null), new("tiny-q5_1", "cpu", 8, null),
        new("base-q5_1", "cpu", 2, null), new("base-q5_1", "cpu", 4, null), new("base-q5_1", "cpu", 8, null),
        new("small-q5_1", "cpu", 2, null), new("small-q5_1", "cpu", 4, null), new("small-q5_1", "cpu", 8, null),
        new("small-q5_1", "cpu", 8, "en"),
        new("large-v3-turbo-q5_0", "cpu", 8, null),
        // audio_ctx 256 (≈ 5,1 s ≥ đoạn dài nhất 4,35 s): encoder không phải xử lý 30 s đệm.
        new("tiny-q5_1", "cpu", 2, null, 256), new("base-q5_1", "cpu", 2, null, 256),
        new("small-q5_1", "cpu", 2, null, 256), new("small-q5_1", "cpu", 4, null, 256),
        new("large-v3-turbo-q5_0", "cpu", 8, null, 256),
        new("small-q5_1", "vulkan", 4, null), new("large-v3-turbo-q5_0", "vulkan", 4, null),
        new("large-v3-turbo-q5_0", "vulkan", 4, null, 256),
    ];
}

List<BenchChunk> BuildChunks()
{
    var list = new List<BenchChunk>();
    var settings = new VadSettings();
    foreach (var name in references.Keys)
    {
        var clip = WavReader.Read(Path.Combine(fixtureDir, name + ".wav")).Samples;
        float[] stream = [.. new float[8_000], .. clip, .. new float[16_000]];
        using var vad = new SileroVad();
        var seg = new VadSegmenter(vad, settings);
        var produced = seg.Push(stream).Concat(seg.Flush()).Where(c => c.DropReason == SpeechDropReason.None).ToList();
        for (var i = 0; i < produced.Count; i++)
            list.Add(new BenchChunk(name, $"{name}#{i + 1}", produced[i].Pcm16kMono, false));
    }

    // Đoạn không có lời đưa thẳng vào ASR (giả lập VAD lọt) để xem whisper có "bịa" chữ không.
    list.Add(new BenchChunk("probe", "music_3s", Music(3), true));
    list.Add(new BenchChunk("probe", "silence_2s", new float[32_000], true));
    var rng = new Random(7);
    list.Add(new BenchChunk("probe", "noise_3s", Enumerable.Range(0, 48_000).Select(_ => (float)(rng.NextDouble() * 2 - 1) * 0.02f).ToArray(), true));
    return list;
}

async Task<ConfigResult> RunAsync(BenchConfig cfg)
{
    await using var client = new AsrWorkerClient(workerPath);
    var info = await client.InitializeAsync(new AsrOptions("whisper.cpp", $"ggml-{cfg.Model}.bin", modelDir, cfg.Device, Threads: cfg.Threads, AudioContext: cfg.AudioCtx));

    // Lượt đầu tiên chậm hơn (cấp phát, nạp shader GPU) → chạy nháp, không tính.
    await client.TranscribeAsync(new AsrRequest("warmup", chunks[0].Pcm, cfg.Language, null));

    var rows = new List<ChunkResult>();
    foreach (var c in chunks)
    {
        var sw = Stopwatch.StartNew();
        var r = await client.TranscribeAsync(new AsrRequest(c.Id, c.Pcm, cfg.Language, null));
        rows.Add(new ChunkResult(c.Id, c.Group, c.IsProbe, c.Seconds, (int)sw.ElapsedMilliseconds, r.ElapsedMs, r.Text,
                                 AsrResultFilter.Clean(r), r.Language, r.AvgProbability, r.NoSpeechProb));
    }

    client.WorkerProcess!.Refresh();
    var peakMb = (int)(client.WorkerProcess.PeakWorkingSet64 / 1024 / 1024);

    var speech = rows.Where(r => !r.IsProbe).ToList();
    var lat = speech.Select(r => r.WallMs).Order().ToArray();

    var sentences = references.Keys.Select(name =>
    {
        var text = "";
        foreach (var r in speech.Where(r => r.Group == name))
            text = (text + " " + OverlapDeduper.TrimOverlap(text, r.Filtered ?? "")).Trim();
        return new SentenceResult(name, references[name], text, WordErrors(references[name], text), Words(references[name]).Length);
    }).ToList();
    var wer = (double)sentences.Sum(s => s.Errors) / sentences.Sum(s => s.RefWords);

    return new ConfigResult(cfg, info.LoadMs, client.Runtime ?? "", speech.Sum(r => r.WallMs) / 1000.0 / speech.Sum(r => r.AudioSeconds),
                            Percentile(lat, 0.5), Percentile(lat, 0.95), lat[^1], wer, peakMb, sentences, rows, null);
}

void WriteReport(List<ConfigResult> results)
{
    var dir = Path.Combine(root, "artifacts", "bench");
    Directory.CreateDirectory(dir);
    var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
    var md = new StringBuilder();
    md.AppendLine($"# ASR benchmark {DateTime.Now:yyyy-MM-dd HH:mm}\n");
    md.AppendLine($"CPU logic threads: {Environment.ProcessorCount}\n");
    md.AppendLine("| Model | Thiết bị | Luồng | audio_ctx | Ngôn ngữ | Nạp (ms) | RTF | p50 (ms) | p95 (ms) | max (ms) | WER | RAM đỉnh (MB) | Runtime |");
    md.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|");
    foreach (var r in results)
        md.AppendLine(r.Error is not null
            ? $"| {r.Config.Model} | {r.Config.Device} | {r.Config.Threads} | {r.Config.AudioCtx} | {r.Config.Language ?? "auto"} | lỗi: {r.Error} |||||||| "
            : $"| {r.Config.Model} | {r.Config.Device} | {r.Config.Threads} | {r.Config.AudioCtx} | {r.Config.Language ?? "auto"} | {r.LoadMs} | {r.Rtf:F3} | {r.P50Ms} | {r.P95Ms} | {r.MaxMs} | {r.Wer:P1} | {r.PeakMb} | {r.Runtime} |");

    md.AppendLine("\n## Bản nhận dạng và đoạn thử ảo giác\n");
    foreach (var r in results.Where(r => r.Error is null))
    {
        md.AppendLine($"### {r.Config.Label}\n");
        foreach (var s in r.Sentences)
            md.AppendLine($"- {s.Name} ({s.Errors}/{s.RefWords} lỗi): {s.Hypothesis}");
        foreach (var p in r.Chunks.Where(c => c.IsProbe))
            md.AppendLine($"- **{p.Id}** → `{(p.Text.Length == 0 ? "(rỗng)" : p.Text)}` → sau lọc: {(p.Filtered is null ? "**bỏ**" : $"`{p.Filtered}`")} (lang {p.Language}, no_speech {p.NoSpeech:F2}, prob {p.AvgProb:F2})");
        md.AppendLine();
    }

    File.WriteAllText(Path.Combine(dir, $"asr-{stamp}.md"), md.ToString());
    File.WriteAllText(Path.Combine(dir, $"asr-{stamp}.json"),
        JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    Console.WriteLine($"\nBáo cáo: {Path.Combine(dir, $"asr-{stamp}.md")}");
}

static string[] Words(string s) =>
    new string(s.ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) || ch == '\'' ? ch : ' ').ToArray())
        .Split(' ', StringSplitOptions.RemoveEmptyEntries);

/// <summary>Khoảng cách Levenshtein theo từ (thay + xoá + chèn).</summary>
static int WordErrors(string reference, string hypothesis)
{
    var r = Words(reference);
    var h = Words(hypothesis);
    var d = new int[r.Length + 1, h.Length + 1];
    for (var i = 0; i <= r.Length; i++) d[i, 0] = i;
    for (var j = 0; j <= h.Length; j++) d[0, j] = j;
    for (var i = 1; i <= r.Length; i++)
        for (var j = 1; j <= h.Length; j++)
            d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + (r[i - 1] == h[j - 1] ? 0 : 1));
    return d[r.Length, h.Length];
}

static int Percentile(int[] sorted, double p) =>
    sorted.Length == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];

static float[] Music(double seconds)
{
    double[][] chords = [[261.6, 329.6, 392.0], [220.0, 277.2, 329.6], [196.0, 246.9, 293.7], [174.6, 220.0, 261.6]];
    var n = (int)(seconds * 16_000);
    var note = 16_000 / 4;
    var x = new float[n];
    for (var i = 0; i < n; i++)
    {
        var chord = chords[i / note % chords.Length];
        var env = Math.Exp(-3.0 * (i % note) / note);
        double v = 0;
        foreach (var f in chord)
            for (var h = 1; h <= 4; h++)
                v += Math.Sin(2 * Math.PI * f * h * i / 16_000) / (h * h);
        x[i] = (float)(0.08 * env * v);
    }
    return x;
}

static string FindRepoRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "SuniTranslator.slnx")))
            return dir.FullName;
    throw new DirectoryNotFoundException("Không tìm thấy thư mục gốc repo (SuniTranslator.slnx).");
}

string? Opt(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

record BenchConfig(string Model, string Device, int Threads, string? Language, int AudioCtx = 0)
{
    public string Label => $"{Model} {Device} t{Threads} {Language ?? "auto"}{(AudioCtx > 0 ? $" ctx{AudioCtx}" : "")}";
}

record BenchChunk(string Group, string Id, float[] Pcm, bool IsProbe)
{
    public double Seconds => Pcm.Length / 16_000.0;
}

record ChunkResult(string Id, string Group, bool IsProbe, double AudioSeconds, int WallMs, int WorkerMs,
                   string Text, string? Filtered, string Language, float AvgProb, float NoSpeech);

record SentenceResult(string Name, string Reference, string Hypothesis, int Errors, int RefWords);

record ConfigResult(BenchConfig Config, int LoadMs, string Runtime, double Rtf, int P50Ms, int P95Ms, int MaxMs,
                    double Wer, int PeakMb, List<SentenceResult> Sentences, List<ChunkResult> Chunks, string? Error);
