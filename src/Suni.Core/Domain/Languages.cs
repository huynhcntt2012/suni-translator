namespace Suni.Core.Domain;

public sealed record Language(string Code, string EnglishName, string NativeName);

/// <summary>Ngôn ngữ ưu tiên thử nghiệm (FR-10). Mã theo ISO 639-1, trùng mã Whisper.</summary>
public static class Languages
{
    public static readonly IReadOnlyList<Language> Supported =
    [
        new("vi", "Vietnamese", "Tiếng Việt"),
        new("en", "English", "English"),
        new("ja", "Japanese", "日本語"),
        new("ko", "Korean", "한국어"),
        new("zh", "Chinese", "中文"),
        new("fr", "French", "Français"),
        new("de", "German", "Deutsch"),
        new("es", "Spanish", "Español"),
    ];

    public static Language? Find(string? code) =>
        code is null ? null : Supported.FirstOrDefault(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));

    /// <summary>Tên tiếng Anh để đưa vào prompt; ngôn ngữ ngoài danh sách thì dùng nguyên mã.</summary>
    public static string EnglishNameOf(string code) => Find(code)?.EnglishName ?? code;

    /// <summary>Ngôn ngữ viết không dùng khoảng trắng giữa từ (ảnh hưởng ngắt dòng, khử trùng lặp).</summary>
    public static bool IsCjk(string? code) => code is "ja" or "zh" or "ko";
}
