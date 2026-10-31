using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Suni.Asr.Protocol;

/// <summary>
/// Framing: [uint32 LE length của (type + payload)][uint8 type][payload].
/// Payload là JSON UTF-8, riêng <see cref="MessageType.Pcm"/> là float32 LE.
/// </summary>
public static class FrameCodec
{
    public const int MaxFrameBytes = 64 * 1024 * 1024;

    public static async Task WriteJsonAsync<T>(Stream stream, MessageType type, T payload, CancellationToken ct = default) =>
        await WriteAsync(stream, type, JsonSerializer.SerializeToUtf8Bytes(payload), ct).ConfigureAwait(false);

    public static async Task WritePcmAsync(Stream stream, ReadOnlyMemory<float> samples, CancellationToken ct = default) =>
        await WriteAsync(stream, MessageType.Pcm, MemoryMarshal.AsBytes(samples.Span).ToArray(), ct).ConfigureAwait(false);

    public static async Task WriteAsync(Stream stream, MessageType type, ReadOnlyMemory<byte> payload, CancellationToken ct = default)
    {
        var header = new byte[5];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)(payload.Length + 1));
        header[4] = (byte)type;
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(payload, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Đọc một frame; trả null khi stream đã đóng.</summary>
    public static async Task<Frame?> ReadAsync(Stream stream, CancellationToken ct = default)
    {
        var header = new byte[5];
        if (!await ReadExactlyOrEofAsync(stream, header, ct).ConfigureAwait(false))
            return null;

        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length < 1 || length > MaxFrameBytes)
            throw new InvalidDataException($"Độ dài frame không hợp lệ: {length}.");

        var payload = new byte[length - 1];
        if (!await ReadExactlyOrEofAsync(stream, payload, ct).ConfigureAwait(false))
            throw new EndOfStreamException("Pipe đóng giữa frame.");
        return new Frame((MessageType)header[4], payload);
    }

    private static async Task<bool> ReadExactlyOrEofAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[total..], ct).ConfigureAwait(false);
            if (n == 0)
                return total == 0 ? false : throw new EndOfStreamException("Pipe đóng giữa frame.");
            total += n;
        }
        return true;
    }
}

public sealed record Frame(MessageType Type, byte[] Payload)
{
    public T ReadJson<T>() => JsonSerializer.Deserialize<T>(Payload)
                              ?? throw new InvalidDataException($"Payload {Type} rỗng.");

    public float[] ReadPcm() => MemoryMarshal.Cast<byte, float>(Payload).ToArray();
}
