namespace Suni.Tts;

/// <summary>
/// Phát hiện ASR nghe lại chính giọng đọc (nguồn thu vô tình gồm cả Suni, vd. endpoint loopback):
/// câu gốc mới trùng một câu vừa đọc trong <see cref="Window"/> gần đây thì coi là tiếng vọng, không đọc lại —
/// chặn vòng lặp đọc → thu → đọc. So sánh bỏ dấu câu, hoa/thường và khoảng trắng.
/// </summary>
public sealed class EchoGuard
{
    private readonly Queue<(string Text, DateTimeOffset At)> _recent = new();

    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(15);

    public void Spoken(string text, DateTimeOffset at)
    {
        lock (_recent)
        {
            _recent.Enqueue((Normalize(text), at));
            while (_recent.Count > 10)
                _recent.Dequeue();
        }
    }

    public bool IsEcho(string? original, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(original))
            return false;
        var key = Normalize(original);
        if (key.Length == 0)
            return false;
        lock (_recent)
            return _recent.Any(r => now - r.At <= Window && (r.Text == key || (key.Length >= 8 && r.Text.Contains(key))));
    }

    private static string Normalize(string s) =>
        new string(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
}
