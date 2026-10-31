namespace Suni.Core.Domain;

/// <summary>Sinh ID có thứ tự thời gian (UUID v7) với tiền tố dễ đọc.</summary>
public static class Ids
{
    public static string NewSessionId() => "sess_" + Guid.CreateVersion7().ToString("N");

    public static string NewSegmentId() => "seg_" + Guid.CreateVersion7().ToString("N");
}
