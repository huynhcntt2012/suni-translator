namespace Suni.Pipeline;

/// <summary>
/// Sắp xếp segment theo <c>seq</c> trước khi hiển thị (SDD 6.3).
/// Segment tới sớm được giữ lại tối đa <see cref="HoldMs"/> chờ segment còn thiếu; quá hạn thì bỏ qua khoảng trống.
/// Segment tới muộn (seq nhỏ hơn đã phát) được phát ngay với cờ <c>IsLate</c> để UI chèn đúng vị trí.
/// Không thread-safe: chỉ gọi từ một consumer.
/// </summary>
public sealed class Sequencer<T>(long firstSeq = 1, long holdMs = 2000)
{
    private readonly SortedDictionary<long, (T Item, long ArrivedAtMs)> _held = new();
    private readonly HashSet<long> _skipped = [];

    public long HoldMs { get; } = holdMs;

    /// <summary>Seq kế tiếp đang chờ.</summary>
    public long NextSeq { get; private set; } = firstSeq;

    public int HeldCount => _held.Count;

    public IReadOnlyList<SequencedItem<T>> Offer(long seq, T item, long nowMs)
    {
        if (seq < NextSeq)
            return [new SequencedItem<T>(seq, item, IsLate: true)];

        if (!_held.ContainsKey(seq))
            _held[seq] = (item, nowMs);
        return Drain(nowMs);
    }

    /// <summary>Đánh dấu seq sẽ không bao giờ tới (bị drop/lỗi) để không phải chờ.</summary>
    public IReadOnlyList<SequencedItem<T>> Skip(long seq, long nowMs)
    {
        if (seq >= NextSeq)
            _skipped.Add(seq);
        return Drain(nowMs);
    }

    /// <summary>Gọi định kỳ để giải phóng segment đã chờ quá hạn.</summary>
    public IReadOnlyList<SequencedItem<T>> Tick(long nowMs) => Drain(nowMs);

    private List<SequencedItem<T>> Drain(long nowMs)
    {
        var output = new List<SequencedItem<T>>();
        while (true)
        {
            if (_skipped.Remove(NextSeq))
            {
                NextSeq++;
                continue;
            }

            if (_held.Remove(NextSeq, out var ready))
            {
                output.Add(new SequencedItem<T>(NextSeq, ready.Item, IsLate: false));
                NextSeq++;
                continue;
            }

            if (_held.Count == 0)
                break;

            // Khoảng trống: chỉ nhảy qua khi segment sớm nhất đang giữ đã chờ đủ lâu.
            var (firstSeq, first) = _held.First();
            if (nowMs - first.ArrivedAtMs < HoldMs)
                break;

            _skipped.RemoveWhere(s => s < firstSeq);
            NextSeq = firstSeq;
        }
        return output;
    }
}

public readonly record struct SequencedItem<T>(long Seq, T Item, bool IsLate);
