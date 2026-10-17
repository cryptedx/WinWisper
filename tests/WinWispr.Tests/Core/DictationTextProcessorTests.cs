using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Core;

namespace WinWispr.Tests.Core;

[TestClass]
public sealed class DictationTextProcessorTests
{
    [TestMethod]
    public void Dictionary_matches_whole_phrases_longest_first_without_cascading()
    {
        PersonalDictionary dictionary = Parse("win = Windows\nWin Whisper = WinWispr\nWinWispr = Wrong\nC++ = C#\npreis = $5");
        Assert.AreEqual("WinWispr Windows window C# $5", DictationTextProcessor.Process(
            "WIN WHISPER win window C++ Preis", AppSettings.Default, dictionary));
    }

    [TestMethod]
    public void Voice_commands_are_opt_in_and_handle_recognizer_punctuation()
    {
        const string text = "Titel Doppelpunkt. Inhalt. Neuer Absatz. Aufzählung Milch. Aufzählung Brot. Neue Zeile Ende.";
        Assert.AreEqual(text, DictationTextProcessor.Process(text, AppSettings.Default, PersonalDictionary.Empty));
        Assert.AreEqual("Titel: Inhalt.\r\n\r\n• Milch.\r\n• Brot.\r\nEnde.",
            DictationTextProcessor.Process(text, AppSettings.Default with { VoiceCommandsEnabled = true }, PersonalDictionary.Empty));
        Assert.AreEqual("Doppelpunkte", DictationTextProcessor.Process("Doppelpunkte",
            AppSettings.Default with { VoiceCommandsEnabled = true }, PersonalDictionary.Empty));
    }

    [TestMethod]
    public void Dictionary_output_is_literal_even_when_it_contains_voice_commands()
    {
        var settings = AppSettings.Default with { VoiceCommandsEnabled = true };
        Assert.AreEqual("neuer Absatz", DictationTextProcessor.Process("Test", settings, Parse("Test = neuer Absatz")));
    }

    [TestMethod]
    public void Malformed_duplicate_or_oversized_entries_are_rejected()
    {
        foreach (string input in new[] { "missing separator", "= value", "key =", "a = x\nA = y",
            "! = x", new string('x', 201) + " = y", new string('x', 41_001) })
        {
            Assert.IsFalse(PersonalDictionary.TryParse(input, out _, out string? error));
            Assert.IsNotNull(error);
        }
        Assert.IsFalse(PersonalDictionary.TryParse(null, out _, out _));
        Assert.IsFalse(PersonalDictionary.TryParse(
            string.Join('\n', Enumerable.Range(0, 101).Select(i => $"word{i} = replacement")), out _, out _));
        Assert.IsTrue(PersonalDictionary.TryParse("\r\n", out PersonalDictionary empty, out _));
        Assert.AreEqual(0, empty.Entries.Count);
    }

    [TestMethod]
    public void Old_settings_disable_new_options_and_new_settings_round_trip()
    {
        var old = JsonSerializer.Deserialize<AppSettings>(
            "{\"Mode\":0,\"Hotkey\":{\"Modifiers\":5,\"VirtualKey\":88},\"MicrophoneId\":null}")!;
        Assert.IsFalse(old.VoiceCommandsEnabled);
        var settings = old with { VoiceCommandsEnabled = true };
        Assert.AreEqual(settings, JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings)));
    }

    [TestMethod]
    public void Saved_settings_no_longer_contain_the_personal_dictionary()
    {
        StringAssert.DoesNotMatch(JsonSerializer.Serialize(AppSettings.Default), new System.Text.RegularExpressions.Regex("PersonalDictionary"));
    }

    private static PersonalDictionary Parse(string text)
    {
        Assert.IsTrue(PersonalDictionary.TryParse(text, out PersonalDictionary dictionary, out string? error), error);
        return dictionary;
    }
}
