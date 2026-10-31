using Suni.Audio.Processing;
using Suni.Core.Abstractions;
using Suni.Core.Domain;
using Suni.Core.Settings;

namespace Suni.Audio;

public sealed class AudioCaptureFactory : IAudioCaptureFactory
{
    public IAudioCapture Create(AudioSource source) => source.Mode switch
    {
        CaptureMode.ProcessLoopback => WasapiLoopbackCapture.ForProcess(
            source.CapturePid ?? source.RootPid ?? throw new ArgumentException("Nguồn ứng dụng thiếu PID.", nameof(source)),
            includeTree: true),
        CaptureMode.SystemExcludeSelf => WasapiLoopbackCapture.ForSystemExcludingSelf(),
        CaptureMode.EndpointLoopback => WasapiLoopbackCapture.ForEndpoint(),
        _ => throw new NotSupportedException($"Chế độ thu {source.Mode} chưa hỗ trợ."),
    };
}

/// <summary>Downsampler → Silero VAD → VadSegmenter cho một phiên.</summary>
public sealed class SileroSpeechDetector : ISpeechDetector
{
    private readonly Downsampler _down;
    private readonly SileroVad _vad;
    private readonly VadSegmenter _segmenter;
    private float[] _mono = [];

    public SileroSpeechDetector(AudioFormat format, VadSettings settings)
    {
        _down = new Downsampler(format.SampleRate, format.Channels);
        _vad = new SileroVad();
        _segmenter = new VadSegmenter(_vad, settings);
    }

    public bool IsSpeaking => _segmenter.IsSpeaking;

    public IReadOnlyList<SpeechChunk> Push(ReadOnlySpan<float> interleaved)
    {
        var max = _down.MaxOutputLength(interleaved.Length);
        if (_mono.Length < max)
            _mono = new float[max];
        var n = _down.Process(interleaved, _mono);
        return _segmenter.Push(_mono.AsSpan(0, n));
    }

    public IReadOnlyList<SpeechChunk> Flush() => _segmenter.Flush();

    public void Dispose() => _vad.Dispose();
}

public sealed class SileroSpeechDetectorFactory : ISpeechDetectorFactory
{
    public ISpeechDetector Create(AudioFormat captureFormat, VadSettings settings) => new SileroSpeechDetector(captureFormat, settings);
}
