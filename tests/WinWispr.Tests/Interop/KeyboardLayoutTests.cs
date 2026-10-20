using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Core;
using WinWispr.Interop;

namespace WinWispr.Tests.Interop;

[TestClass]
public sealed class KeyboardLayoutTests
{
    private const HotkeyModifiers ControlAlt = HotkeyModifiers.Control | HotkeyModifiers.Alt;

    // What AltGr types on each layout, without and with Shift.
    private static readonly KeyboardLayout German = Fake("German (Germany)", new() { [0x45] = "€", [0x51] = "@", [0x4D] = "µ" });
    private static readonly KeyboardLayout Czech = Fake("Czech (Czechia)", new() { [0x56] = "@", [0x42] = "{" });
    private static readonly KeyboardLayout English = Fake("English (United States)", new());

    [TestMethod]
    public void A_ctrl_alt_hotkey_clashes_where_altgr_types_a_character_with_its_key()
    {
        Assert.AreEqual(new HotkeyCharacterClash("€", "German (Germany)"),
            KeyboardLayout.FindClash(new Hotkey(ControlAlt, 0x45), [English, German]));
        Assert.AreEqual(new HotkeyCharacterClash("@", "Czech (Czechia)"),
            KeyboardLayout.FindClash(new Hotkey(ControlAlt, 0x56), [German, Czech]));
        Assert.AreEqual(new HotkeyCharacterClash("{", "Czech (Czechia)"),
            KeyboardLayout.FindClash(new Hotkey(ControlAlt, 0x42), [German, Czech]));
    }

    [TestMethod]
    public void A_ctrl_alt_hotkey_whose_key_types_nothing_with_altgr_is_free()
    {
        Assert.IsNull(KeyboardLayout.FindClash(new Hotkey(ControlAlt, 0x54), [German, Czech, English]));
        Assert.IsNull(KeyboardLayout.FindClash(new Hotkey(ControlAlt, 0x45), [English]));
        Assert.IsNull(KeyboardLayout.FindClash(new Hotkey(ControlAlt, 0x45), []));
    }

    [TestMethod]
    public void Only_ctrl_alt_with_or_without_shift_is_altgr()
    {
        KeyboardLayout shifted = Fake("Shifted", new(), new() { [0x45] = "Ä" });

        Assert.AreEqual(new HotkeyCharacterClash("Ä", "Shifted"),
            KeyboardLayout.FindClash(new Hotkey(ControlAlt | HotkeyModifiers.Shift, 0x45), [shifted]));
        Assert.IsNull(KeyboardLayout.FindClash(new Hotkey(ControlAlt, 0x45), [shifted]));
        foreach (HotkeyModifiers modifiers in new[]
        {
            HotkeyModifiers.Shift | HotkeyModifiers.Alt,
            HotkeyModifiers.Control | HotkeyModifiers.Shift,
            HotkeyModifiers.Alt,
            ControlAlt | HotkeyModifiers.Windows
        })
            Assert.IsNull(KeyboardLayout.FindClash(new Hotkey(modifiers, 0x45), [German]), modifiers.ToString());
    }

    [TestMethod]
    public void The_new_default_hotkeys_clash_on_no_layout_and_the_old_translate_hotkey_on_german()
    {
        AppSettings defaults = AppSettings.Default;
        foreach (Hotkey hotkey in new[]
        {
            defaults.Hotkey, defaults.BoardHotkey, defaults.TaskHotkey, defaults.EnglishDictationHotkey,
            defaults.TranslateHotkey, defaults.ReplayHotkey
        })
            Assert.IsNull(KeyboardLayout.FindClash(hotkey, [German, Czech]), hotkey.ToString());

        AppSettings existing = AppSettings.ExistingInstallationDefaults;
        Assert.AreEqual("€", KeyboardLayout.FindClash(existing.TranslateHotkey, [German])?.Character);
        Assert.AreEqual("@", KeyboardLayout.FindClash(existing.ReplayHotkey, [Czech])?.Character);
        Assert.AreEqual("{", KeyboardLayout.FindClash(existing.BoardHotkey, [Czech])?.Character);
    }

    [TestMethod]
    public void The_installed_german_layout_reports_euro_for_altgr_e_and_nothing_for_altgr_b()
    {
        KeyboardLayout german = KeyboardLayout.Installed().FirstOrDefault(layout => layout.Name == "German (Germany)")
            ?? throw new AssertInconclusiveException("The German (Germany) keyboard layout is not installed on this machine.");

        Assert.AreEqual(new HotkeyCharacterClash("€", "German (Germany)"), KeyboardLayout.FindClash(new Hotkey(ControlAlt, 0x45), [german]));
        Assert.AreEqual("@", KeyboardLayout.FindClash(new Hotkey(ControlAlt, 0x51), [german])?.Character);
        Assert.IsNull(KeyboardLayout.FindClash(new Hotkey(ControlAlt, 0x42), [german]));
        Assert.IsNull(KeyboardLayout.FindClash(new Hotkey(ControlAlt, 0x56), [german]));
        // Asking leaves no dead key behind: the same question gives the same answer.
        Assert.AreEqual("€", KeyboardLayout.FindClash(new Hotkey(ControlAlt, 0x45), [german])?.Character);
    }

    private static KeyboardLayout Fake(string name, Dictionary<uint, string> altGr, Dictionary<uint, string>? shiftAltGr = null) =>
        new(name, (virtualKey, shift) => (shift ? shiftAltGr : altGr)?.GetValueOrDefault(virtualKey, "") ?? "");
}
