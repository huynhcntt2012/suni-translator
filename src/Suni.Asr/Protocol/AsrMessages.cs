using System.Text.Json.Serialization;

namespace Suni.Asr.Protocol;

/// <summary>Loại frame trên named pipe app ↔ ASR worker (SDD 4.2).</summary>
public enum MessageType : byte
{
    Hello = 1,
    Init = 2,
    InitOk = 3,
    InitError = 4,
    Transcribe = 5,
    Result = 6,
    Error = 7,
    Ping = 8,
    Pong = 9,
    Shutdown = 10,

    /// <summary>PCM float32 little-endian, gửi ngay sau <see cref="Transcribe"/>.</summary>
    Pcm = 32,
}

public sealed record HelloMessage(
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("engine")] string Engine,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("devices")] IReadOnlyList<string> Devices);

public sealed record InitMessage(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("model_dir")] string ModelDir,
    [property: JsonPropertyName("device")] string Device,
    [property: JsonPropertyName("compute_type")] string ComputeType,
    [property: JsonPropertyName("threads")] int Threads,
    [property: JsonPropertyName("beam_size")] int BeamSize,
    [property: JsonPropertyName("audio_ctx")] int AudioContext = 0);

public sealed record InitOkMessage(
    [property: JsonPropertyName("load_ms")] int LoadMs,
    [property: JsonPropertyName("runtime")] string Runtime);

public sealed record ResultMessage(
    [property: JsonPropertyName("segment_id")] string SegmentId,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("language")] string Language,
    [property: JsonPropertyName("language_probability")] float LanguageProbability,
    [property: JsonPropertyName("avg_probability")] float AvgProbability,
    [property: JsonPropertyName("no_speech_probability")] float NoSpeechProbability,
    [property: JsonPropertyName("elapsed_ms")] int ElapsedMs);

public sealed record TranscribeMessage(
    [property: JsonPropertyName("segment_id")] string SegmentId,
    [property: JsonPropertyName("language")] string? Language,
    [property: JsonPropertyName("initial_prompt")] string? InitialPrompt,
    [property: JsonPropertyName("n_samples")] int SampleCount);

public sealed record ErrorMessage(
    [property: JsonPropertyName("segment_id")] string? SegmentId,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

public static class PipeNames
{
    public const string TokenEnvironmentVariable = "SUNI_ASR_TOKEN";
    public const string PipeEnvironmentVariable = "SUNI_ASR_PIPE";

    public static string ForSession(Guid id) => $"suni-asr-{id:N}";
}
