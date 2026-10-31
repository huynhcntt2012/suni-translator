using Suni.Core.Events;

namespace Suni.Pipeline.Tests;

public class InMemoryEventBusTests
{
    [Fact]
    public void Subscriber_ReceivesOnlyMatchingType()
    {
        var bus = new InMemoryEventBus();
        var levels = new List<AudioLevel>();
        using var _ = bus.Subscribe<AudioLevel>(levels.Add);

        bus.Publish(new AudioLevel(-6, -20, false));
        bus.Publish(new VoiceActivity("s", true));

        Assert.Single(levels);
    }

    [Fact]
    public void Dispose_Unsubscribes()
    {
        var bus = new InMemoryEventBus();
        var count = 0;
        var sub = bus.Subscribe<AudioLevel>(_ => count++);
        sub.Dispose();

        bus.Publish(new AudioLevel(-6, -20, false));

        Assert.Equal(0, count);
    }

    [Fact]
    public void FailingHandler_DoesNotBlockOthers()
    {
        var bus = new InMemoryEventBus();
        Exception? reported = null;
        bus.HandlerFailed += ex => reported = ex;
        var received = false;
        using var a = bus.Subscribe<AudioLevel>(_ => throw new InvalidOperationException("boom"));
        using var b = bus.Subscribe<AudioLevel>(_ => received = true);

        bus.Publish(new AudioLevel(-6, -20, false));

        Assert.True(received);
        Assert.IsType<InvalidOperationException>(reported);
    }
}
