using Suni.Core.Domain;

namespace Suni.Core.Abstractions;

/// <summary>
/// Một nguồn âm thanh hiển thị trong danh sách chọn (FR-01). <see cref="RootPid"/> nhận diện ứng dụng (gom các tiến trình
/// cùng exe); <see cref="CapturePid"/> là PID thực sự đưa vào process loopback (null = dùng RootPid).
/// </summary>
public sealed record AudioSource(
    CaptureMode Mode,
    string DisplayName,
    int? RootPid = null,
    string? ExeName = null,
    string? IconPath = null,
    bool IsActive = false,
    int SessionCount = 0,
    int? CapturePid = null);

public interface IAudioSourceEnumerator
{
    /// <summary>Liệt kê app đang có audio session, đã gom theo tiến trình gốc và loại trừ chính ứng dụng.</summary>
    Task<IReadOnlyList<AudioSource>> GetSourcesAsync(CancellationToken ct = default);
}

public readonly record struct AudioFormat(int SampleRate, int Channels);

/// <summary>Khối PCM float32 interleaved; <see cref="DevicePositionFrames"/> dùng làm sample clock.</summary>
public delegate void PcmFramesHandler(ReadOnlySpan<float> frames, long devicePositionFrames, bool discontinuity);

public interface IAudioCapture : IAsyncDisposable
{
    AudioFormat Format { get; }

    /// <summary>Gọi trên thread capture: chỉ được copy vào buffer, không xử lý nặng.</summary>
    event PcmFramesHandler? FramesAvailable;

    event Action<Events.CaptureState, ErrorCode?>? StateChanged;

    Task StartAsync(CancellationToken ct = default);
    Task StopAsync();
}

/// <summary>Lý do một đoạn bị VAD loại (vẫn được báo để ghi nhận DROPPED / chẩn đoán).</summary>
public enum SpeechDropReason { None, MusicGate }

/// <summary>
/// Một đoạn lời nói do VAD cắt ra: PCM 16 kHz mono, đã gồm pre-roll/post-roll.
/// Thời gian tính theo sample clock của phiên (ms).
/// </summary>
public sealed record SpeechChunk(
    long StartMs,
    long EndMs,
    float[] Pcm16kMono,
    bool ForcedCut,
    float SpeechRatio,
    SpeechDropReason DropReason = SpeechDropReason.None)
{
    public long DurationMs => EndMs - StartMs;
}

public interface IAudioCaptureFactory
{
    IAudioCapture Create(AudioSource source);
}

/// <summary>Chuỗi tiền xử lý + VAD: nhận PCM thô của capture, trả các đoạn lời nói 16 kHz mono.</summary>
public interface ISpeechDetector : IDisposable
{
    bool IsSpeaking { get; }

    IReadOnlyList<SpeechChunk> Push(ReadOnlySpan<float> interleaved);

    /// <summary>Đóng đoạn đang mở (khi dừng/tạm dừng).</summary>
    IReadOnlyList<SpeechChunk> Flush();
}

public interface ISpeechDetectorFactory
{
    ISpeechDetector Create(AudioFormat captureFormat, Settings.VadSettings settings);
}
