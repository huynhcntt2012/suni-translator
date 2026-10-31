using Suni.Core.Abstractions;
using Suni.Core.Settings;

namespace Suni.Storage.Tests;

public class JsonSettingsStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"suni_settings_{Guid.NewGuid():N}", "settings.json");

    [Fact]
    public async Task Missing_ReturnsDefaults()
    {
        var s = await new JsonSettingsStore(_path).LoadAsync();
        Assert.Equal(new AppSettings().Translation, s.Translation);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTrips()
    {
        var store = new JsonSettingsStore(_path);
        var defaults = new AppSettings();
        var settings = defaults with
        {
            Capture = defaults.Capture with { LastSourceExe = "chrome.exe" },
            Asr = defaults.Asr with { Model = "large-v3-turbo-q5_0", Device = "vulkan", SourceLang = "ja" },
            Translation = defaults.Translation with
            {
                OllamaUrl = "http://192.168.1.96:12121", Model = "qwen3.5:9b", TargetLang = "vi", Style = TranslationStyle.Literal,
            },
        };

        await store.SaveAsync(settings);
        var loaded = await store.LoadAsync();

        Assert.Equal(settings.Capture, loaded.Capture);
        Assert.Equal(settings.Asr, loaded.Asr);
        Assert.Equal(settings.Translation, loaded.Translation);
        Assert.Contains("\"style\": \"literal\"", await File.ReadAllTextAsync(_path));
        Assert.False(File.Exists(_path + ".tmp"));
    }

    [Fact]
    public async Task CorruptFile_ReturnsDefaults()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        await File.WriteAllTextAsync(_path, "{ not json");
        var s = await new JsonSettingsStore(_path).LoadAsync();
        Assert.Equal(new AppSettings().Asr, s.Asr);
    }

    public void Dispose()
    {
        var dir = Path.GetDirectoryName(_path)!;
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
    }
}
