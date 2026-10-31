namespace Suni.Core.Domain;

/// <summary>Trạng thái chuẩn của segment (SRS 4.4).</summary>
public enum SegmentStatus
{
    Captured,
    VoiceDetected,
    AsrPending,
    AsrDone,
    TranslatePending,
    Translated,
    Displayed,
    AsrFailed,
    TranslateFailed,
    Dropped,

    /// <summary>Mảnh của câu dài đã được gộp vào mảnh cuối để dịch chung (không xuất riêng).</summary>
    Merged,
}

public static class SegmentStatusExtensions
{
    public static bool IsTerminal(this SegmentStatus s) =>
        s is SegmentStatus.Displayed or SegmentStatus.AsrFailed or SegmentStatus.Dropped or SegmentStatus.Merged;

    public static bool IsFailure(this SegmentStatus s) =>
        s is SegmentStatus.AsrFailed or SegmentStatus.TranslateFailed;
}
