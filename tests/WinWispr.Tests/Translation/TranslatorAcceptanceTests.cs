using System.Diagnostics;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Speech;
using WinWispr.Tests.Speech;
using WinWispr.Translation;

namespace WinWispr.Tests.Translation;

/// <summary>
/// The Translator against the real Opus-MT int8 models on the shipped ONNX Runtime. They carry the
/// WindowsAcceptance category, which the default suites skip, and they are inconclusive unless
/// WINWISPR_TRANSLATION_MODEL_DIR names the folder with the ten pinned model files (see
/// <see cref="TranslationModelFixtures"/>). Procedure: docs/testing/translation-acceptance.md.
/// </summary>
[TestClass]
public sealed class TranslatorAcceptanceTests
{
    /// <summary>The user's hard requirement: a translation never takes longer than three seconds.</summary>
    private static readonly TimeSpan LatencyBudget = TimeSpan.FromSeconds(3);

    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    [DataRow("de-en")]
    [DataRow("en-de")]
    public void Tokenizer_reproduces_the_reference_ids_and_detokenized_text(string key)
    {
        TranslationDirection direction = key == "de-en"
            ? TranslationDirection.GermanToEnglish
            : TranslationDirection.EnglishToGerman;
        string directory = TranslationModelFixtures.DirectionDirectory(direction);
        var tokenizer = new MarianTokenizer(directory);
        using JsonDocument parity = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Translation", "Fixtures", "tokenizer-parity.json")));
        JsonElement reference = parity.RootElement.GetProperty("directions").GetProperty(key);
        var mismatches = new List<string>();

        int encoded = 0;
        foreach (JsonElement item in reference.GetProperty("encode").EnumerateArray())
        {
            encoded++;
            int[] expected = item.GetProperty("ids").EnumerateArray().Select(id => id.GetInt32()).ToArray();
            int[] actual = tokenizer.Encode(item.GetProperty("text").GetString()!);
            if (!expected.SequenceEqual(actual))
                mismatches.Add($"encode {item.GetProperty("id").GetString()}");
        }

        int decoded = 0;
        foreach (JsonElement item in reference.GetProperty("decode").EnumerateArray())
        {
            decoded++;
            int[] ids = item.GetProperty("target_ids").EnumerateArray().Select(id => id.GetInt32()).ToArray();
            if (tokenizer.Decode(ids) != item.GetProperty("text").GetString())
                mismatches.Add($"decode {item.GetProperty("id").GetString()}");
        }

        Assert.AreEqual(44, encoded);
        Assert.AreEqual(44, decoded);
        Assert.AreEqual(0, mismatches.Count, "differs from the Python reference: " + string.Join(", ", mismatches));
    }

    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    public async Task Fixture_sentences_keep_numbers_names_and_negation_in_both_directions()
    {
        string models = TranslationModelFixtures.Require();
        using var translator = new Translator(() => models);
        var problems = new List<string>();

        foreach (FixtureSentence sentence in FixtureSentence.All)
        {
            TranslationResult result = await translator.TranslateAsync(sentence.Source, sentence.Direction);
            if (result is not TranslationResult.Translated translated)
            {
                problems.Add($"{sentence.Case}: {result}");
                continue;
            }

            Console.WriteLine($"{sentence.Case}: {translated.Text}");
            if (string.IsNullOrWhiteSpace(translated.Text))
                problems.Add($"{sentence.Case}: empty translation");
            foreach (string violation in sentence.Violations(translated.Text))
                problems.Add($"{sentence.Case}: {violation} in \"{translated.Text}\"");
        }

        Assert.AreEqual(0, problems.Count, Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    public async Task A_warm_translation_never_takes_longer_than_three_seconds()
    {
        string models = TranslationModelFixtures.Require();
        using var translator = new Translator(() => models);
        var problems = new List<string>();
        var timings = new List<string>();

        foreach (TranslationDirection direction in Enum.GetValues<TranslationDirection>())
        {
            // The first call of a direction loads it and runs once cold; the budget applies to warm translations.
            await translator.TranslateAsync("Warm up.", direction);
            Assert.IsTrue(translator.IsLoaded(direction));
        }

        foreach (FixtureSentence sentence in FixtureSentence.All)
        {
            var watch = Stopwatch.StartNew();
            TranslationResult result = await translator.TranslateAsync(sentence.Source, sentence.Direction);
            watch.Stop();
            timings.Add($"{sentence.Case}: {watch.Elapsed.TotalSeconds:0.00} s");
            Assert.IsInstanceOfType<TranslationResult.Translated>(result, sentence.Case);
            if (watch.Elapsed > LatencyBudget)
                problems.Add($"{sentence.Case} took {watch.Elapsed.TotalSeconds:0.00} s");
        }

        Console.WriteLine(string.Join(Environment.NewLine, timings));
        Assert.AreEqual(0, problems.Count, "over the three-second budget: " + string.Join("; ", problems));
    }

    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    public async Task Loading_is_lazy_per_direction_and_a_translation_during_a_preload_waits_for_it()
    {
        string directory = TranslationModelFixtures.Require();
        int resolved = 0;
        using var translator = new Translator(() => { Interlocked.Increment(ref resolved); return directory; });
        Assert.AreEqual(0, resolved, "constructing the module must not touch the model");
        Assert.IsFalse(translator.IsLoaded(TranslationDirection.GermanToEnglish));
        Assert.IsFalse(translator.IsLoadPending(TranslationDirection.GermanToEnglish));

        var watch = Stopwatch.StartNew();
        translator.Preload(TranslationDirection.GermanToEnglish);
        TimeSpan preloadCall = watch.Elapsed;
        Assert.IsTrue(preloadCall < TimeSpan.FromMilliseconds(500), $"Preload blocked for {preloadCall.TotalMilliseconds:0} ms");
        Assert.IsTrue(translator.IsLoadPending(TranslationDirection.GermanToEnglish), "a load takes about two seconds");

        TranslationResult first = await translator.TranslateAsync("Das ist ein Test.", TranslationDirection.GermanToEnglish);

        Assert.IsInstanceOfType<TranslationResult.Translated>(first);
        Assert.IsFalse(translator.IsLoadPending(TranslationDirection.GermanToEnglish));
        Assert.IsTrue(translator.IsLoaded(TranslationDirection.GermanToEnglish));
        Assert.IsFalse(translator.IsLoaded(TranslationDirection.EnglishToGerman), "the other direction is never preloaded");
        Assert.AreEqual(1, resolved, "the translation during the load must not start a second load");

        TranslationResult second = await translator.TranslateAsync("Das ist noch ein Test.", TranslationDirection.GermanToEnglish);
        Assert.IsInstanceOfType<TranslationResult.Translated>(second);
        Assert.AreEqual(1, resolved, "a loaded direction is reused");
    }

    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    public async Task A_sentence_over_the_token_limit_is_cut_into_chunks_not_refused_and_the_model_stays_usable()
    {
        string models = TranslationModelFixtures.Require();
        using var translator = new Translator(() => models);
        string tooLong = string.Join(
            ' ',
            Enumerable.Range(0, 40).Select(i => $"und dann prüfen wir Punkt {i} der Liste und den Server")) + " und das war es";

        TranslationResult result = await translator.TranslateAsync(
            tooLong, TranslationUse.Dictation, TranslationDirection.GermanToEnglish);

        Assert.IsInstanceOfType<TranslationResult.Translated>(result, "a long sentence is split into chunks, never refused");
        int words = ((TranslationResult.Translated)result).Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        Assert.IsTrue(words >= tooLong.Split(' ').Length * 0.6, $"{words} words out of {tooLong.Split(' ').Length}: the text was cut short");
        TranslationResult after = await translator.TranslateAsync("Der Server antwortet nicht mehr.", TranslationDirection.GermanToEnglish);
        Assert.IsInstanceOfType<TranslationResult.Translated>(after);
    }

    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    public async Task A_chunk_the_model_cannot_finish_is_a_failed_result_not_a_runaway_or_an_exception()
    {
        string models = TranslationModelFixtures.Require();
        using var translator = new Translator(() => models);
        // One sentence repeated 60 times without punctuation makes the greedy decoder loop until the output cap.
        string loop = string.Join(' ', Enumerable.Repeat("Der Server antwortet nicht mehr", 60));

        TranslationResult result = await translator.TranslateAsync(
            loop, TranslationUse.Dictation, TranslationDirection.GermanToEnglish);

        Assert.IsTrue(result is TranslationResult.Failed or TranslationResult.Translated, result.ToString());
        TranslationResult after = await translator.TranslateAsync("Der Server antwortet nicht mehr.", TranslationDirection.GermanToEnglish);
        Assert.IsInstanceOfType<TranslationResult.Translated>(after, "the model stays usable after a failed chunk");
    }

    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    public async Task Speech_recognition_still_loads_and_recognizes_with_the_translation_binding_present()
    {
        TrustedSpeechFixtures.Fixtures speech = TrustedSpeechFixtures.Require();
        string models = TranslationModelFixtures.Require();
        using var translator = new Translator(() => models);
        // Translation loads first and binds the managed ONNX Runtime package to the shipped onnxruntime.dll.
        TranslationResult translated = await translator.TranslateAsync("Das ist ein Test.", TranslationDirection.GermanToEnglish);
        Assert.IsInstanceOfType<TranslationResult.Translated>(translated);

        float[] samples = Pcm16Wave.ReadMono16Khz(speech.GermanClip);
        using SpeechRuntime runtime = SpeechRuntime.LoadForAcceptance(speech.ModelDirectory, SpeechBackend.Cpu);
        string text = runtime.Recognize(samples);

        Assert.IsFalse(string.IsNullOrWhiteSpace(text));
        TranslationResult again = await translator.TranslateAsync("Das ist ein zweiter Test.", TranslationDirection.GermanToEnglish);
        Assert.IsInstanceOfType<TranslationResult.Translated>(again, "translation still works after speech loaded");
    }
}
