using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Suni.App.Hotkeys;
using Suni.App.ViewModels;

namespace Suni.App;

public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _vm;
    private bool _saved;

    public SettingsWindow(SettingsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.CloseRequested += saved =>
        {
            _saved = saved;
            Close();
        };
        Loaded += async (_, _) => await vm.OpenAsync();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_saved)
            _vm.Cancelled();
        base.OnClosing(e);
    }

    /// <summary>Ghi tổ hợp phím vào ô; Tab vẫn để di chuyển giữa các ô.</summary>
    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox { DataContext: HotkeyRow row })
            return;
        var modifiers = Keyboard.Modifiers;
        if (e.Key == Key.Tab && modifiers is ModifierKeys.None or ModifierKeys.Shift)
            return;

        e.Handled = true;
        if (modifiers == ModifierKeys.None && e.Key is Key.Back or Key.Delete)
        {
            _vm.SetHotkey(row, "");
            return;
        }
        if (modifiers == ModifierKeys.None && e.Key == Key.Escape)
            return;

        if (Hotkey.FromKeyEvent(e.Key, e.SystemKey, modifiers) is { } hotkey)
        {
            if (hotkey.IsValid)
                _vm.SetHotkey(row, hotkey.ToString());
            else
                row.Set("Cần thêm Ctrl, Alt hoặc Win", HotkeyStatus.Invalid);
        }
    }
}
