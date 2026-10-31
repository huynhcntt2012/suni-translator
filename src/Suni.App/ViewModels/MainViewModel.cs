using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Suni.App.Overlay;
using Suni.App.Services;
using Suni.Core.Abstractions;
using Suni.Core.Domain;
using Suni.Core.Events;
using Suni.Core.Settings;
using Suni.Pipeline;
using Suni.Storage;
using Suni.Translation;

namespace Suni.App.ViewModels;

/// <summary>
/// Dashboard (SRS 3.1): chọn nguồn thật, kết nối Ollama, Start/Stop pipeline thu âm → VAD → ASR → dịch,
/// hiển thị câu gốc ngay và điền bản dịch khi có.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private const int MaxLines = 500;
    private const string TranslationFailedText = "⚠ bản dịch thất bại";

    private readonly IAudioSourceEnumerator _sources;
    private readonly IPipelineOrchestrator _pipeline;
    private readonly WhisperWorkerProvider _asr;
    private readonly SettingsService _settingsService;
    private readonly HttpClient _http;
    private readonly Dispatcher _dispatcher;
    private readonly List<IDisposable> _subscriptions = [];
    private AppSettings _settings => _settingsService.Current;

    private readonly HistoryStore _history;
    private readonly HistoryRecorder _recorder;
    private readonly ExportService _export;
    private readonly Func<HistoryWindow> _historyWindow;
    private Session? _lastSession;
    private readonly OverlayService _overlay;
    private readonly Func<SettingsWindow> _settingsWindow;
    private readonly TtsService _tts;
    private bool _syncingTts;
    private bool _syncingOverlayLock;

    public MainViewModel(IAudioSourceEnumerator sources, IPipelineOrchestrator pipeline, WhisperWorkerProvider asr,
                         SettingsService settingsService, HttpClient http, IEventBus bus,
                         HistoryStore history, HistoryRecorder recorder, ExportService export, Func<HistoryWindow> historyWindow,
                         OverlayService overlay, Func<SettingsWindow> settingsWindow, TtsService tts)
    {
        _overlay = overlay;
        _settingsWindow = settingsWindow;
        _tts = tts;
        tts.StateChanged += () => Ui(() => TtsStatus = !TtsEnabled ? null
            : tts.IsSpeaking ? (tts.Pending > 0 ? $"🔊 đang đọc · chờ {tts.Pending}" : "🔊 đang đọc") : "🔊 bật");
        tts.Warning += message => Ui(() => StatusMessage = message);
        overlay.StateChanged += () => Ui(() =>
        {
            IsOverlayVisible = overlay.IsVisible;
            _syncingOverlayLock = true;
            OverlayLocked = overlay.IsLocked;
            _syncingOverlayLock = false;
        });
        _sources = sources;
        _pipeline = pipeline;
        _asr = asr;
        _settingsService = settingsService;
        _http = http;
        _history = history;
        _recorder = recorder;
        _export = export;
        _historyWindow = historyWindow;
        _dispatcher = Application.Current.Dispatcher;

        _subscriptions.Add(bus.Subscribe<SessionStarted>(e => Ui(() =>
        {
            _lastSession = e.Settings.Privacy.SaveTranscript ? e.Session : null;
            ExportCommand.NotifyCanExecuteChanged();
        })));
        _recorder.WriteFailed += ex => Ui(() => StatusMessage = $"Không lưu được lịch sử: {ex.Message}");

        foreach (var m in AppPaths.AvailableAsrModels())
            AsrModels.Add(m);
        // Theo benchmark P0: GPU → large-v3-turbo (~0,4 s/đoạn); chỉ CPU → base (small trên CPU chậm hơn thời gian thực).
        AsrModel = new[] { "large-v3-turbo-q5_0", "base-q5_1", new AsrSettings().Model }.FirstOrDefault(AsrModels.Contains)
                   ?? AsrModels.FirstOrDefault();
        if (AsrModels.Count == 0)
            StatusMessage = $"Chưa có model ASR trong {AppPaths.AsrModelDirectory}";

        _subscriptions.Add(bus.Subscribe<AudioLevel>(e => Ui(() => InputLevel = Math.Clamp((e.PeakDb + 60) / 60, 0, 1))));
        _subscriptions.Add(bus.Subscribe<VoiceActivity>(e => Ui(() => IsSpeaking = e.IsSpeaking)));
        _subscriptions.Add(bus.Subscribe<CaptureStateChanged>(e => Ui(() => OnCaptureState(e))));
        _subscriptions.Add(bus.Subscribe<SegmentFinal>(e => Ui(() => AddLine(e.Segment))));
        _subscriptions.Add(bus.Subscribe<SegmentTranslated>(e => Ui(() => OnTranslated(e))));
        _subscriptions.Add(bus.Subscribe<SegmentFailed>(e => Ui(() => OnSegmentFailed(e))));
        _subscriptions.Add(bus.Subscribe<OllamaStateChanged>(e => Ui(() => OnOllamaState(e))));
        _subscriptions.Add(bus.Subscribe<PipelineHealth>(e => Ui(() =>
        {
            QueueLength = e.SpeechQueueLength + e.TranslateQueueLength;
            IsLagging = e.IsLagging;
            LatencyText = e.LatencyP50Ms is { } p50 ? $"{p50 / 1000.0:F1} s" : "–";
        })));
    }

    public IReadOnlyList<Language> TargetLanguages { get; } = Languages.Supported;

    public IReadOnlyList<string> SourceLanguages { get; } = ["auto", .. Languages.Supported.Select(l => l.Code)];

    public ObservableCollection<string> AsrModels { get; } = [];

    public ObservableCollection<AudioSource> AudioSources { get; } = [];

    public ObservableCollection<string> OllamaModels { get; } = [];

    public ObservableCollection<SubtitleLine> Lines { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private AudioSource? _selectedSource;

    [ObservableProperty] private string _sourceLang = "auto";
    [ObservableProperty] private Language _targetLang = Languages.Find("vi")!;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string? _asrModel;

    /// <summary>Chạy ASR bằng GPU (Vulkan); worker tự lùi về CPU nếu không có GPU phù hợp.</summary>
    [ObservableProperty] private bool _useGpu = true;

    [ObservableProperty] private string _ollamaUrl = "";
    [ObservableProperty] private string? _ollamaModel;
    [ObservableProperty] private string _ollamaStatus = "Chưa kết nối — chỉ hiện câu gốc";
    [ObservableProperty] private bool _ollamaConnected;
    [ObservableProperty] private string _pipelineStatus = "Ready";
    [ObservableProperty] private string? _statusMessage;
    [ObservableProperty] private double _inputLevel;
    [ObservableProperty] private bool _isSpeaking;
    [ObservableProperty] private int _queueLength;
    [ObservableProperty] private bool _isLagging;
    [ObservableProperty] private string _latencyText = "–";
    [ObservableProperty] private string? _asrRuntime;

    /// <summary>Phiên hiện tại có dịch (đã kết nối Ollama khi Start) — overlay dựa vào đây để biết có chờ bản dịch không.</summary>
    [ObservableProperty] private bool _translationActive;

    /// <summary>Đọc bản dịch bằng giọng offline (FR-23) — lưu ngay vào cài đặt.</summary>
    [ObservableProperty] private bool _ttsEnabled;
    [ObservableProperty] private string? _ttsStatus;

    async partial void OnTtsEnabledChanged(bool value)
    {
        TtsStatus = value ? "🔊 bật" : null;
        if (!_syncingTts)
            await _settingsService.UpdateAsync(s => s with { Tts = s.Tts with { Enabled = value } });
    }

    [ObservableProperty] private bool _isOverlayVisible;
    [ObservableProperty] private bool _overlayLocked;

    [RelayCommand]
    private Task ToggleOverlayAsync() => _overlay.ToggleAsync();

    async partial void OnOverlayLockedChanged(bool value)
    {
        if (!_syncingOverlayLock)
            await _overlay.SetLockedAsync(value);
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _isBusy;

    /// <summary>Gọi khi cửa sổ mở: nạp cài đặt đã lưu, liệt kê nguồn, tự kết nối Ollama nếu đã cấu hình trước đó.</summary>
    public async Task InitializeAsync()
    {
        await _settingsService.LoadAsync();
        if (App.StartupNotice is { } notice)
            StatusMessage = notice;
        SourceLang = _settings.Asr.SourceLang ?? "auto";
        TargetLang = Languages.Find(_settings.Translation.TargetLang) ?? TargetLang;
        UseGpu = _settings.Asr.Device != "cpu";
        if (AsrModels.Contains(_settings.Asr.Model) && _settings.Asr.Model != new AsrSettings().Model)
            AsrModel = _settings.Asr.Model;
        // Gợi ý Ollama trên máy này; người dùng vẫn phải bấm Kết nối (không tự gọi địa chỉ chưa xác nhận).
        OllamaUrl = string.IsNullOrEmpty(_settings.Translation.OllamaUrl) ? "http://127.0.0.1:11434" : _settings.Translation.OllamaUrl;

        _syncingOverlayLock = true;
        OverlayLocked = _settings.Subtitle.Locked;
        _syncingOverlayLock = false;
        _syncingTts = true;
        TtsEnabled = _settings.Tts.Enabled;
        _syncingTts = false;
        _settingsService.Changed += s => Ui(() =>
        {
            _syncingTts = true;
            TtsEnabled = s.Tts.Enabled; // đổi từ khay / Settings
            _syncingTts = false;
        });
        if (_settings.Subtitle.OverlayVisible)
            await _overlay.ShowAsync();

        await RefreshSourcesAsync();
        if (!string.IsNullOrEmpty(_settings.Translation.OllamaUrl))
            await ConnectOllamaAsync();
    }

    [RelayCommand]
    private async Task RefreshSourcesAsync()
    {
        try
        {
            var previousExe = SelectedSource?.ExeName ?? _settings.Capture.LastSourceExe;
            var previousMode = SelectedSource?.Mode;
            var list = await _sources.GetSourcesAsync();
            AudioSources.Clear();
            foreach (var s in list)
                AudioSources.Add(s);
            SelectedSource = list.FirstOrDefault(s => previousExe is not null
                                                      && string.Equals(s.ExeName, previousExe, StringComparison.OrdinalIgnoreCase))
                             ?? list.FirstOrDefault(s => previousMode is not null && s.Mode == previousMode && s.ExeName is null)
                             ?? list.FirstOrDefault(s => s.IsActive && s.Mode == CaptureMode.ProcessLoopback)
                             ?? list.FirstOrDefault();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Không liệt kê được nguồn âm thanh: {ex.Message}";
        }
    }

    /// <summary>Test Connection (FR-14, FR-15): GET /api/tags, liệt kê model, cho biết đang dùng AI trên PC hay server LAN.</summary>
    [RelayCommand]
    private async Task ConnectOllamaAsync()
    {
        OllamaConnected = false;
        if (!OllamaUrlClassifier.TryParse(OllamaUrl, out var uri))
        {
            OllamaStatus = "URL không hợp lệ (ví dụ http://127.0.0.1:11434)";
            return;
        }

        var kind = OllamaUrlClassifier.Classify(uri!);
        if (kind == OllamaEndpointKind.Other && _settings.Privacy.OfflineStrict)
        {
            OllamaStatus = "Bị chặn: không phải máy này hay mạng LAN";
            StatusMessage = $"{uri!.Host} không thuộc mạng nội bộ. Chế độ offline chỉ cho phép Ollama trên máy này hoặc trong LAN.";
            return;
        }

        OllamaStatus = "Đang kết nối…";
        var check = await new OllamaClient(_http, uri!).TestConnectionAsync();
        if (!check.Ok)
        {
            OllamaStatus = $"Không kết nối được: {check.Error}";
            return;
        }

        var models = await new OllamaClient(_http, uri!).ListModelsAsync();
        OllamaModels.Clear();
        foreach (var m in models.Select(m => m.Name).Order())
            OllamaModels.Add(m);
        OllamaModel = OllamaModels.FirstOrDefault(m => m == _settings.Translation.Model) ?? OllamaModels.FirstOrDefault();

        var where = kind == OllamaEndpointKind.Local ? "AI Offline trên PC" : "AI Offline trên Server LAN";
        OllamaStatus = $"{where} · {models.Count} model · {check.RoundTripMs} ms";
        OllamaConnected = models.Count > 0;
        if (models.Count == 0)
            OllamaStatus = $"{where} · chưa có model nào (ollama pull …)";
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        if (SelectedSource is null || AsrModel is null)
            return;

        IsBusy = true;
        StatusMessage = null;
        PipelineStatus = "Đang nạp model…";
        try
        {
            var translate = OllamaConnected && OllamaModel is not null;
            var source = SelectedSource;
            await _settingsService.UpdateAsync(s => s with
            {
                Capture = s.Capture with { LastSourceExe = source.ExeName },
                Asr = s.Asr with
                {
                    Model = AsrModel,
                    Device = UseGpu ? "vulkan" : "cpu",
                    SourceLang = SourceLang == "auto" ? null : SourceLang,
                },
                Translation = s.Translation with
                {
                    TargetLang = TargetLang.Code,
                    OllamaUrl = OllamaConnected ? OllamaUrl.Trim() : s.Translation.OllamaUrl,
                    Model = OllamaModel ?? s.Translation.Model,
                },
            });

            // Chưa kết nối Ollama → chạy chỉ ASR (để trống model dịch cho phiên này).
            var sessionSettings = translate ? _settings : _settings with { Translation = _settings.Translation with { Model = "" } };
            Lines.Clear();
            await _pipeline.StartAsync(source, sessionSettings);
            TranslationActive = translate;
            IsRunning = true;
            PipelineStatus = "Capturing";
            AsrRuntime = _asr.Runtime is { } rt ? $"ASR: {AsrModel} · {rt}" : null;
            if (!translate)
                StatusMessage = "Chưa kết nối Ollama: chỉ hiện câu gốc. Bấm Kết nối để bật dịch.";
        }
        catch (Exception ex)
        {
            PipelineStatus = "Error";
            StatusMessage = $"Không bắt đầu được: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanStart() => !IsRunning && !IsBusy && SelectedSource is not null && AsrModel is not null;

    /// <summary>Start nếu đang dừng, Stop nếu đang chạy — cho phím tắt và menu khay.</summary>
    public async Task ToggleRunningAsync()
    {
        if (IsRunning)
            await StopCommand.ExecuteAsync(null);
        else if (StartCommand.CanExecute(null))
            await StartCommand.ExecuteAsync(null);
    }

    [RelayCommand(CanExecute = nameof(IsRunning))]
    private async Task StopAsync()
    {
        PipelineStatus = "Đang dừng…";
        await _pipeline.StopAsync();
        IsRunning = false;
        IsSpeaking = false;
        InputLevel = 0;
        PipelineStatus = "Ready";
    }

    [RelayCommand]
    private void Copy()
    {
        var sb = new StringBuilder();
        foreach (var l in Lines.OrderBy(l => l.Seq)) // danh sách hiển thị mới → cũ; bản copy theo thứ tự thời gian
        {
            sb.Append('[').Append(l.Time).Append("] ").AppendLine(l.Original);
            if (!string.IsNullOrEmpty(l.Translation) && l.Translation != TranslationFailedText)
                sb.Append("           ").AppendLine(l.Translation);
        }
        if (sb.Length > 0)
            Clipboard.SetText(sb.ToString());
    }

    /// <summary>Xuất phiên đang chạy / vừa chạy (song ngữ) từ lịch sử đã lưu.</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync(ExportFormat format)
    {
        var session = _lastSession!;
        try
        {
            await _recorder.FlushAsync();
            var segments = await _history.ListBySessionAsync(session.Id);
            if (segments.Count == 0)
            {
                StatusMessage = "Phiên này chưa có câu nào để xuất.";
                return;
            }
            var path = await _export.ExportAsync(session, segments, format, ExportContent.Bilingual, Application.Current.MainWindow);
            if (path is not null)
                StatusMessage = $"Đã xuất {segments.Count} câu ra {path}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Xuất thất bại: {ex.Message}";
        }
    }

    private bool CanExport() => _lastSession is not null;

    /// <summary>Settings đã lưu (để cửa sổ chính báo lại phím tắt không đăng ký được).</summary>
    public event Action? SettingsSaved;

    [RelayCommand]
    private void OpenSettings()
    {
        var window = _settingsWindow();
        window.Owner = Application.Current.MainWindow;
        window.ShowDialog();
        SettingsSaved?.Invoke(); // lưu hay huỷ thì phím tắt đều vừa được đăng ký lại → cập nhật banner
    }

    [RelayCommand]
    private void OpenHistory()
    {
        var window = _historyWindow();
        window.Owner = Application.Current.MainWindow;
        window.Show();
    }

    partial void OnIsSpeakingChanged(bool value)
    {
        if (IsRunning)
            PipelineStatus = value ? "Đang nghe…" : "Capturing";
    }

    private void OnCaptureState(CaptureStateChanged e)
    {
        switch (e.State)
        {
            case CaptureState.SourceLost:
                StatusMessage = "Ứng dụng nguồn đã đóng. Hãy chọn nguồn khác và bấm Start.";
                break;
            case CaptureState.DeviceChanged:
                StatusMessage = "Thiết bị phát đã thay đổi. Bấm Stop rồi Start để thu lại.";
                break;
            case CaptureState.Error:
                PipelineStatus = "Error";
                StatusMessage = e.Detail ?? e.Error?.ToString();
                break;
        }
    }

    private void OnOllamaState(OllamaStateChanged e)
    {
        switch (e.State)
        {
            case OllamaState.Connected:
                OllamaConnected = true;
                if (StatusMessage?.Contains("Ollama", StringComparison.Ordinal) == true)
                    StatusMessage = null;
                break;
            case OllamaState.Disconnected:
            case OllamaState.ModelMissing:
                OllamaStatus = e.Detail ?? "Mất kết nối Ollama";
                StatusMessage = e.Detail;
                break;
        }
    }

    private void OnSegmentFailed(SegmentFailed e)
    {
        if (e.Segment.Status == SegmentStatus.TranslateFailed)
            SetTranslation(e.Segment.Id, TranslationFailedText);
        else if (e.Error == ErrorCode.PipeOverload)
            StatusMessage = "Đang chậm: bỏ bớt đoạn cũ. Thử model ASR nhỏ hơn.";
    }

    /// <summary>Câu mới nhất ở đầu danh sách; quá giới hạn thì bỏ câu cũ nhất ở cuối.</summary>
    private void AddLine(Segment s)
    {
        var lang = s.LangDetected is { } l ? Languages.Find(l)?.NativeName ?? l : "?";
        Lines.Insert(0, new SubtitleLine(s.Id, s.Seq, TimeSpan.FromMilliseconds(s.StartMs).ToString(@"hh\:mm\:ss"), lang,
                                         s.TextOriginal ?? "", s.TextTranslated));
        while (Lines.Count > MaxLines)
            Lines.RemoveAt(Lines.Count - 1);
    }

    /// <summary>
    /// Câu dài bị cắt thành nhiều mảnh: các dòng mảnh đã hiện câu gốc từng phần; khi bản dịch của cả câu về,
    /// thay chúng bằng một dòng (giờ của mảnh đầu, câu gốc đầy đủ, bản dịch).
    /// </summary>
    private void OnTranslated(SegmentTranslated e)
    {
        if (e.MergedSegmentIds is not { Count: > 1 } ids)
        {
            SetTranslation(e.Segment.Id, e.Segment.TextTranslated);
            return;
        }

        var idSet = ids.ToHashSet();
        var indexes = Enumerable.Range(0, Lines.Count).Where(i => idSet.Contains(Lines[i].SegmentId)).ToList();
        if (indexes.Count == 0)
            return; // đã bị đẩy khỏi danh sách
        // Giờ/seq lấy từ mảnh sớm nhất; dòng gộp đứng ở chỗ mảnh mới nhất (đầu danh sách) để thứ tự vẫn mới → cũ.
        var earliest = indexes.Select(i => Lines[i]).MinBy(l => l.Seq)!;
        var merged = new SubtitleLine(e.Segment.Id, earliest.Seq, earliest.Time, earliest.Language,
                                      e.Segment.TextOriginal ?? earliest.Original, e.Segment.TextTranslated);
        var target = indexes.Min();
        foreach (var i in indexes.Where(i => i != target).OrderDescending())
            Lines.RemoveAt(i);
        Lines[target] = merged;
    }

    private void SetTranslation(string segmentId, string? text)
    {
        for (var i = Lines.Count - 1; i >= 0; i--)
        {
            if (Lines[i].SegmentId == segmentId)
            {
                Lines[i].Translation = text;
                return;
            }
        }
    }

    private void Ui(Action action) => _dispatcher.InvokeAsync(action);
}

public sealed partial class SubtitleLine(string segmentId, long seq, string time, string language, string original, string? translation)
    : ObservableObject
{
    public string SegmentId { get; } = segmentId;
    public long Seq { get; } = seq;
    public string Time { get; } = time;
    public string Language { get; } = language;
    public string Original { get; } = original;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PrimaryText))]
    private string? _translation = translation;

    /// <summary>Dòng chính trên overlay: bản dịch nếu có, không thì câu gốc.</summary>
    public string PrimaryText => Translation ?? Original;
}
