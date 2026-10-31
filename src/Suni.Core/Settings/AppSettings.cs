using Suni.Core.Abstractions;
using Suni.Core.Domain;

namespace Suni.Core.Settings;

/// <summary>Cấu hình ứng dụng (SDD 9). Giá trị mặc định là điểm khởi đầu, cần benchmark.</summary>
public sealed record AppSettings
{
    public CaptureSettings Capture { get; init; } = new();
    public VadSettings Vad { get; init; } = new();
    public AsrSettings Asr { get; init; } = new();
    public TranslationSettings Translation { get; init; } = new();
    public PipelineSettings Pipeline { get; init; } = new();
    public SubtitleSettings Subtitle { get; init; } = new();
    public PrivacySettings Privacy { get; init; } = new();
    public HotkeySettings Hotkeys { get; init; } = new();
    public UiSettings Ui { get; init; } = new();
    public TtsSettings Tts { get; init; } = new();
}

/// <summary>Đọc bản dịch bằng giọng offline (FR-23, FR-24).</summary>
public sealed record TtsSettings
{
    public bool Enabled { get; init; }

    /// <summary>Rỗng = tự chọn giọng theo ngôn ngữ đích.</summary>
    public string VoiceId { get; init; } = "";

    /// <summary>Rỗng = thiết bị phát mặc định của Windows.</summary>
    public string OutputDeviceId { get; init; } = "";

    public double Rate { get; init; } = 1.1;
    public double Volume { get; init; } = 1.0;

    /// <summary>Số câu chờ tối đa; đọc không kịp thì bỏ câu cũ nhất để giọng đọc theo kịp hiện tại.</summary>
    public int MaxPending { get; init; } = 2;
}

/// <summary>Phím tắt toàn cục (FR-21), dạng "Ctrl+Alt+O"; chuỗi rỗng = tắt phím đó.</summary>
public sealed record HotkeySettings
{
    public string Overlay { get; init; } = "Ctrl+Alt+O";
    public string OverlayLock { get; init; } = "Ctrl+Alt+L";
    public string StartStop { get; init; } = "Ctrl+Alt+S";
}

public sealed record UiSettings
{
    /// <summary>Đóng cửa sổ chính khi đang dịch → thu nhỏ xuống khay thay vì thoát (SDD 8.3).</summary>
    public bool CloseToTray { get; init; } = true;

    /// <summary>Thông báo khay khi mất nguồn / mất kết nối Ollama / lỗi lúc cửa sổ chính đang ẩn.</summary>
    public bool TrayNotifications { get; init; } = true;
}

public sealed record CaptureSettings
{
    public CaptureMode Mode { get; init; } = CaptureMode.ProcessLoopback;
    public bool ProcessTree { get; init; } = true;
    public string? LastSourceExe { get; init; }
    public bool AutoReconnect { get; init; } = true;
}

public sealed record VadSettings
{
    public float Threshold { get; init; } = 0.5f;
    public float NegThreshold { get; init; } = 0.35f;
    public int MinSpeechMs { get; init; } = 250;
    public int MinSilenceMs { get; init; } = 500;
    public double MaxChunkSec { get; init; } = 4.0;
    public int PreRollMs { get; init; } = 200;
    public int PostRollMs { get; init; } = 150;
    public int OverlapMs { get; init; } = 300;
    public bool MusicGate { get; init; } = true;

    /// <summary>Tỉ lệ khung có lời tối thiểu trong đoạn; thấp hơn → MusicGate loại.</summary>
    public float MinSpeechRatio { get; init; } = 0.3f;
}

public sealed record AsrSettings
{
    public string Engine { get; init; } = "whisper.cpp";

    /// <summary>Tên model không gồm tiền tố/đuôi file: "small-q5_1" ↔ ggml-small-q5_1.bin.</summary>
    public string Model { get; init; } = "small-q5_1";

    /// <summary>whisper audio_ctx; 0 = mặc định (30 s).</summary>
    public int AudioContext { get; init; }
    public string Device { get; init; } = "cpu";
    public string ComputeType { get; init; } = "int8";
    public int Threads { get; init; }
    public int BeamSize { get; init; } = 1;

    /// <summary>null = Auto Detect.</summary>
    public string? SourceLang { get; init; }
    public int TimeoutSec { get; init; } = 20;
    public bool Partial { get; init; }
}

public sealed record TranslationSettings
{
    /// <summary>Rỗng mặc định — không hardcode IP; người dùng phải Test Connection.</summary>
    public string OllamaUrl { get; init; } = "";
    public string Model { get; init; } = "";
    public string TargetLang { get; init; } = "vi";
    public TranslationStyle Style { get; init; } = TranslationStyle.Natural;
    public int TimeoutSec { get; init; } = 30;
    public int Retry { get; init; } = 2;
    public int Concurrency { get; init; } = 1;
    public int ContextSegments { get; init; } = 2;
}

public sealed record PipelineSettings
{
    public int SampleRate { get; init; } = 16000;
    public int SpeechQueueMax { get; init; } = 8;
    public int QueueMax { get; init; } = 30;
}

public sealed record SubtitleSettings
{
    public bool Bilingual { get; init; } = true;
    public bool AlwaysOnTop { get; init; } = true;
    public double FontSize { get; init; } = 20;
    public int MaxLines { get; init; } = 2;
    public double Opacity { get; init; } = 0.6;
    public bool Locked { get; init; }

    /// <summary>Overlay đang bật (mở lại khi khởi động app).</summary>
    public bool OverlayVisible { get; init; }

    /// <summary>Vị trí/kích thước overlay (DIP); null = giữa đáy màn hình chính.</summary>
    public double? Left { get; init; }
    public double? Top { get; init; }
    public double Width { get; init; } = 900;

    /// <summary>Ẩn câu sau bao nhiêu giây không có câu mới (0 = luôn hiện).</summary>
    public double ClearAfterSec { get; init; } = 8;
}

public sealed record PrivacySettings
{
    public bool SaveRawAudio { get; init; }
    public bool SaveTranscript { get; init; } = true;
    public bool Telemetry { get; init; }
    public bool OfflineStrict { get; init; } = true;
}

public interface ISettingsStore
{
    Task<AppSettings> LoadAsync(CancellationToken ct = default);
    Task SaveAsync(AppSettings settings, CancellationToken ct = default);
}
