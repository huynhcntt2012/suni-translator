using Suni.Audio.Processing;
using Suni.Core.Abstractions;
using Suni.Core.Settings;

namespace Suni.Audio.Tests;

public class VadSegmenterTests
{
    private const int Rate = 16_000;
    private const int Frame = 512; // 32 ms

    /// <summary>Model giả: xác suất theo kịch bản từng khung. PCM chứa chỉ số khung để kiểm tra cắt đúng chỗ.</summary>
    private sealed class ScriptedModel(params float[] probs) : IVoiceActivityModel
    {
        private int _i;
        public int FrameSize => Frame;
        public float Process(ReadOnlySpan<float> frame) => _i < probs.Length ? probs[_i++] : 0f;
        public void Reset() => _i = 0;
    }

    private static readonly VadSettings Defaults = new()
    {
        Threshold = 0.5f, NegThreshold = 0.35f, MinSpeechMs = 250, MinSilenceMs = 500, MaxChunkSec = 4.0,
        PreRollMs = 200, PostRollMs = 150, OverlapMs = 300, MusicGate = true, MinSpeechRatio = 0.3f,
    };

    /// <summary>Kịch bản theo ms: (giây im lặng, giây lời, ...) → mảng xác suất từng khung.</summary>
    private static float[] Script(params (double Seconds, float Prob)[] parts) =>
        parts.SelectMany(p => Enumerable.Repeat(p.Prob, (int)Math.Round(p.Seconds * Rate / Frame))).ToArray();

    private static List<SpeechChunk> RunAll(float[] probs, VadSettings? settings = null)
    {
        var seg = new VadSegmenter(new ScriptedModel(probs), settings ?? Defaults);
        var pcm = new float[probs.Length * Frame];
        for (var i = 0; i < pcm.Length; i++)
            pcm[i] = i; // giá trị = chỉ số mẫu tuyệt đối
        var chunks = new List<SpeechChunk>();
        for (var pos = 0; pos < pcm.Length; pos += 1_000) // khối lẻ, không trùng ranh giới khung
            chunks.AddRange(seg.Push(pcm.AsSpan(pos, Math.Min(1_000, pcm.Length - pos))));
        chunks.AddRange(seg.Flush());
        return chunks;
    }

    private static long FrameMs(int frames) => frames * Frame * 1000L / Rate;

    [Fact]
    public void Silence_ProducesNothing() => Assert.Empty(RunAll(Script((5, 0.02f))));

    [Fact]
    public void SingleUtterance_HasPreAndPostRoll()
    {
        // 1 s im lặng (31 khung) → 1,5 s lời → 1 s im lặng
        var chunks = RunAll(Script((1, 0.02f), (1.5, 0.95f), (1, 0.02f)));

        var c = Assert.Single(chunks);
        var speechStartMs = FrameMs(31);
        var speechEndMs = FrameMs(31 + 47);
        Assert.Equal(speechStartMs - 200, c.StartMs);
        Assert.Equal(speechEndMs + 150, c.EndMs);
        Assert.False(c.ForcedCut);
        Assert.Equal(SpeechDropReason.None, c.DropReason);
        Assert.Equal(1f, c.SpeechRatio);

        // PCM khớp đúng vị trí trên sample clock.
        Assert.Equal(c.StartMs * Rate / 1000, (long)c.Pcm16kMono[0]);
        Assert.Equal((c.EndMs - c.StartMs) * Rate / 1000, c.Pcm16kMono.Length);
    }

    [Fact]
    public void ShortBlip_IsIgnored() =>
        Assert.Empty(RunAll(Script((1, 0.02f), (0.15, 0.9f), (1, 0.02f))));

    [Fact]
    public void PauseShorterThanMinSilence_KeepsOneSegment() =>
        Assert.Single(RunAll(Script((0.5, 0.02f), (1, 0.9f), (0.3, 0.05f), (1, 0.9f), (1, 0.02f))));

    [Fact]
    public void PauseLongerThanMinSilence_SplitsSegments() =>
        Assert.Equal(2, RunAll(Script((0.5, 0.02f), (1, 0.9f), (0.7, 0.05f), (1, 0.9f), (1, 0.02f))).Count);

    [Fact]
    public void Hysteresis_ProbBetweenThresholdsDoesNotEndSpeech() =>
        Assert.Single(RunAll(Script((0.5, 0.02f), (1, 0.9f), (1.5, 0.4f), (1, 0.9f), (1, 0.02f))));

    [Fact]
    public void LongSpeech_IsForceCutWithOverlap_AtQuietestFrame()
    {
        var probs = Script((0.5, 0.02f), (10, 0.9f), (1, 0.02f)).ToArray();
        probs[15 + 110] = 0.55f; // khung "yên" hơn ở ~3,5 s → điểm cắt mong muốn
        var chunks = RunAll(probs);

        Assert.True(chunks.Count >= 3);
        Assert.All(chunks, c => Assert.True(c.DurationMs <= 4_000 + 200 + 150, $"đoạn dài {c.DurationMs} ms"));
        Assert.True(chunks[0].ForcedCut);
        Assert.False(chunks[^1].ForcedCut);
        Assert.Equal(FrameMs(125) + Frame / 2 * 1000 / Rate, chunks[0].EndMs);

        for (var i = 1; i < chunks.Count; i++)
            Assert.Equal(300, chunks[i - 1].EndMs - chunks[i].StartMs); // chồng 300 ms
    }

    [Fact]
    public void LowSpeechRatio_IsFlaggedByMusicGate()
    {
        // Nhạc: xác suất dao động quanh ngưỡng → phần lớn khung nằm giữa neg_threshold và threshold.
        var probs = Enumerable.Range(0, 100).Select(i => i % 5 == 0 ? 0.6f : 0.4f).ToArray();
        var chunks = RunAll([.. Script((0.5, 0.02f)), .. probs, .. Script((1, 0.02f))]);

        var c = Assert.Single(chunks);
        Assert.Equal(SpeechDropReason.MusicGate, c.DropReason);
        Assert.True(c.SpeechRatio < 0.3f);

        var noGate = RunAll([.. Script((0.5, 0.02f)), .. probs, .. Script((1, 0.02f))], Defaults with { MusicGate = false });
        Assert.Equal(SpeechDropReason.None, Assert.Single(noGate).DropReason);
    }

    [Fact]
    public void Flush_ClosesOpenSpeech()
    {
        var chunks = RunAll(Script((0.5, 0.02f), (1, 0.9f)));
        var c = Assert.Single(chunks);
        Assert.Equal(FrameMs(16 + 31), c.EndMs);
    }

    [Fact]
    public void PreRoll_IsClampedAtStreamStart()
    {
        var c = Assert.Single(RunAll(Script((1, 0.9f), (1, 0.02f))));
        Assert.Equal(0, c.StartMs);
    }
}
