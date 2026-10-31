namespace Suni.Core.Abstractions;

public sealed record AsrOptions(
    string Engine,
    string Model,
    string ModelDirectory,
    string Device = "cpu",
    string ComputeType = "int8",
    int Threads = 0,
    int BeamSize = 1,
    // whisper audio_ctx (0 = mặc định 1500 ≈ 30 s). Nhỏ hơn → encoder nhanh hơn; phải ≥ 50 × độ dài đoạn (giây).
    int AudioContext = 0);

public sealed record AsrInfo(string Engine, string Version, string Model, int LoadMs);

public sealed record AsrRequest(
    string SegmentId,
    ReadOnlyMemory<float> Pcm16kMono,
    string? Language,
    string? InitialPrompt);

public sealed record AsrWord(string Text, int StartMs, int EndMs, float Probability);

public sealed record AsrResult(
    string SegmentId,
    string Text,
    string Language,
    float LanguageProbability,
    IReadOnlyList<AsrWord> Words,
    float AvgProbability,
    float NoSpeechProb,
    int ElapsedMs);

public interface IAsrEngine : IAsyncDisposable
{
    Task<AsrInfo> InitializeAsync(AsrOptions options, CancellationToken ct = default);

    Task<AsrResult> TranscribeAsync(AsrRequest request, CancellationToken ct = default);
}
