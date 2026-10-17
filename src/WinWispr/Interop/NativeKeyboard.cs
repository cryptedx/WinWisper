using System.Runtime.InteropServices;

namespace WinWispr.Interop;

/// <summary>
/// The two keyboard facts the paste and the selection reader share: whether the user still holds a modifier key,
/// and sending Ctrl plus a letter. Neither ever releases a key the user is holding.
/// </summary>
internal static class NativeKeyboard
{
    internal const ushort C = 0x43;
    internal const ushort V = 0x56;
    private const uint InputKeyboard = 1;
    private const uint KeyUp = 0x0002;
    private const ushort Control = 0x11;

    /// <summary>True while Shift, Ctrl, Alt or a Windows key is physically down.</summary>
    internal static bool AreModifiersPressed() =>
        (GetAsyncKeyState(0x10) & 0x8000) != 0
        || (GetAsyncKeyState(0x11) & 0x8000) != 0
        || (GetAsyncKeyState(0x12) & 0x8000) != 0
        || (GetAsyncKeyState(0x5B) & 0x8000) != 0
        || (GetAsyncKeyState(0x5C) & 0x8000) != 0;

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

    private static INPUT Keyboard(ushort virtualKey, uint flags) => new()
    {
        Type = InputKeyboard,
        Data = new INPUT_UNION
        {
            Keyboard = new KEYBDINPUT
            {
                VirtualKey = virtualKey,
                Flags = flags
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
