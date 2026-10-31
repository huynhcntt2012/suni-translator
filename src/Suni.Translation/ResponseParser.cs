using System.Text.Json;
using System.Text.RegularExpressions;

namespace Suni.Translation;

/// <summary>
/// Làm sạch và kiểm tra output của LLM (SDD 5.3). Không bao giờ tự chế nội dung:
/// thất bại thì trả null để hiển thị "bản dịch thất bại".
/// </summary>
public static partial class ResponseParser
{
    public static bool TryParse(string? content, string sourceText, out string? translation)
    {
        translation = null;
        if (string.IsNullOrWhiteSpace(content))
            return false;

        var cleaned = ThinkBlock().Replace(content, "");
        cleaned = CodeFence().Replace(cleaned, "$1").Trim();

        var candidate = ReadTranslation(cleaned);
        if (candidate is null)
        {
            var match = FirstJsonObject().Match(cleaned);
            if (match.Success)
                candidate = ReadTranslation(match.Value);
        }

        candidate = candidate?.Trim();
        if (string.IsNullOrEmpty(candidate) || !IsPlausibleLength(candidate, sourceText))
            return false;

        translation = candidate;
        return true;
    }

    /// <summary>Chặn trường hợp model "giải thích dài" thay vì dịch.</summary>
    public static bool IsPlausibleLength(string translation, string source) =>
        translation.Length <= source.Length * 4 + 40;

    private static string? ReadTranslation(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("translation", out var t)
                && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ThinkBlock();

    [GeneratedRegex(@"```(?:json)?\s*(.*?)\s*```", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex CodeFence();

    [GeneratedRegex(@"\{.*\}", RegexOptions.Singleline)]
    private static partial Regex FirstJsonObject();
}
