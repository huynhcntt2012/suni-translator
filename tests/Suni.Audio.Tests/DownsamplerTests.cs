using Suni.Audio.Processing;

namespace Suni.Audio.Tests;

public class DownsamplerTests
{
    private static float[] StereoTone(double freq, double seconds, float amp = 0.5f, int rate = 48_000)
    {
        var frames = (int)(rate * seconds);
        var x = new float[frames * 2];
        for (var i = 0; i < frames; i++)
            x[2 * i] = x[2 * i + 1] = amp * MathF.Sin((float)(2 * Math.PI * freq * i / rate));
        return x;
    }

    private static float[] Run(Downsampler d, float[] input)
    {
        var output = new float[d.MaxOutputLength(input.Length)];
        var n = d.Process(input, output);
        return output[..n];
    }

    /// <summary>Biên độ RMS×√2, bỏ phần quá độ đầu.</summary>
    private static float Amplitude(float[] x, int skip = 1000) =>
        MathF.Sqrt(x.Skip(skip).Select(v => v * v).Average()) * MathF.Sqrt(2);

    [Fact]
    public void OutputLength_IsInputFramesDividedByFactor()
    {
        var d = new Downsampler(48_000, 2);
        Assert.Equal(16_000, Run(d, StereoTone(440, 1)).Length);
        Assert.Equal(3, d.Factor);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(1_000)]
    [InlineData(3_400)]
    [InlineData(6_500)]
    public void Passband_IsPreserved(double freq)
    {
        var y = Run(new Downsampler(48_000, 2), StereoTone(freq, 1));
        Assert.InRange(LevelMeter.ToDb(Amplitude(y) / 0.5f), -0.5f, 0.2f);
    }

    [Theory]
    [InlineData(9_000)]
    [InlineData(12_000)]
    [InlineData(20_000)]
    public void AboveNyquist_IsRejected(double freq)
    {
        // Nếu không lọc, các tần số này sẽ bị gập (alias) vào băng 0–8 kHz và làm bẩn ASR/VAD.
        var y = Run(new Downsampler(48_000, 2), StereoTone(freq, 1));
        Assert.True(LevelMeter.ToDb(Amplitude(y) / 0.5f) < -60, $"{freq} Hz còn {LevelMeter.ToDb(Amplitude(y) / 0.5f):F1} dB");
    }

    [Fact]
    public void Downmix_AveragesChannels()
    {
        var x = new float[48_000 * 2];
        for (var i = 0; i < 48_000; i++)
        {
            var s = 0.4f * MathF.Sin(2 * MathF.PI * 500 * i / 48_000);
            x[2 * i] = s;      // trái
            x[2 * i + 1] = 0;  // phải im lặng
        }
        var y = Run(new Downsampler(48_000, 2), x);
        Assert.Equal(0.2f, Amplitude(y), 0.01f);
    }

    [Fact]
    public void ChunkedProcessing_EqualsOneShot()
    {
        var input = StereoTone(1_234, 0.5);
        var expected = Run(new Downsampler(48_000, 2), input);

        var d = new Downsampler(48_000, 2);
        var actual = new List<float>();
        var rng = new Random(42);
        var buffer = new float[d.MaxOutputLength(input.Length)];
        for (var pos = 0; pos < input.Length;)
        {
            var len = Math.Min(input.Length - pos, 2 * rng.Next(1, 700)); // giữ trọn frame stereo
            var n = d.Process(input.AsSpan(pos, len), buffer);
            actual.AddRange(buffer[..n]);
            pos += len;
        }
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void NonIntegerRatio_IsRejected() =>
        Assert.Throws<NotSupportedException>(() => new Downsampler(44_100, 2));

    [Fact]
    public void SameRate_IsPassThroughMono()
    {
        var y = Run(new Downsampler(16_000, 1), [0.1f, 0.2f, -0.3f]);
        Assert.Equal([0.1f, 0.2f, -0.3f], y);
    }
}
