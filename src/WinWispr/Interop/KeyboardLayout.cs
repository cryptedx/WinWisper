using System.Globalization;
using System.Runtime.InteropServices;
using WinWispr.Core;

namespace WinWispr.Interop;

/// <summary>A hotkey that AltGr also presses on a keyboard layout, and the character AltGr types there.</summary>
internal readonly record struct HotkeyCharacterClash(string Character, string LayoutName);

/// <summary>A keyboard layout: its name and what AltGr, with or without Shift, plus a virtual key types on it.</summary>
internal sealed record KeyboardLayout(string Name, Func<uint, bool, string> AltGrCharacter)
{
    private const int VkShift = 0x10;
    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLeftShift = 0xA0;
    private const int VkLeftControl = 0xA2;
    private const int VkRightMenu = 0xA5;

    /// <summary>
    /// Windows reports AltGr as Ctrl+Alt: a keyboard's right Alt arrives together with a left Ctrl that Windows adds. A
    /// hotkey of exactly Ctrl+Alt (or Ctrl+Alt+Shift) plus a key therefore also catches AltGr (or Shift+AltGr) plus that
    /// key, and the application never receives the character. Returns the first layout on which that key types a
    /// character with AltGr, or null. A dead key counts too: it starts an accented letter.
    /// </summary>
    internal static HotkeyCharacterClash? FindClash(Hotkey hotkey, IEnumerable<KeyboardLayout> layouts)
    {
        if ((hotkey.Modifiers & ~HotkeyModifiers.Shift) != (HotkeyModifiers.Control | HotkeyModifiers.Alt))
            return null;
        bool shift = (hotkey.Modifiers & HotkeyModifiers.Shift) != 0;
        foreach (KeyboardLayout layout in layouts)
        {
            string character = layout.AltGrCharacter(hotkey.VirtualKey, shift);
            if (character.Length > 0)
                return new HotkeyCharacterClash(character, layout.Name);
        }
        return null;
    }

    /// <summary>The layouts of this session, the ones the taskbar's input switcher offers.</summary>
    internal static IReadOnlyList<KeyboardLayout> Installed()
    {
        int count = GetKeyboardLayoutList(0, null);
        if (count <= 0)
            return [];
        var handles = new nint[count];
        count = GetKeyboardLayoutList(count, handles);
        return [.. handles.Take(Math.Max(0, count)).Select(handle =>
            new KeyboardLayout(NameOf(handle), (virtualKey, shift) => AltGrCharacterOn(handle, virtualKey, shift)))];
    }

    private static string AltGrCharacterOn(nint layout, uint virtualKey, bool shift)
    {
        var state = new byte[256];
        state[VkControl] = state[VkMenu] = state[VkLeftControl] = state[VkRightMenu] = 0x80;
        if (shift)
            state[VkShift] = state[VkLeftShift] = 0x80;
        var buffer = new char[8];
        // 0x4 leaves the keyboard state as it is, a pending dead key included.
        int count = ToUnicodeEx(
            virtualKey, MapVirtualKeyEx(virtualKey, 0, layout), state, buffer, buffer.Length, 0x4, layout);
        // A dead key reports -1 and its own character.
        string text = count == 0 ? "" : new string(buffer, 0, Math.Min(Math.Abs(count), buffer.Length));
        return text.Any(char.IsControl) ? "" : text;
    }

    private static string NameOf(nint layout)
    {
        int language = unchecked((int)((long)layout & 0xFFFF));
        try
        {
            return CultureInfo.GetCultureInfo(language).EnglishName;
        }
        catch (CultureNotFoundException)
        {
            return $"0x{language:X4}";
        }
    }

    [DllImport("user32.dll")]
    private static extern int GetKeyboardLayoutList(int count, [Out] nint[]? layouts);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyEx(uint code, uint mapType, nint layout);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(
        uint virtualKey,
        uint scanCode,
        byte[] keyState,
        [Out] char[] buffer,
        int bufferSize,
        uint flags,
        nint layout);
}
