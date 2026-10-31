namespace Suni.Audio.Tests;

public class WavFileTests
{
    [Fact]
    public void WriteThenRead_RoundTripsFloat32()
    {
        var path = Path.Combine(Path.GetTempPath(), $"suni_{Guid.NewGuid():N}.wav");
        try
        {
            float[] samples = [0f, 0.5f, -0.5f, 1f, -1f, 0.25f];
            using (var w = new WavWriter(path, 48_000, 2))
            {
                w.Write(samples.AsSpan(0, 4));
                w.Write(samples.AsSpan(4));
            }

            var wav = WavReader.Read(path);
            Assert.Equal(48_000, wav.SampleRate);
            Assert.Equal(2, wav.Channels);
            Assert.Equal(samples, wav.Samples);
            Assert.Equal(44 + samples.Length * 4, new FileInfo(path).Length);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
