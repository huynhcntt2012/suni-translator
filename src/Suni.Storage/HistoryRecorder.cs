using System.Threading.Channels;
using Suni.Core.Abstractions;
using Suni.Core.Events;

namespace Suni.Storage;

/// <summary>
/// Ghi lịch sử từ sự kiện pipeline (FR-18, SDD 7): một luồng ghi duy nhất, gom lô mỗi ~200 ms hoặc 50 thao tác,
/// mỗi lô một transaction. Không chặn pipeline: sự kiện chỉ được xếp hàng. Bỏ qua phiên tắt "lưu transcript".
/// Lỗi ghi được báo qua <see cref="WriteFailed"/> (DB_WRITE_FAILED) và không làm dừng phiên.
/// </summary>
public sealed class HistoryRecorder : IAsyncDisposable
{
    private const int MaxBatch = 50;
    private static readonly TimeSpan BatchWindow = TimeSpan.FromMilliseconds(200);

    private readonly HistoryStore _store;
    private readonly Channel<HistoryWrite> _queue = Channel.CreateUnbounded<HistoryWrite>(new UnboundedChannelOptions { SingleReader = true });
    private readonly HashSet<string> _recording = [];
    private readonly List<IDisposable> _subscriptions = [];
    private readonly Task _writer;

    public HistoryRecorder(HistoryStore store, IEventBus bus)
    {
        _store = store;
        _subscriptions.Add(bus.Subscribe<SessionStarted>(e =>
        {
            if (!e.Settings.Privacy.SaveTranscript)
                return;
            lock (_recording)
                _recording.Add(e.Session.Id);
            Enqueue(HistoryWrite.Session(e.Session));
        }));
        _subscriptions.Add(bus.Subscribe<SessionEnded>(e =>
        {
            if (IsRecording(e.Session.Id))
                Enqueue(HistoryWrite.Session(e.Session));
            lock (_recording)
                _recording.Remove(e.Session.Id);
        }));
        _subscriptions.Add(bus.Subscribe<SegmentFinal>(e => EnqueueSegment(e.Segment)));
        _subscriptions.Add(bus.Subscribe<SegmentTranslated>(e =>
        {
            if (!IsRecording(e.Segment.SessionId))
                return;
            Enqueue(HistoryWrite.Segment(e.Segment));
            if (e.MergedSegmentIds is { Count: > 1 } ids)
                Enqueue(HistoryWrite.Merged(ids, e.Segment.Id));
        }));
        // Chỉ lưu lỗi của câu đã có chữ (dịch thất bại); đoạn ASR rỗng/bị bỏ không có giá trị lịch sử.
        _subscriptions.Add(bus.Subscribe<SegmentFailed>(e =>
        {
            if (e.Segment.TextOriginal is not null)
                EnqueueSegment(e.Segment);
        }));
        _writer = Task.Run(WriteLoopAsync);
    }

    public event Action<Exception>? WriteFailed;

    /// <summary>Chờ mọi thao tác đã xếp hàng được ghi xong (dùng trước khi đọc lại, vd. mở History ngay sau Stop).</summary>
    public async Task FlushAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(new FlushMarker(done));
        await done.Task.ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _subscriptions)
            s.Dispose();
        _queue.Writer.TryComplete();
        await _writer.ConfigureAwait(false);
    }

    private bool IsRecording(string sessionId)
    {
        lock (_recording)
            return _recording.Contains(sessionId);
    }

    private void EnqueueSegment(Core.Domain.Segment segment)
    {
        if (IsRecording(segment.SessionId))
            Enqueue(HistoryWrite.Segment(segment));
    }

    private void Enqueue(HistoryWrite write) => _queue.Writer.TryWrite(write);

    private async Task WriteLoopAsync()
    {
        var reader = _queue.Reader;
        var batch = new List<HistoryWrite>(MaxBatch);
        var flushes = new List<TaskCompletionSource>();
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            // Gom thêm trong một khoảng ngắn để nhiều sự kiện liên tiếp chung một transaction.
            await Task.Delay(BatchWindow).ConfigureAwait(false);
            while (batch.Count < MaxBatch && reader.TryRead(out var w))
            {
                if (w is FlushMarker f)
                    flushes.Add(f.Done);
                else
                    batch.Add(w);
            }

            try
            {
                await _store.WriteBatchAsync(batch).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                WriteFailed?.Invoke(ex);
            }
            batch.Clear();
            foreach (var f in flushes)
                f.TrySetResult();
            flushes.Clear();
        }
    }

    private sealed record FlushMarker(TaskCompletionSource Done) : HistoryWrite;
}
