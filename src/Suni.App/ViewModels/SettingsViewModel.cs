using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Suni.App.Hotkeys;
using Suni.App.Services;
using Suni.Core.Settings;

namespace Suni.App.ViewModels;

/// <summary>
/// Màn Settings (SRS 3.2): đổi phím tắt toàn cục và hành vi khay. Trong lúc mở, phím tắt của app tạm ngưng để ô ghi phím
/// nhận được tổ hợp cũ; mỗi tổ hợp được kiểm tra trùng nhau và trùng với ứng dụng khác ngay khi ghi.
/// </summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly HotkeyService _hotkeys;

    private readonly TtsService _tts;

    public SettingsViewModel(SettingsService settings, HotkeyService hotkeys, TtsService tts)
    {
        _settings = settings;
        _hotkeys = hotkeys;
        _tts = tts;
        var s = settings.Current;
        _ttsRate = s.Tts.Rate;
        _ttsVolume = s.Tts.Volume;
        Rows =
        [
            new HotkeyRow(HotkeyAction.Overlay, "Bật/tắt overlay phụ đề", s.Hotkeys.Overlay),
            new HotkeyRow(HotkeyAction.OverlayLock, "Khoá/mở khoá overlay (chuột xuyên qua)", s.Hotkeys.OverlayLock),
            new HotkeyRow(HotkeyAction.StartStop, "Bắt đầu/dừng thu", s.Hotkeys.StartStop),
        ];
        _closeToTray = s.Ui.CloseToTray;
        _trayNotifications = s.Ui.TrayNotifications;
    }

    public ObservableCollection<HotkeyRow> Rows { get; }

    [ObservableProperty] private bool _closeToTray;
    [ObservableProperty] private bool _trayNotifications;
    [ObservableProperty] private string? _message;

    /// <summary>Đóng cửa sổ (true = đã lưu).</summary>
    public event Action<bool>? CloseRequested;

    public async Task OpenAsync()
    {
        _hotkeys.Suspend();
        Validate();
        await LoadTtsChoicesAsync();
    }

    // ------------------------------------------------------------ đọc bản dịch (TTS)

    public ObservableCollection<Choice> Voices { get; } = [];
    public ObservableCollection<Choice> OutputDevices { get; } = [];

    [ObservableProperty] private Choice? _selectedVoice;
    [ObservableProperty] private Choice? _selectedOutputDevice;
    [ObservableProperty] private double _ttsRate;
    [ObservableProperty] private double _ttsVolume;
    [ObservableProperty] private string? _voiceHint;

    private async Task LoadTtsChoicesAsync()
    {
        var current = _settings.Current.Tts;
        var target = _settings.Current.Translation.TargetLang;

        Voices.Clear();
        Voices.Add(new Choice("", $"Tự động theo ngôn ngữ đích ({target})"));
        var voices = await _tts.VoicesAsync();
        foreach (var v in voices.OrderBy(v => v.Language).ThenBy(v => v.DisplayName))
            Voices.Add(new Choice(v.Id, $"{v.DisplayName} ({v.Language})"));
        SelectedVoice = Voices.FirstOrDefault(v => v.Id == current.VoiceId) ?? Voices[0];
        var languages = voices.Select(v => v.Language).Distinct().Order().ToList();
        VoiceHint = $"Giọng có trên máy: {(languages.Count == 0 ? "chưa có" : string.Join(", ", languages))}. " +
                    "Thêm giọng (vd. tiếng Việt): Windows Settings → Time & language → Speech → Add voices.";

        OutputDevices.Clear();
        OutputDevices.Add(new Choice("", "Thiết bị mặc định của Windows"));
        foreach (var d in await _tts.OutputDevicesAsync())
            OutputDevices.Add(new Choice(d.Id, d.Name));
        SelectedOutputDevice = OutputDevices.FirstOrDefault(d => d.Id == current.OutputDeviceId) ?? OutputDevices[0];
    }

    private TtsSettings EditedTts() => _settings.Current.Tts with
    {
        VoiceId = SelectedVoice?.Id ?? "",
        OutputDeviceId = SelectedOutputDevice?.Id ?? "",
        Rate = Math.Round(TtsRate, 1),
        Volume = Math.Round(TtsVolume, 2),
    };

    [RelayCommand]
    private Task PreviewTtsAsync() => _tts.PreviewAsync(EditedTts(), _settings.Current.Translation.TargetLang);

    /// <summary>Gọi khi cửa sổ đóng mà không lưu: đăng ký lại phím cũ.</summary>
    public void Cancelled() => _hotkeys.Resume();

    public void SetHotkey(HotkeyRow row, string text)
    {
        row.Text = text;
        Validate();
    }

    public void Validate()
    {
        foreach (var row in Rows)
        {
            if (string.IsNullOrWhiteSpace(row.Text))
            {
                row.Set("Tắt", HotkeyStatus.Disabled);
                continue;
            }
            if (!Hotkey.TryParse(row.Text, out var hotkey))
            {
                row.Set("Không hợp lệ — cần Ctrl, Alt hoặc Win + một phím", HotkeyStatus.Invalid);
                continue;
            }
            var duplicate = Rows.FirstOrDefault(r => r != row && Hotkey.TryParse(r.Text, out var other) && other == hotkey);
            if (duplicate is not null)
                row.Set($"Trùng với \"{duplicate.Label}\"", HotkeyStatus.Invalid);
            else if (!_hotkeys.IsAvailable(hotkey))
                row.Set("Đang bị ứng dụng khác dùng — chọn tổ hợp khác", HotkeyStatus.Taken);
            else
                row.Set("Dùng được", HotkeyStatus.Ok);
        }
        SaveCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void Defaults()
    {
        var d = new HotkeySettings();
        foreach (var (action, text) in HotkeyService.Entries(d))
            Rows.Single(r => r.Action == action).Text = text;
        CloseToTray = new UiSettings().CloseToTray;
        TrayNotifications = new UiSettings().TrayNotifications;
        TtsRate = new TtsSettings().Rate;
        TtsVolume = new TtsSettings().Volume;
        SelectedVoice = Voices.FirstOrDefault();
        SelectedOutputDevice = OutputDevices.FirstOrDefault();
        Validate();
    }

    private bool CanSave() => Rows.All(r => r.Status is not HotkeyStatus.Invalid);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        var text = (HotkeyAction a) => Hotkey.TryParse(Rows.Single(r => r.Action == a).Text, out var h) ? h.ToString() : "";
        var hotkeys = new HotkeySettings
        {
            Overlay = text(HotkeyAction.Overlay),
            OverlayLock = text(HotkeyAction.OverlayLock),
            StartStop = text(HotkeyAction.StartStop),
        };
        var tts = EditedTts();
        await _settings.UpdateAsync(s => s with
        {
            Hotkeys = hotkeys,
            Ui = s.Ui with { CloseToTray = CloseToTray, TrayNotifications = TrayNotifications },
            Tts = tts with { Enabled = s.Tts.Enabled },
        });
        var result = _hotkeys.Apply(hotkeys);
        var taken = result.Where(r => r.Status == HotkeyStatus.Taken).Select(r => r.Text).ToList();
        if (taken.Count > 0)
        {
            // Đã lưu, nhưng báo để người dùng đổi tiếp thay vì đóng im lặng.
            Message = $"Đã lưu. Chưa đăng ký được: {string.Join(", ", taken)} (ứng dụng khác đang dùng).";
            _hotkeys.Suspend();
            Validate();
            return;
        }
        CloseRequested?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => CloseRequested?.Invoke(false);
}

public sealed record Choice(string Id, string Label);

public sealed partial class HotkeyRow(HotkeyAction action, string label, string text) : ObservableObject
{
    public HotkeyAction Action { get; } = action;
    public string Label { get; } = label;

    [ObservableProperty] private string _text = text;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private HotkeyStatus _status;

    public void Set(string statusText, HotkeyStatus status)
    {
        StatusText = statusText;
        Status = status;
    }
}
