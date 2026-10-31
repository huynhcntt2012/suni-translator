using Suni.Core.Abstractions;
using Suni.Core.Settings;

namespace Suni.Audio.Processing;

/// <summary>
/// Cắt dòng PCM 16 kHz mono thành các đoạn lời nói (SDD 3.3):
/// hysteresis threshold/neg_threshold, bỏ đoạn quá ngắn, đóng đoạn khi im lặng đủ lâu,
/// ép cắt tại khung "yên" nhất trong 1 s cuối khi đoạn dài quá max_chunk (đoạn sau chồng overlap_ms),
/// và đánh dấu đoạn có tỉ lệ khung lời nói thấp (nhạc/nhiễu) bằng MusicGate.
/// Thời gian theo số mẫu đã nhận (sample clock). Không thread-safe.
/// </summary>
public sealed class VadSegmenter
{
    private readonly IVoiceActivityModel _model;
    private readonly VadSettings _s;
    private readonly int _rate;
    private readonly int _frameSize;

    private readonly int _minSpeech, _minSilence, _maxChunk, _preRoll, _postRoll, _overlap, _lookback;

    private readonly float[] _frame;
    private int _frameFill;

    // Âm thanh giữ lại: từ (đầu đoạn - pre-roll) tới hiện tại. _audioStart là chỉ số mẫu tuyệt đối của _audio[0].
    private float[] _audio;
    private int _audioLen;
    private long _audioStart;

    private bool _inSpeech;
    private long _segStart;     // mẫu bắt đầu lời nói (chưa gồm pre-roll)
    private int _segPad;        // pre-roll áp cho đoạn hiện tại (0 khi là phần tiếp sau ép cắt)
    private long _silenceStart = -1;
    private readonly List<(long Start, float Prob)> _frames = [];

    public VadSegmenter(IVoiceActivityModel model, VadSettings settings, int sampleRate = 16_000)
    {
        _model = model;
        _s = settings;
        _rate = sampleRate;
        _frameSize = model.FrameSize;
        _frame = new float[_frameSize];

        _minSpeech = Samples(settings.MinSpeechMs);
        _minSilence = Samples(settings.MinSilenceMs);
        _maxChunk = Samples((int)(settings.MaxChunkSec * 1000));
        _preRoll = Samples(settings.PreRollMs);
        _postRoll = Samples(settings.PostRollMs);
        _overlap = Samples(settings.OverlapMs);
        _lookback = Samples(1000);
        _audio = new float[_maxChunk + _preRoll + _rate];
    }

    public bool IsSpeaking => _inSpeech;

    /// <summary>Tổng số mẫu đã đưa vào VAD (sample clock).</summary>
    public long ProcessedSamples => _audioStart + _audioLen;

    /// <summary>Xác suất lời nói của khung gần nhất (cho đồng hồ/diagnostics).</summary>
    public float LastProbability { get; private set; }

    public IReadOnlyList<SpeechChunk> Push(ReadOnlySpan<float> samples)
    {
        List<SpeechChunk>? output = null;
        while (!samples.IsEmpty)
        {
            var n = Math.Min(samples.Length, _frameSize - _frameFill);
            samples[..n].CopyTo(_frame.AsSpan(_frameFill));
            _frameFill += n;
            samples = samples[n..];
            if (_frameFill < _frameSize)
                break;
            _frameFill = 0;
            if (OnFrame() is { } chunk)
                (output ??= []).Add(chunk);
        }
        return output ?? (IReadOnlyList<SpeechChunk>)[];
    }

    /// <summary>Kết thúc dòng: đóng đoạn đang mở (nếu có).</summary>
    public IReadOnlyList<SpeechChunk> Flush()
    {
        if (!_inSpeech)
            return [];
        var chunk = Close(_silenceStart >= 0 ? _silenceStart : ProcessedSamples);
        return chunk is null ? [] : [chunk];
    }

    public void Reset()
    {
        _model.Reset();
        _frameFill = 0;
        _audioLen = 0;
        _audioStart = 0;
        _inSpeech = false;
        _silenceStart = -1;
        _frames.Clear();
    }

    private SpeechChunk? OnFrame()
    {
        var start = ProcessedSamples;
        Append(_frame);
        var end = start + _frameSize;
        var p = _model.Process(_frame);
        LastProbability = p;

        if (!_inSpeech)
        {
            if (p >= _s.Threshold)
            {
                _inSpeech = true;
                _segStart = start;
                _segPad = _preRoll;
                _silenceStart = -1;
                _frames.Clear();
                _frames.Add((start, p));
            }
            else
            {
                TrimBefore(end - _preRoll);
            }
            return null;
        }

        _frames.Add((start, p));
        if (p < _s.NegThreshold)
        {
            if (_silenceStart < 0)
                _silenceStart = start;
        }
        else if (p >= _s.Threshold)
        {
            _silenceStart = -1; // vùng giữa neg_threshold và threshold: giữ nguyên trạng thái
        }

        if (_silenceStart >= 0 && end - _silenceStart >= _minSilence)
            return Close(_silenceStart);
        if (end - _segStart >= _maxChunk)
            return ForceCut(end);
        return null;
    }

    private SpeechChunk? Close(long speechEnd)
    {
        _inSpeech = false;
        _silenceStart = -1;
        SpeechChunk? chunk = null;
        // Phần tiếp sau ép cắt chỉ đáng gửi khi có nội dung mới ngoài vùng overlap.
        var minLength = _segPad == 0 ? _overlap + _frameSize : _minSpeech;
        if (speechEnd - _segStart >= minLength)
        {
            var from = Math.Max(_audioStart, _segStart - _segPad);
            var to = Math.Min(ProcessedSamples, speechEnd + _postRoll);
            chunk = Emit(from, to, speechEnd, forced: false);
        }
        _frames.Clear();
        TrimBefore(ProcessedSamples - _preRoll);
        return chunk;
    }

    private SpeechChunk ForceCut(long end)
    {
        // Cắt tại giữa khung có xác suất thấp nhất trong 1 s cuối (nhưng sau phần lời tối thiểu).
        var cut = end;
        var best = float.MaxValue;
        foreach (var (fs, prob) in _frames)
        {
            if (fs < end - _lookback || fs <= _segStart + _minSpeech)
                continue;
            if (prob < best)
            {
                best = prob;
                cut = fs + _frameSize / 2;
            }
        }

        var from = Math.Max(_audioStart, _segStart - _segPad);
        var chunk = Emit(from, cut, cut, forced: true);

        // Đoạn tiếp theo bắt đầu sớm hơn điểm cắt một khoảng overlap để không mất chữ.
        _segStart = Math.Max(_audioStart, cut - _overlap);
        _segPad = 0;
        _frames.RemoveAll(f => f.Start + _frameSize <= _segStart);
        if (_silenceStart >= 0 && _silenceStart < _segStart)
            _silenceStart = _segStart;
        TrimBefore(_segStart);
        return chunk;
    }

    private SpeechChunk Emit(long from, long to, long speechEnd, bool forced)
    {
        var counted = 0;
        var speech = 0;
        foreach (var (fs, prob) in _frames)
        {
            if (fs >= speechEnd)
                continue;
            counted++;
            if (prob >= _s.Threshold)
                speech++;
        }
        var ratio = counted == 0 ? 0f : (float)speech / counted;
        var drop = _s.MusicGate && ratio < _s.MinSpeechRatio ? SpeechDropReason.MusicGate : SpeechDropReason.None;

        var pcm = _audio.AsSpan((int)(from - _audioStart), (int)(to - from)).ToArray();
        return new SpeechChunk(Ms(from), Ms(to), pcm, forced, ratio, drop);
    }

    private void Append(ReadOnlySpan<float> samples)
    {
        if (_audioLen + samples.Length > _audio.Length)
            Array.Resize(ref _audio, Math.Max(_audio.Length * 2, _audioLen + samples.Length));
        samples.CopyTo(_audio.AsSpan(_audioLen));
        _audioLen += samples.Length;
    }

    private void TrimBefore(long absolute)
    {
        var drop = (int)Math.Clamp(absolute - _audioStart, 0, _audioLen);
        if (drop == 0)
            return;
        Array.Copy(_audio, drop, _audio, 0, _audioLen - drop);
        _audioLen -= drop;
        _audioStart += drop;
    }

    private int Samples(int ms) => (int)((long)ms * _rate / 1000);

    private long Ms(long samples) => samples * 1000 / _rate;
}
