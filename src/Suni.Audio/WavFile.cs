using System.Runtime.InteropServices;
using System.Text;

namespace Suni.Audio;

/// <summary>Ghi WAV float32 (WAVE_FORMAT_IEEE_FLOAT); kích thước header được vá khi Dispose.</summary>
public sealed class WavWriter : IDisposable
{
    private readonly Stream _stream;
    private readonly BinaryWriter _writer;
    private long _dataBytes;

    public WavWriter(Stream stream, int sampleRate, int channels)
    {
        _stream = stream;
        _writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: false);
        SampleRate = sampleRate;
        Channels = channels;
        WriteHeader();
    }

    public WavWriter(string path, int sampleRate, int channels)
        : this(new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read), sampleRate, channels) { }

    public int SampleRate { get; }
    public int Channels { get; }

    public void Write(ReadOnlySpan<float> samples)
    {
        _writer.Write(MemoryMarshal.AsBytes(samples));
        _dataBytes += samples.Length * sizeof(float);
    }

    public void Dispose()
    {
        _writer.Flush();
        if (_stream.CanSeek)
        {
            _stream.Position = 4;
            _writer.Write((uint)(36 + _dataBytes));
            _stream.Position = 40;
            _writer.Write((uint)_dataBytes);
        }
        _writer.Dispose();
    }

    private void WriteHeader()
    {
        _writer.Write("RIFF"u8);
        _writer.Write(0u);
        _writer.Write("WAVE"u8);
        _writer.Write("fmt "u8);
        _writer.Write(16u);
        _writer.Write((ushort)3); // IEEE float
        _writer.Write((ushort)Channels);
        _writer.Write((uint)SampleRate);
        _writer.Write((uint)(SampleRate * Channels * 4));
        _writer.Write((ushort)(Channels * 4));
        _writer.Write((ushort)32);
        _writer.Write("data"u8);
        _writer.Write(0u);
    }
}

public sealed record WavData(int SampleRate, int Channels, float[] Samples)
{
    public double DurationSeconds => (double)Samples.Length / Channels / SampleRate;
}

public static class WavReader
{
    /// <summary>Đọc WAV PCM16 hoặc float32 (đủ cho fixture và file do <see cref="WavWriter"/> ghi).</summary>
    public static WavData Read(string path)
    {
        using var reader = new BinaryReader(File.OpenRead(path));
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "RIFF")
            throw new InvalidDataException("Không phải file RIFF.");
        reader.ReadUInt32();
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "WAVE")
            throw new InvalidDataException("Không phải file WAVE.");

        int format = 0, channels = 0, sampleRate = 0, bits = 0;
        while (reader.BaseStream.Position + 8 <= reader.BaseStream.Length)
        {
            var id = Encoding.ASCII.GetString(reader.ReadBytes(4));
            var size = reader.ReadUInt32();
            var next = reader.BaseStream.Position + size + (size & 1);
            if (id == "fmt ")
            {
                format = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                sampleRate = (int)reader.ReadUInt32();
                reader.ReadUInt32();
                reader.ReadUInt16();
                bits = reader.ReadUInt16();
                if (format == 0xFFFE && size >= 40) // WAVE_FORMAT_EXTENSIBLE: lấy subformat
                {
                    reader.ReadBytes(8);
                    format = reader.ReadUInt16();
                }
            }
            else if (id == "data")
            {
                var bytes = reader.ReadBytes((int)Math.Min(size, reader.BaseStream.Length - reader.BaseStream.Position));
                float[] samples = (format, bits) switch
                {
                    (3, 32) => MemoryMarshal.Cast<byte, float>(bytes).ToArray(),
                    (1, 16) => MemoryMarshal.Cast<byte, short>(bytes).ToArray().Select(s => s / 32768f).ToArray(),
                    _ => throw new NotSupportedException($"WAV format {format}/{bits}-bit chưa hỗ trợ."),
                };
                return new WavData(sampleRate, channels, samples);
            }
            reader.BaseStream.Position = next;
        }
        throw new InvalidDataException("Không tìm thấy chunk data.");
    }
}
