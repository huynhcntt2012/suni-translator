using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Suni.App.Overlay;

/// <summary>
/// Cửa sổ phụ đề nổi (SDD 8.2, TC-11): trong suốt, luôn trên cùng, không hiện ở taskbar,
/// không chiếm focus (WS_EX_NOACTIVATE) để game/video không bị mất focus; khi khoá thì chuột xuyên qua (WS_EX_TRANSPARENT).
/// </summary>
public partial class OverlayWindow : Window
{
    private readonly OverlayViewModel _vm;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _saveDebounce = new() { Interval = TimeSpan.FromSeconds(1) };
    private nint _hwnd;
    private int _ticks;

    public OverlayWindow(OverlayViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        RestorePosition();

        _timer.Tick += (_, _) =>
        {
            _vm.Feed.Tick();
            // Cửa sổ borderless/fullscreen của game có thể đẩy overlay xuống dưới: nâng lại mỗi 2 s.
            if (++_ticks % 2 == 0 && _hwnd != 0)
                Win32.BringToTopmost(_hwnd);
        };
        _saveDebounce.Tick += async (_, _) =>
        {
            _saveDebounce.Stop();
            await _vm.SavePositionAsync(Left, Top, Width);
        };
        LocationChanged += (_, _) => RestartSave();
        SizeChanged += (_, e) => { if (e.WidthChanged) RestartSave(); };
        MouseEnter += (_, _) => UpdateToolbar(true);
        MouseLeave += (_, _) => UpdateToolbar(false);
        _vm.PropertyChanged += OnVmChanged;
    }

    public nint Handle => _hwnd;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        Win32.SetExStyle(_hwnd, Win32.WsExToolWindow | Win32.WsExNoActivate, 0);
        ApplyLock();
        _timer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _saveDebounce.Stop();
        _vm.PropertyChanged -= OnVmChanged;
        base.OnClosed(e);
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OverlayViewModel.Locked))
            ApplyLock();
    }

    private void ApplyLock()
    {
        if (_hwnd == 0)
            return;
        if (_vm.Locked)
            Win32.SetExStyle(_hwnd, Win32.WsExTransparent | Win32.WsExLayered, 0);
        else
            Win32.SetExStyle(_hwnd, 0, Win32.WsExTransparent);
        WidthGrip.Visibility = _vm.Locked ? Visibility.Collapsed : Visibility.Visible;
        UpdateToolbar(IsMouseOver);
    }

    private void UpdateToolbar(bool hover) =>
        Toolbar.Visibility = hover && !_vm.Locked ? Visibility.Visible : Visibility.Collapsed;

    private void OnPanelMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_vm.Locked && e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    private void OnWidthDrag(object sender, DragDeltaEventArgs e) =>
        Width = Math.Max(MinWidth, Width + e.HorizontalChange);

    private void OnWidthDragCompleted(object sender, DragCompletedEventArgs e) => RestartSave();

    private void RestartSave()
    {
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    /// <summary>Vị trí đã lưu nếu vẫn nằm trong vùng màn hình hiện có; không thì giữa đáy màn hình chính.</summary>
    private void RestorePosition()
    {
        var s = _vm.Settings;
        Width = Math.Clamp(s.Width, MinWidth, SystemParameters.VirtualScreenWidth);
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                              SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (s.Left is { } left && s.Top is { } top && screen.IntersectsWith(new Rect(left, top, Width, 80)))
        {
            Left = left;
            Top = top;
            return;
        }
        var work = SystemParameters.WorkArea;
        Width = Math.Min(Width, work.Width - 40);
        Left = work.Left + (work.Width - Width) / 2;
        Top = work.Bottom - 220;
    }
}
