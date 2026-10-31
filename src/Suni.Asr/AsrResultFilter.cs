using System.Text.RegularExpressions;
using Suni.Core.Abstractions;

namespace Suni.Asr;

/// <summary>
/// Lọc kết quả ASR trước khi dịch (SDD 4.4, TC-04): whisper sinh chú thích như "(upbeat music)", "[BLANK_AUDIO]",
/// "♪" hoặc câu quen thuộc ("Thanks for watching") khi đoạn không có lời. Trả về text sạch, hoặc null nếu nên bỏ đoạn.
/// </summary>
public static partial class AsrResultFilter
{
    public const float NoSpeechThreshold = 0.6f;

    /// <summary>
    /// Xác suất token trung bình tối thiểu. Benchmark P0: lời thật ≥ 0,53 (114 đoạn), tiếng ồn bị đoán là
    /// tiếng Na Uy "nn" chỉ 0,08–0,31. Cần kiểm lại với giọng người thật có nhiễu.
    /// </summary>
    public const float MinAvgProbability = 0.4f;

    /// <summary>Câu whisper hay "bịa" từ dữ liệu huấn luyện phụ đề; chỉ loại khi đó là TOÀN BỘ nội dung đoạn.</summary>
    private static readonly string[] KnownHallucinations =
    [
        "thanks for watching", "thank you for watching", "thank you", "please subscribe", "subtitles by", "bye",
        "ご視聴ありがとうございました", "字幕", "감사합니다", "시청해주셔서 감사합니다", "谢谢观看", "请不吝点赞",
        "cảm ơn các bạn đã theo dõi", "hẹn gặp lại các bạn",
    ];

    public static string? Clean(AsrResult result) => Clean(result.Text, result.NoSpeechProb, result.AvgProbability);

    public static string? Clean(string text, float noSpeechProbability, float avgProbability = 1f)
    {
        if (avgProbability < MinAvgProbability)
            return null;

        var cleaned = Annotation().Replace(text, " ");
        cleaned = Spaces().Replace(cleaned, " ").Trim();
        if (cleaned.Length == 0 || cleaned.All(c => char.IsPunctuation(c) || char.IsSymbol(c) || char.IsWhiteSpace(c)))
            return null;

        var normalized = cleaned.Trim('.', '!', '?', '。', '！', ' ').ToLowerInvariant();
        if (KnownHallucinations.Any(h => normalized == h))
            return null;

        // Xác suất "không có lời" cao + nội dung ngắn: nhiều khả năng là bịa.
        if (noSpeechProbability >= NoSpeechThreshold && cleaned.Length < 20)
            return null;

        if (IsRepetitive(cleaned))
            return null;
        return cleaned;
    }

    /// <summary>Một cụm lặp ≥ 3 lần liên tiếp chiếm gần hết đoạn (lỗi lặp vô hạn của whisper).</summary>
    private static bool IsRepetitive(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var size = 1; size <= 4 && size * 3 <= words.Length; size++)
        {
            var repeats = 1;
            for (var i = size; i + size <= words.Length; i += size)
            {
                if (words.AsSpan(i, size).SequenceEqual(words.AsSpan(i - size, size)))
                {
                    if (++repeats >= 3 && repeats * size >= words.Length * 0.8)
                        return true;
                }
                else
                {
                    repeats = 1;
                }
            }
        }
        return false;
    }

    // (music), [BLANK_AUDIO], *laughs*, ♪ ... ♪, （拍手）, 【音楽】
    [GeneratedRegex(@"\([^)]*\)|\[[^\]]*\]|\*[^*]*\*|♪[^♪]*♪|[♪♫]|（[^）]*）|【[^】]*】")]
    private static partial Regex Annotation();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
