namespace Suni.Audio.Processing;

/// <summary>
/// Downmix về mono + hạ tần số lấy mẫu theo hệ số nguyên (48 kHz → 16 kHz cho ASR/VAD).
/// FIR low-pass windowed-sinc (Blackman), tính theo kiểu polyphase: chỉ tính mẫu đầu ra.
/// Xử lý dạng stream: kết quả không phụ thuộc cách chia khối đầu vào.
/// Độ trễ nhóm = (Taps - 1) / 2 mẫu đầu vào (~2 ms ở 48 kHz với 191 tap); băng thông ~6,5 kHz, chặn từ ~8 kHz.
/// </summary>
public sealed class Downsampler
{
    private readonly int _channels;
    private readonly int _factor;
    private readonly float[] _taps;
    private readonly float[] _history;  // vòng lặp các mẫu mono gần nhất, độ dài = số tap
    private int _historyPos;
    private int _phase;                 // số mẫu đầu vào đã qua kể từ mẫu đầu ra gần nhất

    public Downsampler(int inputRate, int channels, int outputRate = 16_000, int taps = 191)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        if (inputRate % outputRate != 0)
            throw new NotSupportedException($"Chỉ hỗ trợ tỉ lệ nguyên ({inputRate} → {outputRate}).");

        InputRate = inputRate;
        OutputRate = outputRate;
        _channels = channels;
        _factor = inputRate / outputRate;
        _taps = _factor == 1 ? [1f] : DesignLowPass(taps, cutoffHz: 0.45 * outputRate, inputRate);
        _history = new float[_taps.Length];
    }

    public int InputRate { get; }
    public int OutputRate { get; }
    public int Factor => _factor;
    public int DelayInputSamples => (_taps.Length - 1) / 2;

    /// <summary>Số mẫu đầu ra tối đa cho <paramref name="interleavedSamples"/> mẫu đầu vào.</summary>
    public int MaxOutputLength(int interleavedSamples) => interleavedSamples / _channels / _factor + 1;

    /// <summary>Trả về số mẫu mono đã ghi vào <paramref name="output"/>.</summary>
    public int Process(ReadOnlySpan<float> interleaved, Span<float> output)
    {
        var frames = interleaved.Length / _channels;
        var written = 0;
        var n = _taps.Length;
        var scale = 1f / _channels;

        for (var f = 0; f < frames; f++)
        {
            float mono = 0;
            for (var c = 0; c < _channels; c++)
                mono += interleaved[f * _channels + c];

            _history[_historyPos] = mono * scale;
            _historyPos = _historyPos + 1 == n ? 0 : _historyPos + 1;

            if (++_phase < _factor)
                continue;
            _phase = 0;

            // Tích chập: _history[_historyPos] là mẫu cũ nhất.
            float acc = 0;
            var idx = _historyPos;
            for (var k = n - 1; k >= 0; k--)
            {
                acc += _taps[k] * _history[idx];
                idx = idx + 1 == n ? 0 : idx + 1;
            }
            output[written++] = acc;
        }
        return written;
    }

    public void Reset()
    {
        Array.Clear(_history);
        _historyPos = 0;
        _phase = 0;
    }

    private static float[] DesignLowPass(int taps, double cutoffHz, int sampleRate)
    {
        if (taps % 2 == 0)
            taps++;
        var h = new double[taps];
        var fc = cutoffHz / sampleRate;
        var m = taps - 1;
        double sum = 0;
        for (var i = 0; i < taps; i++)
        {
            var x = i - m / 2.0;
            var sinc = x == 0 ? 2 * fc : Math.Sin(2 * Math.PI * fc * x) / (Math.PI * x);
            var blackman = 0.42 - 0.5 * Math.Cos(2 * Math.PI * i / m) + 0.08 * Math.Cos(4 * Math.PI * i / m);
            h[i] = sinc * blackman;
            sum += h[i];
        }
        return h.Select(v => (float)(v / sum)).ToArray(); // độ lợi DC = 1
    }
}
