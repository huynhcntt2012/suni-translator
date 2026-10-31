using Suni.Core.Abstractions;
using Suni.Core.Domain;
using Suni.Core.Events;
using Suni.Core.Settings;
using Suni.Tts;

namespace Suni.App.Services;

/// <summary>
/// Đọc bản dịch khi có (FR-23/24): nghe sự kiện <c>segment.translated</c>, chọn giọng theo ngôn ngữ đích,
/// xếp vào <see cref="TtsQueue"/>. Cài đặt (bật/tắt, giọng, thiết bị, tốc độ) có hiệu lực ngay, không cần Start lại.
/// </summary>
public sealed class TtsService : IAsyncDisposable
{
    private readonly SettingsService _settings;
    private readonly WindowsTtsEngine _engine = new();
    private readonly TtsQueue _queue;
    private readonly List<IDisposable> _subscriptions = [];
    private IReadOnlyList<TtsVoice>? _voices;
    private string _targetLang = "vi";
    private CaptureMode? _captureMode;
    private readonly HashSet<string> _warnedMissing = [];
    private readonly EchoGuard _echo = new();

    public TtsService(SettingsService settings, IEventBus bus)
    {
        _settings = settings;
        _queue = new TtsQueue(_engine, settings.Current.Tts.MaxPending);
        _queue.Started += item =>
        {
            _echo.Spoken(item.Text, DateTimeOffset.UtcNow);
            StateChanged?.Invoke();
        };
        _queue.Finished += _ => StateChanged?.Invoke();
        _queue.Skipped += _ => StateChanged?.Invoke();
        _queue.Failed += (_, ex) => Warning?.Invoke($"Không đọc được: {ex.Message}");

        _subscriptions.Add(bus.Subscribe<SessionStarted>(e =>
        {
            _targetLang = e.Session.TargetLang;
            _captureMode = e.Session.CaptureMode;
            _warnedMissing.Clear();
            WarnIfSelfCapture();
        }));
        _subscriptions.Add(bus.Subscribe<SessionEnded>(_ =>
        {
            _captureMode = null;
            _queue.Clear(); // bấm Stop = im lặng ngay
        }));
        _subscriptions.Add(bus.Subscribe<SegmentTranslated>(e => _ = OnTranslatedAsync(e)));
        settings.Changed += s =>
        {
            if (!s.Tts.Enabled)
                _queue.Clear();
            else
                WarnIfSelfCapture();
        };
    }

    /// <summary>Đang đọc / số câu chờ thay đổi.</summary>
    public event Action? StateChanged;

    /// <summary>Thông báo cho người dùng (thiếu giọng, lỗi phát, nguy cơ tự thu giọng đọc).</summary>
    public event Action<string>? Warning;

    public bool IsSpeaking => _queue.Speaking is not null;

    public int Pending => _queue.Pending;

    public async Task<IReadOnlyList<TtsVoice>> VoicesAsync() => _voices ??= await _engine.ListVoicesAsync();

    public Task<IReadOnlyList<TtsOutputDevice>> OutputDevicesAsync() => _engine.ListOutputDevicesAsync();

    /// <summary>Nghe thử với cài đặt đang chỉnh (chưa lưu); dừng câu đang đọc.</summary>
    public async Task PreviewAsync(TtsSettings tts, string lang)
    {
        var voice = VoicePicker.Pick(await VoicesAsync(), tts.VoiceId, lang);
        var text = (voice?.Language ?? lang).StartsWith("vi", StringComparison.OrdinalIgnoreCase)
            ? "Xin chào, đây là giọng đọc bản dịch của Suni."
            : "Hello, this is how Suni reads the translation.";
        _queue.Clear();
        _queue.Enqueue(new TtsItem("preview", text, ToOptions(tts, voice)));
    }

    public void Stop() => _queue.Clear();

    private async Task OnTranslatedAsync(SegmentTranslated e)
    {
        var tts = _settings.Current.Tts;
        var text = e.Segment.DisplayTranslation;
        if (!tts.Enabled || string.IsNullOrWhiteSpace(text))
            return;

        // Lưới an toàn chống vòng lặp: câu gốc trùng câu vừa đọc trong vài giây gần đây = ASR nghe lại chính giọng đọc.
        if (_echo.IsEcho(e.Segment.TextOriginal, DateTimeOffset.UtcNow))
        {
            Warning?.Invoke("Nguồn đang thu lại cả giọng đọc của Suni — đã bỏ câu lặp. Hãy chọn nguồn là ứng dụng cụ thể.");
            return;
        }

        var voice = VoicePicker.Pick(await VoicesAsync(), tts.VoiceId, _targetLang);
        if (voice is null)
        {
            if (_warnedMissing.Add(_targetLang))
                Warning?.Invoke($"Windows chưa có giọng đọc {Languages.Find(_targetLang)?.NativeName ?? _targetLang}. " +
                                "Cài thêm trong Settings → Time & language → Speech → Add voices.");
            return;
        }
        _queue.Enqueue(new TtsItem(e.Segment.Id, text, ToOptions(tts, voice)));
        StateChanged?.Invoke();
    }

    /// <summary>Thu endpoint loopback nghe cả loa → sẽ thu lại giọng đọc và dịch lại (vòng lặp).</summary>
    private void WarnIfSelfCapture()
    {
        if (_settings.Current.Tts.Enabled && _captureMode == CaptureMode.EndpointLoopback)
            Warning?.Invoke("Nguồn đang thu toàn bộ loa (endpoint loopback) nên sẽ thu lại cả giọng đọc. " +
                            "Hãy chọn nguồn là ứng dụng cụ thể hoặc 'Toàn bộ âm thanh hệ thống'.");
    }

    private static TtsOptions ToOptions(TtsSettings tts, TtsVoice? voice) =>
        new(voice?.Id, string.IsNullOrEmpty(tts.OutputDeviceId) ? null : tts.OutputDeviceId, tts.Rate, tts.Volume);

    public async ValueTask DisposeAsync()
    {
        foreach (var s in _subscriptions)
            s.Dispose();
        await _queue.DisposeAsync();
        _engine.Dispose();
    }
}
