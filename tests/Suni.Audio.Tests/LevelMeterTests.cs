namespace Suni.Audio.Tests;

public class LevelMeterTests
{
    [Fact]
    public void Silence_IsFloor()
    {
        var (peak, rms) = LevelMeter.Measure(new float[480]);
        Assert.Equal(LevelMeter.FloorDb, peak);
        Assert.Equal(LevelMeter.FloorDb, rms);
    }

    [Fact]
    public void FullScaleSquare_IsZeroDb()
    {
        var samples = Enumerable.Range(0, 480).Select(i => i % 2 == 0 ? 1f : -1f).ToArray();
        var (peak, rms) = LevelMeter.Measure(samples);
        Assert.Equal(0f, peak, 3);
        Assert.Equal(0f, rms, 3);
    }

    [Fact]
    public void HalfScaleSine_HasExpectedPeakAndRms()
    {
        var samples = Enumerable.Range(0, 48_000).Select(i => 0.5f * MathF.Sin(2 * MathF.PI * 1000 * i / 48_000)).ToArray();
        var (peak, rms) = LevelMeter.Measure(samples);
        Assert.Equal(-6.02f, peak, 1);
        Assert.Equal(-9.03f, rms, 1);
    }
}
