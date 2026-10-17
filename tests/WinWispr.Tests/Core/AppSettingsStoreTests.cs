using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Core;

namespace WinWispr.Tests.Core;

[TestClass]
public sealed class AppSettingsStoreTests
{
    [TestMethod]
    public void Invalid_unrelated_fields_and_a_legacy_dictionary_field_do_not_fail_the_load()
    {
        foreach (string field in new[] { "\"Mode\":99", "\"Hotkey\":{}", "\"MicrophoneId\":42", "\"VoiceCommandsEnabled\":{}", "\"SpeechMode\":null" })
            Assert.AreEqual(AppSettings.Default,
                AppSettingsStore.Parse("{" + field + ",\"PersonalDictionary\":\"old dictionary text\"}"), field);
        Assert.AreEqual(AppSettings.Default, AppSettingsStore.Parse("{\"PersonalDictionary\":42}"));
    }

    [TestMethod]
    public void Automatic_update_checks_are_on_unless_the_file_turns_them_off()
    {
        Assert.IsTrue(AppSettingsStore.Parse("{}").AutomaticUpdateChecks);
        Assert.IsTrue(AppSettingsStore.Parse("{\"AutomaticUpdateChecks\":\"no\"}").AutomaticUpdateChecks);
        Assert.IsFalse(AppSettingsStore.Parse("{\"AutomaticUpdateChecks\":false}").AutomaticUpdateChecks);
        Assert.IsFalse(AppSettingsStore.Parse(JsonSerializer.Serialize(AppSettings.Default with { AutomaticUpdateChecks = false }))
            .AutomaticUpdateChecks);
    }

    [TestMethod]
    public void English_dictation_defaults_to_shift_alt_e_in_hold_mode()
    {
        AppSettings settings = AppSettingsStore.Parse("{}");

        Assert.AreEqual(new Hotkey(HotkeyModifiers.Shift | HotkeyModifiers.Alt, 0x45), settings.EnglishDictationHotkey);
        Assert.AreEqual(RecordingMode.Hold, settings.EnglishDictationMode);
        Assert.AreEqual(settings, AppSettings.Default);
    }

    [TestMethod]
    public void English_dictation_hotkey_and_mode_round_trip_through_the_file()
    {
        InDirectory(path =>
        {
            var store = new AppSettingsStore(path);
            AppSettings custom = store.Load() with
            {
                EnglishDictationHotkey = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x4A),
                EnglishDictationMode = RecordingMode.Toggle
            };
            Assert.AreEqual(SettingsSaveStatus.Saved, store.Save(custom).Status);

            Assert.AreEqual(custom, new AppSettingsStore(path).Load());
        });
    }

    [TestMethod]
    public void Invalid_stored_english_dictation_values_fall_back_to_the_defaults()
    {
        AppSettings defaults = AppSettings.Default;
        foreach (string field in new[]
        {
            "\"EnglishDictationHotkey\":{\"Modifiers\":0,\"VirtualKey\":69}",
            "\"EnglishDictationHotkey\":{\"Modifiers\":99,\"VirtualKey\":69}",
            "\"EnglishDictationHotkey\":{\"Modifiers\":4,\"VirtualKey\":27}",
            "\"EnglishDictationHotkey\":{\"Modifiers\":4,\"VirtualKey\":89}",
            "\"EnglishDictationHotkey\":{\"Modifiers\":4,\"VirtualKey\":0}",
            "\"EnglishDictationHotkey\":{\"Modifiers\":4,\"VirtualKey\":300}",
            "\"EnglishDictationHotkey\":\"Shift+Alt+E\"",
            "\"EnglishDictationHotkey\":null",
            "\"EnglishDictationHotkey\":{}"
        })
        {
            AppSettings settings = AppSettingsStore.Parse("{" + field + "}");
            Assert.AreEqual(defaults.EnglishDictationHotkey, settings.EnglishDictationHotkey, field);
        }
        foreach (string field in new[] { "\"EnglishDictationMode\":99", "\"EnglishDictationMode\":\"Hold\"", "\"EnglishDictationMode\":null" })
            Assert.AreEqual(RecordingMode.Hold, AppSettingsStore.Parse("{" + field + "}").EnglishDictationMode, field);
    }

    [TestMethod]
    public void An_invalid_english_dictation_field_does_not_disturb_the_other_settings()
    {
        AppSettings settings = AppSettingsStore.Parse("{\"Mode\":1,\"EnglishDictationMode\":99}");

        Assert.AreEqual(RecordingMode.Hold, settings.Mode);
        Assert.AreEqual(RecordingMode.Hold, settings.EnglishDictationMode);
    }

    [TestMethod]
    public void The_translate_hotkey_defaults_to_ctrl_alt_e()
    {
        AppSettings settings = AppSettingsStore.Parse("{}");

        Assert.AreEqual(new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x45), settings.TranslateHotkey);
        Assert.AreEqual(settings, AppSettings.Default);
    }

    [TestMethod]
    public void The_translate_hotkey_round_trips_through_the_file()
    {
        InDirectory(path =>
        {
            var store = new AppSettingsStore(path);
            AppSettings custom = store.Load() with
            {
                TranslateHotkey = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x4B)
            };
            Assert.AreEqual(SettingsSaveStatus.Saved, store.Save(custom).Status);

            Assert.AreEqual(custom, new AppSettingsStore(path).Load());
        });
    }

    [TestMethod]
    public void An_invalid_stored_translate_hotkey_falls_back_to_the_default_without_disturbing_the_rest()
    {
        AppSettings defaults = AppSettings.Default;
        foreach (string field in new[]
        {
            "\"TranslateHotkey\":{\"Modifiers\":0,\"VirtualKey\":69}",
            "\"TranslateHotkey\":{\"Modifiers\":99,\"VirtualKey\":69}",
            "\"TranslateHotkey\":{\"Modifiers\":2,\"VirtualKey\":27}",
            "\"TranslateHotkey\":{\"Modifiers\":2,\"VirtualKey\":89}",
            "\"TranslateHotkey\":{\"Modifiers\":2,\"VirtualKey\":0}",
            "\"TranslateHotkey\":{\"Modifiers\":2,\"VirtualKey\":300}",
            "\"TranslateHotkey\":\"Ctrl+Alt+E\"",
            "\"TranslateHotkey\":null",
            "\"TranslateHotkey\":{}"
        })
        {
            AppSettings settings = AppSettingsStore.Parse("{\"Mode\":1," + field + "}");
            Assert.AreEqual(defaults.TranslateHotkey, settings.TranslateHotkey, field);
            Assert.AreEqual(RecordingMode.Hold, settings.Mode, field);
        }
    }

    [TestMethod]
    public void Saves_leave_no_temporary_files_and_keep_a_backup()
    {
        InDirectory(path =>
        {
            var store = new AppSettingsStore(path);
            AppSettings settings = store.Load() with { VoiceCommandsEnabled = true };
            Assert.AreEqual(SettingsSaveStatus.Saved, store.Save(settings).Status);
            var restarted = new AppSettingsStore(path);
            Assert.AreEqual(SettingsSaveStatus.Saved,
                restarted.Save(restarted.Load() with { SettingsWindowPlacement = new(1, 2, 640, 760) }).Status);
            Assert.AreEqual(new WindowPlacement(1, 2, 640, 760), new AppSettingsStore(path).Load().SettingsWindowPlacement);
            Assert.IsTrue(AppSettingsStore.Parse(File.ReadAllText(path + ".bak")).VoiceCommandsEnabled);
            Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp-*").Length);
        });
    }

    [TestMethod]
    public void Saved_settings_drop_the_legacy_dictionary_field()
    {
        InDirectory(path =>
        {
            File.WriteAllText(path, "{\"Mode\":1,\"PersonalDictionary\":\"name = Name\"}");
            var store = new AppSettingsStore(path);
            Assert.AreEqual(SettingsSaveStatus.Saved, store.Save(store.Load()).Status);
            StringAssert.DoesNotMatch(File.ReadAllText(path), new System.Text.RegularExpressions.Regex("PersonalDictionary"));
        });
    }

    [TestMethod]
    public void Corrupt_or_missing_primary_recovers_backup_without_overwriting_it_with_corruption()
    {
        foreach (bool missing in new[] { false, true })
            InDirectory(path =>
            {
                AppSettings settings = AppSettings.Default with { Mode = RecordingMode.Hold };
                File.WriteAllText(path + ".bak", JsonSerializer.Serialize(settings));
                if (!missing) File.WriteAllText(path, "{broken");
                var store = new AppSettingsStore(path);
                Assert.AreEqual(settings, store.Load());
                Assert.AreEqual("Settings were recovered from a backup. Please check your settings.", store.LoadWarning);
                Assert.AreEqual(SettingsSaveStatus.Saved, store.Save(settings with { VoiceCommandsEnabled = true }).Status);
                Assert.AreEqual(RecordingMode.Hold, new AppSettingsStore(path).Load().Mode);
                Assert.AreEqual(settings, AppSettingsStore.Parse(File.ReadAllText(path + ".bak")));
                if (!missing)
                    Assert.AreEqual("{broken", File.ReadAllText(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.corrupt-*").Single()));
            });
    }

    [TestMethod]
    public void Failed_load_cannot_overwrite_original_even_if_file_becomes_readable_later()
    {
        InDirectory(path =>
        {
            File.WriteAllText(path, "{broken");
            var store = new AppSettingsStore(path);
            AppSettings fallback = store.Load();
            Assert.IsNotNull(store.LoadWarning);
            File.WriteAllText(path, "recovered externally");
            Assert.AreEqual(
                new SettingsSaveResult(SettingsSaveStatus.Failed, AppSettingsStore.SaveFailedMessage),
                store.Save(fallback));
            Assert.AreEqual("recovered externally", File.ReadAllText(path));
        });
    }

    [TestMethod]
    public void Failed_atomic_replace_leaves_original_and_backup_intact()
    {
        InDirectory(path =>
        {
            var store = new AppSettingsStore(path);
            AppSettings settings = store.Load() with { VoiceCommandsEnabled = true };
            store.Save(settings);
            store.Save(settings);
            byte[] original = File.ReadAllBytes(path);
            byte[] backup = File.ReadAllBytes(path + ".bak");
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.AreEqual(SettingsSaveStatus.Failed, store.Save(settings with { VoiceCommandsEnabled = false }).Status);
            CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
            CollectionAssert.AreEqual(backup, File.ReadAllBytes(path + ".bak"));
            Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp-*").Length);
        });
    }

    [TestMethod]
    public void Settings_saves_do_not_touch_the_dictionary_or_its_backup()
    {
        InDirectory(path =>
        {
            File.WriteAllText(path + ".dictionary", "new = New");
            File.WriteAllText(path + ".dictionary.bak", "old = Old");
            DateTime timestamp = new(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(path + ".dictionary", timestamp);
            var store = new AppSettingsStore(path);
            using (var locked = new FileStream(path + ".dictionary", FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.AreEqual(SettingsSaveStatus.Saved, store.Save(store.Load() with { VoiceCommandsEnabled = true }).Status);
            Assert.AreEqual(timestamp, File.GetLastWriteTimeUtc(path + ".dictionary"));
            Assert.AreEqual("old = Old", File.ReadAllText(path + ".dictionary.bak"));
        });
    }

    [TestMethod]
    public void Unreadable_settings_do_not_lock_the_dictionary()
    {
        InDirectory(path =>
        {
            File.WriteAllText(path, "{broken");
            File.WriteAllText(path + ".bak", "{broken backup");
            File.WriteAllText(path + ".dictionary", "name = Name");
            var settings = new AppSettingsStore(path);
            settings.Load();
            Assert.IsNotNull(settings.LoadWarning);

            var dictionary = new PersonalDictionaryStore(path);
            dictionary.Load();
            Assert.IsNull(dictionary.LoadWarning);
            Assert.AreEqual(DictionarySaveStatus.Saved, dictionary.Replace("other = Other").Status);
            Assert.AreEqual("other = Other", File.ReadAllText(path + ".dictionary"));
            Assert.AreEqual("{broken", File.ReadAllText(path));
        });
    }

    private static void InDirectory(Action<string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "WinWispr-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { check(Path.Combine(directory, "settings.json")); }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
