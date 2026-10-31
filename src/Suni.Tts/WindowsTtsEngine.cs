using Windows.Devices.Enumeration;
using Windows.Media.Core;
using Windows.Media.Devices;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace Suni.Tts;

/// <summary>
/// TTS bằng giọng OneCore của Windows (offline). Phát qua <see cref="MediaPlayer"/> trong chính tiến trình app,
/// nên chế độ thu "toàn hệ thống trừ chính mình" (ADR-04) không thu lại giọng đọc.
/// </summary>
public sealed class WindowsTtsEngine : ITtsEngine, IDisposable
{
    private readonly SpeechSynthesizer _synth = new();
    private readonly MediaPlayer _player = new() { AutoPlay = false, AudioCategory = MediaPlayerAudioCategory.Speech };
    private string? _deviceId;

    public Task<IReadOnlyList<TtsVoice>> ListVoicesAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TtsVoice>>(
            [.. SpeechSynthesizer.AllVoices.Select(v => new TtsVoice(v.Id, v.Language, v.DisplayName))]);

    public async Task<IReadOnlyList<TtsOutputDevice>> ListOutputDevicesAsync(CancellationToken ct = default)
    {
        var defaultId = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);
        var devices = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioRenderSelector()).AsTask(ct);
        return [.. devices.Select(d => new TtsOutputDevice(d.Id, d.Name, d.Id == defaultId))];
    }

    public async Task SpeakAsync(string text, TtsOptions options, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        _synth.Voice = options.VoiceId is { Length: > 0 } id
            ? SpeechSynthesizer.AllVoices.FirstOrDefault(v => v.Id == id) ?? SpeechSynthesizer.DefaultVoice
            : SpeechSynthesizer.DefaultVoice;
        _synth.Options.SpeakingRate = Math.Clamp(options.Rate, 0.5, 3.0);
        _synth.Options.AudioVolume = Math.Clamp(options.Volume, 0, 1);

        using var stream = await _synth.SynthesizeTextToStreamAsync(text).AsTask(ct);
        await SetDeviceAsync(options.OutputDeviceId, ct);

        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Ended(MediaPlayer p, object a) => done.TrySetResult();
        void Failed(MediaPlayer p, MediaPlayerFailedEventArgs a) =>
            done.TrySetException(new InvalidOperationException($"Không phát được giọng đọc: {a.ErrorMessage}"));

        _player.MediaEnded += Ended;
        _player.MediaFailed += Failed;
        try
        {
            _player.Source = MediaSource.CreateFromStream(stream, stream.ContentType);
            _player.Play();
            using (ct.Register(() => done.TrySetCanceled(ct)))
                await done.Task;
        }
        finally
        {
            _player.MediaEnded -= Ended;
            _player.MediaFailed -= Failed;
            _player.Pause();
            _player.Source = null;
        }
    }

    private async Task SetDeviceAsync(string? deviceId, CancellationToken ct)
    {
        if (deviceId == _deviceId)
            return;
        DeviceInformation? device = null;
        if (deviceId is { Length: > 0 })
        {
            try
            {
                device = await DeviceInformation.CreateFromIdAsync(deviceId).AsTask(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Thiết bị đã rút / không còn → phát ra thiết bị mặc định thay vì im lặng.
            }
        }
        _player.AudioDevice = device;
        _deviceId = deviceId;
    }

    public void Dispose()
    {
        _player.Dispose();
        _synth.Dispose();
    }
}
