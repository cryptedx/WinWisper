using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Core;

namespace WinWispr.Tests.Core;

[TestClass]
public sealed class PersonalDictionaryStoreTests
{
    private const string LegacyEntries = "legacy = Legacy";

    [TestMethod]
    public void Rule1_existing_file_applies_even_when_empty()
    {
        InDirectory(path =>
        {
            File.WriteAllText(path + ".dictionary", "");
            File.WriteAllText(path + ".dictionary.bak", "name = Name");
            PersonalDictionaryStore store = Load(path);
            Assert.AreEqual("", store.Text);
            Assert.AreEqual(0, store.Dictionary.Entries.Count);
            Assert.IsNull(store.LoadWarning);
            Assert.IsTrue(store.CanEdit);
        });
    }

    [TestMethod]
    public void Rule2_missing_file_is_restored_from_its_backup_with_a_warning()
    {
        InDirectory(path =>
        {
            File.WriteAllText(path, JsonSerializer.Serialize(AppSettings.Default));
            File.WriteAllText(path + ".dictionary.bak", "name = Name");
            PersonalDictionaryStore store = Load(path);
            Assert.AreEqual("name = Name", store.Text);
            Assert.AreEqual("Name", store.Dictionary.Entries["name"]);
            Assert.AreEqual(PersonalDictionaryStore.RecoveredFromBackupWarning, store.LoadWarning);
            Assert.IsTrue(store.CanEdit);
            Assert.AreEqual("name = Name", File.ReadAllText(path + ".dictionary"));
            Assert.AreEqual("name = Name", File.ReadAllText(path + ".dictionary.bak"));
        });
    }

    [TestMethod]
    public void Rule3_legacy_field_is_migrated_once_from_settings_or_its_backup()
    {
        foreach (bool fromBackup in new[] { false, true })
            InDirectory(path =>
            {
                string settings = "{\"Mode\":1,\"PersonalDictionary\":\"" + LegacyEntries + "\"}";
                if (fromBackup)
                {
                    File.WriteAllText(path, "{broken");
                    File.WriteAllText(path + ".bak", settings);
                }
                else
                    File.WriteAllText(path, settings);

                PersonalDictionaryStore store = Load(path);

                Assert.AreEqual(LegacyEntries, store.Text);
                Assert.IsNull(store.LoadWarning);
                Assert.AreEqual(LegacyEntries, File.ReadAllText(path + ".dictionary"));
                Assert.IsFalse(File.Exists(path + ".dictionary.legacy"));
            });
    }

    [TestMethod]
    public void Rule4_no_dictionary_anywhere_is_empty_and_editable()
    {
        InDirectory(path =>
        {
            PersonalDictionaryStore store = Load(path);
            Assert.AreEqual("", store.Text);
            Assert.IsTrue(store.CanEdit);
            Assert.IsFalse(File.Exists(path + ".dictionary"));
            Assert.AreEqual(DictionarySaveStatus.Saved, store.Replace("Win Whisper = WinWispr\r\nGrüße = Grüße").Status);
            Assert.AreEqual("Win Whisper = WinWispr\r\nGrüße = Grüße", Load(path).Text);
            Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp-*").Length);
        });
    }

    [TestMethod]
    public void Rule5_unreadable_file_locks_editing_even_after_the_lock_is_released()
    {
        InDirectory(path =>
        {
            File.WriteAllText(path + ".dictionary", "name = Name");
            var store = new PersonalDictionaryStore(path);
            using (var locked = new FileStream(path + ".dictionary", FileMode.Open, FileAccess.Read, FileShare.None))
                store.Load();
            Assert.AreEqual(PersonalDictionaryStore.UnreadableWarning, store.LoadWarning);
            Assert.IsFalse(store.CanEdit);
            Assert.AreEqual(
                new DictionarySaveResult(DictionarySaveStatus.Locked, PersonalDictionaryStore.UnreadableWarning),
                store.Replace("other = Other"));
            Assert.AreEqual("name = Name", File.ReadAllText(path + ".dictionary"));
        });
    }

    [TestMethod]
    public void Rule6_file_wins_over_a_differing_legacy_field_which_is_kept_once()
    {
        InDirectory(path =>
        {
            File.WriteAllText(path, "{\"PersonalDictionary\":\"" + LegacyEntries + "\"}");
            File.WriteAllText(path + ".dictionary", "name = Name");
            PersonalDictionaryStore store = Load(path);
            Assert.AreEqual("name = Name", store.Text);
            Assert.IsNull(store.LoadWarning);
            Assert.IsTrue(store.CanEdit);
            Assert.AreEqual(LegacyEntries, File.ReadAllText(path + ".dictionary.legacy"));

            File.WriteAllText(path, "{\"PersonalDictionary\":\"later = Later\"}");
            Load(path);
            Assert.AreEqual(LegacyEntries, File.ReadAllText(path + ".dictionary.legacy"));
        });
    }

    [TestMethod]
    public void Matching_legacy_field_writes_no_legacy_copy()
    {
        InDirectory(path =>
        {
            File.WriteAllText(path, "{\"PersonalDictionary\":\"name = Name\"}");
            File.WriteAllText(path + ".dictionary", "name = Name");
            Load(path);
            Assert.IsFalse(File.Exists(path + ".dictionary.legacy"));
        });
    }

    [TestMethod]
    public void Invalid_text_is_rejected_with_the_parser_error_and_keeps_the_saved_dictionary()
    {
        InDirectory(path =>
        {
            PersonalDictionaryStore store = Load(path);
            store.Replace("name = Name");
            DictionarySaveResult result = store.Replace("missing separator");
            Assert.AreEqual(DictionarySaveStatus.Invalid, result.Status);
            StringAssert.StartsWith(result.Message, "Check line 1:");
            Assert.AreEqual("name = Name", store.Text);
            Assert.AreEqual("name = Name", File.ReadAllText(path + ".dictionary"));
        });
    }

    [TestMethod]
    public void Saved_edit_updates_the_parsed_dictionary()
    {
        InDirectory(path =>
        {
            PersonalDictionaryStore store = Load(path);
            store.Replace("Win Whisper = WinWispr");
            Assert.AreEqual("WinWispr", store.Dictionary.Entries["win whisper"]);
        });
    }

    [TestMethod]
    public void External_changes_cannot_be_overwritten_by_a_stale_edit()
    {
        InDirectory(path =>
        {
            PersonalDictionaryStore store = Load(path);
            store.Replace("name = Name");
            File.WriteAllText(path + ".dictionary", "external = External");
            Assert.AreEqual(
                new DictionarySaveResult(DictionarySaveStatus.Locked, PersonalDictionaryStore.ChangedOnDiskMessage),
                store.Replace(""));
            Assert.AreEqual("external = External", File.ReadAllText(path + ".dictionary"));
        });
    }

    [TestMethod]
    public void Explicit_clear_survives_restart_and_keeps_a_recovery_copy()
    {
        InDirectory(path =>
        {
            PersonalDictionaryStore store = Load(path);
            store.Replace("name = Name");
            store.Replace("");
            PersonalDictionaryStore restarted = Load(path);
            Assert.AreEqual("", restarted.Text);
            Assert.IsNull(restarted.LoadWarning);
            Assert.AreEqual("name = Name", File.ReadAllText(path + ".dictionary.bak"));
        });
    }

    [TestMethod]
    public void Failed_write_keeps_the_previous_file_and_backup()
    {
        InDirectory(path =>
        {
            PersonalDictionaryStore store = Load(path);
            store.Replace("old = Old");
            store.Replace("current = Current");
            DictionarySaveResult result;
            using (var locked = new FileStream(path + ".dictionary", FileMode.Open, FileAccess.Read, FileShare.Read))
                result = store.Replace("");
            Assert.AreEqual(
                new DictionarySaveResult(DictionarySaveStatus.Failed, PersonalDictionaryStore.SaveFailedMessage),
                result);
            Assert.AreEqual("current = Current", File.ReadAllText(path + ".dictionary"));
            Assert.AreEqual("old = Old", File.ReadAllText(path + ".dictionary.bak"));
            Assert.AreEqual("current = Current", store.Text);
            Assert.AreEqual(0, Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp-*").Length);
        });
    }

    private static PersonalDictionaryStore Load(string path)
    {
        var store = new PersonalDictionaryStore(path);
        store.Load();
        return store;
    }

    private static void InDirectory(Action<string> check)
    {
        string directory = Path.Combine(Path.GetTempPath(), "WinWispr-dictionary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { check(Path.Combine(directory, "settings.json")); }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
