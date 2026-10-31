using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Suni.App.Services;
using Suni.App.ViewModels;
using Suni.Core.Settings;

namespace Suni.App.Overlay;

/// <summary>Overlay phụ đề (FR-17): font, độ mờ nền, song ngữ, số dòng, khoá — lưu ngay vào cài đặt.</summary>
public sealed partial class OverlayViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly SettingsService _settings;

    public OverlayViewModel(MainViewModel main, SettingsService settings)
    {
        _main = main;
        _settings = settings;
        Feed = new OverlayFeed(main.Lines);
        var s = settings.Current.Subtitle;
        _fontSize = s.FontSize;
        _opacity = s.Opacity;
        _bilingual = s.Bilingual;
        _maxLines = s.MaxLines;
        _locked = s.Locked;
        ApplyFeedSettings(s);

        Feed.Lines.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ShowPlaceholder));
        main.PropertyChanged += OnMainChanged;
    }

    public OverlayFeed Feed { get; }

    public SubtitleSettings Settings => _settings.Current.Subtitle;

    public ObservableCollection<SubtitleLine> Lines => Feed.Lines;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OriginalFontSize))]
    private double _fontSize;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PanelBrush))]
    private double _opacity;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowOriginal))]
    private bool _bilingual;

    [ObservableProperty] private int _maxLines;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPlaceholder))]
    private bool _locked;

    public double OriginalFontSize => Math.Round(FontSize * 0.72);

    /// <summary>Hiện dòng câu gốc phía trên bản dịch: chỉ khi bật song ngữ và phiên có dịch.</summary>
    public bool ShowOriginal => Bilingual && _main.TranslationActive;

    public Brush PanelBrush => new SolidColorBrush(Color.FromArgb((byte)Math.Round(Math.Clamp(Opacity, 0, 1) * 255), 0, 0, 0));

    /// <summary>Khung gợi ý khi chưa có chữ — chỉ khi chưa khoá, để biết chỗ mà kéo.</summary>
    public bool ShowPlaceholder => Lines.Count == 0 && !Locked;

    public event Action? CloseRequested;

    [RelayCommand] private Task BiggerAsync() => SetFontAsync(FontSize + 2);
    [RelayCommand] private Task SmallerAsync() => SetFontAsync(FontSize - 2);

    [RelayCommand]
    private Task MoreOpaqueAsync() => SaveAsync(s => s with { Opacity = Opacity = Math.Round(Math.Min(1, Opacity + 0.1), 1) });

    [RelayCommand]
    private Task LessOpaqueAsync() => SaveAsync(s => s with { Opacity = Opacity = Math.Round(Math.Max(0, Opacity - 0.1), 1) });

    [RelayCommand]
    private Task ToggleBilingualAsync() => SaveAsync(s => s with { Bilingual = Bilingual = !Bilingual });

    [RelayCommand]
    private Task CycleLinesAsync()
    {
        MaxLines = MaxLines >= 3 ? 1 : MaxLines + 1; // 1–3 dòng (SRS 3.2)
        Feed.MaxLines = MaxLines;
        Feed.Rebuild();
        return SaveAsync(s => s with { MaxLines = MaxLines });
    }

    [RelayCommand]
    public Task ToggleLockAsync() => SetLockedAsync(!Locked);

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke();

    public Task SetLockedAsync(bool locked)
    {
        Locked = locked;
        return SaveAsync(s => s with { Locked = locked });
    }

    public Task SavePositionAsync(double left, double top, double width) =>
        SaveAsync(s => s with { Left = Math.Round(left), Top = Math.Round(top), Width = Math.Round(width) });

    public void Detach()
    {
        _main.PropertyChanged -= OnMainChanged;
        Feed.Detach();
    }

    private Task SetFontAsync(double size)
    {
        FontSize = Math.Clamp(size, 12, 72);
        return SaveAsync(s => s with { FontSize = FontSize });
    }

    private Task SaveAsync(Func<SubtitleSettings, SubtitleSettings> change) =>
        _settings.UpdateAsync(all => all with { Subtitle = change(all.Subtitle) });

    private void ApplyFeedSettings(SubtitleSettings s)
    {
        Feed.MaxLines = s.MaxLines;
        Feed.ClearAfter = TimeSpan.FromSeconds(s.ClearAfterSec);
        Feed.Rebuild();
    }

    private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.TranslationActive))
            OnPropertyChanged(nameof(ShowOriginal));
    }
}
