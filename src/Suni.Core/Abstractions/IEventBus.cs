using Suni.Core.Events;

namespace Suni.Core.Abstractions;

public interface IEventBus
{
    void Publish(PipelineEvent e);

    /// <summary>Đăng ký nhận sự kiện kiểu <typeparamref name="T"/>; dispose để huỷ.</summary>
    IDisposable Subscribe<T>(Action<T> handler) where T : PipelineEvent;
}
