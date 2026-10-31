using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Suni.App.Overlay;
using Suni.Core.Settings;

namespace Suni.App.Hotkeys;

public enum HotkeyAction { Overlay, OverlayLock, StartStop }

/// <summary>Kết quả đăng ký một phím: Ok, Disabled (để trống), Invalid (chuỗi sai), Taken (app khác đang giữ).</summary>
public enum HotkeyStatus { Ok, Disabled, Invalid, Taken }

public sealed record HotkeyRegistration(HotkeyAction Action, string Text, HotkeyStatus Status);

/// <summary>
/// Phím tắt toàn cục (FR-21) qua RegisterHotKey trên cửa sổ chính. Đăng ký lại toàn bộ mỗi khi cài đặt đổi;
/// tạm ngưng khi đang ghi phím mới trong màn Settings (không thì tổ hợp cũ bị hệ thống nuốt trước khi ô nhập thấy).
/// </summary>
public sealed class HotkeyService
{
    private const int BaseId = 0x5300;
    private nint _hwnd;
    private HotkeySettings _current = new();
    private readonly List<int> _registered = [];

    public event Action<HotkeyAction>? Triggered;

    public IReadOnlyList<HotkeyRegistration> LastResult { get; private set; } = [];

    public void Attach(Window owner)
    {
        _hwnd = new WindowInteropHelper(owner).Handle;
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
    }

    public IReadOnlyList<HotkeyRegistration> Apply(HotkeySettings settings)
    {
        _current = settings;
        UnregisterAll();
        var result = new List<HotkeyRegistration>();
        foreach (var (action, text) in Entries(settings))
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                result.Add(new(action, "", HotkeyStatus.Disabled));
                continue;
            }
            if (!Hotkey.TryParse(text, out var hotkey))
            {
                result.Add(new(action, text, HotkeyStatus.Invalid));
                continue;
            }
            var id = BaseId + (int)action;
            if (_hwnd != 0 && Win32.RegisterHotKey(_hwnd, id, ToWin32(hotkey.Modifiers) | Win32.ModNoRepeat,
                                                  (uint)KeyInterop.VirtualKeyFromKey(hotkey.Key)))
            {
                _registered.Add(id);
                result.Add(new(action, hotkey.ToString(), HotkeyStatus.Ok));
            }
            else
            {
                result.Add(new(action, hotkey.ToString(), HotkeyStatus.Taken));
            }
        }
        LastResult = result;
        return result;
    }

    /// <summary>Thử xem tổ hợp có đăng ký được không (gọi khi đã <see cref="Suspend"/>, để không đụng phím của chính app).</summary>
    public bool IsAvailable(Hotkey hotkey)
    {
        if (_hwnd == 0)
            return false;
        const int probeId = BaseId + 0xFF;
        if (!Win32.RegisterHotKey(_hwnd, probeId, ToWin32(hotkey.Modifiers) | Win32.ModNoRepeat, (uint)KeyInterop.VirtualKeyFromKey(hotkey.Key)))
            return false;
        Win32.UnregisterHotKey(_hwnd, probeId);
        return true;
    }

    public void Suspend() => UnregisterAll();

    public void Resume() => Apply(_current);

    public void Detach() => UnregisterAll();

    public static IEnumerable<(HotkeyAction Action, string Text)> Entries(HotkeySettings s) =>
    [
        (HotkeyAction.Overlay, s.Overlay),
        (HotkeyAction.OverlayLock, s.OverlayLock),
        (HotkeyAction.StartStop, s.StartStop),
    ];

    private void UnregisterAll()
    {
        foreach (var id in _registered)
            Win32.UnregisterHotKey(_hwnd, id);
        _registered.Clear();
    }

    private static uint ToWin32(ModifierKeys m) =>
        (m.HasFlag(ModifierKeys.Alt) ? Win32.ModAlt : 0)
        | (m.HasFlag(ModifierKeys.Control) ? Win32.ModControl : 0)
        | (m.HasFlag(ModifierKeys.Shift) ? Win32.ModShift : 0)
        | (m.HasFlag(ModifierKeys.Windows) ? Win32.ModWin : 0);

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == Win32.WmHotkey && (int)wParam - BaseId is var a and >= 0 and <= (int)HotkeyAction.StartStop)
        {
            Triggered?.Invoke((HotkeyAction)a);
            handled = true;
        }
        return 0;
    }
}
