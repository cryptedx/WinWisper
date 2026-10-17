using System.Globalization;
using WinWispr.Core;

namespace WinWispr.Views;

/// <summary>
/// The single place that decides how each dictation notice and each translation notice looks on the overlay.
/// The two kinds are separate types but share one mapping, so a notice that both can raise looks the same.
/// </summary>
internal static class DictationNoticePresentation
{
    private static readonly TimeSpan PasteNoticeDuration = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan TranslationNotInstalledDuration = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan MicrophoneFallbackDuration = TimeSpan.FromSeconds(4);

    private static readonly TimeSpan SelectionTooLongDuration = TimeSpan.FromSeconds(4);

    internal static OverlayStatus For(TranslationNotice notice) => notice.Kind switch
    {
        TranslationNoticeKind.NoTextSelected => OverlayStatus.Result("No text selected"),
        TranslationNoticeKind.ClipboardHasNoText => OverlayStatus.Result("Clipboard has no text"),
        TranslationNoticeKind.SelectionTooLong => OverlayStatus.Result(
            "Selection too long",
            notice.Limit is int limit
                ? $"Maximum {limit.ToString("N0", CultureInfo.InvariantCulture)} characters"
                : null,
            SelectionTooLongDuration),
        TranslationNoticeKind.TranslationNotInstalled => For(new DictationNotice(DictationNoticeKind.TranslationNotInstalled)),
        TranslationNoticeKind.TranslationFailed => OverlayStatus.Result("Translation failed"),
        TranslationNoticeKind.ClipboardUnavailable => For(new DictationNotice(DictationNoticeKind.ClipboardUnavailable)),
        TranslationNoticeKind.CopiedToClipboard => For(new DictationNotice(DictationNoticeKind.CopiedToClipboard)),
        _ => throw new ArgumentOutOfRangeException(nameof(notice), notice.Kind, null)
    };

    internal static OverlayStatus For(DictationNotice notice) => notice.Kind switch
    {
        DictationNoticeKind.MicrophoneUnavailable => OverlayStatus.Result("Microphone unavailable"),
        DictationNoticeKind.RecordingLimitReached => OverlayStatus.Result("Recording limit reached"),
        DictationNoticeKind.TranscriptionTimedOut => OverlayStatus.Result("Transcription timed out"),
        DictationNoticeKind.TranscriptionFailed => OverlayStatus.Result("Transcription failed"),
        DictationNoticeKind.NoSpeech => OverlayStatus.Result("No speech detected"),
        DictationNoticeKind.CopiedToClipboard => OverlayStatus.Result(
            "Automatic paste unavailable", "Press Ctrl+V to paste", PasteNoticeDuration),
        DictationNoticeKind.ClipboardUnavailable => OverlayStatus.Result("Clipboard unavailable"),
        DictationNoticeKind.NothingToReplay => OverlayStatus.Result("No dictation to insert yet"),
        DictationNoticeKind.TaskReady => OverlayStatus.Result("Task ready"),
        DictationNoticeKind.TaskCaptureFailed => OverlayStatus.Result("Task capture failed"),
        DictationNoticeKind.TaskCaptureUnavailable => OverlayStatus.Result("Task capture unavailable"),
        DictationNoticeKind.TeamsMicrophone => OverlayStatus.Result(notice.Detail ?? "Check Teams microphone"),
        DictationNoticeKind.MicrophoneFallback => OverlayStatus.Result(
            "Windows default microphone used", "Saved microphone unavailable", MicrophoneFallbackDuration),
        DictationNoticeKind.TranslationNotInstalled => OverlayStatus.Result(
            "Translation not installed", "Install WinWispr Models to add it", TranslationNotInstalledDuration),
        DictationNoticeKind.TranslationFailedTranscriptCopied => OverlayStatus.Result(
            "Translation failed \u2013 transcript copied", "Press Ctrl+V to paste it", PasteNoticeDuration),
        _ => throw new ArgumentOutOfRangeException(nameof(notice), notice.Kind, null)
    };
}
