using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Translation;

namespace WinWispr.Tests.Translation;

/// <summary>When a dictation translation has to wait for the model, so that "Loading translation…" shows only then.</summary>
[TestClass]
public sealed class TranslatorLoadStatusTests
{
    private const string German = "Ich möchte heute mit dir über den Termin sprechen.";
    private const string English = "I would like to talk with you about the appointment today.";

    [TestMethod]
    public async Task German_text_waits_for_the_load_until_the_direction_is_resident()
    {
        var factory = new FakeTranslationEngines();
        using var translator = new Translator(() => "models", factory.Create);

        Assert.IsTrue(translator.WouldWaitForLoadToEnglish(German));
        Assert.AreEqual(0, factory.Loads, "asking must not start a load");

        await translator.TranslateToEnglishAsync(German, TranslationUse.Dictation);

        Assert.IsFalse(translator.WouldWaitForLoadToEnglish(German));
    }

    [TestMethod]
    public async Task A_load_that_is_still_running_counts_as_waiting()
    {
        var factory = new FakeTranslationEngines { Gate = new ManualResetEventSlim(false) };
        using var translator = new Translator(() => "models", factory.Create);
        translator.Preload(TranslationDirection.GermanToEnglish);
        await factory.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsTrue(translator.WouldWaitForLoadToEnglish(German));

        factory.Gate.Set();
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("12345 / 678")]
    [DataRow(English)]
    public void Text_that_needs_no_model_never_waits_for_a_load(string text)
    {
        using var translator = new Translator(() => "models", new FakeTranslationEngines().Create);

        Assert.IsFalse(translator.WouldWaitForLoadToEnglish(text));
    }

    [TestMethod]
    public async Task English_text_waits_for_the_other_direction_for_a_selection()
    {
        var factory = new FakeTranslationEngines();
        using var translator = new Translator(() => "models", factory.Create);

        Assert.IsTrue(translator.WouldWaitForLoad(English));
        Assert.IsTrue(translator.WouldWaitForLoad(German));
        await translator.TranslateAsync(English);
        Assert.IsFalse(translator.WouldWaitForLoad(English), "English to German is resident now");
        Assert.IsTrue(translator.WouldWaitForLoad(German), "German to English is not");
        Assert.AreEqual(1, factory.Loads, "asking must not start a load");
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("12345 / 678")]
    public void Text_without_letters_never_waits_for_a_selection_load(string text)
    {
        using var translator = new Translator(() => "models", new FakeTranslationEngines().Create);

        Assert.IsFalse(translator.WouldWaitForLoad(text));
    }

    [TestMethod]
    public async Task Preparing_a_selection_starts_the_load_of_the_direction_its_language_needs()
    {
        var germanFactory = new FakeTranslationEngines();
        using var german = new Translator(() => "models", germanFactory.Create);
        german.PreloadForSelection(German);
        await WaitUntilAsync(() => german.IsLoaded(TranslationDirection.GermanToEnglish));
        CollectionAssert.AreEqual(new[] { TranslationDirection.GermanToEnglish }, germanFactory.LoadedDirections);

        var englishFactory = new FakeTranslationEngines();
        using var english = new Translator(() => "models", englishFactory.Create);
        english.PreloadForSelection(English);
        await WaitUntilAsync(() => english.IsLoaded(TranslationDirection.EnglishToGerman));
        CollectionAssert.AreEqual(new[] { TranslationDirection.EnglishToGerman }, englishFactory.LoadedDirections);
    }

    [TestMethod]
    public async Task A_translation_after_the_preparation_reuses_the_load_it_started()
    {
        var factory = new FakeTranslationEngines();
        using var translator = new Translator(() => "models", factory.Create);

        translator.PreloadForSelection(German);
        var result = await translator.TranslateAsync(German);

        Assert.IsInstanceOfType<TranslationResult.Translated>(result);
        Assert.AreEqual(1, factory.Loads);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("12345 / 678")]
    public async Task Preparing_text_without_letters_loads_nothing(string text)
    {
        var factory = new FakeTranslationEngines();
        using var translator = new Translator(() => "models", factory.Create);

        translator.PreloadForSelection(text);
        await Task.Delay(150);

        Assert.AreEqual(0, factory.Loads);
    }

    [TestMethod]
    public async Task Preparing_a_selection_over_the_limit_loads_nothing_because_it_will_not_be_translated()
    {
        var factory = new FakeTranslationEngines();
        using var translator = new Translator(
            () => "models", factory.Create, limits: new TranslationLimits { SelectionMaxCharacters = 10 });

        translator.PreloadForSelection(German);
        await Task.Delay(150);

        Assert.AreEqual(0, factory.Loads);
    }

    [TestMethod]
    public void Preparing_after_the_translator_was_disposed_does_not_throw()
    {
        var translator = new Translator(() => "models", new FakeTranslationEngines().Create);
        translator.Dispose();

        translator.PreloadForSelection(German);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            Assert.IsTrue(DateTime.UtcNow < deadline, "The condition did not hold within 10 seconds.");
            await Task.Delay(10);
        }
    }
}
