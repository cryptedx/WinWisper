using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Translation;

namespace WinWispr.Tests.Translation;

/// <summary>
/// Language detection measured through the Translator's interface on the qualification fixtures (QUALIFICATION.md
/// section 5, pre-declared criteria in <c>evidence/langid/criteria.md</c>): the 40 frozen sources, the 75 distinct
/// model outputs and 160 synthetic challenge inputs. The labels are the ones fixed before the first detector run:
/// de, en, any (either direction is acceptable) and none (no letters). No model is needed.
/// </summary>
[TestClass]
public sealed class LanguageDetectionTests
{
    private sealed record Item(string Id, string Language, string Text, string Category = "");

    private static readonly Lazy<JsonDocument> Fixtures = new(() => JsonDocument.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Translation", "Fixtures", "language-detection.json"))));

    private static IReadOnlyList<Item> Load(string section) =>
        Fixtures.Value.RootElement.GetProperty(section).EnumerateArray()
            .Select(element => new Item(
                element.GetProperty("id").GetString()!,
                element.TryGetProperty("language", out JsonElement language)
                    ? language.GetString()!
                    : element.GetProperty("expected").GetString()!,
                element.GetProperty("text").GetString()!,
                element.TryGetProperty("category", out JsonElement category) ? category.GetString()! : string.Empty))
            .ToList();

    private static Translator NewTranslator() => new(() => null, new FakeTranslationEngines().Create);

    private static int WordCount(string text) => Regex.Matches(text, @"\p{L}+").Count;

    private static DetectedLanguage Expected(string label) => label == "de" ? DetectedLanguage.German : DetectedLanguage.English;

    [TestMethod]
    public void The_fixture_data_is_complete()
    {
        Assert.AreEqual(40, Load("sources").Count);
        Assert.AreEqual(75, Load("outputs").Count);
        Assert.AreEqual(160, Load("challenge").Count);
    }

    [TestMethod]
    public void Every_fixture_source_and_every_fixture_output_is_detected_correctly_including_mixed_cases()
    {
        using Translator translator = NewTranslator();
        var wrong = new List<string>();

        foreach (Item item in Load("sources").Concat(Load("outputs")))
        {
            DetectedLanguage detected = translator.DetectLanguage(item.Text);
            if (detected != Expected(item.Language))
                wrong.Add($"{item.Id} ({item.Language}) detected {detected}: {item.Text}");
        }

        Assert.AreEqual(0, wrong.Count, Environment.NewLine + string.Join(Environment.NewLine, wrong));
    }

    [TestMethod]
    public void Challenge_inputs_of_four_or_more_words_resolve_at_least_95_percent_correctly_and_never_wrongly()
    {
        using Translator translator = NewTranslator();
        var labelled = Load("challenge").Where(i => i.Language is "de" or "en" && WordCount(i.Text) >= 4).ToList();
        var confidentlyWrong = new List<string>();
        int correct = 0;

        foreach (Item item in labelled)
        {
            DetectedLanguage detected = translator.DetectLanguage(item.Text);
            if (detected == Expected(item.Language))
                correct++;
            else if (detected is DetectedLanguage.German or DetectedLanguage.English)
                confidentlyWrong.Add($"{item.Id} detected {detected}: {item.Text}");
        }

        Assert.AreEqual(0, confidentlyWrong.Count, string.Join(Environment.NewLine, confidentlyWrong));
        Assert.IsTrue(correct >= Math.Ceiling(labelled.Count * 0.95), $"{correct} of {labelled.Count} resolved");
    }

    [TestMethod]
    public void Challenge_inputs_of_one_to_three_words_are_at_most_five_percent_confidently_wrong()
    {
        using Translator translator = NewTranslator();
        var labelled = Load("challenge").Where(i => i.Language is "de" or "en" && WordCount(i.Text) is >= 1 and <= 3).ToList();
        var confidentlyWrong = labelled
            .Where(i => translator.DetectLanguage(i.Text) is DetectedLanguage.German or DetectedLanguage.English
                && translator.DetectLanguage(i.Text) != Expected(i.Language))
            .Select(i => i.Id)
            .ToList();

        Assert.IsTrue(
            confidentlyWrong.Count <= labelled.Count * 0.05,
            $"{confidentlyWrong.Count} of {labelled.Count} confidently wrong: {string.Join(", ", confidentlyWrong)}");
    }

    [TestMethod]
    public void Inputs_without_a_language_signal_are_never_German_or_English()
    {
        using Translator translator = NewTranslator();

        foreach (Item item in Load("challenge").Where(i => i.Language == "none"))
        {
            DetectedLanguage detected = translator.DetectLanguage(item.Text);
            Assert.IsTrue(
                detected is DetectedLanguage.NoLetters or DetectedLanguage.Ambiguous,
                $"{item.Id}: {item.Text} detected {detected}");
            if (!item.Text.Any(char.IsLetter))
                Assert.AreEqual(DetectedLanguage.NoLetters, detected, item.Id);
        }
    }

    [TestMethod]
    public void Genuinely_ambiguous_inputs_never_throw_and_always_give_a_defined_answer()
    {
        using Translator translator = NewTranslator();

        foreach (Item item in Load("challenge").Where(i => i.Language == "any"))
            Assert.IsTrue(Enum.IsDefined(translator.DetectLanguage(item.Text)), item.Id);
    }

    [TestMethod]
    public void Shared_words_and_names_are_ambiguous_not_a_guess()
    {
        using Translator translator = NewTranslator();

        foreach (string text in new[] { "Server", "Chef", "Gift", "Project Atlas", "Power BI", "Microsoft Teams" })
            Assert.AreEqual(DetectedLanguage.Ambiguous, translator.DetectLanguage(text), text);
    }

    [TestMethod]
    public void Quoted_foreign_clauses_do_not_decide_the_language_of_the_sentence()
    {
        using Translator translator = NewTranslator();

        Assert.AreEqual(
            DetectedLanguage.English,
            translator.DetectLanguage("He said \"Wir liefern am Montag und die Rechnung folgt später\" and then he left."));
        Assert.AreEqual(
            DetectedLanguage.German,
            translator.DetectLanguage("Er sagte: \"We will ship on Monday and the invoice follows later\" und ging."));
    }

    [TestMethod]
    public void An_umlaut_or_sharp_s_alone_points_to_German_but_a_name_cannot_decide_alone()
    {
        using Translator translator = NewTranslator();

        Assert.AreEqual(DetectedLanguage.German, translator.DetectLanguage("Straße"));
        Assert.AreEqual(DetectedLanguage.English, translator.DetectLanguage("Mötley Crüe is on tour this summer and the tickets are sold out."));
    }
}
