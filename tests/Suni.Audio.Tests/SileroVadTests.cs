using Suni.Audio.Processing;
using Suni.Core.Abstractions;
using Suni.Core.Settings;
using Xunit.Abstractions;

namespace Suni.Audio.Tests;

/// <summary>
/// Kiểm thử với model Silero thật và giọng nói thật (fixture TTS Windows, xem fixtures/audio/make-tts-fixtures.ps1),
/// đi qua toàn bộ chuỗi 48 kHz stereo → Downsampler → Silero → VadSegmenter.
/// </summary>
public class SileroVadTests(ITestOutputHelper output)
{
    private const int Rate = 16_000;

    private static float[] LoadSentence(string name)
    {
        var wav = WavReader.Read(Path.Combine(AppContext.BaseDirectory, "fixtures", "tts", name + ".wav"));
        Assert.Equal(Rate, wav.SampleRate);
        Assert.Equal(1, wav.Channels);
        return wav.Samples;
    }

    /// <summary>Vùng có tiếng thực sự trong clip (bỏ khoảng lặng đầu/cuối do TTS tự thêm).</summary>
    private static (int Start, int End) VoicedSpan(float[] x)
    {
        var first = Array.FindIndex(x, v => MathF.Abs(v) > 0.01f);
        var last = Array.FindLastIndex(x, v => MathF.Abs(v) > 0.01f);
        return (first, last + 1);
    }

    /// <summary>"Nhạc" tổng hợp: hợp âm có hoạ âm, đổi nốt mỗi 250 ms, có envelope — không có lời.</summary>
    private static float[] Music(double seconds)
    {
        double[][] chords = [[261.6, 329.6, 392.0], [220.0, 277.2, 329.6], [196.0, 246.9, 293.7], [174.6, 220.0, 261.6]];
        var n = (int)(seconds * Rate);
        var x = new float[n];
        var note = Rate / 4;
        for (var i = 0; i < n; i++)
        {
            var chord = chords[i / note % chords.Length];
            var t = (double)i / Rate;
            var env = Math.Exp(-3.0 * (i % note) / note);
            double v = 0;
            foreach (var f in chord)
                for (var h = 1; h <= 4; h++)
                    v += Math.Sin(2 * Math.PI * f * h * t) / (h * h);
            x[i] = (float)(0.08 * env * v);
        }
        return x;
    }

    private sealed record Region(string Name, int Start, int End, bool IsSpeech);

    private static (float[] Audio, List<Region> Regions) BuildTimeline()
    {
        var audio = new List<float>();
        var regions = new List<Region>();

        void Silence(double s) => audio.AddRange(new float[(int)(s * Rate)]);
        void Sentence(string name)
        {
            var clip = LoadSentence(name);
            var (s, e) = VoicedSpan(clip);
            regions.Add(new Region(name, audio.Count + s, audio.Count + e, true));
            audio.AddRange(clip);
        }
        void Add(string name, float[] clip)
        {
            regions.Add(new Region(name, audio.Count, audio.Count + clip.Length, false));
            audio.AddRange(clip);
        }

        Silence(1.0); Sentence("en_1");
        Silence(1.2); Sentence("en_2");
        Silence(0.8); Add("music", Music(3.0));
        Silence(0.8); Sentence("en_3");
        Silence(1.0); Sentence("en_4");
        Silence(1.0);
        return ([.. audio], regions);
    }

    /// <summary>Upsample ×3 (nội suy tuyến tính) và nhân đôi kênh để giả lập dòng thu 48 kHz stereo.</summary>
    private static float[] To48kStereo(float[] x)
    {
        var y = new float[x.Length * 3 * 2];
        for (var i = 0; i < x.Length; i++)
        {
            var next = i + 1 < x.Length ? x[i + 1] : x[i];
            for (var k = 0; k < 3; k++)
            {
                var v = x[i] + (next - x[i]) * k / 3f;
                y[(i * 3 + k) * 2] = y[(i * 3 + k) * 2 + 1] = v;
            }
        }
        return y;
    }

    private static List<SpeechChunk> RunPipeline(float[] stereo48k, VadSettings settings)
    {
        using var vad = new SileroVad();
        var down = new Downsampler(48_000, 2);
        var seg = new VadSegmenter(vad, settings);
        var chunks = new List<SpeechChunk>();
        var mono = new float[down.MaxOutputLength(960)];
        for (var pos = 0; pos < stereo48k.Length; pos += 960) // gói 10 ms như WASAPI
        {
            var n = down.Process(stereo48k.AsSpan(pos, Math.Min(960, stereo48k.Length - pos)), mono);
            chunks.AddRange(seg.Push(mono.AsSpan(0, n)));
        }
        chunks.AddRange(seg.Flush());
        return chunks;
    }

    private static long Ms(int samples) => samples * 1000L / Rate;

    [Fact]
    public void Silence_HasLowProbability()
    {
        using var vad = new SileroVad();
        var frame = new float[vad.FrameSize];
        var probs = Enumerable.Range(0, 30).Select(_ => vad.Process(frame)).ToList();
        Assert.All(probs, p => Assert.True(p < 0.1f, $"p = {p}"));
    }

    [Fact]
    public void WrongFrameSize_Throws()
    {
        using var vad = new SileroVad();
        Assert.Throws<ArgumentException>(() => vad.Process(new float[100]));
    }

    [Fact]
    public void RealSpeech_IsSegmented_MusicAndSilenceAreNot()
    {
        var (audio, regions) = BuildTimeline();
        var chunks = RunPipeline(To48kStereo(audio), new VadSettings());

        output.WriteLine("Vùng chuẩn:");
        foreach (var r in regions)
            output.WriteLine($"  {r.Name,-6} {Ms(r.Start),6}–{Ms(r.End),6} ms {(r.IsSpeech ? "lời" : "không lời")}");
        output.WriteLine("Đoạn VAD:");
        foreach (var c in chunks)
            output.WriteLine($"  {c.StartMs,6}–{c.EndMs,6} ms  dài {c.DurationMs,5}  ratio {c.SpeechRatio:F2}  cắt ép {c.ForcedCut}  {c.DropReason}");

        var kept = chunks.Where(c => c.DropReason == SpeechDropReason.None).ToList();

        // 1. Mỗi câu được phủ gần hết bởi các đoạn giữ lại (không mất chữ).
        foreach (var r in regions.Where(r => r.IsSpeech))
        {
            var covered = 0L;
            for (var t = Ms(r.Start); t < Ms(r.End); t += 10)
                if (kept.Any(c => c.StartMs <= t && t < c.EndMs))
                    covered += 10;
            var ratio = (double)covered / (Ms(r.End) - Ms(r.Start));
            output.WriteLine($"Phủ {r.Name}: {ratio:P1}");
            Assert.True(ratio >= 0.98, $"{r.Name} chỉ được phủ {ratio:P1}");
        }

        // 2. Không đoạn giữ lại nào nằm trong vùng nhạc (không gửi nhạc cho ASR → không dịch bịa, TC-04).
        var music = regions.Single(r => r.Name == "music");
        var musicOverlapMs = kept.Sum(c => Math.Max(0, Math.Min(c.EndMs, Ms(music.End)) - Math.Max(c.StartMs, Ms(music.Start))));
        output.WriteLine($"Đoạn giữ lại chồng lên nhạc: {musicOverlapMs} ms");
        Assert.True(musicOverlapMs <= 300, $"{musicOverlapMs} ms nhạc lọt vào ASR");

        // 3. Không đoạn nào vượt max_chunk (+ pre/post roll).
        Assert.All(chunks, c => Assert.True(c.DurationMs <= 4_000 + 200 + 150 + 32));

        // 4. Ranh giới đầu câu: đoạn đầu tiên phủ câu bắt đầu không muộn hơn điểm có tiếng.
        foreach (var r in regions.Where(r => r.IsSpeech))
        {
            var first = kept.Where(c => c.EndMs > Ms(r.Start)).MinBy(c => c.StartMs)!;
            Assert.True(first.StartMs <= Ms(r.Start), $"{r.Name}: đoạn bắt đầu {first.StartMs} > {Ms(r.Start)}");
            Assert.True(Ms(r.Start) - first.StartMs <= 600, $"{r.Name}: pre-roll quá dài ({Ms(r.Start) - first.StartMs} ms)");
        }
    }
}
