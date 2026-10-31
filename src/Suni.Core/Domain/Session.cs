namespace Suni.Core.Domain;

public enum CaptureMode
{
    /// <summary>Thu theo PID + cây tiến trình con.</summary>
    ProcessLoopback,

    /// <summary>Toàn hệ thống, loại trừ chính ứng dụng (ADR-04).</summary>
    SystemExcludeSelf,

    /// <summary>Endpoint loopback truyền thống (dự phòng).</summary>
    EndpointLoopback,

    Microphone,
}

public enum SessionStatus { Running, Paused, Stopped, Faulted }

public enum OllamaEndpointKind { Local, Lan, Other }

/// <summary>Một phiên thu/dịch. Đổi nguồn = kết thúc phiên và mở phiên mới.</summary>
public sealed record Session
{
    public required string Id { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? EndedAt { get; init; }

    public required CaptureMode CaptureMode { get; init; }
    public string? SourceApp { get; init; }
    public int? SourcePid { get; init; }

    /// <summary>null = Auto Detect.</summary>
    public string? SourceLang { get; init; }
    public required string TargetLang { get; init; }

    public string? AsrEngine { get; init; }
    public string? AsrModel { get; init; }
    public OllamaEndpointKind? OllamaEndpointKind { get; init; }
    public string? OllamaModel { get; init; }

    public SessionStatus Status { get; init; } = SessionStatus.Running;
    public string? Title { get; init; }
}
