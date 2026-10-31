using Suni.Core.Domain;

namespace Suni.Core.Tests;

public class SegmentStateMachineTests
{
    [Fact]
    public void HappyPath_IsAllowed()
    {
        SegmentStatus[] path =
        [
            SegmentStatus.Captured, SegmentStatus.VoiceDetected, SegmentStatus.AsrPending, SegmentStatus.AsrDone,
            SegmentStatus.TranslatePending, SegmentStatus.Translated, SegmentStatus.Displayed,
        ];

        for (var i = 1; i < path.Length; i++)
            Assert.True(SegmentStateMachine.CanTransition(path[i - 1], path[i]), $"{path[i - 1]} -> {path[i]}");
    }

    [Theory]
    [InlineData(SegmentStatus.AsrPending, SegmentStatus.AsrFailed)]
    [InlineData(SegmentStatus.TranslatePending, SegmentStatus.TranslateFailed)]
    [InlineData(SegmentStatus.TranslateFailed, SegmentStatus.TranslatePending)]
    [InlineData(SegmentStatus.TranslateFailed, SegmentStatus.Displayed)]
    [InlineData(SegmentStatus.VoiceDetected, SegmentStatus.Dropped)]
    public void FailureAndRetryTransitions_AreAllowed(SegmentStatus from, SegmentStatus to) =>
        Assert.True(SegmentStateMachine.CanTransition(from, to));

    [Theory]
    [InlineData(SegmentStatus.Captured, SegmentStatus.Translated)]
    [InlineData(SegmentStatus.AsrDone, SegmentStatus.AsrPending)]
    [InlineData(SegmentStatus.Translated, SegmentStatus.TranslatePending)]
    [InlineData(SegmentStatus.Displayed, SegmentStatus.Translated)]
    [InlineData(SegmentStatus.Translated, SegmentStatus.Dropped)]
    public void InvalidTransitions_AreRejected(SegmentStatus from, SegmentStatus to)
    {
        Assert.False(SegmentStateMachine.CanTransition(from, to));
        var ex = Assert.Throws<InvalidSegmentTransitionException>(() => SegmentStateMachine.Transition(from, to));
        Assert.Equal(from, ex.From);
        Assert.Equal(to, ex.To);
    }

    [Fact]
    public void TerminalStates_HaveNoOutgoingTransitions()
    {
        foreach (var from in Enum.GetValues<SegmentStatus>().Where(s => s.IsTerminal()))
            foreach (var to in Enum.GetValues<SegmentStatus>())
                Assert.False(SegmentStateMachine.CanTransition(from, to), $"{from} -> {to}");
    }

    [Fact]
    public void EveryStatus_HasTransitionRule()
    {
        foreach (var s in Enum.GetValues<SegmentStatus>())
            _ = SegmentStateMachine.CanTransition(s, SegmentStatus.Dropped); // không ném KeyNotFound
    }
}
