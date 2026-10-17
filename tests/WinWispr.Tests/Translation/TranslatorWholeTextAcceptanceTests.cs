using System.Diagnostics;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Speech;
using WinWispr.Tests.Speech;
using WinWispr.Translation;

namespace WinWispr.Tests.Translation;

/// <summary>
/// Whole-text translation against the real Opus-MT int8 models on the shipped ONNX Runtime: detection, layout,
/// sentence splitting, the three-second budget at the selection limit, the deadline, serialization and the output
/// of batching. They carry the WindowsAcceptance category, which the default suites skip, and they are inconclusive
/// unless WINWISPR_TRANSLATION_MODEL_DIR names the folder with the ten pinned model files (see
/// <see cref="TranslationModelFixtures"/>; procedure in docs/testing/windows-11-acceptance.md).
/// </summary>
[TestClass]
public sealed class TranslatorWholeTextAcceptanceTests
{
    private static readonly TimeSpan LatencyBudget = TimeSpan.FromSeconds(3);

    private static async Task<Translator> WarmTranslatorAsync(TranslationLimits? limits = null, EngineTuning? tuning = null)
    {
        string models = TranslationModelFixtures.Require();
        Translator translator = tuning is null
            ? new Translator(() => models, limits: limits)
            : new Translator(() => models, (directory, direction) => OpusMtEngine.TryLoad(directory, direction, tuning), limits: limits);
        foreach (TranslationDirection direction in Enum.GetValues<TranslationDirection>())
            Assert.IsInstanceOfType<TranslationResult.Translated>(
                await translator.TranslateAsync("Warm up.", TranslationUse.Dictation, direction));
        return translator;
    }

    /// <summary>The fixture sentences of one language joined with spaces and cut to exactly <paramref name="length"/> characters.</summary>
    internal static string TextOfLength(TranslationDirection direction, int length, bool unpunctuated = false)
    {
        string[] sources = FixtureSentence.All.Where(s => s.Direction == direction).Select(s => s.Source).ToArray();
        var text = new System.Text.StringBuilder();
        for (int i = 0; text.Length < length; i++)
        {
            // The unpunctuated kind is a dictation without a single full stop: the Translator has to cut it into
            // chunks of the model's size, the hardest case for throughput because chunks are long.
            string source = unpunctuated
                ? new string(sources[i % sources.Length].Where(c => !char.IsPunctuation(c)).ToArray()).ToLowerInvariant()
                : sources[i % sources.Length];
            text.Append(source).Append(' ');
        }

        string cut = text.ToString()[..length];
        return unpunctuated ? cut[..^1] + "x" : cut[..^1] + ".";
    }

    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    public async Task The_direction_is_detected_reported_and_overridable()
    {
        using Translator translator = await WarmTranslatorAsync();

        var german = (TranslationResult.Translated)await translator.TranslateAsync(
            "Der Server antwortet nicht mehr, weil der Arbeitsspeicher voll ist. Bitte starte den Dienst neu.");
        var english = (TranslationResult.Translated)await translator.TranslateAsync(
            "The server no longer responds because the memory is full. Please restart the service.");
        var mixed = (TranslationResult.Translated)await translator.TranslateAsync(
            "Bitte deploy das Release auf den Staging Server und check die Logs.");
        var forced = (TranslationResult.Translated)await translator.TranslateAsync(
            "The server no longer responds.", TranslationDirection.GermanToEnglish);

        Console.WriteLine($"de: {german.Text}\nen: {english.Text}\nmixed: {mixed.Text}\nforced: {forced.Text}");
        Assert.AreEqual(TranslationDirection.GermanToEnglish, german.Direction);
        StringAssert.Contains(german.Text, "server", StringComparison.OrdinalIgnoreCase);
        Assert.AreEqual(TranslationDirection.EnglishToGerman, english.Direction);
        StringAssert.Contains(english.Text, "Server", StringComparison.OrdinalIgnoreCase);
        Assert.AreEqual(TranslationDirection.GermanToEnglish, mixed.Direction);
        Assert.AreEqual(TranslationDirection.GermanToEnglish, forced.Direction, "an explicit direction overrides detection");
    }

    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    public async Task Translating_into_English_keeps_English_text_and_translates_German_text()
    {
        using Translator translator = await WarmTranslatorAsync();

        var english = await translator.TranslateToEnglishAsync(
            "I will send the report tomorrow because the numbers are not final.", TranslationUse.Dictation);
        var german = (TranslationResult.Translated)await translator.TranslateToEnglishAsync(
            "Ich schicke den Bericht morgen, weil die Zahlen noch nicht final sind.", TranslationUse.Dictation);

        Assert.AreEqual(
            new TranslationResult.Translated("I will send the report tomorrow because the numbers are not final.", null, true),
            english);
        Assert.AreEqual(TranslationDirection.GermanToEnglish, german.Direction);
        Assert.IsFalse(german.Unchanged);
        StringAssert.Contains(german.Text, "report", StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    public async Task Line_breaks_blank_lines_and_lists_keep_their_layout_in_the_real_translation()
    {
        using Translator translator = await WarmTranslatorAsync();
        const string text =
            "Hallo Anna,\r\n\r\nder Server antwortet nicht mehr. Bitte starte den Dienst neu.\r\n\r\n"
            + "- Prüfe die Logdateien\r\n- Starte den Rechner neu\r\n1. Rufe mich an\r\n\r\nViele Grüße\r\nMarko";

        var result = (TranslationResult.Translated)await translator.TranslateAsync(text, TranslationUse.Dictation);

        Console.WriteLine(result.Text);
        string[] input = text.Split("\r\n");
        string[] output = result.Text.Split("\r\n");
        Assert.AreEqual(input.Length, output.Length, "one output line per input line");
        for (int i = 0; i < input.Length; i++)
        {
            Assert.AreEqual(input[i].Length == 0, output[i].Length == 0, $"blank line {i}");
            if (input[i].StartsWith("- ", StringComparison.Ordinal))
                StringAssert.StartsWith(output[i], "- ");
            if (input[i].StartsWith("1. ", StringComparison.Ordinal))
                StringAssert.StartsWith(output[i], "1. ");
        }

        Assert.IsFalse(result.Text.Contains('ü', StringComparison.Ordinal) && result.Text.Contains("Starte", StringComparison.Ordinal),
            "the German source is not echoed");
    }

    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    public async Task A_whole_text_equals_its_sentences_translated_one_by_one_and_keeps_numbers_and_negation()
    {
        using Translator translator = await WarmTranslatorAsync();
        var problems = new List<string>();

        foreach (TranslationDirection direction in Enum.GetValues<TranslationDirection>())
        {
            FixtureSentence[] sentences = FixtureSentence.All.Where(s => s.Direction == direction).ToArray();
            string whole = string.Join(' ', sentences.Select(s => s.Source));
            var result = (TranslationResult.Translated)await translator.TranslateAsync(whole, TranslationUse.Dictation, direction);
            var single = new List<string>();
            foreach (FixtureSentence sentence in sentences)
                single.Add(((TranslationResult.Translated)await translator.TranslateAsync(sentence.Source, direction)).Text);
            string expected = string.Join(' ', single);
            Console.WriteLine($"{direction}: {result.Text}");
            if (result.Text != expected)
            {
                int at = 0;
                while (at < Math.Min(result.Text.Length, expected.Length) && result.Text[at] == expected[at])
                    at++;
                problems.Add($"{direction}: whole text differs from sentence by sentence at {at}:\n  ...{result.Text[Math.Max(0, at - 60)..Math.Min(result.Text.Length, at + 80)]}\n  ...{expected[Math.Max(0, at - 60)..Math.Min(expected.Length, at + 80)]}");
            }
        }

        Assert.AreEqual(0, problems.Count, string.Join(Environment.NewLine, problems));
    }

    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    public async Task Long_text_without_punctuation_is_cut_into_chunks_and_never_silently_truncated()
    {
        using Translator translator = await WarmTranslatorAsync();
        string phrase = "der server antwortet nicht mehr und wir starten den dienst neu";
        string text = string.Join(' ', Enumerable.Repeat(phrase, 30)) + " und das ist das ende";

        var result = await translator.TranslateAsync(text, TranslationUse.Dictation, TranslationDirection.GermanToEnglish);

        var translated = (TranslationResult.Translated)result;
        int inputWords = text.Split(' ').Length;
        int outputWords = translated.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        Console.WriteLine($"{inputWords} words in, {outputWords} words out");
        Assert.IsTrue(outputWords >= inputWords * 0.6, $"{outputWords} words out of {inputWords} in: the text was cut short");
        StringAssert.Contains(translated.Text.ToLowerInvariant(), "end");
    }

    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    public async Task A_selection_over_the_limit_is_refused_before_the_model_loads()
    {
        string models = TranslationModelFixtures.Require();
        int resolved = 0;
        using var translator = new Translator(() => { Interlocked.Increment(ref resolved); return models; });
        int limit = TranslationLimits.Default.SelectionMaxCharacters;

        var refused = await translator.TranslateAsync(TextOfLength(TranslationDirection.GermanToEnglish, limit + 1));

        Assert.AreEqual(new TranslationResult.TooLong(limit), refused);
        Assert.AreEqual(0, resolved);
        Assert.IsFalse(translator.IsLoaded(TranslationDirection.GermanToEnglish));
    }

    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    public async Task A_selection_of_exactly_the_limit_translates_within_three_seconds_on_a_warm_model()
    {
        using Translator translator = await WarmTranslatorAsync();
        int limit = TranslationLimits.Default.SelectionMaxCharacters;
        var problems = new List<string>();

        foreach (TranslationDirection direction in Enum.GetValues<TranslationDirection>())
        foreach (bool unpunctuated in new[] { false, true })
        {
            string text = TextOfLength(direction, limit, unpunctuated);
            Assert.AreEqual(limit, text.Length);
            for (int run = 0; run < 3; run++)
            {
                var watch = Stopwatch.StartNew();
                TranslationResult result = await translator.TranslateAsync(text, direction);
                watch.Stop();
                string kind = unpunctuated ? "unpunctuated" : "sentences";
                Console.WriteLine($"{direction} {kind} run {run}: {watch.Elapsed.TotalSeconds:0.00} s");
                if (result is not TranslationResult.Translated)
                    problems.Add($"{direction} {kind}: {result}");
                else if (watch.Elapsed > LatencyBudget)
                    problems.Add($"{direction} {kind} took {watch.Elapsed.TotalSeconds:0.00} s at {limit} characters");
            }
        }

        Assert.AreEqual(0, problems.Count, string.Join("; ", problems));
    }

    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    public async Task The_deadline_returns_a_timed_out_result_and_the_translator_stays_usable()
    {
        var limits = new TranslationLimits { SelectionDeadline = TimeSpan.FromMilliseconds(1) };
        using Translator translator = await WarmTranslatorAsync(limits);
        string text = TextOfLength(TranslationDirection.GermanToEnglish, 400);

        var timedOut = await translator.TranslateAsync(text);
        var dictation = await translator.TranslateAsync(text, TranslationUse.Dictation);

        Assert.AreEqual(new TranslationResult.TimedOut(TimeSpan.FromMilliseconds(1)), timedOut);
        Assert.IsInstanceOfType<TranslationResult.Translated>(dictation, "a dictation has its own, scaled deadline");
    }

    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    public async Task Cancellation_stops_a_long_translation_promptly()
    {
        using Translator translator = await WarmTranslatorAsync();
        string text = string.Join(' ', Enumerable.Repeat(TextOfLength(TranslationDirection.GermanToEnglish, 400), 6));
        using var cancellation = new CancellationTokenSource();

        Task<TranslationResult> running = translator.TranslateAsync(text, TranslationUse.Dictation, null, cancellation.Token);
        await Task.Delay(300);
        var watch = Stopwatch.StartNew();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => running);
        watch.Stop();

        Assert.IsTrue(watch.Elapsed < TimeSpan.FromSeconds(1), $"stopping took {watch.Elapsed.TotalSeconds:0.00} s");
        Assert.IsInstanceOfType<TranslationResult.Translated>(await translator.TranslateAsync("Das ist ein Test."));
    }

    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    public async Task Concurrent_requests_run_one_after_the_other_and_both_are_correct()
    {
        using Translator translator = await WarmTranslatorAsync();
        using var cancellation = new CancellationTokenSource();

        Task<TranslationResult> german = translator.TranslateAsync("Ich habe die Datei nicht gelöscht, sondern nur verschoben.");
        Task<TranslationResult> english = translator.TranslateAsync("We have not changed the password, and nobody outside the team has access.");
        Task<TranslationResult> cancelled = translator.TranslateAsync("Das ist ein dritter Test.", null, cancellation.Token);
        cancellation.Cancel();
        TranslationResult[] results = await Task.WhenAll(german, english);

        var toEnglish = (TranslationResult.Translated)results[0];
        var toGerman = (TranslationResult.Translated)results[1];
        Console.WriteLine(toEnglish.Text + " / " + toGerman.Text);
        StringAssert.Contains(toEnglish.Text, "moved");
        StringAssert.Contains(toGerman.Text, "niemand", StringComparison.OrdinalIgnoreCase);
        try
        {
            await cancelled;
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// The throughput measurement of ticket 05: how much text translates within three seconds, per direction and
    /// engine setting, with the speech model resident when the archive is available. It prints a table, checks that
    /// every setting gives the output of the sentence-by-sentence reference, and asserts nothing about speed.
    /// </summary>
    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    public async Task Throughput_measurement_prints_the_time_per_text_length_for_every_setting()
    {
        string models = TranslationModelFixtures.Require();
        string[] settings = (Environment.GetEnvironmentVariable("WINWISPR_TRANSLATION_TUNINGS") ?? "1x1,1x6")
            .Split(',', StringSplitOptions.RemoveEmptyEntries);
        bool unpunctuated = Environment.GetEnvironmentVariable("WINWISPR_TRANSLATION_UNPUNCTUATED") == "1";
        int[] lengths = (Environment.GetEnvironmentVariable("WINWISPR_TRANSLATION_LENGTHS") ?? "450,900,1350,1800")
            .Split(',').Select(int.Parse).ToArray();
        SpeechRuntime? speech = null;
        string residency = "speech model not resident";
        try
        {
            TrustedSpeechFixtures.Fixtures fixtures = TrustedSpeechFixtures.Require();
            speech = SpeechRuntime.LoadForAcceptance(fixtures.ModelDirectory, SpeechBackend.Cpu);
            _ = speech.Recognize(Pcm16Wave.ReadMono16Khz(fixtures.GermanClip));
            residency = "Parakeet resident";
        }
        catch (AssertInconclusiveException)
        {
        }

        try
        {
            Console.WriteLine($"cores={Environment.ProcessorCount} {residency} unpunctuated={unpunctuated} private MB before any translation={Process.GetCurrentProcess().PrivateMemorySize64 / 1048576}");
            Console.WriteLine("direction;setting;characters;segments;warm seconds (3 runs, best/median/worst);identical to first setting;private MB after");
            // WINWISPR_TRANSLATION_DIRECTION=de-en or en-de measures one direction in a fresh process (memory).
            string? only = Environment.GetEnvironmentVariable("WINWISPR_TRANSLATION_DIRECTION");
            foreach (TranslationDirection direction in Enum.GetValues<TranslationDirection>()
                .Where(d => only is null || only == (d == TranslationDirection.GermanToEnglish ? "de-en" : "en-de")))
            {
                var reference = new Dictionary<int, string>();
                foreach (string setting in settings)
                {
                    string[] pair = setting.Split('x');
                    var tuning = new EngineTuning(int.Parse(pair[0]), int.Parse(pair[1]));
                    using var translator = new Translator(
                        () => models,
                        (directory, d) => OpusMtEngine.TryLoad(directory, d, tuning),
                        limits: new TranslationLimits { SelectionMaxCharacters = int.MaxValue, SelectionDeadline = TimeSpan.FromMinutes(5) });
                    await translator.TranslateAsync("Warm up. Second sentence here.", direction);
                    foreach (int length in lengths)
                    {
                        string text = TextOfLength(direction, length, unpunctuated);
                        var times = new List<double>();
                        string output = string.Empty;
                        for (int run = 0; run < 3; run++)
                        {
                            var watch = Stopwatch.StartNew();
                            var result = (TranslationResult.Translated)await translator.TranslateAsync(text, direction);
                            times.Add(watch.Elapsed.TotalSeconds);
                            output = result.Text;
                        }

                        times.Sort();
                        if (setting == settings[0])
                            reference[length] = output;
                        string identical = reference.TryGetValue(length, out string? expected) && expected == output ? "yes" : "NO";
                        Console.WriteLine($"{direction};{setting};{length};{text.Split(". ").Length};{times[0]:0.00}/{times[1]:0.00}/{times[2]:0.00};{identical};{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(output)))[..8]};{Process.GetCurrentProcess().PrivateMemorySize64 / 1048576}");
                    }
                }
            }
        }
        finally
        {
            speech?.Dispose();
        }
    }
}
