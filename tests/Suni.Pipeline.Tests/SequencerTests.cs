namespace Suni.Pipeline.Tests;

public class SequencerTests
{
    private static long[] Seqs<T>(IReadOnlyList<SequencedItem<T>> items) => items.Select(i => i.Seq).ToArray();

    [Fact]
    public void InOrder_EmitsImmediately()
    {
        var s = new Sequencer<string>();
        Assert.Equal([1L], Seqs(s.Offer(1, "a", 0)));
        Assert.Equal([2L], Seqs(s.Offer(2, "b", 10)));
        Assert.Equal(3, s.NextSeq);
    }

    [Fact]
    public void OutOfOrder_HoldsUntilGapFilled()
    {
        var s = new Sequencer<string>();
        Assert.Empty(s.Offer(2, "b", 0));
        Assert.Empty(s.Offer(3, "c", 0));
        Assert.Equal([1L, 2L, 3L], Seqs(s.Offer(1, "a", 100)));
        Assert.Equal(0, s.HeldCount);
    }

    [Fact]
    public void Gap_IsSkippedAfterHoldTimeout()
    {
        var s = new Sequencer<string>(holdMs: 2000);
        Assert.Empty(s.Offer(2, "b", 0));
        Assert.Empty(s.Tick(1999));
        Assert.Equal([2L], Seqs(s.Tick(2000)));
        Assert.Equal(3, s.NextSeq);
    }

    [Fact]
    public void LateArrival_IsEmittedImmediatelyAsLate()
    {
        var s = new Sequencer<string>(holdMs: 2000);
        s.Offer(2, "b", 0);
        s.Tick(2000);

        var late = s.Offer(1, "a", 2500);
        var item = Assert.Single(late);
        Assert.Equal(1, item.Seq);
        Assert.True(item.IsLate);
    }

    [Fact]
    public void Skip_ReleasesWaitingSegmentsWithoutTimeout()
    {
        var s = new Sequencer<string>();
        Assert.Empty(s.Offer(2, "b", 0));
        Assert.Equal([2L], Seqs(s.Skip(1, 10)));
    }

    [Fact]
    public void Skip_InFuture_IsRememberedUntilReached()
    {
        var s = new Sequencer<string>();
        s.Skip(2, 0);
        Assert.Equal([1L], Seqs(s.Offer(1, "a", 0)));
        Assert.Equal([3L], Seqs(s.Offer(3, "c", 0)));
    }
}
