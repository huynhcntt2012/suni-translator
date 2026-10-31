namespace Suni.Core.Domain;

/// <summary>
/// Ma trận chuyển trạng thái hợp lệ của segment. Mọi worker phải đi qua đây.
/// </summary>
public static class SegmentStateMachine
{
    private static readonly Dictionary<SegmentStatus, SegmentStatus[]> Allowed = new()
    {
        [SegmentStatus.Captured] = [SegmentStatus.VoiceDetected, SegmentStatus.Dropped],
        [SegmentStatus.VoiceDetected] = [SegmentStatus.AsrPending, SegmentStatus.Dropped],
        [SegmentStatus.AsrPending] = [SegmentStatus.AsrDone, SegmentStatus.AsrFailed, SegmentStatus.Dropped],
        [SegmentStatus.AsrDone] = [SegmentStatus.TranslatePending, SegmentStatus.Dropped],
        [SegmentStatus.TranslatePending] = [SegmentStatus.Translated, SegmentStatus.TranslateFailed, SegmentStatus.Dropped, SegmentStatus.Merged],
        [SegmentStatus.Translated] = [SegmentStatus.Displayed],
        // Bản dịch lỗi có thể được thử lại khi Ollama kết nối lại hoặc người dùng bấm retry.
        [SegmentStatus.TranslateFailed] = [SegmentStatus.TranslatePending, SegmentStatus.Displayed],
        [SegmentStatus.Displayed] = [],
        [SegmentStatus.AsrFailed] = [],
        [SegmentStatus.Dropped] = [],
        [SegmentStatus.Merged] = [],
    };

    public static bool CanTransition(SegmentStatus from, SegmentStatus to) =>
        Allowed.TryGetValue(from, out var targets) && Array.IndexOf(targets, to) >= 0;

    public static SegmentStatus Transition(SegmentStatus from, SegmentStatus to) =>
        CanTransition(from, to) ? to : throw new InvalidSegmentTransitionException(from, to);
}

public sealed class InvalidSegmentTransitionException(SegmentStatus from, SegmentStatus to)
    : InvalidOperationException($"Không thể chuyển segment từ {from} sang {to}.")
{
    public SegmentStatus From { get; } = from;
    public SegmentStatus To { get; } = to;
}
