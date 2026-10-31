using Suni.App.Services;

namespace Suni.App.Overlay;

/// <summary>Bật/tắt và khoá overlay; nhớ trạng thái trong cài đặt. Phím tắt nằm ở <see cref="Hotkeys.HotkeyService"/>.</summary>
public sealed class OverlayService(Func<OverlayViewModel> createViewModel, SettingsService settings)
{
    private OverlayWindow? _window;
    private OverlayViewModel? _vm;

    public bool IsVisible => _window is { IsVisible: true };

    public bool IsLocked => _vm?.Locked ?? settings.Current.Subtitle.Locked;

    /// <summary>Overlay hiện/ẩn hoặc khoá/mở khoá (để màn chính và khay cập nhật).</summary>
    public event Action? StateChanged;

    public async Task ShowAsync()
    {
        if (_window is null)
        {
            _vm = createViewModel();
            _vm.CloseRequested += () => _ = HideAsync();
            _vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(OverlayViewModel.Locked))
                    StateChanged?.Invoke();
            };
            _window = new OverlayWindow(_vm);
            _window.Closed += (_, _) =>
            {
                _vm?.Detach();
                _window = null;
                _vm = null;
            };
        }
        _window.Show();
        await settings.UpdateAsync(s => s with { Subtitle = s.Subtitle with { OverlayVisible = true } });
        StateChanged?.Invoke();
    }

    public async Task HideAsync()
    {
        _window?.Close();
        await settings.UpdateAsync(s => s with { Subtitle = s.Subtitle with { OverlayVisible = false } });
        StateChanged?.Invoke();
    }

    public Task ToggleAsync() => IsVisible ? HideAsync() : ShowAsync();

    public Task ToggleLockAsync() => SetLockedAsync(!IsLocked);

    public async Task SetLockedAsync(bool locked)
    {
        if (_vm is not null)
            await _vm.SetLockedAsync(locked);
        else
            await settings.UpdateAsync(s => s with { Subtitle = s.Subtitle with { Locked = locked } });
        StateChanged?.Invoke();
    }

    /// <summary>Đóng overlay khi thoát app mà không đổi lựa chọn "overlay đang bật" (để lần sau tự mở lại).</summary>
    public void CloseForExit() => _window?.Close();
}
