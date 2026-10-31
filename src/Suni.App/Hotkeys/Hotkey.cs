using System.Windows.Input;

namespace Suni.App.Hotkeys;

/// <summary>
/// Một tổ hợp phím tắt toàn cục: chuỗi lưu trong cài đặt ("Ctrl+Alt+O") ↔ modifier + phím WPF.
/// Bắt buộc có Ctrl, Alt hoặc Win để không nuốt mất phím gõ bình thường của ứng dụng khác.
/// </summary>
public readonly record struct Hotkey(ModifierKeys Modifiers, Key Key)
{
    private static readonly (ModifierKeys Flag, string Name)[] ModifierNames =
    [
        (ModifierKeys.Control, "Ctrl"), (ModifierKeys.Alt, "Alt"), (ModifierKeys.Shift, "Shift"), (ModifierKeys.Windows, "Win"),
    ];

    public bool IsValid =>
        Key != Key.None && !IsModifierKey(Key)
        && (Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0;

    public override string ToString()
    {
        var modifiers = Modifiers;
        var parts = ModifierNames.Where(m => modifiers.HasFlag(m.Flag)).Select(m => m.Name).ToList();
        parts.Add(KeyName(Key));
        return string.Join('+', parts);
    }

    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var modifiers = ModifierKeys.None;
        Key? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var modifier = ModifierNames.FirstOrDefault(m => string.Equals(m.Name, raw, StringComparison.OrdinalIgnoreCase)
                                                             || (m.Flag == ModifierKeys.Control && raw.Equals("Control", StringComparison.OrdinalIgnoreCase)));
            if (modifier.Name is not null)
            {
                modifiers |= modifier.Flag;
                continue;
            }
            if (key is not null || !TryParseKey(raw, out var k))
                return false; // hai phím thường, hoặc tên phím lạ
            key = k;
        }

        if (key is null)
            return false;
        hotkey = new Hotkey(modifiers, key.Value);
        return hotkey.IsValid;
    }

    /// <summary>Tạo từ sự kiện bàn phím WPF (Alt+phím đến dưới dạng Key.System). Null nếu mới chỉ nhấn phím modifier.</summary>
    public static Hotkey? FromKeyEvent(Key key, Key systemKey, ModifierKeys modifiers)
    {
        var actual = key == Key.System ? systemKey : key;
        if (actual == Key.None || IsModifierKey(actual))
            return null;
        return new Hotkey(modifiers, actual);
    }

    public static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;

    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num" + (char)('0' + (key - Key.NumPad0)),
        Key.OemPlus => "Plus",
        Key.OemMinus => "Minus",
        Key.OemComma => "Comma",
        Key.OemPeriod => "Period",
        _ => key.ToString(),
    };

    private static bool TryParseKey(string name, out Key key)
    {
        key = Key.None;
        if (name.Length == 1 && char.IsDigit(name[0]))
        {
            key = Key.D0 + (name[0] - '0');
            return true;
        }
        if (name.StartsWith("Num", StringComparison.OrdinalIgnoreCase) && name.Length == 4 && char.IsDigit(name[3]))
        {
            key = Key.NumPad0 + (name[3] - '0');
            return true;
        }
        key = name.ToLowerInvariant() switch
        {
            "plus" => Key.OemPlus,
            "minus" => Key.OemMinus,
            "comma" => Key.OemComma,
            "period" => Key.OemPeriod,
            _ => Enum.TryParse<Key>(name, ignoreCase: true, out var k) && !int.TryParse(name, out _) ? k : Key.None,
        };
        return key != Key.None;
    }
}
