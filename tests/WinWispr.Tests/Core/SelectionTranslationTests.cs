using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Core;
using WinWispr.Diagnostics;
using WinWispr.Translation;

namespace WinWispr.Tests.Core;

/// <summary>
/// SelectionTranslation through its effects record: what a translate press leads to (a notice, a preview, a
/// clipboard content), driven by fakes without any UI, clipboard or model.
/// </summary>
[TestClass]
public sealed class SelectionTranslationTests
{
    private const string German = "Das ist ein Test.";
    private const string English = "This is a test.";

    [TestMethod]
    public async Task A_selection_is_translated_and_the_preview_shows_the_text_with_its_direction()
    {
        var harness = new Harness();

        await harness.Press();

        Assert.AreEqual(1, harness.Previews.Count);
        Assert.AreEqual(new TranslationPreview(English, TranslationDirection.GermanToEnglish), harness.Previews[0]);
        Assert.AreEqual(0, harness.Notices.Count);
    }

    [TestMethod]
    public async Task The_selection_reaches_the_translator_exactly_as_read_because_the_dictionary_never_applies()
    {
        const string selection = "  Ich  sagte Teams.\r\n\r\n  Zweiter Absatz. ";
        var harness = new Harness { Text = selection };

        await harness.Press();

        CollectionAssert.AreEqual(new[] { selection }, harness.Translated);
    }

    [TestMethod]
    public async Task Copy_puts_the_translation_on_the_clipboard_and_ends_the_translation_without_a_notice()
    {
        var harness = new Harness { Choice = PreviewChoice.Copy };

        await harness.Press();

        CollectionAssert.AreEqual(new[] { English }, harness.Copies);
        Assert.AreEqual(0, harness.Notices.Count);
        Assert.AreEqual(SelectionTranslationPhase.Idle, harness.Phases[^1]);
    }

    [TestMethod]
    public async Task Cancel_closes_the_preview_without_copying_and_without_a_notice()
    {
        var harness = new Harness { Choice = PreviewChoice.Cancel };

        await harness.Press();

        Assert.AreEqual(0, harness.Copies.Count);
        Assert.AreEqual(0, harness.Notices.Count);
        Assert.AreEqual(SelectionTranslationPhase.Idle, harness.Phases[^1]);
    }

    [TestMethod]
    public async Task A_copy_that_fails_reports_clipboard_unavailable_and_logs_the_exception_type()
    {
        var harness = new Harness
        {
            Choice = PreviewChoice.Copy,
            CopyOverride = (_, _) => throw new InvalidOperationException("secret text " + English)
        };

        await harness.Press();

        CollectionAssert.AreEqual(
            new[] { new TranslationNotice(TranslationNoticeKind.ClipboardUnavailable) }, harness.Notices);
        CollectionAssert.AreEqual(
            new[] { (TechnicalEvent.ClipboardUnavailable, nameof(InvalidOperationException)) }, harness.Logged);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("  \r\n\t ")]
    public async Task Nothing_selected_shows_no_text_selected_and_translates_nothing(string? selection)
    {
        var harness = new Harness { Text = selection };

        await harness.Press();

        CollectionAssert.AreEqual(
            new[] { new TranslationNotice(TranslationNoticeKind.NoTextSelected) }, harness.Notices);
        Assert.AreEqual(0, harness.Translated.Count);
        Assert.AreEqual(0, harness.Previews.Count);
        Assert.AreEqual(0, harness.Prepared.Count, "an empty selection must not start a model load");
    }

    [TestMethod]
    public async Task A_selection_over_the_limit_shows_the_limit_and_shows_no_preview()
    {
        var harness = new Harness
        {
            Result = new TranslationResult.TooLong(3000)
        };

        await harness.Press();

        CollectionAssert.AreEqual(
            new[] { new TranslationNotice(TranslationNoticeKind.SelectionTooLong, 3000) }, harness.Notices);
        Assert.AreEqual(0, harness.Previews.Count);
        Assert.AreEqual(0, harness.Logged.Count, "a long selection is not a failure");
    }

    [TestMethod]
    public async Task Without_an_installed_translation_nothing_is_read_and_the_notice_points_to_settings()
    {
        var harness = new Harness { Installed = false };

        await harness.Press();

        CollectionAssert.AreEqual(
            new[] { new TranslationNotice(TranslationNoticeKind.TranslationNotInstalled) }, harness.Notices);
        Assert.AreEqual(0, harness.Reads, "the selection reader must not touch the clipboard when nothing can translate");
        Assert.AreEqual(0, harness.Translated.Count);
    }

    [TestMethod]
    public async Task A_model_that_vanished_after_the_check_shows_translation_not_installed_and_is_logged()
    {
        var harness = new Harness { Result = new TranslationResult.NotInstalled() };

        await harness.Press();

        CollectionAssert.AreEqual(
            new[] { new TranslationNotice(TranslationNoticeKind.TranslationNotInstalled) }, harness.Notices);
        CollectionAssert.AreEqual(new[] { (TechnicalEvent.TranslationNotInstalled, (string?)null) }, harness.Logged);
    }

    [TestMethod]
    public async Task A_timeout_shows_translation_failed_and_is_logged_by_kind()
    {
        var harness = new Harness { Result = new TranslationResult.TimedOut(TimeSpan.FromSeconds(5)) };

        await harness.Press();

        CollectionAssert.AreEqual(
            new[] { new TranslationNotice(TranslationNoticeKind.TranslationFailed) }, harness.Notices);
        CollectionAssert.AreEqual(new[] { (TechnicalEvent.TranslationTimedOut, (string?)null) }, harness.Logged);
        Assert.AreEqual(0, harness.Previews.Count);
    }

    [TestMethod]
    public async Task A_failed_translation_shows_translation_failed_without_logging_twice()
    {
        // The Translator already logged a load or engine failure with its exception type.
        var harness = new Harness { Result = new TranslationResult.Failed("engine broke on " + German) };

        await harness.Press();

        CollectionAssert.AreEqual(
            new[] { new TranslationNotice(TranslationNoticeKind.TranslationFailed) }, harness.Notices);
        Assert.AreEqual(0, harness.Logged.Count);
        Assert.AreEqual(0, harness.Previews.Count);
    }

    [TestMethod]
    public async Task An_exception_from_the_translation_shows_translation_failed_and_logs_only_its_type()
    {
        var harness = new Harness
        {
            TranslateOverride = (_, _) => throw new InvalidDataException("contains " + German)
        };

        await harness.Press();

        CollectionAssert.AreEqual(
            new[] { new TranslationNotice(TranslationNoticeKind.TranslationFailed) }, harness.Notices);
        CollectionAssert.AreEqual(
            new[] { (TechnicalEvent.TranslationFailed, nameof(InvalidDataException)) }, harness.Logged);
    }

    [TestMethod]
    public async Task A_selection_that_cannot_be_read_shows_translation_failed_and_translates_nothing()
    {
        var harness = new Harness { ReadOverride = _ => throw new TimeoutException("keys held") };

        await harness.Press();

        CollectionAssert.AreEqual(
            new[] { new TranslationNotice(TranslationNoticeKind.TranslationFailed) }, harness.Notices);
        CollectionAssert.AreEqual(
            new[] { (TechnicalEvent.TranslationFailed, nameof(TimeoutException)) }, harness.Logged);
        Assert.AreEqual(0, harness.Translated.Count);
    }

    [TestMethod]
    public async Task The_language_is_asked_for_and_the_load_starts_as_soon_as_the_selection_is_read()
    {
        var harness = new Harness();

        await harness.Press();

        CollectionAssert.AreEqual(
            new[] { "read", "prepare:" + German, "translate" },
            harness.Order.Where(entry => !entry.StartsWith("phase:", StringComparison.Ordinal)).ToArray());
    }

    [TestMethod]
    public async Task The_phases_run_from_reading_through_translating_to_the_preview_and_back_to_idle()
    {
        var harness = new Harness();

        await harness.Press();

        CollectionAssert.AreEqual(
            new[]
            {
                SelectionTranslationPhase.Reading,
                SelectionTranslationPhase.Translating,
                SelectionTranslationPhase.Preview,
                SelectionTranslationPhase.Idle
            },
            harness.Phases);
    }

    [TestMethod]
    public async Task A_notice_follows_the_return_to_idle_so_that_ending_the_overlay_cannot_hide_it()
    {
        var harness = new Harness { Text = null };

        await harness.Press();

        CollectionAssert.AreEqual(
            new[] { "phase:Reading", "read", "phase:Idle", "notice:NoTextSelected" }, harness.Order.ToArray());
    }

    [TestMethod]
    public async Task Cancelling_while_the_translation_runs_stops_it_without_a_preview_or_notice()
    {
        var translating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken seen = default;
        var harness = new Harness
        {
            TranslateOverride = async (_, token) =>
            {
                seen = token;
                translating.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return new TranslationResult.Failed("unreachable");
            }
        };

        Task press = harness.Press();
        await translating.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(harness.Selection.IsTranslating);

        Assert.IsTrue(harness.Selection.Cancel());
        await press.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsTrue(seen.IsCancellationRequested);
        Assert.AreEqual(0, harness.Previews.Count);
        Assert.AreEqual(0, harness.Notices.Count);
        Assert.AreEqual(0, harness.Logged.Count);
        Assert.IsFalse(harness.Selection.IsActive);
        Assert.IsFalse(harness.Selection.IsTranslating);
    }

    [TestMethod]
    public async Task Cancelling_while_the_selection_is_read_ends_without_a_notice()
    {
        var reading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = new Harness
        {
            ReadOverride = async token =>
            {
                reading.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return null;
            }
        };

        Task press = harness.Press();
        await reading.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(harness.Selection.IsTranslating, "Esc is recognized from the moment the press is accepted");

        Assert.IsTrue(harness.Selection.Cancel());
        await press.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(0, harness.Notices.Count);
        Assert.AreEqual(0, harness.Translated.Count);
    }

    [TestMethod]
    public async Task Cancel_does_nothing_when_no_translation_is_active()
    {
        var harness = new Harness();

        Assert.IsFalse(harness.Selection.Cancel());
        Assert.IsFalse(harness.Selection.IsActive);
        await harness.Press();
        Assert.IsFalse(harness.Selection.Cancel());
    }

    [TestMethod]
    public async Task Cancelling_while_the_preview_is_open_closes_it_without_copying()
    {
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken seen = default;
        var harness = new Harness
        {
            PreviewOverride = async (_, token) =>
            {
                seen = token;
                shown.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return PreviewChoice.Copy;
            }
        };

        Task press = harness.Press();
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(harness.Selection.IsActive);
        Assert.IsFalse(harness.Selection.IsTranslating, "the preview has focus and handles Esc itself");

        Assert.IsTrue(harness.Selection.Cancel());
        await press.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsTrue(seen.IsCancellationRequested);
        Assert.AreEqual(0, harness.Copies.Count);
        Assert.AreEqual(0, harness.Notices.Count);
        Assert.IsFalse(harness.Selection.IsActive);
    }

    [TestMethod]
    public async Task A_press_during_dictation_is_ignored_silently_in_every_dictation_state()
    {
        var harness = new Harness { Dictation = true };

        await harness.Press();

        Assert.AreEqual(0, harness.Reads);
        Assert.AreEqual(0, harness.Notices.Count);
        Assert.AreEqual(0, harness.Phases.Count);
        Assert.IsFalse(harness.Selection.IsActive);
    }

    [TestMethod]
    public async Task A_press_while_another_translation_runs_is_ignored()
    {
        var translating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = new Harness
        {
            TranslateOverride = async (_, _) =>
            {
                translating.SetResult();
                await release.Task;
                return new TranslationResult.Translated(English, TranslationDirection.GermanToEnglish);
            }
        };

        Task first = harness.Press();
        await translating.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await harness.Press();
        Assert.AreEqual(1, harness.Reads);
        Assert.AreEqual(0, harness.Notices.Count);

        release.SetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(1, harness.Previews.Count);
    }

    [TestMethod]
    public async Task A_press_while_the_preview_is_open_is_ignored_and_the_next_press_works_afterwards()
    {
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var choose = new TaskCompletionSource<PreviewChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = new Harness
        {
            PreviewOverride = (_, _) =>
            {
                shown.TrySetResult();
                return choose.Task;
            }
        };

        Task first = harness.Press();
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await harness.Press();
        Assert.AreEqual(1, harness.Reads);
        Assert.AreEqual(1, harness.Previews.Count);

        choose.SetResult(PreviewChoice.Cancel);
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        await harness.Press();

        Assert.AreEqual(2, harness.Reads);
    }

    [TestMethod]
    public async Task A_translation_can_start_again_after_every_kind_of_ending()
    {
        var harness = new Harness { Text = null };
        await harness.Press();
        harness.Text = German;
        await harness.Press();
        harness.Choice = PreviewChoice.Copy;
        await harness.Press();

        Assert.AreEqual(3, harness.Reads);
        Assert.AreEqual(2, harness.Previews.Count);
    }

    [TestMethod]
    public async Task Disposing_cancels_a_running_translation_without_a_notice()
    {
        var translating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = new Harness
        {
            TranslateOverride = async (_, token) =>
            {
                translating.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return new TranslationResult.Failed("unreachable");
            }
        };

        Task press = harness.Press();
        await translating.Task.WaitAsync(TimeSpan.FromSeconds(10));
        harness.Selection.Dispose();
        await press.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(0, harness.Notices.Count);
        await harness.Press();
        Assert.AreEqual(1, harness.Reads, "a disposed coordinator accepts no press");
    }

    [TestMethod]
    public async Task Replace_activates_the_captured_window_and_delivers_the_translation_there()
    {
        var harness = new Harness { Choice = PreviewChoice.Replace, Target = new WindowTarget(77) };

        await harness.Press();

        CollectionAssert.AreEqual(
            new[] { "reactivate:77", "deliver:77:" + English },
            harness.Order.Where(entry => entry.StartsWith("reactivate:", StringComparison.Ordinal)
                || entry.StartsWith("deliver:", StringComparison.Ordinal)).ToArray());
        Assert.AreEqual(0, harness.Copies.Count, "the delivery leaves the translation on the clipboard itself");
        Assert.AreEqual(0, harness.Notices.Count);
        Assert.AreEqual(SelectionTranslationPhase.Idle, harness.Phases[^1]);
    }

    [TestMethod]
    public async Task The_target_is_captured_when_the_press_is_accepted_before_the_selection_is_read()
    {
        var harness = new Harness { Choice = PreviewChoice.Replace, Target = new WindowTarget(5) };

        await harness.Press();

        CollectionAssert.AreEqual(new[] { 0 }, harness.Captures, "once, before the first read");
    }

    [TestMethod]
    public async Task Replace_becomes_copy_with_the_copied_notice_when_the_window_cannot_be_reactivated()
    {
        var harness = new Harness { Choice = PreviewChoice.Replace, Reactivated = false };

        await harness.Press();

        CollectionAssert.AreEqual(new[] { English }, harness.Copies);
        Assert.AreEqual(0, harness.Delivered.Count, "nothing is pasted into whatever window is in front");
        CollectionAssert.AreEqual(
            new[] { new TranslationNotice(TranslationNoticeKind.CopiedToClipboard) }, harness.Notices);
    }

    [TestMethod]
    public async Task A_window_that_throws_on_reactivation_counts_as_gone()
    {
        var harness = new Harness
        {
            Choice = PreviewChoice.Replace,
            ReactivateOverride = (_, _) => throw new InvalidOperationException("boom")
        };

        await harness.Press();

        CollectionAssert.AreEqual(new[] { English }, harness.Copies);
        CollectionAssert.AreEqual(
            new[] { new TranslationNotice(TranslationNoticeKind.CopiedToClipboard) }, harness.Notices);
    }

    [TestMethod]
    public async Task A_replace_whose_paste_could_not_be_sent_shows_the_copied_notice()
    {
        var harness = new Harness { Choice = PreviewChoice.Replace, Delivery = DeliveryResult.CopiedToClipboard };

        await harness.Press();

        CollectionAssert.AreEqual(
            new[] { new TranslationNotice(TranslationNoticeKind.CopiedToClipboard) }, harness.Notices);
        Assert.AreEqual(0, harness.Copies.Count);
    }

    [TestMethod]
    public async Task A_replace_that_cannot_reach_the_clipboard_reports_it_and_logs_the_exception_type()
    {
        var harness = new Harness
        {
            Choice = PreviewChoice.Replace,
            DeliverOverride = (_, _, _) => throw new InvalidOperationException("secret " + English)
        };

        await harness.Press();

        CollectionAssert.AreEqual(
            new[] { new TranslationNotice(TranslationNoticeKind.ClipboardUnavailable) }, harness.Notices);
        CollectionAssert.AreEqual(
            new[] { (TechnicalEvent.ClipboardUnavailable, nameof(InvalidOperationException)) }, harness.Logged);
    }

    [TestMethod]
    public async Task Swap_translates_the_original_selection_in_the_opposite_direction_and_shows_the_new_result()
    {
        var harness = new Harness();
        harness.Choices.Enqueue(PreviewChoice.Swap);

        await harness.Press();

        CollectionAssert.AreEqual(
            new[] { (German, TranslationDirection.EnglishToGerman) }, harness.Swapped,
            "the original text, not the displayed translation");
        Assert.AreEqual(2, harness.Previews.Count);
        Assert.AreEqual(
            new TranslationPreview("swapped:" + German, TranslationDirection.EnglishToGerman), harness.Previews[1]);
        Assert.AreEqual(0, harness.Notices.Count);
    }

    [TestMethod]
    public async Task Copy_and_replace_after_a_swap_use_the_swapped_translation()
    {
        var copy = new Harness();
        copy.Choices.Enqueue(PreviewChoice.Swap);
        copy.Choices.Enqueue(PreviewChoice.Copy);
        await copy.Press();
        CollectionAssert.AreEqual(new[] { "swapped:" + German }, copy.Copies);

        var replace = new Harness();
        replace.Choices.Enqueue(PreviewChoice.Swap);
        replace.Choices.Enqueue(PreviewChoice.Replace);
        await replace.Press();
        CollectionAssert.AreEqual(new[] { "swapped:" + German }, replace.Delivered.Select(entry => entry.Text).ToArray());
    }

    [TestMethod]
    public async Task A_selection_that_translated_from_english_swaps_back_to_german_to_english()
    {
        var harness = new Harness
        {
            Text = English,
            Result = new TranslationResult.Translated(German, TranslationDirection.EnglishToGerman)
        };
        harness.Choices.Enqueue(PreviewChoice.Swap);

        await harness.Press();

        CollectionAssert.AreEqual(
            new[] { (English, TranslationDirection.GermanToEnglish) }, harness.Swapped);
    }

    [TestMethod]
    public async Task A_second_swap_returns_to_the_first_result_without_translating_again()
    {
        var harness = new Harness();
        harness.Choices.Enqueue(PreviewChoice.Swap);
        harness.Choices.Enqueue(PreviewChoice.Swap);
        harness.Choices.Enqueue(PreviewChoice.Replace);

        await harness.Press();

        Assert.AreEqual(3, harness.Previews.Count);
        Assert.AreEqual(harness.Previews[0], harness.Previews[2]);
        Assert.AreEqual(1, harness.Swapped.Count, "the first direction's result is kept");
        CollectionAssert.AreEqual(new[] { English }, harness.Delivered.Select(entry => entry.Text).ToArray());
    }

    [TestMethod]
    public async Task A_failed_swap_keeps_the_previous_result_and_reports_a_translation_notice()
    {
        var harness = new Harness { SwapResult = new TranslationResult.Failed("broke on " + German) };
        harness.Choices.Enqueue(PreviewChoice.Swap);
        harness.Choices.Enqueue(PreviewChoice.Copy);

        await harness.Press();

        Assert.AreEqual(2, harness.Previews.Count);
        Assert.AreEqual(harness.Previews[0], harness.Previews[1], "the previous result is shown again");
        CollectionAssert.AreEqual(new[] { English }, harness.Copies);
        CollectionAssert.AreEqual(
            new[] { new TranslationNotice(TranslationNoticeKind.TranslationFailed) }, harness.Notices);
    }

    [TestMethod]
    public async Task A_swap_that_times_out_or_throws_is_logged_by_kind_and_keeps_the_result()
    {
        var timedOut = new Harness { SwapResult = new TranslationResult.TimedOut(TimeSpan.FromSeconds(5)) };
        timedOut.Choices.Enqueue(PreviewChoice.Swap);
        await timedOut.Press();
        CollectionAssert.AreEqual(new[] { (TechnicalEvent.TranslationTimedOut, (string?)null) }, timedOut.Logged);
        Assert.AreEqual(timedOut.Previews[0], timedOut.Previews[1]);

        var thrown = new Harness
        {
            SwapOverride = (_, _, _) => throw new InvalidDataException("contains " + German)
        };
        thrown.Choices.Enqueue(PreviewChoice.Swap);
        await thrown.Press();
        CollectionAssert.AreEqual(
            new[] { (TechnicalEvent.TranslationFailed, nameof(InvalidDataException)) }, thrown.Logged);
        Assert.AreEqual(thrown.Previews[0], thrown.Previews[1]);
        CollectionAssert.AreEqual(
            new[] { new TranslationNotice(TranslationNoticeKind.TranslationFailed) }, thrown.Notices);
    }

    [TestMethod]
    public async Task A_failed_swap_can_be_tried_again()
    {
        var harness = new Harness { SwapResult = new TranslationResult.Failed("x") };
        harness.Choices.Enqueue(PreviewChoice.Swap);
        harness.Choices.Enqueue(PreviewChoice.Swap);

        await harness.Press();

        Assert.AreEqual(2, harness.Swapped.Count, "a failure is not cached");
        Assert.AreEqual(3, harness.Previews.Count);
    }

    [TestMethod]
    public async Task While_a_swap_runs_the_translation_is_busy_no_preview_choice_is_open_and_esc_cancels_it()
    {
        var swapping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = new Harness
        {
            SwapOverride = async (_, _, token) =>
            {
                swapping.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return new TranslationResult.Failed("unreachable");
            }
        };
        harness.Choices.Enqueue(PreviewChoice.Swap);

        Task press = harness.Press();
        await swapping.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsTrue(harness.Selection.IsTranslating, "Esc is recognized while the swap runs");
        Assert.AreEqual(SelectionTranslationPhase.Translating, harness.Phases[^1]);
        Assert.AreEqual(1, harness.Previews.Count, "the preview waits for the result");

        Assert.IsTrue(harness.Selection.Cancel());
        await press.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(0, harness.Notices.Count);
        Assert.AreEqual(0, harness.Copies.Count);
        Assert.AreEqual(SelectionTranslationPhase.Idle, harness.Phases[^1]);
    }

    [TestMethod]
    public async Task The_phases_around_a_swap_return_to_the_preview()
    {
        var harness = new Harness();
        harness.Choices.Enqueue(PreviewChoice.Swap);

        await harness.Press();

        CollectionAssert.AreEqual(
            new[]
            {
                SelectionTranslationPhase.Reading,
                SelectionTranslationPhase.Translating,
                SelectionTranslationPhase.Preview,
                SelectionTranslationPhase.Translating,
                SelectionTranslationPhase.Preview,
                SelectionTranslationPhase.Idle
            },
            harness.Phases);
    }

    [TestMethod]
    public async Task A_result_without_a_direction_has_nothing_to_swap()
    {
        var harness = new Harness { Result = new TranslationResult.Translated("123", null) };
        harness.Choices.Enqueue(PreviewChoice.Swap);

        await harness.Press();

        Assert.AreEqual(0, harness.Swapped.Count);
        Assert.AreEqual(2, harness.Previews.Count);
    }


    // --- Translate clipboard (tray) ---

    [TestMethod]
    public async Task A_clipboard_translation_reads_the_clipboard_not_the_selection_and_never_captures_a_window()
    {
        var harness = new Harness { ClipboardText = German };

        await harness.PressClipboard();

        Assert.AreEqual(0, harness.Reads, "no Ctrl+C selection read for the clipboard");
        Assert.AreEqual(1, harness.ClipboardReads);
        Assert.AreEqual(0, harness.Captures.Count, "the focus and the foreground window are not touched");
        CollectionAssert.AreEqual(new[] { German }, harness.Translated);
        Assert.AreEqual(0, harness.Copies.Count, "reading never writes the clipboard");
    }

    [TestMethod]
    public async Task The_clipboard_preview_offers_only_copy_and_cancel()
    {
        var harness = new Harness();

        await harness.PressClipboard();

        Assert.AreEqual(
            new TranslationPreview(English, TranslationDirection.GermanToEnglish, CanReplace: false), harness.Previews.Single());
    }

    [TestMethod]
    public async Task The_selection_preview_still_offers_replace()
    {
        var harness = new Harness();

        await harness.Press();

        Assert.IsTrue(harness.Previews.Single().CanReplace);
    }

    [TestMethod]
    public async Task Copy_from_a_clipboard_preview_replaces_the_clipboard_with_the_translation()
    {
        var harness = new Harness { Choice = PreviewChoice.Copy };

        await harness.PressClipboard();

        CollectionAssert.AreEqual(new[] { English }, harness.Copies);
        Assert.AreEqual(0, harness.Notices.Count);
    }

    [TestMethod]
    public async Task Replace_and_swap_do_nothing_for_a_clipboard_translation_and_the_preview_stays()
    {
        var harness = new Harness();
        harness.Choices.Enqueue(PreviewChoice.Replace);
        harness.Choices.Enqueue(PreviewChoice.Swap);
        harness.Choices.Enqueue(PreviewChoice.Cancel);

        await harness.PressClipboard();

        Assert.AreEqual(3, harness.Previews.Count);
        Assert.AreEqual(0, harness.Delivered.Count);
        Assert.AreEqual(0, harness.Swapped.Count);
        Assert.AreEqual(0, harness.Copies.Count);
        CollectionAssert.DoesNotContain(harness.Order, "reactivate:7");
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" \r\n\t")]
    public async Task A_clipboard_without_text_shows_clipboard_has_no_text_and_no_preview(string? text)
    {
        var harness = new Harness { ClipboardText = text };

        await harness.PressClipboard();

        CollectionAssert.AreEqual(
            new[] { new TranslationNotice(TranslationNoticeKind.ClipboardHasNoText) }, harness.Notices);
        Assert.AreEqual(0, harness.Previews.Count);
        Assert.AreEqual(0, harness.Translated.Count);
        Assert.AreEqual(0, harness.Prepared.Count);
    }

    [TestMethod]
    public async Task A_clipboard_translation_without_an_installed_model_reads_nothing()
    {
        var harness = new Harness { Installed = false };

        await harness.PressClipboard();

        CollectionAssert.AreEqual(
            new[] { new TranslationNotice(TranslationNoticeKind.TranslationNotInstalled) }, harness.Notices);
        Assert.AreEqual(0, harness.ClipboardReads);
    }

    [TestMethod]
    public async Task A_clipboard_translation_is_ignored_while_a_dictation_is_active()
    {
        var harness = new Harness { Dictation = true };

        await harness.PressClipboard();

        Assert.AreEqual(0, harness.ClipboardReads);
        Assert.AreEqual(0, harness.Notices.Count);
        Assert.AreEqual(0, harness.Phases.Count);
    }

    [TestMethod]
    public async Task A_clipboard_translation_is_ignored_while_a_preview_is_open_and_after_it_a_new_one_starts()
    {
        var gate = new TaskCompletionSource<PreviewChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = new Harness
        {
            PreviewOverride = (_, _) =>
            {
                shown.SetResult();
                return gate.Task;
            }
        };
        Task first = harness.Press();
        await shown.Task;

        await harness.PressClipboard();
        await harness.Press();

        Assert.AreEqual(1, harness.Previews.Count);
        Assert.AreEqual(0, harness.ClipboardReads);
        gate.SetResult(PreviewChoice.Cancel);
        await first;
        Assert.IsFalse(harness.Selection.IsActive);
    }

    [TestMethod]
    public async Task A_selection_press_is_ignored_while_a_clipboard_translation_runs_and_esc_cancels_it()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = new Harness
        {
            TranslateOverride = async (_, token) =>
            {
                started.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return new TranslationResult.Failed("unreachable");
            }
        };
        Task run = harness.PressClipboard();
        await started.Task;

        await harness.Press();
        Assert.IsTrue(harness.Selection.IsTranslating);
        Assert.AreEqual(0, harness.Reads);
        Assert.IsTrue(harness.Selection.Cancel());
        await run;

        Assert.AreEqual(0, harness.Notices.Count);
        Assert.AreEqual(SelectionTranslationPhase.Idle, harness.Phases[^1]);
    }

    [TestMethod]
    public async Task A_clipboard_selection_over_the_limit_shows_the_limit()
    {
        var harness = new Harness { Result = new TranslationResult.TooLong(3000) };

        await harness.PressClipboard();

        CollectionAssert.AreEqual(
            new[] { new TranslationNotice(TranslationNoticeKind.SelectionTooLong, 3000) }, harness.Notices);
        Assert.AreEqual(0, harness.Previews.Count);
    }

    [TestMethod]
    public async Task Dismissing_for_a_dictation_while_translating_cancels_it_and_returns_the_selection_window()
    {
        var translating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken seen = default;
        var harness = new Harness
        {
            Target = new WindowTarget(99),
            TranslateOverride = async (_, token) =>
            {
                seen = token;
                translating.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return new TranslationResult.Failed("unreachable");
            }
        };

        Task press = harness.Press();
        await translating.Task.WaitAsync(TimeSpan.FromSeconds(10));

        WindowTarget? target = harness.Selection.DismissForDictation();
        await press.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(new WindowTarget(99), target);
        Assert.IsTrue(seen.IsCancellationRequested);
        Assert.IsFalse(harness.Selection.IsActive);
        Assert.AreEqual(0, harness.Previews.Count);
        Assert.AreEqual(0, harness.Copies.Count);
        Assert.AreEqual(0, harness.Notices.Count);
        Assert.AreEqual(0, harness.Logged.Count);
    }

    [TestMethod]
    public async Task Dismissing_for_a_dictation_while_the_preview_is_open_closes_it_as_cancel()
    {
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken seen = default;
        var harness = new Harness
        {
            Target = new WindowTarget(99),
            PreviewOverride = async (_, token) =>
            {
                seen = token;
                shown.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return PreviewChoice.Copy;
            }
        };

        Task press = harness.Press();
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(harness.Selection.IsActive);

        WindowTarget? target = harness.Selection.DismissForDictation();
        await press.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(new WindowTarget(99), target);
        Assert.IsTrue(seen.IsCancellationRequested);
        Assert.IsFalse(harness.Selection.IsActive);
        Assert.AreEqual(0, harness.Copies.Count, "Cancel leaves the clipboard as it is.");
        Assert.AreEqual(0, harness.Delivered.Count);
        Assert.AreEqual(0, harness.Notices.Count);
    }

    [TestMethod]
    public async Task Dismissing_for_a_dictation_from_a_clipboard_translation_returns_the_tray_target()
    {
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = new Harness
        {
            PreviewOverride = async (_, token) =>
            {
                shown.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return PreviewChoice.Copy;
            }
        };

        Task press = harness.PressClipboard(new WindowTarget(7));
        await shown.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(new WindowTarget(7), harness.Selection.DismissForDictation());
        await press.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(0, harness.Copies.Count);
        Assert.AreEqual(0, harness.Notices.Count);
    }

    [TestMethod]
    public void Dismissing_for_a_dictation_does_nothing_when_no_translation_is_active()
    {
        var harness = new Harness();

        Assert.IsNull(harness.Selection.DismissForDictation());
        Assert.IsFalse(harness.Selection.IsActive);
    }

    [TestMethod]
    public async Task A_dictation_can_start_right_after_the_dismissal_and_the_translation_ends_without_a_trace()
    {
        var translating = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var harness = new Harness
        {
            TranslateOverride = async (_, token) =>
            {
                translating.SetResult();
                await Task.Delay(Timeout.Infinite, token);
                return new TranslationResult.Failed("unreachable");
            }
        };
        Task press = harness.Press();
        await translating.Task.WaitAsync(TimeSpan.FromSeconds(10));

        harness.Selection.DismissForDictation();
        harness.Dictation = true; // the dictation session starts from the same press
        await press.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(SelectionTranslationPhase.Idle, harness.Phases[^1]);
        Assert.AreEqual(0, harness.Notices.Count);
        Assert.AreEqual(0, harness.Copies.Count);
        // A later translate press stays ignored while the dictation owns the session.
        await harness.Press();
        Assert.AreEqual(1, harness.Reads);
    }

    private sealed class Harness
    {
        private SelectionTranslation? _coordinator;

        /// <summary>The text the fake reader returns; null means nothing was selected.</summary>
        internal string? Text { get; set; } = German;

        internal bool Dictation { get; set; }
        internal bool Installed { get; set; } = true;
        internal PreviewChoice Choice { get; set; } = PreviewChoice.Cancel;
        internal TranslationResult Result { get; set; } =
            new TranslationResult.Translated(English, TranslationDirection.GermanToEnglish);
        internal Func<CancellationToken, Task<string?>>? ReadOverride { get; init; }
        internal Func<string, CancellationToken, Task<TranslationResult>>? TranslateOverride { get; init; }
        internal Func<TranslationPreview, CancellationToken, Task<PreviewChoice>>? PreviewOverride { get; init; }
        internal Func<string, CancellationToken, Task>? CopyOverride { get; init; }
        internal Func<WindowTarget, CancellationToken, Task<bool>>? ReactivateOverride { get; init; }
        internal Func<WindowTarget, string, CancellationToken, Task<DeliveryResult>>? DeliverOverride { get; init; }
        internal Func<string, TranslationDirection, CancellationToken, Task<TranslationResult>>? SwapOverride { get; init; }

        /// <summary>Scripted choices, taken first; the preview then answers with <see cref="Choice"/>.</summary>
        internal Queue<PreviewChoice> Choices { get; } = new();
        internal WindowTarget Target { get; set; } = new(42);
        internal bool Reactivated { get; set; } = true;
        internal DeliveryResult Delivery { get; set; } = DeliveryResult.PasteSent;

        /// <summary>The answer of a swap; by default the text prefixed with "swapped:" in the asked direction.</summary>
        internal TranslationResult? SwapResult { get; set; }
        /// <summary>How many selections had been read at each capture of the target.</summary>
        internal List<int> Captures { get; } = [];
        internal List<(WindowTarget Target, string Text)> Delivered { get; } = [];
        internal List<(string Text, TranslationDirection Direction)> Swapped { get; } = [];

        internal int Reads { get; private set; }

        /// <summary>The text the fake clipboard holds; null means it holds no text.</summary>
        internal string? ClipboardText { get; set; } = German;
        internal int ClipboardReads { get; private set; }
        internal List<string> Translated { get; } = [];
        internal List<string> Prepared { get; } = [];
        internal List<TranslationPreview> Previews { get; } = [];
        internal List<string> Copies { get; } = [];
        internal List<TranslationNotice> Notices { get; } = [];
        internal List<SelectionTranslationPhase> Phases { get; } = [];
        internal List<(TechnicalEvent Event, string? ExceptionType)> Logged { get; } = [];
        internal List<string> Order { get; } = [];

        /// <summary>The coordinator under test.</summary>
        internal SelectionTranslation Selection => _coordinator ??= Create();

        internal Task Press() => Selection.PressAsync();

        internal Task PressClipboard(WindowTarget? target = null) =>
            Selection.PressClipboardAsync(target ?? new WindowTarget(7));

        private SelectionTranslation Create() => new(new SelectionTranslationEffects(
            ReadSelectionAsync: async token =>
            {
                Reads++;
                Order.Add("read");
                return ReadOverride is { } read ? await read(token) : Text;
            },
            TranslateAsync: async (text, token) =>
            {
                Order.Add("translate");
                Translated.Add(text);
                return TranslateOverride is { } translate ? await translate(text, token) : Result;
            },
            ShowPreviewAsync: async (preview, token) =>
            {
                Previews.Add(preview);
                if (PreviewOverride is { } show)
                    return await show(preview, token);
                return Choices.Count > 0 ? Choices.Dequeue() : Choice;
            },
            CopyAsync: async (text, token) =>
            {
                Copies.Add(text);
                if (CopyOverride is { } copy)
                    await copy(text, token);
            },
            ShowNotice: notice =>
            {
                Order.Add("notice:" + notice.Kind);
                Notices.Add(notice);
            },
            IsDictationActive: () => Dictation,
            IsTranslationInstalled: () => Installed,
            CaptureTarget: () =>
            {
                Captures.Add(Reads);
                return Target;
            },
            TranslateInDirectionAsync: async (text, direction, token) =>
            {
                Swapped.Add((text, direction));
                if (SwapOverride is { } swap)
                    return await swap(text, direction, token);
                return SwapResult ?? new TranslationResult.Translated("swapped:" + text, direction);
            },
            ReactivateAsync: async (target, token) =>
            {
                Order.Add("reactivate:" + target.Hwnd);
                return ReactivateOverride is { } reactivate ? await reactivate(target, token) : Reactivated;
            },
            DeliverAsync: async (target, text, token) =>
            {
                Order.Add($"deliver:{target.Hwnd}:{text}");
                Delivered.Add((target, text));
                return DeliverOverride is { } deliver ? await deliver(target, text, token) : Delivery;
            })
        {
            ReadClipboardTextAsync = token =>
            {
                ClipboardReads++;
                Order.Add("readClipboard");
                return Task.FromResult(ClipboardText);
            },
            PrepareTranslation = text =>
            {
                Order.Add("prepare:" + text);
                Prepared.Add(text);
            },
            LogFailure = (technicalEvent, exception) => Logged.Add((technicalEvent, exception?.GetType().Name)),
            PhaseChanged = phase =>
            {
                Order.Add("phase:" + phase);
                Phases.Add(phase);
            }
        });
    }
}
