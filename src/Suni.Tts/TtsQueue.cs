using System.Threading.Channels;

namespace Suni.Tts;

public sealed record TtsItem(string SegmentId, string Text, TtsOptions Options);

/// <summary>
/// Hàng đợi đọc (FR-24): đọc tuần tự từng câu. Khi người nói nhanh hơn giọng đọc, chỉ giữ tối đa
/// <see cref="MaxPending"/> câu chờ và bỏ câu cũ nhất — giọng đọc theo kịp hiện tại thay vì trễ dần cả phút.
/// </summary>
public sealed class TtsQueue : IAsyncDisposable
{
    private readonly ITtsEngine _engine;
    private readonly Channel<TtsItem> _channel;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private CancellationTokenSource? _current;

    public TtsQueue(ITtsEngine engine, int maxPending = 2)
    {
        _engine = engine;
        MaxPending = Math.Max(1, maxPending);
        _channel = Channel.CreateBounded<TtsItem>(
            new BoundedChannelOptions(MaxPending) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest },
            dropped => Skipped?.Invoke(dropped));
        _loop = Task.Run(LoopAsync);
    }

    public int MaxPending { get; }

    public int Pending => _channel.Reader.Count;

    public TtsItem? Speaking { get; private set; }

    public event Action<TtsItem>? Started;
    public event Action<TtsItem>? Finished;
    /// <summary>Câu bị bỏ vì hàng đợi đầy (đọc không kịp).</summary>
    public event Action<TtsItem>? Skipped;
    public event Action<TtsItem, Exception>? Failed;

    public void Enqueue(TtsItem item) => _channel.Writer.TryWrite(item);

    /// <summary>Dừng câu đang đọc và bỏ các câu đang chờ (tắt TTS, dừng phiên).</summary>
    public void Clear()
    {
        while (_channel.Reader.TryRead(out _)) { }
        _current?.Cancel();
    }

    private async Task LoopAsync()
    {
        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(_stop.Token))
            {
                using var current = _current = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                Speaking = item;
                Started?.Invoke(item);
                try
                {
                    await _engine.SpeakAsync(item.Text, item.Options, current.Token);
                }
                catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
                {
                    // Clear() huỷ câu đang đọc
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Failed?.Invoke(item, ex);
                }
                finally
                {
                    Speaking = null;
                    _current = null;
                    Finished?.Invoke(item);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // dispose
        }
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        await _stop.CancelAsync();
        await _loop;
        _stop.Dispose();
    }
}
