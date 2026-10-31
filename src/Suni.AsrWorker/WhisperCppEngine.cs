using System.Diagnostics;
using System.Text;
using Suni.Asr.Protocol;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace Suni.AsrWorker;

/// <summary>whisper.cpp qua Whisper.net. Một processor dùng lại cho mọi đoạn; worker xử lý tuần tự.</summary>
internal sealed class WhisperCppEngine : IDisposable
{
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private InitMessage? _init;
    private string _language = "auto";

    public string Runtime { get; private set; } = "";

    public int Load(InitMessage init)
    {
        var modelPath = Path.Combine(init.ModelDir, init.Model);
        if (!File.Exists(modelPath))
            throw new WorkerException("ASR_MODEL_MISSING", $"Không tìm thấy model: {modelPath}");

        // Phải đặt trước lần tạo factory đầu tiên; GPU luôn có CPU làm dự phòng.
        RuntimeOptions.RuntimeLibraryOrder = init.Device switch
        {
            "vulkan" => [RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu],
            "cuda" => [RuntimeLibrary.Cuda, RuntimeLibrary.Vulkan, RuntimeLibrary.Cpu],
            _ => [RuntimeLibrary.Cpu, RuntimeLibrary.CpuNoAvx],
        };

        var sw = Stopwatch.StartNew();
        _factory = WhisperFactory.FromPath(modelPath, new WhisperFactoryOptions { UseGpu = init.Device != "cpu" });
        _init = init;
        _processor = Build(prompt: null);
        Runtime = $"{RuntimeOptions.LoadedLibrary}";
        return (int)sw.ElapsedMilliseconds;
    }

    public ResultMessage Transcribe(TranscribeMessage request, float[] pcm)
    {
        if (_processor is null)
            throw new WorkerException("ASR_MODEL_MISSING", "Chưa nạp model.");

        var sw = Stopwatch.StartNew();
        var language = request.Language ?? "auto";

        // Prompt thay đổi theo từng đoạn → cần processor riêng (whisper.cpp nhận prompt lúc build).
        var processor = request.InitialPrompt is { Length: > 0 } prompt ? Build(prompt) : _processor;
        try
        {
            if (processor == _processor && language != _language)
            {
                processor.ChangeLanguage(language);
                _language = language;
            }
            else if (processor != _processor)
            {
                processor.ChangeLanguage(language);
            }

            var text = new StringBuilder();
            string? detected = null;
            float probSum = 0, noSpeech = 0;
            var count = 0;
            foreach (var seg in Collect(processor, pcm))
            {
                text.Append(seg.Text);
                detected ??= seg.Language;
                probSum += seg.Probability;
                noSpeech = Math.Max(noSpeech, seg.NoSpeechProbability);
                count++;
            }

            return new ResultMessage(
                request.SegmentId,
                text.ToString().Trim(),
                detected ?? (language == "auto" ? "" : language),
                LanguageProbability: -1, // whisper.cpp không trả xác suất ngôn ngữ trong lượt decode
                AvgProbability: count == 0 ? 0 : probSum / count,
                NoSpeechProbability: noSpeech,
                ElapsedMs: (int)sw.ElapsedMilliseconds);
        }
        finally
        {
            if (processor != _processor)
                processor.Dispose();
        }
    }

    private static List<SegmentData> Collect(WhisperProcessor processor, float[] pcm)
    {
        var list = new List<SegmentData>();
        var task = Task.Run(async () =>
        {
            await foreach (var seg in processor.ProcessAsync(pcm))
                list.Add(seg);
        });
        task.GetAwaiter().GetResult();
        return list;
    }

    private WhisperProcessor Build(string? prompt)
    {
        var init = _init!;
        var builder = _factory!.CreateBuilder()
            .WithLanguage(_language)
            .WithNoContext()          // mỗi đoạn độc lập; ngữ cảnh truyền qua prompt
            .WithProbabilities()
            .WithTemperature(0f);
        if (init.Threads > 0)
            builder = builder.WithThreads(init.Threads);
        if (init.AudioContext > 0)
            builder = builder.WithAudioContextSize(init.AudioContext);
        if (init.BeamSize > 1)
            builder = builder.WithBeamSearchSamplingStrategy(b => b.WithBeamSize(init.BeamSize));
        if (prompt is not null)
            builder = builder.WithPrompt(prompt);
        return builder.Build();
    }

    public void Dispose()
    {
        _processor?.Dispose();
        _factory?.Dispose();
    }
}

internal sealed class WorkerException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}
