using System.Runtime.InteropServices;
using WinWispr.Core;

namespace WinWispr.Interop;

/// <summary>
/// The keyboard facts the paste, the selection reader and the hotkeys share: which modifier keys the user holds,
/// sending Ctrl plus a letter, and the menu mask. None of them ever releases a key the user is holding.
/// </summary>
internal static class NativeKeyboard
{
    internal const ushort C = 0x43;
    internal const ushort V = 0x56;

    /// <summary>An unassigned virtual key (0xE8): applications ignore it, and AutoHotkey uses it the same way.</summary>
    internal const ushort MenuMask = 0xE8;

    /// <summary>Marks WinWispr's own menu mask in the low-level hook's extra information.</summary>
    internal const nint MenuMaskTag = 0x57577370;

    private const uint InputKeyboard = 1;
    private const uint KeyUp = 0x0002;
    private const ushort Control = 0x11;

    /// <summary>True while Shift, Ctrl, Alt or a Windows key is physically down.</summary>
    internal static bool AreModifiersPressed() => HeldModifiers() != HotkeyModifiers.None;

    /// <summary>The modifier keys that are physically down, as hotkey modifiers.</summary>
    internal static HotkeyModifiers HeldModifiers()
    {
        HotkeyModifiers held = HotkeyModifiers.None;
        if ((GetAsyncKeyState(0x10) & 0x8000) != 0)
            held |= HotkeyModifiers.Shift;
        if ((GetAsyncKeyState(0x11) & 0x8000) != 0)
            held |= HotkeyModifiers.Control;
        if ((GetAsyncKeyState(0x12) & 0x8000) != 0)
            held |= HotkeyModifiers.Alt;
        if ((GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0)
            held |= HotkeyModifiers.Windows;
        return held;
    }

    /// <summary>Sends Ctrl down, the key down, the key up, Ctrl up. Returns how many of the four events were queued.</summary>
    internal static int SendControlChord(ushort virtualKey)
    {
        var inputs = new[]
        {
            Keyboard(Control, 0),
            Keyboard(virtualKey, 0),
            Keyboard(virtualKey, KeyUp),
            Keyboard(Control, KeyUp)
        };
        return checked((int)SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()));
    }

    /// <summary>
    /// Taps <see cref="MenuMask"/> while the user still holds Alt or a Windows key, so that the focused application
    /// sees a key between that modifier's press and release and does not treat the release as a menu key.
    /// </summary>
    internal static int SendMenuMask()
    {
        var inputs = new[] { Keyboard(MenuMask, 0, MenuMaskTag), Keyboard(MenuMask, KeyUp, MenuMaskTag) };
        return checked((int)SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()));
    }

    private static INPUT Keyboard(ushort virtualKey, uint flags, nint extraInfo = 0) => new()
    {
        Type = InputKeyboard,
        Data = new INPUT_UNION
        {
            Keyboard = new KEYBDINPUT
            {
                VirtualKey = virtualKey,
                Flags = flags,
                ExtraInfo = extraInfo
            }
        }
    };

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint Type;
        public INPUT_UNION Data;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct INPUT_UNION
    {
        [FieldOffset(0)]
        public KEYBDINPUT Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint inputCount, [In] INPUT[] inputs, int inputSize);
}
