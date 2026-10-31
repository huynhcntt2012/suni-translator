namespace Suni.Tts;

/// <summary>
/// Đọc bản dịch bằng giọng offline (FR-23..24). Bản đầu dùng giọng có sẵn trong Windows (OneCore) — không tải thêm,
/// không vướng giấy phép; Piper (GPL-3.0) chỉ xem xét sau, chạy process riêng và cần pháp chế duyệt.
/// </summary>
public interface ITtsEngine
{
    Task<IReadOnlyList<TtsVoice>> ListVoicesAsync(CancellationToken ct = default);

    Task<IReadOnlyList<TtsOutputDevice>> ListOutputDevicesAsync(CancellationToken ct = default);

    /// <summary>Tổng hợp và phát; hoàn tất khi phát xong. Huỷ = dừng ngay.</summary>
    Task SpeakAsync(string text, TtsOptions options, CancellationToken ct = default);
}

public sealed record TtsVoice(string Id, string Language, string DisplayName);

public sealed record TtsOutputDevice(string Id, string Name, bool IsDefault);

/// <param name="VoiceId">null = giọng mặc định của Windows.</param>
/// <param name="OutputDeviceId">null = thiết bị phát mặc định.</param>
/// <param name="Rate">0,5–3 (1 = bình thường).</param>
/// <param name="Volume">0–1.</param>
public sealed record TtsOptions(string? VoiceId, string? OutputDeviceId, double Rate = 1, double Volume = 1);

/// <summary>Chọn giọng: giọng người dùng chọn nếu còn tồn tại, không thì giọng đầu tiên đúng ngôn ngữ đích.</summary>
public static class VoicePicker
{
    public static TtsVoice? Pick(IReadOnlyList<TtsVoice> voices, string? preferredId, string targetLang)
    {
        if (!string.IsNullOrEmpty(preferredId) && voices.FirstOrDefault(v => v.Id == preferredId) is { } chosen)
            return chosen;
        // "en" khớp "en-GB", "en-US"; ưu tiên khớp đầy đủ nếu ngôn ngữ đích có vùng (vd. "zh-TW").
        return voices.FirstOrDefault(v => string.Equals(v.Language, targetLang, StringComparison.OrdinalIgnoreCase))
               ?? voices.FirstOrDefault(v => v.Language.StartsWith(targetLang + "-", StringComparison.OrdinalIgnoreCase)
                                             || string.Equals(v.Language.Split('-')[0], targetLang.Split('-')[0], StringComparison.OrdinalIgnoreCase));
    }
}
