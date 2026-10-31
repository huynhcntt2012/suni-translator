using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Suni.Core.Settings;

namespace Suni.Storage;

/// <summary>
/// Lưu cài đặt ra file JSON (%LOCALAPPDATA%\Suni\settings.json). Thiết kế đích là bảng <c>settings</c> trong SQLite;
/// dùng file trong lúc chưa có repository — cùng interface <see cref="ISettingsStore"/>.
/// File hỏng hoặc thiếu → trả về mặc định, không chặn việc mở app.
/// </summary>
public sealed class JsonSettingsStore(string path) : ISettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static string DefaultPath => Path.Combine(SuniDatabase.DataRoot, "settings.json");

    public async Task<AppSettings> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(path))
            return new AppSettings();
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<AppSettings>(stream, Options, ct).ConfigureAwait(false) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        await using (var stream = File.Create(temp))
            await JsonSerializer.SerializeAsync(stream, settings, Options, ct).ConfigureAwait(false);
        File.Move(temp, path, overwrite: true); // ghi nguyên tử: không để lại file dở nếu app tắt giữa chừng
    }
}
