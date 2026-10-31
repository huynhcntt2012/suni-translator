using Suni.Core.Domain;

namespace Suni.Core.Events;

/// <summary>Sự kiện nội bộ (SDD 2.3). UI chỉ nhận dữ liệu qua các sự kiện này.</summary>
public abstract record PipelineEvent(string Type)
{
    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;
}

public enum CaptureState { Starting, Running, Paused, Stopped, SourceLost, DeviceChanged, Error }

/// <summary>Phiên đã bắt đầu thu (kèm cấu hình của phiên, vd. có được lưu transcript hay không).</summary>
public sealed record SessionStarted(Session Session, Settings.AppSettings Settings)
    : PipelineEvent("session.started");

/// <summary>Phiên đã dừng; <see cref="Session"/> có EndedAt và Status cuối.</summary>
public sealed record SessionEnded(Session Session)
    : PipelineEvent("session.ended");

public sealed record CaptureStateChanged(string? SessionId, CaptureState State, ErrorCode? Error = null, string? Detail = null)
    : PipelineEvent("capture.state");

/// <summary>Mức tín hiệu 20 Hz (dBFS). Bus chỉ giữ bản mới nhất.</summary>
public sealed record AudioLevel(float PeakDb, float RmsDb, bool IsSilent)
    : PipelineEvent("capture.level");

public sealed record VoiceActivity(string SessionId, bool IsSpeaking)
    : PipelineEvent("segment.voice");

public sealed record SegmentPartial(string SessionId, string SegmentId, long Seq, string Text)
    : PipelineEvent("segment.partial");

/// <summary>Đúng schema SRS 6.3; Translation có thể null khi chưa dịch xong.</summary>
public sealed record SegmentFinal(Segment Segment, string TargetLang)
    : PipelineEvent("segment.final");

/// <summary>
/// Bản dịch của một câu. Khi câu dài bị VAD ép cắt thành nhiều mảnh, các mảnh được gộp lại rồi dịch một lần:
/// <see cref="Segment"/> là mảnh cuối (TextOriginal = cả câu đã gộp), <see cref="MergedSegmentIds"/> liệt kê mọi mảnh theo thứ tự.
/// </summary>
public sealed record SegmentTranslated(Segment Segment, IReadOnlyList<string>? MergedSegmentIds = null)
    : PipelineEvent("segment.translated");

public sealed record SegmentFailed(Segment Segment, ErrorCode Error)
    : PipelineEvent("segment.failed");

public sealed record PipelineHealth(int SpeechQueueLength, int TranslateQueueLength, bool IsLagging,
                                    int? LatencyP50Ms, int? LatencyP95Ms)
    : PipelineEvent("pipeline.health");

public enum OllamaState { Unknown, Connected, Disconnected, ModelLoading, ModelMissing }

public sealed record OllamaStateChanged(OllamaState State, OllamaEndpointKind? Kind, string? Detail = null)
    : PipelineEvent("ollama.state");
