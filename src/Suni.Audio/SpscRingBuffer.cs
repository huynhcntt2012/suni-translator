namespace Suni.Audio;

/// <summary>
/// Ring buffer lock-free một producer (thread capture WASAPI) / một consumer (PreprocessLoop).
/// Khi đầy, producer ghi đè dữ liệu cũ nhất và bật cờ <see cref="Overflowed"/> (SDD 6.2).
/// </summary>
public sealed class SpscRingBuffer<T>(int capacity) where T : struct
{
    private readonly T[] _buffer = new T[capacity];
    private long _written; // chỉ producer ghi
    private long _read;    // consumer ghi; producer đẩy lên khi ghi đè
    private int _overflowed;

    public int Capacity => _buffer.Length;

    public int Count => (int)(Volatile.Read(ref _written) - Volatile.Read(ref _read));

    /// <summary>Đã từng bị ghi đè kể từ lần đọc cờ trước (đọc là reset).</summary>
    public bool Overflowed => Interlocked.Exchange(ref _overflowed, 0) == 1;

    public void Write(ReadOnlySpan<T> data)
    {
        if (data.Length > _buffer.Length)
            data = data[^_buffer.Length..];

        var written = Volatile.Read(ref _written);
        var newWritten = written + data.Length;

        // Đẩy con trỏ đọc lên TRƯỚC khi ghi đè, để consumer đang copy vùng cũ phát hiện qua CAS và đọc lại.
        while (true)
        {
            var read = Volatile.Read(ref _read);
            if (newWritten - read <= _buffer.Length)
                break;
            if (Interlocked.CompareExchange(ref _read, newWritten - _buffer.Length, read) == read)
            {
                Volatile.Write(ref _overflowed, 1);
                break;
            }
        }

        var start = (int)(written % _buffer.Length);
        var first = Math.Min(data.Length, _buffer.Length - start);
        data[..first].CopyTo(_buffer.AsSpan(start));
        data[first..].CopyTo(_buffer);
        Volatile.Write(ref _written, newWritten);
    }

    /// <summary>Đọc tối đa <paramref name="destination"/>.Length phần tử; trả về số phần tử đã đọc.</summary>
    public int Read(Span<T> destination)
    {
        while (true)
        {
            var read = Volatile.Read(ref _read);
            var available = (int)Math.Min(Volatile.Read(ref _written) - read, destination.Length);
            if (available <= 0)
                return 0;

            var start = (int)(read % _buffer.Length);
            var first = Math.Min(available, _buffer.Length - start);
            _buffer.AsSpan(start, first).CopyTo(destination);
            _buffer.AsSpan(0, available - first).CopyTo(destination[first..]);

            // Nếu producer đã ghi đè trong lúc copy thì đọc lại từ vị trí mới.
            if (Interlocked.CompareExchange(ref _read, read + available, read) == read)
                return available;
        }
    }
}
