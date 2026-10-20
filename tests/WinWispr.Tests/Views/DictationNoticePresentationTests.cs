using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Core;
using WinWispr.Views;

namespace WinWispr.Tests.Views;

[TestClass]
public sealed class DictationNoticePresentationTests
{
    [TestMethod]
    [DataRow((int)DictationNoticeKind.MicrophoneUnavailable, "Microphone unavailable")]
    [DataRow((int)DictationNoticeKind.RecordingLimitReached, "Recording limit reached")]
    [DataRow((int)DictationNoticeKind.TranscriptionTimedOut, "Transcription timed out")]
    [DataRow((int)DictationNoticeKind.TranscriptionFailed, "Transcription failed")]
    [DataRow((int)DictationNoticeKind.NoSpeech, "No speech detected")]
    [DataRow((int)DictationNoticeKind.ClipboardUnavailable, "Clipboard unavailable")]
    [DataRow((int)DictationNoticeKind.NothingToReplay, "No dictation to insert yet")]
    [DataRow((int)DictationNoticeKind.TaskReady, "Task ready")]
    [DataRow((int)DictationNoticeKind.TaskCaptureFailed, "Task capture failed")]
    [DataRow((int)DictationNoticeKind.TaskCaptureUnavailable, "Task capture unavailable")]
    public void Plain_notices_are_short_results_with_their_text(int kind, string text)
    {
        OverlayStatus status = DictationNoticePresentation.For(new DictationNotice((DictationNoticeKind)kind));

        Assert.AreEqual(OverlayStatus.Result(text), status);
    }

    [TestMethod]
    public void Clipboard_fallback_asks_for_ctrl_v_for_eight_seconds()
    {
        OverlayStatus status = DictationNoticePresentation.For(new DictationNotice(DictationNoticeKind.CopiedToClipboard));

        Assert.AreEqual(
            OverlayStatus.Result("Automatic paste unavailable", "Press Ctrl+V to paste", TimeSpan.FromSeconds(8)),
            status);
    }

    [TestMethod]
    public void A_target_in_menu_mode_asks_for_esc_before_ctrl_v()
    {
        OverlayStatus status = DictationNoticePresentation.For(new DictationNotice(DictationNoticeKind.TargetMenuOpen));

        Assert.AreEqual(
            OverlayStatus.Result("Automatic paste unavailable", "Press Esc, then Ctrl+V", TimeSpan.FromSeconds(8)),
            status);
    }

    [TestMethod]
    public void Microphone_fallback_names_the_device_in_use_and_why_for_four_seconds()
    {
        OverlayStatus status = DictationNoticePresentation.For(new DictationNotice(DictationNoticeKind.MicrophoneFallback));

        Assert.AreEqual(
            OverlayStatus.Result("Windows default microphone used", "Saved microphone unavailable", TimeSpan.FromSeconds(4)),
            status);
    }

    [TestMethod]
    public void Translation_not_installed_points_to_settings_for_four_seconds()
    {
        OverlayStatus status = DictationNoticePresentation.For(new DictationNotice(DictationNoticeKind.TranslationNotInstalled));

        Assert.AreEqual(
            OverlayStatus.Result("Translation not installed", "Install WinWispr Models to add it", TimeSpan.FromSeconds(4)),
            status);
    }

    [TestMethod]
    public void A_failed_dictation_translation_says_the_transcript_is_on_the_clipboard_for_eight_seconds()
    {
        OverlayStatus status = DictationNoticePresentation.For(
            new DictationNotice(DictationNoticeKind.TranslationFailedTranscriptCopied));

        Assert.AreEqual(
            OverlayStatus.Result("Translation failed \u2013 transcript copied", "Press Ctrl+V to paste it", TimeSpan.FromSeconds(8)),
            status);
    }

    [TestMethod]
    public void No_text_selected_is_a_short_result()
    {
        Assert.AreEqual(
            OverlayStatus.Result("No text selected"),
            DictationNoticePresentation.For(new TranslationNotice(TranslationNoticeKind.NoTextSelected)));
    }

    [TestMethod]
    public void A_clipboard_without_text_is_a_short_result()
    {
        Assert.AreEqual(
            OverlayStatus.Result("Clipboard has no text"),
            DictationNoticePresentation.For(new TranslationNotice(TranslationNoticeKind.ClipboardHasNoText)));
    }

    [TestMethod]
    public void Selection_too_long_names_the_limit_for_four_seconds()
    {
        Assert.AreEqual(
            OverlayStatus.Result("Selection too long", "Maximum 3,000 characters", TimeSpan.FromSeconds(4)),
            DictationNoticePresentation.For(new TranslationNotice(TranslationNoticeKind.SelectionTooLong, 3000)));
        Assert.AreEqual(
            OverlayStatus.Result("Selection too long", "Maximum 450 characters", TimeSpan.FromSeconds(4)),
            DictationNoticePresentation.For(new TranslationNotice(TranslationNoticeKind.SelectionTooLong, 450)));
    }

    [TestMethod]
    public void A_selection_notice_without_a_limit_still_shows_selection_too_long()
    {
        OverlayStatus status = DictationNoticePresentation.For(new TranslationNotice(TranslationNoticeKind.SelectionTooLong));

        Assert.AreEqual(OverlayStatus.Result("Selection too long", null, TimeSpan.FromSeconds(4)), status);
    }

    [TestMethod]
    public void A_translation_notice_for_a_missing_model_points_to_settings_like_the_dictation_notice()
    {
        Assert.AreEqual(
            OverlayStatus.Result("Translation not installed", "Install WinWispr Models to add it", TimeSpan.FromSeconds(4)),
            DictationNoticePresentation.For(new TranslationNotice(TranslationNoticeKind.TranslationNotInstalled)));
        Assert.AreEqual(
            DictationNoticePresentation.For(new DictationNotice(DictationNoticeKind.TranslationNotInstalled)),
            DictationNoticePresentation.For(new TranslationNotice(TranslationNoticeKind.TranslationNotInstalled)),
            "both kinds share one presentation");
    }

    [TestMethod]
    [DataRow((int)TranslationNoticeKind.TranslationFailed, "Translation failed")]
    [DataRow((int)TranslationNoticeKind.ClipboardUnavailable, "Clipboard unavailable")]
    public void Plain_translation_notices_are_short_results_with_their_text(int kind, string text)
    {
        Assert.AreEqual(
            OverlayStatus.Result(text),
            DictationNoticePresentation.For(new TranslationNotice((TranslationNoticeKind)kind)));
    }

    [TestMethod]
    public void The_translation_copied_notice_is_the_dictation_copied_notice()
    {
        Assert.AreEqual(
            DictationNoticePresentation.For(new DictationNotice(DictationNoticeKind.CopiedToClipboard)),
            DictationNoticePresentation.For(new TranslationNotice(TranslationNoticeKind.CopiedToClipboard)));
    }

    [TestMethod]
    public void Every_translation_notice_kind_has_a_presentation_without_a_progress_ring()
    {
        foreach (TranslationNoticeKind kind in Enum.GetValues<TranslationNoticeKind>())
        {
            OverlayStatus status = DictationNoticePresentation.For(new TranslationNotice(kind, 100));
            Assert.IsFalse(status.ShowsProgress, kind.ToString());
            Assert.IsFalse(string.IsNullOrWhiteSpace(status.Text), kind.ToString());
            Assert.IsNotNull(status.Duration, kind.ToString());
        }
    }

    [TestMethod]
    public void Translation_progress_statuses_show_a_ring_and_stay_until_replaced()
    {
        Assert.AreEqual(new OverlayStatus("Translating\u2026", null, ShowsProgress: true, Duration: null), OverlayStatus.Translating);
        Assert.AreEqual(
            new OverlayStatus("Loading translation\u2026", null, ShowsProgress: true, Duration: null),
            OverlayStatus.LoadingTranslation);
    }

    [TestMethod]
    public void Teams_notice_shows_its_detail_as_the_text()
    {
        OverlayStatus status = DictationNoticePresentation.For(
            new DictationNotice(DictationNoticeKind.TeamsMicrophone, "Teams meeting microphone is ambiguous"));

        Assert.AreEqual(OverlayStatus.Result("Teams meeting microphone is ambiguous"), status);
    }

    [TestMethod]
    public void Every_kind_has_a_presentation_without_a_progress_ring()
    {
        foreach (DictationNoticeKind kind in Enum.GetValues<DictationNoticeKind>())
        {
            OverlayStatus status = DictationNoticePresentation.For(new DictationNotice(kind, "detail"));
            Assert.IsFalse(status.ShowsProgress, kind.ToString());
            Assert.IsFalse(string.IsNullOrWhiteSpace(status.Text), kind.ToString());
            Assert.IsNotNull(status.Duration, kind.ToString());
        }
    }

    [TestMethod]
    public void Progress_shows_a_ring_and_stays_until_replaced()
    {
        OverlayStatus status = OverlayStatus.Progress("Loading model");

        Assert.AreEqual(new OverlayStatus("Loading model", null, ShowsProgress: true, Duration: null), status);
    }

    [TestMethod]
    public void Result_hides_after_one_and_a_half_seconds_unless_kept_visible()
    {
        Assert.AreEqual(
            new OverlayStatus("Ready", null, ShowsProgress: false, Duration: TimeSpan.FromSeconds(1.5)),
            OverlayStatus.Result("Ready"));
        Assert.IsNull(OverlayStatus.Result("Task changes could not be saved.", detail: null, duration: null).Duration);
    }
}
