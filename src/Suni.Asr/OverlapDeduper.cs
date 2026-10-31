namespace Suni.Asr;

/// <summary>
/// Ghép text của đoạn bị ép cắt với đoạn kế tiếp (SDD 3.3): hai đoạn chồng ~300 ms nên đầu đoạn sau
/// thường lặp lại 1–3 từ cuối đoạn trước. Bản đầu theo từ (ngôn ngữ có khoảng trắng); CJK theo ký tự.
/// </summary>
public static class OverlapDeduper
{
    public const int MaxOverlapWords = 4;

    /// <summary>Trả về phần của <paramref name="next"/> sau khi bỏ tiền tố trùng với hậu tố <paramref name="previous"/>.</summary>
    public static string TrimOverlap(string previous, string next, bool characterBased = false)
    {
        if (string.IsNullOrWhiteSpace(previous) || string.IsNullOrWhiteSpace(next))
            return next.Trim();

        if (characterBased)
        {
            var p = previous.Trim();
            var n = next.Trim();
            for (var len = Math.Min(Math.Min(p.Length, n.Length), MaxOverlapWords * 2); len > 0; len--)
                if (string.Compare(p, p.Length - len, n, 0, len, StringComparison.Ordinal) == 0)
                    return n[len..].TrimStart();
            return n;
        }

        var prevWords = Split(previous);
        var nextWords = Split(next);
        for (var k = Math.Min(MaxOverlapWords, Math.Min(prevWords.Length, nextWords.Length)); k > 0; k--)
        {
            var match = true;
            for (var i = 0; i < k && match; i++)
                match = Normalize(prevWords[prevWords.Length - k + i]) == Normalize(nextWords[i]);
            if (match)
                return string.Join(' ', nextWords[k..]);
        }
        return next.Trim();
    }

    private static string[] Split(string s) => s.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Normalize(string word) =>
        new string(word.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
}
