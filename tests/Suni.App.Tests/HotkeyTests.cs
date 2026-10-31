using System.Windows.Input;
using Suni.App.Hotkeys;

namespace Suni.App.Tests;

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+O", ModifierKeys.Control | ModifierKeys.Alt, Key.O)]
    [InlineData("ctrl + shift + F9", ModifierKeys.Control | ModifierKeys.Shift, Key.F9)]
    [InlineData("Control+Alt+1", ModifierKeys.Control | ModifierKeys.Alt, Key.D1)]
    [InlineData("Win+Num5", ModifierKeys.Windows, Key.NumPad5)]
    [InlineData("Alt+Plus", ModifierKeys.Alt, Key.OemPlus)]
    [InlineData("Ctrl+Space", ModifierKeys.Control, Key.Space)]
    public void Parse_Valid(string text, ModifierKeys modifiers, Key key)
    {
        Assert.True(Hotkey.TryParse(text, out var h));
        Assert.Equal(new Hotkey(modifiers, key), h);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("O")]             // không có modifier → nuốt phím gõ thường
    [InlineData("Shift+O")]       // Shift không đủ
    [InlineData("Ctrl+Alt")]      // thiếu phím chính
    [InlineData("Ctrl+O+P")]      // hai phím chính
    [InlineData("Ctrl+Banana")]
    [InlineData("Ctrl+123")]
    public void Parse_Invalid(string? text) => Assert.False(Hotkey.TryParse(text, out _));

    [Theory]
    [InlineData("Ctrl+Alt+O")]
    [InlineData("Ctrl+Shift+F12")]
    [InlineData("Ctrl+Alt+Shift+Win+7")]
    [InlineData("Alt+Num0")]
    [InlineData("Ctrl+Minus")]
    public void ToString_RoundTrips(string text)
    {
        Assert.True(Hotkey.TryParse(text, out var h));
        Assert.Equal(text, h.ToString());
        Assert.True(Hotkey.TryParse(h.ToString(), out var again));
        Assert.Equal(h, again);
    }

    [Fact]
    public void FromKeyEvent_AltCombos_UseSystemKey() =>
        Assert.Equal(new Hotkey(ModifierKeys.Control | ModifierKeys.Alt, Key.T),
                     Hotkey.FromKeyEvent(Key.System, Key.T, ModifierKeys.Control | ModifierKeys.Alt));

    [Theory]
    [InlineData(Key.LeftCtrl)]
    [InlineData(Key.RightAlt)]
    [InlineData(Key.LeftShift)]
    [InlineData(Key.LWin)]
    public void FromKeyEvent_ModifierOnly_IsNull(Key key) =>
        Assert.Null(Hotkey.FromKeyEvent(key, Key.None, ModifierKeys.Control));
}
