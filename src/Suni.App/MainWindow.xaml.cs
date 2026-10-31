using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using Suni.App.Hotkeys;
using Suni.App.Overlay;
using Suni.App.Services;
using Suni.App.Tray;
using Suni.App.ViewModels;

namespace Suni.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly OverlayService _overlay;
    private readonly HotkeyService _hotkeys;
    private readonly TrayService _tray;
    private readonly SettingsService _settings;
    private bool _toldAboutTray;

    public MainWindow(MainViewModel viewModel, OverlayService overlay, HotkeyService hotkeys, TrayService tray, SettingsService settings)
    {
        InitializeComponent();
        DataContext = viewModel;
        _vm = viewModel;
        _overlay = overlay;
        _hotkeys = hotkeys;
        _tray = tray;
        _settings = settings;
        Icon = tray.WindowIcon;
        tray.Attach(this);
        tray.ExitRequested += ExitApp;

        // Câu mới chèn ở đầu: đang ở đầu danh sách thì thấy ngay; đã cuộn xuống đọc câu cũ thì không bị kéo đi.
        viewModel.Lines.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && e.NewStartingIndex == 0 && IsNearTop())
                LinesList.ScrollIntoView(viewModel.Lines[0]);
        };
        SourceInitialized += (_, _) => _hotkeys.Attach(this);
        Loaded += async (_, _) =>
        {
            await viewModel.InitializeAsync();
            ReportHotkeys(_hotkeys.Apply(_settings.Current.Hotkeys));
        };
        _hotkeys.Triggered += async action =>
        {
            switch (action)
            {
                case HotkeyAction.Overlay: await _overlay.ToggleAsync(); break;
                case HotkeyAction.OverlayLock: await _overlay.ToggleLockAsync(); break;
                case HotkeyAction.StartStop: await _vm.ToggleRunningAsync(); break;
            }
        };
        viewModel.SettingsSaved += () => ReportHotkeys(_hotkeys.LastResult);
    }

    private void ReportHotkeys(IReadOnlyList<HotkeyRegistration> result)
    {
        var taken = result.Where(r => r.Status == HotkeyStatus.Taken).Select(r => r.Text).ToList();
        _vm.StatusMessage = taken.Count > 0
            ? $"Phím tắt đang bị ứng dụng khác dùng: {string.Join(", ", taken)} — đổi trong Settings."
            : _vm.StatusMessage?.StartsWith("Phím tắt", StringComparison.Ordinal) == true ? null : _vm.StatusMessage;
    }

    /// <summary>Đóng cửa sổ khi đang dịch → xuống khay (SDD 8.3); còn lại → thoát thật.</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        if (!App.IsExiting && _vm.IsRunning && _settings.Current.Ui.CloseToTray)
        {
            e.Cancel = true;
            Hide();
            if (!_toldAboutTray)
            {
                _toldAboutTray = true;
                _tray.Notify("Suni vẫn đang dịch", "Bấm icon ở khay để mở lại; chuột phải → Thoát để tắt hẳn.", force: true);
            }
            return;
        }
        App.IsExiting = true;
        _hotkeys.Detach();
        _overlay.CloseForExit();
        base.OnClosing(e);
    }

    private bool IsNearTop()
    {
        var viewer = FindScrollViewer(LinesList);
        return viewer is null || viewer.VerticalOffset < 1.5; // ListBox cuộn theo item: < 2 dòng tính là đang ở đầu
    }

    private static System.Windows.Controls.ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is System.Windows.Controls.ScrollViewer sv)
            return sv;
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindScrollViewer(System.Windows.Media.VisualTreeHelper.GetChild(root, i)) is { } found)
                return found;
        return null;
    }

    private void ExitApp()
    {
        App.IsExiting = true;
        Close();
    }
}
