using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Suni.App.Overlay;
using Suni.App.Services;
using Suni.App.ViewModels;
using Suni.Core.Abstractions;
using Suni.Core.Domain;
using Suni.Core.Events;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Suni.App.Tray;

/// <summary>
/// Icon khay hệ thống (FR-21, SDD 8.3): menu Mở / Bắt đầu-Dừng / Overlay / Khoá overlay / Thoát,
/// tooltip theo trạng thái, và thông báo khi có sự cố trong lúc cửa sổ chính đang ẩn (vd. đang xem video toàn màn hình).
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly MainViewModel _vm;
    private readonly OverlayService _overlay;
    private readonly SettingsService _settings;
    private readonly List<IDisposable> _subscriptions = [];
    private readonly Forms.NotifyIcon _icon;
    private readonly Drawing.Icon _idleIcon = CreateIcon(running: false);
    private readonly Drawing.Icon _runningIcon = CreateIcon(running: true);
    private readonly Forms.ToolStripMenuItem _startStop, _overlayItem, _lockItem, _ttsItem;
    private Window? _main;
    private bool _overloadNotified;

    public TrayService(MainViewModel vm, OverlayService overlay, SettingsService settings, IEventBus bus)
    {
        _vm = vm;
        _overlay = overlay;
        _settings = settings;

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Mở Suni", null, (_, _) => ShowMain());
        _startStop = new Forms.ToolStripMenuItem("Bắt đầu", null, async (_, _) => await _vm.ToggleRunningAsync());
        _overlayItem = new Forms.ToolStripMenuItem("Overlay phụ đề", null, async (_, _) => await _overlay.ToggleAsync());
        _lockItem = new Forms.ToolStripMenuItem("Khoá overlay", null, async (_, _) => await _overlay.ToggleLockAsync());
        _ttsItem = new Forms.ToolStripMenuItem("Đọc bản dịch", null, async (_, _) =>
            await _settings.UpdateAsync(s => s with { Tts = s.Tts with { Enabled = !s.Tts.Enabled } }));
        menu.Items.AddRange([_startStop, _overlayItem, _lockItem, _ttsItem, new Forms.ToolStripSeparator()]);
        menu.Items.Add("Thoát", null, (_, _) => ExitRequested?.Invoke());
        menu.Opening += (_, _) => RefreshMenu();

        _icon = new Forms.NotifyIcon { Icon = _idleIcon, Text = "Suni AI Live Translator", ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) ShowMain(); };

        _vm.PropertyChanged += OnVmChanged;
        _subscriptions.Add(bus.Subscribe<CaptureStateChanged>(e => Ui(() => OnCaptureState(e))));
        _subscriptions.Add(bus.Subscribe<OllamaStateChanged>(e => Ui(() =>
        {
            if (e.State is OllamaState.Disconnected or OllamaState.ModelMissing)
                Notify("Không dịch được", e.Detail ?? "Mất kết nối Ollama — vẫn hiện câu gốc.", Forms.ToolTipIcon.Warning);
        })));
        _subscriptions.Add(bus.Subscribe<SegmentFailed>(e => Ui(() =>
        {
            if (e.Error == ErrorCode.PipeOverload && !_overloadNotified)
            {
                _overloadNotified = true; // một lần mỗi phiên, tránh dồn thông báo
                Notify("Xử lý không kịp", "Đang bỏ bớt đoạn cũ. Thử model ASR nhỏ hơn.", Forms.ToolTipIcon.Warning);
            }
        })));
        _subscriptions.Add(bus.Subscribe<SessionStarted>(_ => Ui(() => _overloadNotified = false)));
    }

    /// <summary>Người dùng chọn Thoát trong menu khay.</summary>
    public event Action? ExitRequested;

    /// <summary>Icon dùng chung cho cửa sổ chính.</summary>
    public BitmapSource WindowIcon => Imaging.CreateBitmapSourceFromHIcon(_idleIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

    public void Attach(Window main) => _main = main;

    /// <summary>Thông báo của khay — chỉ khi cửa sổ chính không hiện (người dùng đang ở app khác / toàn màn hình).</summary>
    public void Notify(string title, string text, Forms.ToolTipIcon icon = Forms.ToolTipIcon.Info, bool force = false)
    {
        if (!force && (!_settings.Current.Ui.TrayNotifications || _main is { IsVisible: true, WindowState: not WindowState.Minimized }))
            return;
        _icon.ShowBalloonTip(5000, title, text, icon);
    }

    public void ShowMain()
    {
        if (_main is null)
            return;
        _main.Show();
        if (_main.WindowState == WindowState.Minimized)
            _main.WindowState = WindowState.Normal;
        _main.Activate();
    }

    public void Dispose()
    {
        foreach (var s in _subscriptions)
            s.Dispose();
        _vm.PropertyChanged -= OnVmChanged;
        _icon.Visible = false;
        _icon.Dispose();
        _idleIcon.Dispose();
        _runningIcon.Dispose();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(MainViewModel.IsRunning) or nameof(MainViewModel.SelectedSource)))
            return;
        _icon.Icon = _vm.IsRunning ? _runningIcon : _idleIcon;
        var text = _vm.IsRunning ? $"Suni — đang dịch: {_vm.SelectedSource?.DisplayName}" : "Suni — sẵn sàng";
        _icon.Text = text.Length > 127 ? text[..127] : text;
    }

    private void OnCaptureState(CaptureStateChanged e)
    {
        switch (e.State)
        {
            case CaptureState.SourceLost:
                Notify("Mất nguồn âm thanh", "Ứng dụng nguồn đã đóng. Mở Suni để chọn nguồn khác.", Forms.ToolTipIcon.Warning);
                break;
            case CaptureState.DeviceChanged:
                Notify("Thiết bị phát đã đổi", "Bấm Stop rồi Start để thu lại.", Forms.ToolTipIcon.Warning);
                break;
            case CaptureState.Error:
                Notify("Lỗi", e.Detail ?? e.Error?.ToString() ?? "Có lỗi khi thu/dịch.", Forms.ToolTipIcon.Error);
                break;
        }
    }

    private void RefreshMenu()
    {
        _startStop.Text = _vm.IsRunning ? "Dừng" : "Bắt đầu";
        _startStop.Enabled = _vm.IsRunning || _vm.StartCommand.CanExecute(null);
        _overlayItem.Checked = _overlay.IsVisible;
        _lockItem.Checked = _overlay.IsLocked;
        _ttsItem.Checked = _settings.Current.Tts.Enabled;
    }

    private static void Ui(Action action) => Application.Current?.Dispatcher.InvokeAsync(action);

    /// <summary>Icon vẽ lúc chạy (không cần file .ico): ô vuông bo góc màu nhấn, chữ "S"; đang dịch thì có chấm xanh.</summary>
    private static Drawing.Icon CreateIcon(bool running)
    {
        using var bmp = new Drawing.Bitmap(32, 32);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var path = new Drawing.Drawing2D.GraphicsPath();
            path.AddArc(1, 1, 10, 10, 180, 90);
            path.AddArc(21, 1, 10, 10, 270, 90);
            path.AddArc(21, 21, 10, 10, 0, 90);
            path.AddArc(1, 21, 10, 10, 90, 90);
            path.CloseFigure();
            using var fill = new Drawing.SolidBrush(Drawing.Color.FromArgb(0x4C, 0x8D, 0xFF));
            g.FillPath(fill, path);
            using var font = new Drawing.Font("Segoe UI", 17, Drawing.FontStyle.Bold, Drawing.GraphicsUnit.Pixel);
            using var format = new Drawing.StringFormat { Alignment = Drawing.StringAlignment.Center, LineAlignment = Drawing.StringAlignment.Center };
            g.DrawString("S", font, Drawing.Brushes.White, new Drawing.RectangleF(0, 0, 32, 31), format);
            if (running)
            {
                using var dot = new Drawing.SolidBrush(Drawing.Color.FromArgb(0x3D, 0xDC, 0x84));
                g.FillEllipse(Drawing.Brushes.White, 19, 19, 13, 13);
                g.FillEllipse(dot, 21, 21, 9, 9);
            }
        }
        var handle = bmp.GetHicon();
        try
        {
            return (Drawing.Icon)Drawing.Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint handle);
}
