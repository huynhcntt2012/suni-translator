using System.IO;

namespace Suni.App.Services;

/// <summary>
/// Vị trí model và worker. Bản cài đặt: cạnh exe / %LOCALAPPDATA%\Suni. Khi chạy từ mã nguồn:
/// dò lên tới thư mục chứa SuniTranslator.slnx và dùng models/ + bin của Suni.AsrWorker.
/// </summary>
public static class AppPaths
{
    private static readonly string? RepoRoot = FindRepoRoot();

    public static string AsrModelDirectory
    {
        get
        {
            // Thứ tự: model người dùng tải thêm (%LOCALAPPDATA%) → model đóng gói cạnh exe → thư mục models/ của repo (khi chạy từ mã nguồn).
            var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                         "Suni", "models", "asr", "whisper.cpp");
            if (HasModels(installed))
                return installed;
            var bundled = Path.Combine(AppContext.BaseDirectory, "models", "asr", "whisper.cpp");
            if (HasModels(bundled))
                return bundled;
            return RepoRoot is null ? installed : Path.Combine(RepoRoot, "models", "asr", "whisper.cpp");
        }
    }

    public static string AsrWorkerPath
    {
        get
        {
            var bundled = Path.Combine(AppContext.BaseDirectory, "workers", "whisper", "Suni.AsrWorker.exe");
            if (File.Exists(bundled) || RepoRoot is null)
                return bundled;
#if DEBUG
            const string config = "Debug";
#else
            const string config = "Release";
#endif
            return Path.Combine(RepoRoot, "src", "Suni.AsrWorker", "bin", config, "net10.0", "Suni.AsrWorker.exe");
        }
    }

    /// <summary>Các model whisper có sẵn: "ggml-small-q5_1.bin" → "small-q5_1".</summary>
    public static IReadOnlyList<string> AvailableAsrModels()
    {
        var dir = AsrModelDirectory;
        if (!Directory.Exists(dir))
            return [];
        return Directory.EnumerateFiles(dir, "ggml-*.bin")
            .Select(f => Path.GetFileNameWithoutExtension(f)["ggml-".Length..])
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool HasModels(string dir) => Directory.Exists(dir) && Directory.EnumerateFiles(dir, "ggml-*.bin").Any();

    private static string? FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "SuniTranslator.slnx")))
                return dir.FullName;
        return null;
    }
}
