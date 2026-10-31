namespace Suni.Audio;

/// <summary>Đo peak/RMS (dBFS) cho đồng hồ tín hiệu (FR-04).</summary>
public static class LevelMeter
{
    public const float FloorDb = -90f;

    public static (float PeakDb, float RmsDb) Measure(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
            return (FloorDb, FloorDb);

        float peak = 0;
        double sumSquares = 0;
        foreach (var s in samples)
        {
            var a = MathF.Abs(s);
            if (a > peak)
                peak = a;
            sumSquares += s * s;
        }
        return (ToDb(peak), ToDb((float)Math.Sqrt(sumSquares / samples.Length)));
    }

    public static float ToDb(float linear) =>
        linear <= 0 ? FloorDb : Math.Max(FloorDb, 20f * MathF.Log10(linear));
}
