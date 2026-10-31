using System.Runtime.InteropServices;

namespace Suni.App.Overlay;

/// <summary>Win32 cho overlay (extended window styles, topmost) và phím tắt toàn cục.</summary>
internal static partial class Win32
{
    public const int GwlExStyle = -20;
    public const long WsExTransparent = 0x0000_0020;
    public const long WsExToolWindow = 0x0000_0080;
    public const long WsExLayered = 0x0008_0000;
    public const long WsExNoActivate = 0x0800_0000;

    public static readonly nint HwndTopmost = -1;
    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoActivate = 0x0010;

    public const int WmHotkey = 0x0312;
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
    public const uint ModNoRepeat = 0x4000;

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static partial nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(nint hwnd, int id);

    public static void SetExStyle(nint hwnd, long add, long remove)
    {
        var style = (long)GetWindowLongPtr(hwnd, GwlExStyle);
        SetWindowLongPtr(hwnd, GwlExStyle, (nint)((style | add) & ~remove));
    }

    public static bool HasExStyle(nint hwnd, long flag) => ((long)GetWindowLongPtr(hwnd, GwlExStyle) & flag) != 0;

    public static void BringToTopmost(nint hwnd) =>
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
}
