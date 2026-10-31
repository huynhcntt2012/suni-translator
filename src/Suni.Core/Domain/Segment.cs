namespace Suni.Core.Domain;

/// <summary>Đơn vị nghiệp vụ: một đoạn lời nói đã được VAD cắt ra.</summary>
public sealed record Segment
{
    public required string Id { get; init; }
    public required string SessionId { get; init; }
    public required long Seq { get; init; }

    /// <summary>Thời điểm theo sample clock của phiên (ms).</summary>
    public required long StartMs { get; init; }
    public required long EndMs { get; init; }

    public bool IsFinal { get; init; } = true;
    public SegmentStatus Status { get; init; } = SegmentStatus.Captured;

    public string? LangDetected { get; init; }
    public float? LangProbability { get; init; }
    public string? TextOriginal { get; init; }
    public string? TextTranslated { get; init; }

    /// <summary>Bản dịch người dùng sửa tay (FR-13); ưu tiên khi xuất file.</summary>
    public string? TextTranslatedEdited { get; init; }

    public int? AsrMs { get; init; }
    public int? LlmMs { get; init; }
    public ErrorCode? Error { get; init; }

    public long DurationMs => EndMs - StartMs;

    public string? DisplayTranslation => TextTranslatedEdited ?? TextTranslated;

    /// <summary>Trả về bản sao ở trạng thái mới, kiểm tra hợp lệ qua <see cref="SegmentStateMachine"/>.</summary>
    public Segment MoveTo(SegmentStatus next) =>
        this with { Status = SegmentStateMachine.Transition(Status, next) };

    public Segment Fail(SegmentStatus failedStatus, ErrorCode error) =>
        MoveTo(failedStatus) with { Error = error };
}
