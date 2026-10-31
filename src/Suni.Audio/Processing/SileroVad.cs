using Microsoft.ML.OnnxRuntime;

namespace Suni.Audio.Processing;

/// <summary>Mô hình cho xác suất có lời nói trên từng khung PCM 16 kHz mono.</summary>
public interface IVoiceActivityModel
{
    /// <summary>Số mẫu mỗi khung.</summary>
    int FrameSize { get; }

    float Process(ReadOnlySpan<float> frame);

    void Reset();
}

/// <summary>
/// Silero VAD v5 (ONNX, MIT) chạy CPU một luồng. Đầu vào mỗi lần: 64 mẫu ngữ cảnh + 512 mẫu mới (32 ms @16 kHz);
/// trạng thái RNN [2,1,128] được mang sang khung sau. Không thread-safe.
/// </summary>
public sealed class SileroVad : IVoiceActivityModel, IDisposable
{
    public const int SampleRate = 16_000;
    private const int Context = 64;
    private const int Frame = 512;

    private readonly InferenceSession _session;
    private readonly RunOptions _runOptions = new();
    private readonly float[] _input = new float[Context + Frame];
    private readonly float[] _state = new float[2 * 1 * 128];
    private readonly long[] _sr = [SampleRate];
    private readonly string[] _outputNames = ["output", "stateN"];

    public SileroVad(string? modelPath = null)
    {
        modelPath ??= DefaultModelPath;
        if (!File.Exists(modelPath))
            throw new FileNotFoundException("Không tìm thấy model Silero VAD.", modelPath);

        using var options = new SessionOptions
        {
            IntraOpNumThreads = 1,
            InterOpNumThreads = 1,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };
        _session = new InferenceSession(modelPath, options);
    }

    public static string DefaultModelPath => Path.Combine(AppContext.BaseDirectory, "models", "vad", "silero_vad.onnx");

    public int FrameSize => Frame;

    public float Process(ReadOnlySpan<float> frame)
    {
        if (frame.Length != Frame)
            throw new ArgumentException($"Khung phải có đúng {Frame} mẫu.", nameof(frame));

        frame.CopyTo(_input.AsSpan(Context));

        using var input = OrtValue.CreateTensorValueFromMemory(_input, [1, Context + Frame]);
        using var state = OrtValue.CreateTensorValueFromMemory(_state, [2, 1, 128]);
        using var sr = OrtValue.CreateTensorValueFromMemory(_sr, []);
        var inputs = new Dictionary<string, OrtValue> { ["input"] = input, ["state"] = state, ["sr"] = sr };

        using var outputs = _session.Run(_runOptions, inputs, _outputNames);
        var probability = outputs[0].GetTensorDataAsSpan<float>()[0];
        outputs[1].GetTensorDataAsSpan<float>().CopyTo(_state);

        // Ngữ cảnh cho khung sau = 64 mẫu cuối của đầu vào lần này.
        _input.AsSpan(Frame, Context).CopyTo(_input);
        return probability;
    }

    public void Reset()
    {
        Array.Clear(_state);
        Array.Clear(_input);
    }

    public void Dispose()
    {
        _runOptions.Dispose();
        _session.Dispose();
    }
}
