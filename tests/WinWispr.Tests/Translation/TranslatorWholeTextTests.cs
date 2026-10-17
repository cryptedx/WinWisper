using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Translation;

namespace WinWispr.Tests.Translation;

/// <summary>
/// The Translator's whole-text behavior through its interface, with a fake engine behind it: the direction (given
/// or detected), the selection length limit, sentence splitting and layout, deadlines, cancellation and
/// serialization. No model and no native runtime are needed. The fake engine puts "[de-en] " or "[en-de] " in
/// front of every segment, so the output shows exactly which sentences reached the model and how the layout was
/// put back together.
/// </summary>
[TestClass]
public sealed class TranslatorWholeTextTests
{
    private const TranslationDirection DeEn = TranslationDirection.GermanToEnglish;
    private const TranslationDirection EnDe = TranslationDirection.EnglishToGerman;

    private static string[] LastSegments(FakeTranslationEngines engines) => engines.Batches[^1].Segments;

    // ---- direction -------------------------------------------------------------------------------------------

    [TestMethod]
    public async Task German_text_without_a_direction_is_translated_into_English_and_the_result_says_so()
    {
        var engines = new FakeTranslationEngines();
        using var translator = new Translator(() => "models", engines.Create);

        var result = await translator.TranslateAsync("Der Server antwortet nicht mehr, weil der Speicher voll ist.");

        Assert.AreEqual(
            new TranslationResult.Translated("[de-en] Der Server antwortet nicht mehr, weil der Speicher voll ist.", DeEn),
            result);
    }

    [TestMethod]
    public async Task English_text_without_a_direction_is_translated_into_German_and_the_result_says_so()
    {
        var engines = new FakeTranslationEngines();
        using var translator = new Translator(() => "models", engines.Create);

        var result = await translator.TranslateAsync("The server no longer responds because the memory is full.");

        Assert.AreEqual(
            new TranslationResult.Translated("[en-de] The server no longer responds because the memory is full.", EnDe),
            result);
    }

    [TestMethod]
    public async Task An_explicit_direction_overrides_detection()
    {
        var engines = new FakeTranslationEngines();
        using var translator = new Translator(() => "models", engines.Create);

        var result = await translator.TranslateAsync("Der Server antwortet nicht mehr.", EnDe);

        Assert.AreEqual(new TranslationResult.Translated("[en-de] Der Server antwortet nicht mehr.", EnDe), result);
        CollectionAssert.AreEqual(new[] { EnDe }, engines.LoadedDirections);
    }

    [TestMethod]
    public async Task Mixed_text_follows_the_language_with_more_evidence()
    {
        var engines = new FakeTranslationEngines();
        using var translator = new Translator(() => "models", engines.Create);

        var german = await translator.TranslateAsync("Bitte deploy das Release auf den Staging Server und check die Logs.");
        var english = await translator.TranslateAsync("Please send the Protokoll to the Vermieter before Friday.");

        Assert.AreEqual(DeEn, ((TranslationResult.Translated)german).Direction);
        Assert.AreEqual(EnDe, ((TranslationResult.Translated)english).Direction);
    }

    [TestMethod]
    public async Task Ambiguous_text_is_translated_from_German_and_unchanged_output_is_reported_as_unchanged()
    {
        var engines = new FakeTranslationEngines { Echo = true };
        using var translator = new Translator(() => "models", engines.Create);

        var result = await translator.TranslateAsync("Project Atlas");

        Assert.AreEqual(new TranslationResult.Translated("Project Atlas", DeEn, Unchanged: true), result);
    }

    [TestMethod]
    public async Task Ambiguous_text_that_the_model_changes_is_not_reported_as_unchanged()
    {
        var engines = new FakeTranslationEngines();
        using var translator = new Translator(() => "models", engines.Create);

        var result = await translator.TranslateAsync("Project Atlas");

        Assert.AreEqual(new TranslationResult.Translated("[de-en] Project Atlas", DeEn), result);
    }

    [TestMethod]
    [DataRow("1.249,50")]
    [DataRow("12345")]
    [DataRow("12.5 / 3,14159")]
    [DataRow("...!?")]
    public async Task Text_without_letters_comes_back_unchanged_without_loading_the_model(string text)
    {
        var engines = new FakeTranslationEngines();
        using var translator = new Translator(() => "models", engines.Create);

        var result = await translator.TranslateAsync(text);

        Assert.AreEqual(new TranslationResult.Translated(text, null, Unchanged: true), result);
        Assert.AreEqual(0, engines.Loads);
    }

    // ---- translate into English (dictation translation) --------------------------------------------------------

    [TestMethod]
    public async Task Translating_into_English_returns_English_text_unchanged_without_loading_anything()
    {
        var engines = new FakeTranslationEngines();
        using var translator = new Translator(() => "models", engines.Create);
        const string transcript = "I will send the report tomorrow because the numbers are not final.";

        var result = await translator.TranslateToEnglishAsync(transcript, TranslationUse.Dictation);

        Assert.AreEqual(new TranslationResult.Translated(transcript, null, Unchanged: true), result);
        Assert.AreEqual(0, engines.Loads);
    }

    [TestMethod]
    public async Task Translating_into_English_translates_German_text_from_German()
    {
        var engines = new FakeTranslationEngines();
        using var translator = new Translator(() => "models", engines.Create);

        var result = await translator.TranslateToEnglishAsync("Ich schicke den Bericht morgen.", TranslationUse.Dictation);

        Assert.AreEqual(new TranslationResult.Translated("[de-en] Ich schicke den Bericht morgen.", DeEn), result);
    }

    [TestMethod]
    public async Task Translating_an_ambiguous_transcript_into_English_translates_it_as_German()
    {
        var engines = new FakeTranslationEngines();
        using var translator = new Translator(() => "models", engines.Create);

        var result = await translator.TranslateToEnglishAsync("Feierabend", TranslationUse.Dictation);

        Assert.AreEqual(new TranslationResult.Translated("[de-en] Feierabend", DeEn), result);
    }

    [TestMethod]
    public async Task Translating_into_English_ignores_the_length_of_a_dictation()
    {
        var engines = new FakeTranslationEngines();
        using var translator = new Translator(() => "models", engines.Create);
        string longTranscript = string.Join(' ', Enumerable.Repeat("Der Bericht ist fertig und liegt im Ordner.", 200));

        var result = await translator.TranslateToEnglishAsync(longTranscript, TranslationUse.Dictation);

        Assert.IsInstanceOfType<TranslationResult.Translated>(result);
        Assert.AreEqual(200, LastSegments(engines).Length, "every sentence reaches the model, nothing is dropped");
    }

    // ---- detection without translating --------------------------------------------------------------------------

    [TestMethod]
    public void The_detected_language_can_be_reported_without_loading_anything()
    {
        var engines = new FakeTranslationEngines();
        using var translator = new Translator(() => "models", engines.Create);

        Assert.AreEqual(DetectedLanguage.German, translator.DetectLanguage("Das ist nicht gut, aber ich kann es ändern."));
        Assert.AreEqual(DetectedLanguage.English, translator.DetectLanguage("That is not good, but I can change it."));
        Assert.AreEqual(DetectedLanguage.Ambiguous, translator.DetectLanguage("Project Atlas"));
        Assert.AreEqual(DetectedLanguage.NoLetters, translator.DetectLanguage("12345"));
        Assert.AreEqual(DetectedLanguage.NoLetters, translator.DetectLanguage("   "));
        Assert.AreEqual(0, engines.Loads);
    }

    [TestMethod]
    public void A_detected_language_names_the_direction_to_preload()
    {
        Assert.AreEqual(DeEn, DetectedLanguage.German.ToDirection());
        Assert.AreEqual(EnDe, DetectedLanguage.English.ToDirection());
        Assert.AreEqual(DeEn, DetectedLanguage.Ambiguous.ToDirection(), "ambiguous text is translated from German");
        Assert.IsNull(DetectedLanguage.NoLetters.ToDirection(), "nothing to load for text without letters");
    }

    [TestMethod]
    public void Detection_never_throws_for_odd_input()
    {
        using var translator = new Translator(() => "models", new FakeTranslationEngines().Create);
        string[] odd = ["", " ", "\0", "😀", "\uD800", new string('ä', 10_000), "a'b'c", "„unclosed", "\"\"", "ß"];

        foreach (string text in odd)
            _ = translator.DetectLanguage(text);
    }

    // ---- the selection length limit ----------------------------------------------------------------------------

    [TestMethod]
    public async Task A_selection_of_exactly_the_limit_is_accepted_and_one_more_character_is_refused()
    {
        var engines = new FakeTranslationEngines();
        var limits = new TranslationLimits { SelectionMaxCharacters = 30 };
        using var translator = new Translator(() => "models", engines.Create, limits: limits);
        string atLimit = "Das ist der Bericht von heute".PadRight(30, '.');

        var accepted = await translator.TranslateAsync(atLimit, TranslationUse.Selection);
        var refused = await translator.TranslateAsync(atLimit + ".", TranslationUse.Selection);

        Assert.AreEqual(30, atLimit.Length);
        Assert.IsInstanceOfType<TranslationResult.Translated>(accepted);
        Assert.AreEqual(new TranslationResult.TooLong(30), refused);
    }

    [TestMethod]
    public async Task A_refused_selection_does_no_other_work_not_even_a_model_load()
    {
        var engines = new FakeTranslationEngines();
        int resolved = 0;
        var limits = new TranslationLimits { SelectionMaxCharacters = 10 };
        using var translator = new Translator(() => { resolved++; return "models"; }, engines.Create, limits: limits);

        var result = await translator.TranslateAsync("Das ist viel zu lang für den Test.", TranslationUse.Selection);

        Assert.AreEqual(new TranslationResult.TooLong(10), result);
        Assert.AreEqual(0, resolved);
        Assert.AreEqual(0, engines.Loads);
    }

    [TestMethod]
    public async Task A_refused_selection_is_too_long_even_when_the_model_is_not_installed()
    {
        var limits = new TranslationLimits { SelectionMaxCharacters = 10 };
        using var translator = new Translator(() => null, new FakeTranslationEngines().Create, limits: limits);

        var result = await translator.TranslateAsync("Das ist viel zu lang für den Test.", TranslationUse.Selection);

        Assert.AreEqual(new TranslationResult.TooLong(10), result);
    }

    [TestMethod]
    public async Task The_limit_applies_to_an_explicit_direction_too_and_the_default_use_is_a_selection()
    {
        var limits = new TranslationLimits { SelectionMaxCharacters = 10 };
        using var translator = new Translator(() => "models", new FakeTranslationEngines().Create, limits: limits);

        var withDirection = await translator.TranslateAsync("Das ist viel zu lang für den Test.", DeEn);
        var withoutDirection = await translator.TranslateAsync("Das ist viel zu lang für den Test.");

        Assert.AreEqual(new TranslationResult.TooLong(10), withDirection);
        Assert.AreEqual(new TranslationResult.TooLong(10), withoutDirection);
    }

    [TestMethod]
    public async Task Dictation_has_no_length_limit()
    {
        var engines = new FakeTranslationEngines();
        var limits = new TranslationLimits { SelectionMaxCharacters = 10 };
        using var translator = new Translator(() => "models", engines.Create, limits: limits);

        var result = await translator.TranslateAsync(
            "Das ist viel zu lang für den Test, aber ein Diktat wird nie abgeschnitten.", TranslationUse.Dictation, DeEn);

        Assert.IsInstanceOfType<TranslationResult.Translated>(result);
    }

    [TestMethod]
    public void The_default_selection_limit_is_a_positive_number_of_characters()
    {
        Assert.IsTrue(TranslationLimits.Default.SelectionMaxCharacters > 0);
        Assert.AreEqual(TimeSpan.FromSeconds(5), TranslationLimits.Default.SelectionDeadline);
    }

    [TestMethod]
    public void The_dictation_deadline_is_three_seconds_plus_sixty_milliseconds_per_token_capped_at_ninety_seconds()
    {
        Func<int, TimeSpan> deadline = TranslationLimits.Default.DictationDeadline;

        Assert.AreEqual(TimeSpan.FromSeconds(3), deadline(0));
        Assert.AreEqual(TimeSpan.FromSeconds(9), deadline(100));
        Assert.AreEqual(TimeSpan.FromSeconds(63), deadline(1000));
        Assert.AreEqual(TimeSpan.FromSeconds(90), deadline(5000));
        Assert.AreEqual(TimeSpan.FromSeconds(90), deadline(int.MaxValue));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Empty_text_translates_to_empty_text_without_loading_the_model_and_without_a_limit_check(bool dictation)
    {
        TranslationUse use = dictation ? TranslationUse.Dictation : TranslationUse.Selection;
        var engines = new FakeTranslationEngines();
        var limits = new TranslationLimits { SelectionMaxCharacters = 5 };
        using var translator = new Translator(() => "models", engines.Create, limits: limits);

        var withoutDirection = await translator.TranslateAsync(new string(' ', 50), use);
        var withDirection = await translator.TranslateAsync("\r\n\r\n", use, EnDe);
        var toEnglish = await translator.TranslateToEnglishAsync("\t", use);

        Assert.AreEqual(new TranslationResult.Translated(string.Empty), withoutDirection);
        Assert.AreEqual(new TranslationResult.Translated(string.Empty, EnDe), withDirection);
        Assert.AreEqual(new TranslationResult.Translated(string.Empty), toEnglish);
        Assert.AreEqual(0, engines.Loads);
    }

    // ---- sentence splitting and layout ----------------------------------------------------------------------------

    [TestMethod]
    public async Task A_paragraph_is_translated_sentence_by_sentence_in_one_batch()
    {
        var engines = new FakeTranslationEngines();
        using var translator = new Translator(() => "models", engines.Create);

        var result = await translator.TranslateAsync(
            "Der Server antwortet nicht. Bitte starte den Dienst neu! Hat das geholfen? Prüfe die Logdateien.", DeEn);

        Assert.AreEqual(1, engines.Batches.Count, "all sentences go through the model in one call");
        CollectionAssert.AreEqual(
            new[]
            {
                "Der Server antwortet nicht.", "Bitte starte den Dienst neu!", "Hat das geholfen?", "Prüfe die Logdateien."
            },
            LastSegments(engines));
        Assert.AreEqual(
            "[de-en] Der Server antwortet nicht. [de-en] Bitte starte den Dienst neu! [de-en] Hat das geholfen? [de-en] Prüfe die Logdateien.",
            ((TranslationResult.Translated)result).Text);
    }

    [TestMethod]
    public async Task Line_breaks_blank_lines_and_indentation_keep_their_layout()
    {
        var engines = new FakeTranslationEngines();
        using var translator = new Translator(() => "models", engines.Create);
        const string text = "Guten Tag,\r\n\r\n  ich melde mich wegen der Rechnung.\nBitte antworte bald.\n\n\nViele Grüße\n";

        var result = (TranslationResult.Translated)await translator.TranslateAsync(text, DeEn);

        Assert.AreEqual(
            "[de-en] Guten Tag,\r\n\r\n  [de-en] ich melde mich wegen der Rechnung.\n[de-en] Bitte antworte bald.\n\n\n[de-en] Viele Grüße\n",
            result.Text);
        CollectionAssert.AreEqual(
            new[] { "Guten Tag,", "ich melde mich wegen der Rechnung.", "Bitte antworte bald.", "Viele Grüße" },
            LastSegments(engines));
    }

    [TestMethod]
    public async Task One_item_per_line_lists_keep_their_markers_and_a_line_per_item()
    {
        var engines = new FakeTranslationEngines();
        using var translator = new Translator(() => "models", engines.Create);
        const string text = "Einkaufsliste:\n- Milch\n- Brot und Butter\n* Eier\n1. Käse\n2) Wurst\n  - Obst\n";

        var result = (TranslationResult.Translated)await translator.TranslateAsync(text, DeEn);

        CollectionAssert.AreEqual(
            new[] { "Einkaufsliste:", "Milch", "Brot und Butter", "Eier", "Käse", "Wurst", "Obst" },
            LastSegments(engines));
        Assert.AreEqual(
            "[de-en] Einkaufsliste:\n- [de-en] Milch\n- [de-en] Brot und Butter\n* [de-en] Eier\n1. [de-en] Käse\n2) [de-en] Wurst\n  - [de-en] Obst\n",
            result.Text);
    }

    [TestMethod]
    public async Task A_German_date_at_the_start_of_a_line_is_not_taken_for_a_list_marker()
    {
        var engines = new FakeTranslationEngines();
        using var translator = new Translator(() => "models", engines.Create);

        await translator.TranslateAsync("3. Oktober ist ein Feiertag.\n1. Punkt eins", DeEn);

        CollectionAssert.AreEqual(new[] { "3. Oktober ist ein Feiertag.", "Punkt eins" }, LastSegments(engines));
    }

    [TestMethod]
    public async Task Abbreviations_decimal_numbers_and_ordinals_do_not_split_a_sentence()
    {
        var engines = new FakeTranslationEngines();
        using var translator = new Translator(() => "models", engines.Create);

        await translator.TranslateAsync(
            "Das kostet ca. 5 Euro, z. B. beim Dr. Müller. Danach gehen wir z.B. zu Prof. Schmidt. "
            + "Am 3. Oktober zahlen wir 3.5 Euro und 1.249,50 Euro. Danke.",
            DeEn);

        CollectionAssert.AreEqual(
            new[]
            {
                "Das kostet ca. 5 Euro, z. B. beim Dr. Müller.",
                "Danach gehen wir z.B. zu Prof. Schmidt.",
                "Am 3. Oktober zahlen wir 3.5 Euro und 1.249,50 Euro.",
                "Danke."
            },
            LastSegments(engines));
    }

    [TestMethod]
    public async Task English_abbreviations_and_initials_do_not_split_a_sentence()
    {
        var engines = new FakeTranslationEngines();
        using var translator = new Translator(() => "models", engines.Create);

        await translator.TranslateAsync(
            "Mr. Smith paid $3.50 on Jan. 5, e.g. in cash. Then J. Miller left. He said \"Go home.\" Nobody moved...  Why?",
            EnDe);

        CollectionAssert.AreEqual(
            new[]
            {
                "Mr. Smith paid $3.50 on Jan. 5, e.g. in cash.",
                "Then J. Miller left.",
                "He said \"Go home.\"",
                "Nobody moved...",
                "Why?"
            },
            LastSegments(engines));
    }

    [TestMethod]
    public async Task A_time_with_a_m_or_p_m_ends_a_sentence_before_a_capital_letter_but_not_before_a_lowercase_one()
    {
        var engines = new FakeTranslationEngines();
        using var translator = new Translator(() => "models", engines.Create);

        await translator.TranslateAsync(
            "We arrive at 6:45 p.m. The rent is due. The meeting ends at 11:00 a.m. in room 4.12. Done.", EnDe);

        CollectionAssert.AreEqual(
            new[]
            {
                "We arrive at 6:45 p.m.", "The rent is due.", "The meeting ends at 11:00 a.m. in room 4.12.", "Done."
            },
            LastSegments(engines));
    }

    [TestMethod]
    public async Task Lines_without_letters_pass_through_and_never_reach_the_model()
    {
        var engines = new FakeTranslationEngines();
        using var translator = new Translator(() => "models", engines.Create);

        var result = (TranslationResult.Translated)await translator.TranslateAsync("Preis:\n----\n42,50 €\nEnde.", DeEn);

        CollectionAssert.AreEqual(new[] { "Preis:", "Ende." }, LastSegments(engines));
        Assert.AreEqual("[de-en] Preis:\n----\n42,50 €\n[de-en] Ende.", result.Text);
    }

    [TestMethod]
    public async Task Long_text_without_punctuation_is_cut_into_chunks_the_model_handles_and_nothing_is_dropped()
    {
        var engines = new FakeTranslationEngines { MaxSegmentTokens = 5 };
        using var translator = new Translator(() => "models", engines.Create);
        string[] words = Enumerable.Range(1, 23).Select(i => "wort" + i).ToArray();
        string text = string.Join(' ', words);

        var result = (TranslationResult.Translated)await translator.TranslateAsync(text, TranslationUse.Dictation, DeEn);

        string[] chunks = LastSegments(engines);
        Assert.IsTrue(chunks.All(chunk => chunk.Split(' ').Length <= 5), "every chunk fits the model");
        Assert.AreEqual(5, chunks.Length);
        Assert.AreEqual(text, string.Join(' ', chunks), "the chunks together are the whole text");
        Assert.AreEqual(text, result.Text.Replace("[de-en] ", string.Empty), "the output keeps every chunk, in order");
    }

    [TestMethod]
    public async Task A_single_word_longer_than_the_model_limit_is_cut_by_characters_and_nothing_is_dropped()
    {
        var engines = new FakeTranslationEngines
        {
            MaxSegmentTokens = 4,
            CountTokens = text => (text.Replace(" ", string.Empty).Length + 3) / 4
        };
        using var translator = new Translator(() => "models", engines.Create);
        string word = string.Concat(Enumerable.Range(0, 10).Select(i => "abcdefghij"));

        await translator.TranslateAsync(word, TranslationUse.Dictation, DeEn);

        string[] chunks = LastSegments(engines);
        Assert.IsTrue(chunks.Length > 1);
        Assert.IsTrue(chunks.All(chunk => engines.CountTokens(chunk) <= 4));
        Assert.AreEqual(word, string.Concat(chunks));
    }

    [TestMethod]
    public async Task Sentences_longer_than_the_model_limit_are_cut_at_word_boundaries_inside_their_line()
    {
        var engines = new FakeTranslationEngines { MaxSegmentTokens = 4 };
        using var translator = new Translator(() => "models", engines.Create);

        var result = (TranslationResult.Translated)await translator.TranslateAsync(
            "eins zwei drei vier fünf sechs.\nkurz.", TranslationUse.Dictation, DeEn);

        CollectionAssert.AreEqual(new[] { "eins zwei drei vier", "fünf sechs.", "kurz." }, LastSegments(engines));
        Assert.AreEqual("[de-en] eins zwei drei vier [de-en] fünf sechs.\n[de-en] kurz.", result.Text);
    }

    // ---- failures, deadlines, cancellation and serialization -----------------------------------------------------------

    [TestMethod]
    public async Task A_selection_that_exceeds_the_deadline_times_out_and_the_work_stops()
    {
        var engines = new FakeTranslationEngines { BlockTranslation = new ManualResetEventSlim(false) };
        var limits = new TranslationLimits { SelectionDeadline = TimeSpan.FromMilliseconds(150) };
        using var translator = new Translator(() => "models", engines.Create, limits: limits);

        var result = await translator.TranslateAsync("Hallo Welt", DeEn).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.AreEqual(new TranslationResult.TimedOut(TimeSpan.FromMilliseconds(150)), result);
        Assert.AreEqual(1, engines.StoppedByCancellation, "the engine was told to stop");
        engines.BlockTranslation.Set();
        var next = await translator.TranslateAsync("Noch einmal", DeEn).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsInstanceOfType<TranslationResult.Translated>(next, "the translator stays usable after a timeout");
    }

    [TestMethod]
    public async Task A_dictation_deadline_scales_with_the_source_tokens()
    {
        var engines = new FakeTranslationEngines { BlockTranslation = new ManualResetEventSlim(false) };
        var asked = new List<int>();
        var limits = new TranslationLimits
        {
            DictationDeadline = tokens =>
            {
                asked.Add(tokens);
                return TimeSpan.FromMilliseconds(100 + tokens);
            }
        };
        using var translator = new Translator(() => "models", engines.Create, limits: limits);

        var result = await translator.TranslateAsync(
            "eins zwei drei. vier fünf", TranslationUse.Dictation, DeEn).WaitAsync(TimeSpan.FromSeconds(20));

        CollectionAssert.AreEqual(new[] { 5 }, asked);
        Assert.AreEqual(new TranslationResult.TimedOut(TimeSpan.FromMilliseconds(105)), result);
    }

    [TestMethod]
    public async Task A_dictation_does_not_use_the_selection_deadline()
    {
        var engines = new FakeTranslationEngines { Pause = TimeSpan.FromMilliseconds(200) };
        var limits = new TranslationLimits { SelectionDeadline = TimeSpan.FromMilliseconds(20) };
        using var translator = new Translator(() => "models", engines.Create, limits: limits);

        var result = await translator.TranslateAsync("Hallo Welt", TranslationUse.Dictation, DeEn);

        Assert.IsInstanceOfType<TranslationResult.Translated>(result);
    }

    [TestMethod]
    public async Task The_deadline_does_not_include_the_time_spent_loading_the_model()
    {
        var engines = new FakeTranslationEngines { Gate = new ManualResetEventSlim(false) };
        var limits = new TranslationLimits { SelectionDeadline = TimeSpan.FromMilliseconds(100) };
        using var translator = new Translator(() => "models", engines.Create, limits: limits);

        Task<TranslationResult> running = translator.TranslateAsync("Hallo Welt", DeEn);
        await engines.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(400);
        engines.Gate.Set();
        var result = await running.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsInstanceOfType<TranslationResult.Translated>(result);
    }

    [TestMethod]
    public async Task Cancelling_a_running_translation_stops_the_engine_promptly()
    {
        var engines = new FakeTranslationEngines { BlockTranslation = new ManualResetEventSlim(false) };
        using var translator = new Translator(() => "models", engines.Create);
        using var cancellation = new CancellationTokenSource();

        Task<TranslationResult> running = translator.TranslateAsync("Hallo Welt", DeEn, cancellation.Token);
        await engines.TranslationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.AreEqual(1, engines.StoppedByCancellation);
    }

    [TestMethod]
    public async Task Cancelling_a_queued_request_does_not_disturb_the_running_one_or_the_next()
    {
        var engines = new FakeTranslationEngines { Pause = TimeSpan.FromMilliseconds(300) };
        using var translator = new Translator(() => "models", engines.Create);
        using var cancellation = new CancellationTokenSource();

        Task<TranslationResult> first = translator.TranslateAsync("Erster Text", DeEn);
        await engines.TranslationStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<TranslationResult> queued = translator.TranslateAsync("Zweiter Text", DeEn, cancellation.Token);
        await Task.Delay(50);
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => queued.WaitAsync(TimeSpan.FromSeconds(10)));
        var third = await translator.TranslateAsync("Dritter Text", DeEn).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(new TranslationResult.Translated("[de-en] Erster Text", DeEn), await first);
        Assert.AreEqual(new TranslationResult.Translated("[de-en] Dritter Text", DeEn), third);
        Assert.AreEqual(1, engines.MaxConcurrentTranslations);
        Assert.AreEqual(2, engines.Batches.Count, "the cancelled request never reached the engine");
    }

    [TestMethod]
    public async Task Queued_requests_do_not_spend_their_deadline_while_waiting_for_the_one_before()
    {
        var engines = new FakeTranslationEngines { Pause = TimeSpan.FromMilliseconds(300) };
        var limits = new TranslationLimits { SelectionDeadline = TimeSpan.FromMilliseconds(500) };
        using var translator = new Translator(() => "models", engines.Create, limits: limits);

        Task<TranslationResult>[] all = Enumerable.Range(0, 3)
            .Select(i => translator.TranslateAsync($"Text {i}", DeEn))
            .ToArray();
        TranslationResult[] results = await Task.WhenAll(all).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.IsTrue(results.All(result => result is TranslationResult.Translated));
        Assert.AreEqual(1, engines.MaxConcurrentTranslations);
    }

    [TestMethod]
    public async Task A_failing_sentence_fails_the_whole_text_and_never_returns_a_partial_translation()
    {
        var engines = new FakeTranslationEngines { RejectText = "Zweiter Satz." };
        using var translator = new Translator(() => "models", engines.Create);

        var result = await translator.TranslateAsync("Erster Satz. Zweiter Satz. Dritter Satz.", DeEn);

        Assert.IsInstanceOfType<TranslationResult.Failed>(result);
    }
}
