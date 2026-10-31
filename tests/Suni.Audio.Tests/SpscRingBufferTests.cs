namespace Suni.Audio.Tests;

public class SpscRingBufferTests
{
    [Fact]
    public void WriteThenRead_ReturnsSameData_AcrossWrapAround()
    {
        var rb = new SpscRingBuffer<float>(8);
        var dst = new float[8];

        rb.Write([1, 2, 3, 4, 5, 6]);
        Assert.Equal(4, rb.Read(dst.AsSpan(0, 4)));
        rb.Write([7, 8, 9, 10]); // vượt qua cuối mảng

        var n = rb.Read(dst);
        Assert.Equal([5f, 6, 7, 8, 9, 10], dst[..n]);
        Assert.False(rb.Overflowed);
    }

    [Fact]
    public void Overflow_DropsOldest_AndSetsFlagOnce()
    {
        var rb = new SpscRingBuffer<float>(4);
        rb.Write([1, 2, 3]);
        rb.Write([4, 5, 6]);

        var dst = new float[4];
        Assert.Equal(4, rb.Read(dst));
        Assert.Equal([3f, 4, 5, 6], dst);
        Assert.True(rb.Overflowed);
        Assert.False(rb.Overflowed);
    }

    [Fact]
    public void Read_WhenEmpty_ReturnsZero() => Assert.Equal(0, new SpscRingBuffer<float>(4).Read(new float[4]));

    [Fact]
    public async Task ConcurrentProducerConsumer_PreservesOrder()
    {
        const int total = 200_000;
        var rb = new SpscRingBuffer<float>(total); // đủ lớn để không ghi đè
        var producer = Task.Run(() =>
        {
            var chunk = new float[97];
            for (var i = 0; i < total; i += chunk.Length)
            {
                var len = Math.Min(chunk.Length, total - i);
                for (var j = 0; j < len; j++)
                    chunk[j] = i + j;
                rb.Write(chunk.AsSpan(0, len));
            }
        });

        var expected = 0f;
        var buf = new float[64];
        while (expected < total)
        {
            var n = rb.Read(buf);
            for (var j = 0; j < n; j++)
                Assert.Equal(expected++, buf[j]);
            if (n == 0)
                await Task.Yield();
        }
        await producer;
    }
}
