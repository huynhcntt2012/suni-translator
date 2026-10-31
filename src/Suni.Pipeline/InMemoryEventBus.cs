using Suni.Core.Abstractions;
using Suni.Core.Events;

namespace Suni.Pipeline;

/// <summary>
/// Event bus in-process. Handler chạy trên thread của bên publish — UI tự marshal về Dispatcher.
/// Lỗi trong một handler không làm hỏng pipeline.
/// </summary>
public sealed class InMemoryEventBus : IEventBus
{
    private readonly Lock _gate = new();
    private Subscription[] _subscriptions = [];

    public event Action<Exception>? HandlerFailed;

    public void Publish(PipelineEvent e)
    {
        foreach (var s in Volatile.Read(ref _subscriptions))
        {
            if (!s.EventType.IsInstanceOfType(e))
                continue;
            try
            {
                s.Handler(e);
            }
            catch (Exception ex)
            {
                HandlerFailed?.Invoke(ex);
            }
        }
    }

    public IDisposable Subscribe<T>(Action<T> handler) where T : PipelineEvent
    {
        var sub = new Subscription(typeof(T), e => handler((T)e), this);
        lock (_gate)
            _subscriptions = [.. _subscriptions, sub];
        return sub;
    }

    private void Remove(Subscription sub)
    {
        lock (_gate)
            _subscriptions = _subscriptions.Where(s => s != sub).ToArray();
    }

    private sealed class Subscription(Type eventType, Action<PipelineEvent> handler, InMemoryEventBus owner) : IDisposable
    {
        public Type EventType { get; } = eventType;
        public Action<PipelineEvent> Handler { get; } = handler;

        public void Dispose() => owner.Remove(this);
    }
}
