using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Audio;
using WinWispr.Core;
using WinWispr.Translation;

namespace WinWispr.Tests.Core;

[TestClass]
public sealed class DictationSessionTests
{
    [TestMethod]
    public async Task Stop_during_preparation_waits_for_capture_and_does_not_leave_it_running()
    {
        using var fixture = SessionFixture.Create(RecordingMode.Hold);
        fixture.Audio.WaitForStart = true;
        Task start = fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        await fixture.Audio.StartEntered.Task;
        Assert.AreEqual(RecordingState.Starting, fixture.Session.State);
        Task stop = fixture.Session.ReleaseAsync(DictationTrigger.PrimaryHotkey);
        Assert.AreEqual(0, fixture.Audio.StopCalls);
        fixture.Audio.ReleaseStart.SetResult();
        await Task.WhenAll(start, stop);
        Assert.AreEqual(1, fixture.Audio.StopCalls);
        Assert.AreEqual(RecordingState.Idle, fixture.Session.State);
    }

    [TestMethod]
    public async Task Cancel_pauses_then_timeout_resumes_same_session_without_losing_audio()
    {
        using var fixture = SessionFixture.Create();
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        fixture.Audio.AppendForTest(0.1f, 0.2f);

        await fixture.Session.RequestCancelAsync(CancellationToken.None);
        fixture.Audio.AppendForTest(0.9f);
        await fixture.Session.CancelTimeoutAsync(CancellationToken.None);
        fixture.Audio.AppendForTest(0.3f);
        await fixture.Session.StopAsync(StopReason.User, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { 0.1f, 0.2f, 0.3f }, fixture.Speech.LastSamples);
        Assert.AreEqual(1, fixture.Audio.PauseCalls);
        Assert.AreEqual(1, fixture.Audio.ResumeCalls);
    }

    [TestMethod]
    public async Task Y_within_cancel_window_discards_without_recognition_or_output()
    {
        using var fixture = SessionFixture.Create();
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        await fixture.Session.RequestCancelAsync(CancellationToken.None);

        await fixture.Session.ConfirmDiscardAsync(CancellationToken.None);

        Assert.AreEqual(RecordingState.Idle, fixture.Session.State);
        Assert.AreEqual(1, fixture.Audio.DiscardCalls);
        Assert.AreEqual(0, fixture.Speech.Calls);
        Assert.AreEqual(0, fixture.Output.Calls);
    }

    [TestMethod]
    public async Task Cancel_window_is_exactly_five_seconds_and_y_after_timeout_does_nothing()
    {
        using var fixture = SessionFixture.Create();
        DateTimeOffset before = DateTimeOffset.UtcNow;
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        await fixture.Session.RequestCancelAsync(CancellationToken.None);

        DateTimeOffset deadline = fixture.Session.CancelDeadline!.Value;
        Assert.IsTrue(deadline >= before.AddSeconds(5));
        Assert.IsTrue(deadline <= DateTimeOffset.UtcNow.AddSeconds(5));

        await fixture.Session.CancelTimeoutAsync(CancellationToken.None);
        await fixture.Session.ConfirmDiscardAsync(CancellationToken.None);

        Assert.AreEqual(RecordingState.Recording, fixture.Session.State);
        Assert.AreEqual(0, fixture.Audio.DiscardCalls);
        await fixture.Session.StopAsync(StopReason.User, CancellationToken.None);
    }

    [TestMethod]
    public async Task Hold_release_during_cancel_window_finishes_after_timeout_instead_of_resuming()
    {
        using var fixture = SessionFixture.Create(RecordingMode.Hold);
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        await fixture.Session.RequestCancelAsync(CancellationToken.None);
        await fixture.Session.ReleaseAsync(DictationTrigger.PrimaryHotkey);

        await fixture.Session.CancelTimeoutAsync(CancellationToken.None);

        Assert.AreEqual(1, fixture.Audio.StopCalls);
        Assert.AreEqual(0, fixture.Audio.ResumeCalls);
        Assert.AreEqual(RecordingState.Idle, fixture.Session.State);
    }

    [TestMethod]
    public async Task Toggle_starts_and_stops_one_session()
    {
        using var fixture = SessionFixture.Create(RecordingMode.Toggle);

        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        Assert.AreEqual(RecordingState.Recording, fixture.Session.State);
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);

        Assert.AreEqual(RecordingState.Idle, fixture.Session.State);
        Assert.AreEqual(1, fixture.Audio.StartCalls);
        Assert.AreEqual(1, fixture.Audio.StopCalls);
    }

    [TestMethod]
    public async Task Task_destination_routes_transcription_to_preview_without_text_output()
    {
        string? captured = null;
        using var fixture = SessionFixture.Create(
            taskCaptured: (text, _) =>
            {
                captured = text;
                return Task.CompletedTask;
            });

        await fixture.Session.PressAsync(DictationTrigger.TaskHotkey, fixture.Target);
        await fixture.Session.StopAsync(StopReason.User, CancellationToken.None);

        Assert.AreEqual("hello", captured);
        Assert.AreEqual(0, fixture.Output.Calls);
        Assert.AreEqual(RecordingState.Idle, fixture.Session.State);
    }

    [TestMethod]
    public async Task Task_toggle_keeps_text_replay_isolated_from_task_capture()
    {
        string? captured = null;
        using var fixture = SessionFixture.Create(
            taskCaptured: (text, _) =>
            {
                captured = text;
                return Task.CompletedTask;
            });

        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        fixture.Speech.Text = "task";
        await fixture.Session.PressAsync(DictationTrigger.TaskHotkey, fixture.Target);
        await fixture.Session.PressAsync(DictationTrigger.TaskHotkey, fixture.Target);
        await fixture.Session.ReplayLastAsync(fixture.Target, CancellationToken.None);

        Assert.AreEqual("task", captured);
        Assert.AreEqual("hello", fixture.Output.LastText);
        Assert.AreEqual(2, fixture.Speech.Calls);
        Assert.AreEqual(2, fixture.Output.Calls);
    }

    [TestMethod]
    public async Task Updated_settings_enable_hold_without_recreating_the_session()
    {
        using var fixture = SessionFixture.Create(RecordingMode.Toggle);

        fixture.Session.UpdateSettings(AppSettings.Default with { Mode = RecordingMode.Hold });
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        Assert.AreEqual(RecordingState.Recording, fixture.Session.State);
        await fixture.Session.ReleaseAsync(DictationTrigger.PrimaryHotkey);

        Assert.AreEqual(RecordingState.Idle, fixture.Session.State);
    }

    [TestMethod]
    public async Task Hold_press_and_release_use_the_same_terminal_path()
    {
        using var fixture = SessionFixture.Create(RecordingMode.Hold);

        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        await fixture.Session.ReleaseAsync(DictationTrigger.PrimaryHotkey);

        Assert.AreEqual(RecordingState.Idle, fixture.Session.State);
        Assert.AreEqual(1, fixture.Audio.StartCalls);
        Assert.AreEqual(1, fixture.Audio.StopCalls);
    }

    [TestMethod]
    public async Task Duplicate_stop_and_stop_during_processing_deliver_once()
    {
        using var fixture = SessionFixture.Create();
        fixture.Speech.WaitForRelease = true;
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        fixture.Audio.AppendForTest(0.1f);

        Task first = fixture.Session.StopAsync(StopReason.User, CancellationToken.None);
        await fixture.Speech.Started.Task;
        Task second = fixture.Session.StopAsync(StopReason.User, CancellationToken.None);
        fixture.Speech.Release.SetResult();
        await Task.WhenAll(first, second);

        Assert.AreEqual(1, fixture.Audio.StopCalls);
        Assert.AreEqual(1, fixture.Speech.Calls);
        Assert.AreEqual(1, fixture.Output.Calls);
    }

    [TestMethod]
    public async Task Microphone_failure_returns_to_idle_without_output()
    {
        using var fixture = SessionFixture.Create();
        fixture.Audio.StartException = new InvalidOperationException();

        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);

        Assert.AreEqual(RecordingState.Idle, fixture.Session.State);
        CollectionAssert.Contains(fixture.Notices, new DictationNotice(DictationNoticeKind.MicrophoneUnavailable));
        Assert.AreEqual(0, fixture.Output.Calls);
    }

    [TestMethod]
    public async Task A_start_notice_is_published_when_the_session_ends_not_while_it_records()
    {
        using var fixture = SessionFixture.Create();
        var fallback = new DictationNotice(DictationNoticeKind.MicrophoneFallback);
        fixture.Audio.StartNotice = fallback;
        fixture.Output.Result = DeliveryResult.PasteSent;

        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        Assert.AreEqual(0, fixture.Notices.Count, "A notice during recording would replace the recording overlay.");
        await fixture.Session.StopAsync(StopReason.User, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { fallback }, fixture.Notices);
        Assert.AreEqual(1, fixture.Output.Calls);
    }

    [TestMethod]
    public async Task An_outcome_notice_replaces_the_start_notice()
    {
        using var fixture = SessionFixture.Create();
        fixture.Audio.StartNotice = new DictationNotice(DictationNoticeKind.MicrophoneFallback);
        fixture.Speech.Text = "  ";

        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        await fixture.Session.StopAsync(StopReason.User, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { new DictationNotice(DictationNoticeKind.NoSpeech) }, fixture.Notices);
    }

    [TestMethod]
    public async Task A_discarded_session_still_reports_its_start_notice_once()
    {
        using var fixture = SessionFixture.Create();
        var fallback = new DictationNotice(DictationNoticeKind.MicrophoneFallback);
        fixture.Audio.StartNotice = fallback;
        fixture.Output.Result = DeliveryResult.PasteSent;

        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        await fixture.Session.RequestCancelAsync(CancellationToken.None);
        await fixture.Session.ConfirmDiscardAsync(CancellationToken.None);
        fixture.Audio.StartNotice = null;
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        await fixture.Session.StopAsync(StopReason.User, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { fallback }, fixture.Notices, "The notice belongs to the session that started with it.");
    }

    [TestMethod]
    public async Task Updated_dictionary_applies_to_the_next_transcript()
    {
        using var fixture = SessionFixture.Create();
        fixture.Speech.Text = "Win Whisper";
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        await fixture.Session.StopAsync(StopReason.User, CancellationToken.None);
        Assert.AreEqual("Win Whisper", fixture.Output.LastText);

        Assert.IsTrue(PersonalDictionary.TryParse("Win Whisper = WinWispr", out PersonalDictionary dictionary, out _));
        fixture.Session.UpdateDictionary(dictionary);
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        await fixture.Session.StopAsync(StopReason.User, CancellationToken.None);

        Assert.AreEqual("WinWispr", fixture.Output.LastText);
    }

    [TestMethod]
    public async Task A_failed_mute_reports_its_message_and_keeps_the_capture_from_starting()
    {
        using var fixture = SessionFixture.Create(meeting: true);
        fixture.Meeting.MuteException = new MeetingMuteException("Teams meeting microphone is ambiguous");

        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);

        Assert.AreEqual(RecordingState.Idle, fixture.Session.State);
        Assert.AreEqual(0, fixture.Audio.StartCalls);
        CollectionAssert.AreEqual(new[] { "mute", "discard", "restore" }, fixture.Events);
        CollectionAssert.AreEqual(
            new[] { new DictationNotice(DictationNoticeKind.TeamsMicrophone, "Teams meeting microphone is ambiguous") },
            fixture.Notices);
    }

    [TestMethod]
    public async Task Start_mutes_the_meeting_before_the_capture_starts()
    {
        using var fixture = SessionFixture.Create(meeting: true);

        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);

        Assert.AreEqual(RecordingState.Recording, fixture.Session.State);
        CollectionAssert.AreEqual(new[] { "mute", "start" }, fixture.Events);
    }

    [TestMethod]
    public async Task A_failed_capture_start_gives_the_meeting_back()
    {
        using var fixture = SessionFixture.Create(meeting: true);
        fixture.Audio.StartException = new InvalidOperationException();

        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);

        Assert.AreEqual(RecordingState.Idle, fixture.Session.State);
        CollectionAssert.AreEqual(new[] { "mute", "start", "discard", "restore" }, fixture.Events);
        CollectionAssert.AreEqual(
            new[] { new DictationNotice(DictationNoticeKind.MicrophoneUnavailable) }, fixture.Notices);
    }

    [TestMethod]
    public async Task A_start_cancelled_after_the_mute_gives_the_meeting_back_without_capturing()
    {
        using var fixture = SessionFixture.Create(meeting: true);
        using var cancellation = new CancellationTokenSource();
        fixture.Meeting.WaitForMute = true;

        Task start = fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target, cancellation.Token);
        await fixture.Meeting.MuteEntered.Task;
        cancellation.Cancel();
        fixture.Meeting.ReleaseMute.SetResult();
        await start;

        Assert.AreEqual(RecordingState.Idle, fixture.Session.State);
        Assert.AreEqual(0, fixture.Audio.StartCalls);
        CollectionAssert.AreEqual(new[] { "mute", "discard", "restore" }, fixture.Events);
        Assert.AreEqual(0, fixture.Notices.Count);
    }

    [TestMethod]
    public async Task Stop_gives_the_meeting_back_before_recognition()
    {
        using var fixture = SessionFixture.Create(meeting: true);
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);

        await fixture.Session.StopAsync(StopReason.User);

        CollectionAssert.AreEqual(
            new[] { "mute", "start", "stop", "restore", "recognize", "deliver" }, fixture.Events);
    }

    [TestMethod]
    public async Task Discarding_the_recording_gives_the_meeting_back()
    {
        using var fixture = SessionFixture.Create(meeting: true);
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        await fixture.Session.RequestCancelAsync(CancellationToken.None);

        await fixture.Session.ConfirmDiscardAsync(CancellationToken.None);

        Assert.AreEqual(RecordingState.Idle, fixture.Session.State);
        CollectionAssert.AreEqual(new[] { "mute", "start", "pause", "discard", "restore" }, fixture.Events);
    }

    [TestMethod]
    public async Task Pause_and_resume_keep_the_meeting_muted()
    {
        using var fixture = SessionFixture.Create(meeting: true);
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);

        await fixture.Session.RequestCancelAsync(CancellationToken.None);
        await fixture.Session.CancelTimeoutAsync(CancellationToken.None);

        Assert.AreEqual(RecordingState.Recording, fixture.Session.State);
        CollectionAssert.AreEqual(new[] { "mute", "start", "pause", "resume" }, fixture.Events);
    }

    [TestMethod]
    public async Task A_failed_pause_gives_the_meeting_back()
    {
        using var fixture = SessionFixture.Create(meeting: true);
        fixture.Audio.PauseException = new InvalidOperationException();
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);

        await fixture.Session.RequestCancelAsync(CancellationToken.None);

        Assert.AreEqual(RecordingState.Idle, fixture.Session.State);
        CollectionAssert.AreEqual(new[] { "mute", "start", "pause", "discard", "restore" }, fixture.Events);
    }

    [TestMethod]
    public async Task A_failed_resume_gives_the_meeting_back()
    {
        using var fixture = SessionFixture.Create(meeting: true);
        fixture.Audio.ResumeException = new InvalidOperationException();
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        await fixture.Session.RequestCancelAsync(CancellationToken.None);

        await fixture.Session.CancelTimeoutAsync(CancellationToken.None);

        Assert.AreEqual(RecordingState.Idle, fixture.Session.State);
        CollectionAssert.AreEqual(
            new[] { "mute", "start", "pause", "resume", "discard", "restore" }, fixture.Events);
    }

    [TestMethod]
    public async Task A_failed_stop_gives_the_meeting_back_without_recognition()
    {
        using var fixture = SessionFixture.Create(meeting: true);
        fixture.Audio.StopException = new InvalidOperationException();
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);

        await fixture.Session.StopAsync(StopReason.User);

        Assert.AreEqual(RecordingState.Idle, fixture.Session.State);
        CollectionAssert.AreEqual(new[] { "mute", "start", "stop", "discard", "restore" }, fixture.Events);
        CollectionAssert.AreEqual(
            new[] { new DictationNotice(DictationNoticeKind.MicrophoneUnavailable) }, fixture.Notices);
    }

    [TestMethod]
    public async Task A_restore_warning_is_published_and_the_dictation_is_still_delivered()
    {
        var warning = new DictationNotice(
            DictationNoticeKind.TeamsMicrophone, "Check Teams microphone; automatic restore was not confirmed");
        using var fixture = SessionFixture.Create(meeting: true);
        fixture.Meeting.RestoreWarning = warning;
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);

        await fixture.Session.StopAsync(StopReason.User);

        Assert.AreEqual(1, fixture.Output.Calls);
        CollectionAssert.AreEqual(new[] { warning }, fixture.Notices);
    }

    [TestMethod]
    public async Task Release_after_dispose_discards_a_live_capture_and_gives_the_meeting_back()
    {
        using var fixture = SessionFixture.Create(meeting: true);
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);

        fixture.Session.Dispose();
        await fixture.Session.ReleaseAsync();

        Assert.AreEqual(1, fixture.Audio.DiscardCalls);
        CollectionAssert.AreEqual(new[] { "mute", "start", "discard", "restore" }, fixture.Events);
    }

    [TestMethod]
    public async Task Release_is_harmless_when_nothing_is_running()
    {
        using var fixture = SessionFixture.Create(meeting: true);

        await fixture.Session.ReleaseAsync();

        Assert.AreEqual(RecordingState.Idle, fixture.Session.State);
        CollectionAssert.AreEqual(new[] { "discard", "restore" }, fixture.Events);
        Assert.AreEqual(0, fixture.Notices.Count);
    }

    [TestMethod]
    public async Task Pause_failure_discards_the_session()
    {
        using var fixture = SessionFixture.Create();
        fixture.Audio.PauseException = new InvalidOperationException();
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);

        await fixture.Session.RequestCancelAsync(CancellationToken.None);

        Assert.AreEqual(RecordingState.Idle, fixture.Session.State);
        Assert.AreEqual(1, fixture.Audio.DiscardCalls);
        CollectionAssert.Contains(fixture.Notices, new DictationNotice(DictationNoticeKind.MicrophoneUnavailable));
    }

    [TestMethod]
    public async Task Shutdown_drops_processing_result_before_delivery()
    {
        using var fixture = SessionFixture.Create();
        fixture.Speech.WaitForRelease = true;
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        Task stop = fixture.Session.StopAsync(StopReason.User, CancellationToken.None);
        await fixture.Speech.Started.Task;

        fixture.Session.Dispose();
        fixture.Speech.Release.SetResult();
        await stop;

        Assert.AreEqual(0, fixture.Output.Calls);
    }

    [TestMethod]
    public async Task Dispose_during_pending_start_discards_capture_and_never_leaves_recording()
    {
        using var fixture = SessionFixture.Create();
        fixture.Audio.WaitForStart = true;

        Task start = fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        await fixture.Audio.StartEntered.Task;

        fixture.Session.Dispose();
        fixture.Audio.ReleaseStart.SetResult();
        await start;

        Assert.AreEqual(RecordingState.Idle, fixture.Session.State);
        Assert.AreEqual(1, fixture.Audio.DiscardCalls);
    }

    [TestMethod]
    public async Task Caller_cancellation_during_pending_start_discards_capture_without_error()
    {
        using var fixture = SessionFixture.Create();
        using var cancellation = new CancellationTokenSource();
        fixture.Audio.WaitForStart = true;

        Task start = fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target, cancellation.Token);
        await fixture.Audio.StartEntered.Task;

        cancellation.Cancel();
        fixture.Audio.ReleaseStart.SetResult();
        await start;

        Assert.AreEqual(RecordingState.Idle, fixture.Session.State);
        Assert.AreEqual(1, fixture.Audio.StartCalls);
        Assert.AreEqual(1, fixture.Audio.DiscardCalls);
        Assert.AreEqual(0, fixture.Notices.Count);
    }

    [TestMethod]
    public async Task Recognition_failure_and_no_speech_never_deliver_text()
    {
        using var failure = SessionFixture.Create();
        failure.Speech.Exception = new InvalidOperationException();
        await failure.Session.PressAsync(DictationTrigger.PrimaryHotkey, failure.Target);
        await failure.Session.StopAsync(StopReason.User, CancellationToken.None);
        Assert.AreEqual(0, failure.Output.Calls);
        CollectionAssert.Contains(failure.Notices, new DictationNotice(DictationNoticeKind.TranscriptionFailed));

        using var empty = SessionFixture.Create();
        empty.Speech.Text = "  ";
        await empty.Session.PressAsync(DictationTrigger.PrimaryHotkey, empty.Target);
        await empty.Session.StopAsync(StopReason.User, CancellationToken.None);
        Assert.AreEqual(0, empty.Output.Calls);
        CollectionAssert.Contains(empty.Notices, new DictationNotice(DictationNoticeKind.NoSpeech));
    }

    [TestMethod]
    public async Task Limit_uses_processing_path_and_notifies_once()
    {
        using var fixture = SessionFixture.Create();
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        await fixture.Session.StopAsync(StopReason.Limit, CancellationToken.None);

        CollectionAssert.Contains(fixture.Notices, new DictationNotice(DictationNoticeKind.RecordingLimitReached));
        Assert.AreEqual(RecordingState.Idle, fixture.Session.State);
    }

    [TestMethod]
    public async Task Replay_uses_processed_text_and_new_target_without_recognition()
    {
        using var fixture = SessionFixture.Create();
        fixture.Speech.Text = "Win Whisper neuer Absatz fertig";
        fixture.Session.UpdateSettings(AppSettings.Default with { VoiceCommandsEnabled = true });
        Assert.IsTrue(PersonalDictionary.TryParse("Win Whisper = WinWispr", out PersonalDictionary dictionary, out _));
        fixture.Session.UpdateDictionary(dictionary);
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        await fixture.Session.StopAsync(StopReason.User, CancellationToken.None);
        fixture.Session.UpdateSettings(AppSettings.Default);
        var newTarget = new WindowTarget(9876);
        await fixture.Session.ReplayLastAsync(newTarget, CancellationToken.None);
        Assert.AreEqual("WinWispr\r\n\r\nfertig", fixture.Output.LastText);
        Assert.AreEqual(newTarget, fixture.Output.LastTarget);
        Assert.AreEqual(2, fixture.Output.Calls);
        Assert.AreEqual(1, fixture.Speech.Calls);
        Assert.AreEqual(1, fixture.Audio.StartCalls);
    }

    [TestMethod]
    public async Task Clipboard_fallback_notifies_for_dictation_and_replay()
    {
        using var fixture = SessionFixture.Create();
        fixture.Output.Result = DeliveryResult.CopiedToClipboard;
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        await fixture.Session.StopAsync(StopReason.User, CancellationToken.None);
        await fixture.Session.ReplayLastAsync(fixture.Target, CancellationToken.None);

        var expectedNotice = new DictationNotice(DictationNoticeKind.CopiedToClipboard);
        CollectionAssert.AreEqual(new[] { expectedNotice, expectedNotice }, fixture.Notices);
        Assert.AreEqual(2, fixture.Output.Calls);
    }

    [TestMethod]
    public async Task A_target_in_menu_mode_reports_its_own_notice()
    {
        using var fixture = SessionFixture.Create();
        fixture.Output.Result = DeliveryResult.TargetMenuOpen;
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        await fixture.Session.StopAsync(StopReason.User, CancellationToken.None);

        CollectionAssert.AreEqual(new[] { new DictationNotice(DictationNoticeKind.TargetMenuOpen) }, fixture.Notices);
    }

    [TestMethod]
    public async Task Sent_paste_stays_silent_for_dictation_and_replay()
    {
        using var fixture = SessionFixture.Create();
        fixture.Output.Result = DeliveryResult.PasteSent;
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        await fixture.Session.StopAsync(StopReason.User, CancellationToken.None);
        await fixture.Session.ReplayLastAsync(fixture.Target, CancellationToken.None);

        Assert.AreEqual(0, fixture.Notices.Count);
        Assert.AreEqual(2, fixture.Output.Calls);
        Assert.AreEqual(RecordingState.Idle, fixture.Session.State);
    }

    [TestMethod]
    public async Task Replay_without_text_or_while_recording_or_after_dispose_does_nothing()
    {
        using var fixture = SessionFixture.Create();
        await fixture.Session.ReplayLastAsync(fixture.Target, CancellationToken.None);
        CollectionAssert.Contains(fixture.Notices, new DictationNotice(DictationNoticeKind.NothingToReplay));
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        await fixture.Session.ReplayLastAsync(fixture.Target, CancellationToken.None);
        Assert.AreEqual(0, fixture.Output.Calls);
        await fixture.Session.StopAsync(StopReason.User, CancellationToken.None);
        fixture.Session.Dispose();
        await fixture.Session.ReplayLastAsync(fixture.Target, CancellationToken.None);
        Assert.AreEqual(1, fixture.Output.Calls);
    }

    [TestMethod]
    public async Task Failed_delivery_can_be_retried_and_concurrent_replay_is_ignored()
    {
        using var fixture = SessionFixture.Create();
        fixture.Output.Exception = new InvalidOperationException();
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        await fixture.Session.StopAsync(StopReason.User, CancellationToken.None);
        fixture.Output.Exception = null;
        fixture.Output.WaitForRelease = true;
        Task first = fixture.Session.ReplayLastAsync(fixture.Target, CancellationToken.None);
        await fixture.Output.Started.Task;
        await fixture.Session.ReplayLastAsync(fixture.Target, CancellationToken.None);
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        fixture.Output.Release.SetResult();
        await first;
        Assert.AreEqual(2, fixture.Output.Calls);
        Assert.AreEqual(1, fixture.Audio.StartCalls);
        Assert.AreEqual(RecordingState.Idle, fixture.Session.State);
    }

    [TestMethod]
    public async Task Empty_recording_does_not_replace_last_result_and_shutdown_cancels_replay()
    {
        using var fixture = SessionFixture.Create();
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        await fixture.Session.StopAsync(StopReason.User, CancellationToken.None);
        fixture.Speech.Text = " ";
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        await fixture.Session.StopAsync(StopReason.User, CancellationToken.None);
        fixture.Output.WaitForRelease = true;
        Task replay = fixture.Session.ReplayLastAsync(fixture.Target, CancellationToken.None);
        await fixture.Output.Started.Task;
        Assert.AreEqual("hello", fixture.Output.LastText);
        fixture.Session.Dispose();
        await replay;
        Assert.AreEqual(RecordingState.Idle, fixture.Session.State);
        await fixture.Session.ReplayLastAsync(fixture.Target, CancellationToken.None);
        Assert.AreEqual(2, fixture.Output.Calls);
    }

    [TestMethod]
    public async Task Final_success_and_failure_notices_follow_idle_so_the_overlay_stays_visible()
    {
        foreach (bool failRecognition in new[] { false, true })
        {
            using var fixture = SessionFixture.Create();
            fixture.Output.Result = DeliveryResult.CopiedToClipboard;
            if (failRecognition)
                fixture.Speech.Exception = new InvalidOperationException();
            var events = new List<string>();
            fixture.Session.StateChanged += state => events.Add(state.ToString());
            fixture.Session.Notice += notice => events.Add(notice.Kind.ToString());

            await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
            await fixture.Session.StopAsync(StopReason.User, CancellationToken.None);

            CollectionAssert.AreEqual(new[]
            {
                "Starting", "Recording", "Processing", "Idle", failRecognition ? "TranscriptionFailed" : "CopiedToClipboard"
            }, events);
        }
    }

    [TestMethod]
    public async Task Toggle_press_by_the_owner_stops_and_hold_release_is_ignored()
    {
        using var fixture = SessionFixture.Create(RecordingMode.Toggle);

        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        await fixture.Session.ReleaseAsync(DictationTrigger.PrimaryHotkey);
        Assert.AreEqual(RecordingState.Recording, fixture.Session.State);
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);

        Assert.AreEqual(RecordingState.Idle, fixture.Session.State);
        Assert.AreEqual(1, fixture.Output.Calls);
    }

    [TestMethod]
    public async Task Hold_session_ignores_release_of_another_trigger()
    {
        using var fixture = SessionFixture.Create(settings: AppSettings.Default with
        {
            Mode = RecordingMode.Hold,
            TaskHotkeyMode = RecordingMode.Hold
        });

        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        await fixture.Session.ReleaseAsync(DictationTrigger.TaskHotkey);
        Assert.AreEqual(RecordingState.Recording, fixture.Session.State);
        await fixture.Session.ReleaseAsync(DictationTrigger.PrimaryHotkey);

        Assert.AreEqual(RecordingState.Idle, fixture.Session.State);
        Assert.AreEqual(1, fixture.Audio.StopCalls);
    }

    [TestMethod]
    public async Task Release_processed_before_the_start_reaches_the_gate_never_captures()
    {
        using var fixture = SessionFixture.Create(RecordingMode.Hold);
        Task queuedStart = Task.CompletedTask;
        Task release = Task.CompletedTask;
        bool raced = false;
        fixture.Session.StateChanged += state =>
        {
            // Idle is published while the command gate is still held, so this press
            // claims ownership but its start waits behind the gate.
            if (state != RecordingState.Idle || raced)
                return;
            raced = true;
            queuedStart = fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
            release = fixture.Session.ReleaseAsync(DictationTrigger.PrimaryHotkey);
        };
        await fixture.Session.PressAsync(DictationTrigger.Tray, fixture.Target);
        await fixture.Session.PressAsync(DictationTrigger.Tray, fixture.Target);
        await Task.WhenAll(queuedStart, release);

        Assert.IsTrue(raced);
        Assert.AreEqual(RecordingState.Idle, fixture.Session.State);
        Assert.AreEqual(1, fixture.Audio.StartCalls);
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        Assert.AreEqual(RecordingState.Recording, fixture.Session.State);
        await fixture.Session.ReleaseAsync(DictationTrigger.PrimaryHotkey);
    }

    [TestMethod]
    public async Task Foreign_hotkeys_are_ignored_while_the_tray_stops_any_session()
    {
        using var fixture = SessionFixture.Create(settings: AppSettings.Default with
        {
            Mode = RecordingMode.Toggle,
            TaskHotkeyMode = RecordingMode.Hold
        });

        await fixture.Session.PressAsync(DictationTrigger.TaskHotkey, fixture.Target);
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        await fixture.Session.PressAsync(DictationTrigger.TaskButton, fixture.Target);
        Assert.AreEqual(RecordingState.Recording, fixture.Session.State);
        await fixture.Session.PressAsync(DictationTrigger.Tray);

        Assert.AreEqual(RecordingState.Idle, fixture.Session.State);
        Assert.AreEqual(1, fixture.Audio.StartCalls);
        Assert.AreEqual(1, fixture.TaskCaptures.Count);
        Assert.AreEqual(0, fixture.Output.Calls);
    }

    [TestMethod]
    public async Task Tray_is_always_toggle_even_when_hold_is_configured()
    {
        using var fixture = SessionFixture.Create(RecordingMode.Hold);

        await fixture.Session.PressAsync(DictationTrigger.Tray, fixture.Target);
        await fixture.Session.ReleaseAsync(DictationTrigger.Tray);
        Assert.AreEqual(RecordingState.Recording, fixture.Session.State);
        await fixture.Session.PressAsync(DictationTrigger.Tray, fixture.Target);

        Assert.AreEqual(RecordingState.Idle, fixture.Session.State);
        Assert.AreEqual(1, fixture.Output.Calls);
    }

    [TestMethod]
    public async Task Mode_is_fixed_when_the_trigger_is_pressed()
    {
        using var fixture = SessionFixture.Create(RecordingMode.Hold);

        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        fixture.Session.UpdateSettings(AppSettings.Default with { Mode = RecordingMode.Toggle });
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        Assert.AreEqual(RecordingState.Recording, fixture.Session.State);
        await fixture.Session.ReleaseAsync(DictationTrigger.PrimaryHotkey);

        Assert.AreEqual(RecordingState.Idle, fixture.Session.State);
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        await fixture.Session.ReleaseAsync(DictationTrigger.PrimaryHotkey);
        Assert.AreEqual(RecordingState.Recording, fixture.Session.State);
        await fixture.Session.StopAsync(StopReason.User);
    }

    [TestMethod]
    public async Task Press_while_loading_starts_on_ready_with_the_captured_target_and_destination()
    {
        using var fixture = SessionFixture.Create(readiness: SpeechReadiness.Loading);
        var pressTarget = new WindowTarget(4321);

        await fixture.Session.PressAsync(DictationTrigger.TaskHotkey, pressTarget);
        Assert.IsTrue(fixture.Session.HasPendingStart);
        Assert.AreEqual(0, fixture.Audio.StartCalls);
        CollectionAssert.AreEqual(new[] { SpeechReadiness.Loading }, fixture.Blocked);

        await fixture.Session.SetSpeechReadinessAsync(SpeechReadiness.Ready);
        Assert.AreEqual(RecordingState.Recording, fixture.Session.State);
        Assert.AreEqual(pressTarget, fixture.Session.Target);
        await fixture.Session.PressAsync(DictationTrigger.TaskHotkey);

        Assert.AreEqual(1, fixture.TaskCaptures.Count);
        Assert.AreEqual(0, fixture.Output.Calls);
        Assert.IsFalse(fixture.Session.HasPendingStart);
    }

    [TestMethod]
    public async Task Hold_release_while_loading_drops_the_pending_start()
    {
        using var fixture = SessionFixture.Create(RecordingMode.Hold, readiness: SpeechReadiness.Loading);

        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        await fixture.Session.ReleaseAsync(DictationTrigger.PrimaryHotkey);
        await fixture.Session.SetSpeechReadinessAsync(SpeechReadiness.Ready);

        Assert.AreEqual(RecordingState.Idle, fixture.Session.State);
        Assert.AreEqual(0, fixture.Audio.StartCalls);
        Assert.IsFalse(fixture.Session.HasPendingStart);
    }

    [TestMethod]
    public async Task Held_task_button_starts_on_ready_and_stops_on_release()
    {
        using var fixture = SessionFixture.Create(
            settings: AppSettings.Default with { TaskButtonMode = RecordingMode.Hold },
            readiness: SpeechReadiness.Loading);

        await fixture.Session.PressAsync(DictationTrigger.TaskButton, fixture.Target);
        await fixture.Session.SetSpeechReadinessAsync(SpeechReadiness.Ready);
        Assert.AreEqual(RecordingState.Recording, fixture.Session.State);
        await fixture.Session.ReleaseAsync(DictationTrigger.TaskButton);

        Assert.AreEqual(RecordingState.Idle, fixture.Session.State);
        Assert.AreEqual(1, fixture.TaskCaptures.Count);
    }

    [TestMethod]
    [DataRow((int)SpeechReadiness.Switching)]
    [DataRow((int)SpeechReadiness.Missing)]
    [DataRow((int)SpeechReadiness.Failed)]
    public async Task Unready_speech_blocks_the_start_and_drops_a_pending_start(int value)
    {
        var readiness = (SpeechReadiness)value;
        using var blocked = SessionFixture.Create(readiness: readiness);
        await blocked.Session.PressAsync(DictationTrigger.PrimaryHotkey, blocked.Target);
        CollectionAssert.AreEqual(new[] { readiness }, blocked.Blocked);
        Assert.AreEqual(0, blocked.Audio.StartCalls);

        using var pending = SessionFixture.Create(readiness: SpeechReadiness.Loading);
        await pending.Session.PressAsync(DictationTrigger.PrimaryHotkey, pending.Target);
        await pending.Session.SetSpeechReadinessAsync(readiness);
        await pending.Session.SetSpeechReadinessAsync(SpeechReadiness.Ready);
        Assert.AreEqual(RecordingState.Idle, pending.Session.State);
        Assert.AreEqual(0, pending.Audio.StartCalls);
        await pending.Session.PressAsync(DictationTrigger.PrimaryHotkey, pending.Target);
        Assert.AreEqual(RecordingState.Recording, pending.Session.State);
        await pending.Session.StopAsync(StopReason.User);
    }

    [TestMethod]
    public async Task Stop_and_tray_drop_a_pending_start()
    {
        using var fixture = SessionFixture.Create(readiness: SpeechReadiness.Loading);

        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        await fixture.Session.StopAsync(StopReason.User);
        Assert.IsFalse(fixture.Session.HasPendingStart);
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        await fixture.Session.PressAsync(DictationTrigger.Tray);
        Assert.IsFalse(fixture.Session.HasPendingStart);
        await fixture.Session.SetSpeechReadinessAsync(SpeechReadiness.Ready);

        Assert.AreEqual(0, fixture.Audio.StartCalls);
    }

    [TestMethod]
    public async Task Existing_task_draft_is_resumed_instead_of_recording()
    {
        using var fixture = SessionFixture.Create();
        fixture.HasTaskDraft = true;

        await fixture.Session.PressAsync(DictationTrigger.TaskButton, fixture.Target);
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);

        Assert.AreEqual(1, fixture.DraftResumes);
        Assert.AreEqual(RecordingState.Recording, fixture.Session.State);
        await fixture.Session.StopAsync(StopReason.User);
        Assert.AreEqual(1, fixture.Output.Calls);
    }

    [TestMethod]
    public async Task Press_without_target_uses_the_foreground_window()
    {
        using var fixture = SessionFixture.Create();

        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey);
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey);

        Assert.AreEqual(fixture.Foreground, fixture.Output.LastTarget);
    }

    [TestMethod]
    public async Task Replay_is_ignored_while_a_start_is_pending()
    {
        using var fixture = SessionFixture.Create();
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        await fixture.Session.SetSpeechReadinessAsync(SpeechReadiness.Loading);
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);

        await fixture.Session.ReplayLastAsync(fixture.Target, CancellationToken.None);

        Assert.AreEqual(1, fixture.Output.Calls);
        Assert.IsTrue(fixture.Session.HasPendingStart);
    }

    // ---- Dictation translation: the English dictation hotkey ----

    private const string German = "Guten Morgen zusammen";

    [TestMethod]
    public async Task The_session_is_active_from_the_accepted_press_to_the_return_to_idle()
    {
        using var fixture = SessionFixture.Create(RecordingMode.Toggle);
        Assert.IsFalse(fixture.Session.IsActive);

        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        Assert.IsTrue(fixture.Session.IsActive);

        await fixture.Session.RequestCancelAsync(CancellationToken.None);
        Assert.AreEqual(RecordingState.CancelPending, fixture.Session.State);
        Assert.IsTrue(fixture.Session.IsActive);
        await fixture.Session.CancelTimeoutAsync(CancellationToken.None);

        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        Assert.IsFalse(fixture.Session.IsActive);
    }

    [TestMethod]
    public async Task A_pending_start_while_the_speech_model_loads_counts_as_active()
    {
        using var fixture = SessionFixture.Create(RecordingMode.Hold, readiness: SpeechReadiness.Loading);

        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        Assert.AreEqual(RecordingState.Idle, fixture.Session.State, "a pending start has not recorded yet");
        Assert.IsTrue(fixture.Session.IsActive);

        await fixture.Session.ReleaseAsync(DictationTrigger.PrimaryHotkey);
        Assert.IsFalse(fixture.Session.IsActive);
    }

    [TestMethod]
    public async Task A_refused_press_does_not_make_the_session_active()
    {
        using var fixture = SessionFixture.Create(readiness: SpeechReadiness.Missing);

        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);

        Assert.IsFalse(fixture.Session.IsActive);
    }

    [TestMethod]
    public async Task English_dictation_translates_the_transcript_and_pastes_the_english_text()
    {
        using var fixture = SessionFixture.Create();
        fixture.Speech.Text = German;
        fixture.Translator.Answer = _ => new TranslationResult.Translated("Good morning everyone", TranslationDirection.GermanToEnglish);

        await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);
        await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);

        CollectionAssert.AreEqual(new[] { German }, fixture.Translator.Inputs);
        Assert.AreEqual("Good morning everyone", fixture.Output.LastText);
        Assert.AreEqual(fixture.Target, fixture.Output.LastTarget);
        Assert.AreEqual(1, fixture.Output.Calls);
        Assert.AreEqual(0, fixture.Notices.Count);
        Assert.AreEqual(RecordingState.Idle, fixture.Session.State);
    }

    [TestMethod]
    public async Task The_personal_dictionary_rewrites_the_german_transcript_before_the_translator_sees_it()
    {
        using var fixture = SessionFixture.Create();
        Assert.IsTrue(PersonalDictionary.TryParse("Win Wisper = WinWispr", out PersonalDictionary dictionary, out _));
        fixture.Session.UpdateDictionary(dictionary);
        fixture.Speech.Text = "Ich nutze Win Wisper jeden Tag";
        fixture.Translator.Answer = text => new TranslationResult.Translated(
            text.Replace("Ich nutze", "I use").Replace("jeden Tag", "every day"));

        await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);
        await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);

        CollectionAssert.AreEqual(new[] { "Ich nutze WinWispr jeden Tag" }, fixture.Translator.Inputs);
        Assert.AreEqual("I use WinWispr every day", fixture.Output.LastText);
    }

    [TestMethod]
    public async Task An_english_transcript_is_delivered_unchanged()
    {
        using var fixture = SessionFixture.Create();
        fixture.Speech.Text = "Good morning everyone";
        fixture.Translator.Answer = text => new TranslationResult.Translated(text, null, Unchanged: true);

        await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);
        await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);

        Assert.AreEqual("Good morning everyone", fixture.Output.LastText);
        Assert.AreEqual(0, fixture.Notices.Count);
    }

    [TestMethod]
    public async Task An_empty_transcript_reports_no_speech_and_does_not_call_the_translator()
    {
        using var fixture = SessionFixture.Create();
        fixture.Speech.Text = "   ";

        await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);
        await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);

        CollectionAssert.AreEqual(new[] { new DictationNotice(DictationNoticeKind.NoSpeech) }, fixture.Notices);
        Assert.AreEqual(0, fixture.Translator.Inputs.Count);
        Assert.AreEqual(0, fixture.Output.Calls);
    }

    [TestMethod]
    public async Task Without_an_installed_translation_the_press_reports_it_and_never_records()
    {
        using var fixture = SessionFixture.Create(meeting: true);
        fixture.Translator.Installed = false;

        await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);

        CollectionAssert.AreEqual(new[] { new DictationNotice(DictationNoticeKind.TranslationNotInstalled) }, fixture.Notices);
        Assert.AreEqual(RecordingState.Idle, fixture.Session.State);
        Assert.AreEqual(0, fixture.Audio.StartCalls);
        CollectionAssert.AreEqual(Array.Empty<string>(), fixture.Events);
        Assert.AreEqual(0, fixture.Translator.Preloads);
        Assert.IsFalse(fixture.Session.HasPendingStart);

        // The session is not stuck: the other hotkeys still work.
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        Assert.AreEqual(RecordingState.Recording, fixture.Session.State);
        await fixture.Session.StopAsync(StopReason.User);
    }

    [TestMethod]
    public async Task Without_an_installed_translation_the_press_is_reported_even_while_the_speech_model_loads()
    {
        using var fixture = SessionFixture.Create(readiness: SpeechReadiness.Loading);
        fixture.Translator.Installed = false;

        await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);

        Assert.AreEqual(DictationNoticeKind.TranslationNotInstalled, fixture.Notices.Single().Kind);
        Assert.IsFalse(fixture.Session.HasPendingStart);
        Assert.AreEqual(0, fixture.Blocked.Count);
    }

    [TestMethod]
    public async Task A_session_whose_translation_is_removed_during_recording_still_ends_with_the_failure_copy()
    {
        using var fixture = SessionFixture.Create();
        fixture.Speech.Text = German;
        fixture.Translator.Answer = _ => new TranslationResult.NotInstalled();

        await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);
        await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);

        CollectionAssert.AreEqual(new[] { German }, fixture.Clipboard);
        Assert.AreEqual(DictationNoticeKind.TranslationFailedTranscriptCopied, fixture.Notices.Single().Kind);
        Assert.AreEqual(0, fixture.Output.Calls);
    }

    [TestMethod]
    public async Task A_failed_timed_out_or_throwing_translation_copies_the_german_transcript_and_pastes_nothing()
    {
        Func<string, TranslationResult>[] answers =
        [
            _ => new TranslationResult.Failed("The translation failed (InvalidOperationException)."),
            _ => new TranslationResult.TimedOut(TimeSpan.FromSeconds(5)),
            _ => throw new InvalidOperationException("engine exploded"),
            _ => new TranslationResult.TooLong(450)
        ];
        foreach (Func<string, TranslationResult> answer in answers)
        {
            using var fixture = SessionFixture.Create();
            fixture.Speech.Text = German;
            fixture.Translator.Answer = answer;

            await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);
            await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);

            CollectionAssert.AreEqual(new[] { German }, fixture.Clipboard);
            CollectionAssert.AreEqual(
                new[] { new DictationNotice(DictationNoticeKind.TranslationFailedTranscriptCopied) }, fixture.Notices);
            Assert.AreEqual(0, fixture.Output.Calls);
            Assert.AreEqual(RecordingState.Idle, fixture.Session.State);
        }
    }

    [TestMethod]
    public async Task A_translation_failure_with_an_unusable_clipboard_reports_the_clipboard_problem()
    {
        using var fixture = SessionFixture.Create();
        fixture.Speech.Text = German;
        fixture.Translator.Answer = _ => new TranslationResult.Failed("x");
        fixture.ClipboardException = new IOException();

        await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);
        await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);

        CollectionAssert.AreEqual(
            new[] { new DictationNotice(DictationNoticeKind.ClipboardUnavailable) }, fixture.Notices);
        Assert.AreEqual(0, fixture.Output.Calls);
    }

    [TestMethod]
    public async Task A_failed_translation_is_not_remembered_as_the_last_dictation()
    {
        using var fixture = SessionFixture.Create();
        fixture.Translator.Answer = _ => new TranslationResult.Failed("x");

        await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);
        await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);
        fixture.Notices.Clear();
        await fixture.Session.ReplayLastAsync(fixture.Target, CancellationToken.None);

        Assert.AreEqual(DictationNoticeKind.NothingToReplay, fixture.Notices.Single().Kind);
    }

    [TestMethod]
    public async Task Replay_inserts_the_translated_text_again()
    {
        using var fixture = SessionFixture.Create();
        fixture.Translator.Answer = _ => new TranslationResult.Translated("Hello there");

        await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);
        await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);
        await fixture.Session.ReplayLastAsync(fixture.Target, CancellationToken.None);

        Assert.AreEqual(2, fixture.Output.Calls);
        Assert.AreEqual("Hello there", fixture.Output.LastText);
    }

    [TestMethod]
    public async Task Y_inside_the_cancel_window_discards_before_recognition_and_translation()
    {
        using var fixture = SessionFixture.Create();
        await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);
        await fixture.Session.RequestCancelAsync(CancellationToken.None);

        await fixture.Session.ConfirmDiscardAsync(CancellationToken.None);

        Assert.AreEqual(RecordingState.Idle, fixture.Session.State);
        Assert.AreEqual(0, fixture.Speech.Calls);
        Assert.AreEqual(0, fixture.Translator.Inputs.Count);
        Assert.AreEqual(0, fixture.Output.Calls);
    }

    [TestMethod]
    public async Task Esc_and_Y_after_the_cancel_window_do_not_stop_the_translation()
    {
        using var fixture = SessionFixture.Create();
        fixture.Speech.Text = German;
        fixture.Translator.WaitForRelease = true;
        fixture.Translator.Answer = _ => new TranslationResult.Translated("Good morning everyone");
        await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);
        Task stop = fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);
        await fixture.Translator.Started.Task;
        Assert.AreEqual(RecordingState.Processing, fixture.Session.State);

        await fixture.Session.RequestCancelAsync(CancellationToken.None);
        await fixture.Session.ConfirmDiscardAsync(CancellationToken.None);
        Assert.IsFalse(fixture.Translator.CancellationRequested);
        fixture.Translator.Release.SetResult();
        await stop;

        Assert.AreEqual("Good morning everyone", fixture.Output.LastText);
        Assert.AreEqual(1, fixture.Output.Calls);
        Assert.AreEqual(RecordingState.Idle, fixture.Session.State);
    }

    [TestMethod]
    public async Task The_translation_runs_to_completion_after_a_cancel_window_that_timed_out()
    {
        using var fixture = SessionFixture.Create();
        fixture.Translator.Answer = _ => new TranslationResult.Translated("Hello");
        await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);
        await fixture.Session.RequestCancelAsync(CancellationToken.None);
        await fixture.Session.CancelTimeoutAsync(CancellationToken.None);
        await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);

        Assert.AreEqual("Hello", fixture.Output.LastText);
    }

    [TestMethod]
    public async Task The_translation_model_starts_loading_at_the_press_and_before_the_recording_starts()
    {
        using var fixture = SessionFixture.Create();

        await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);

        Assert.AreEqual(1, fixture.Translator.Preloads);
        Assert.AreEqual(RecordingState.Recording, fixture.Session.State);
        Assert.AreEqual("preload", fixture.Events[0]);
        await fixture.Session.StopAsync(StopReason.User);
        Assert.AreEqual(1, fixture.Translator.Preloads);
    }

    [TestMethod]
    public async Task A_pending_start_also_preloads_the_translation_and_translates_when_it_starts()
    {
        using var fixture = SessionFixture.Create(readiness: SpeechReadiness.Loading);
        fixture.Speech.Text = German;
        fixture.Translator.Answer = _ => new TranslationResult.Translated("Good morning everyone");

        await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);

        Assert.IsTrue(fixture.Session.HasPendingStart);
        Assert.AreEqual(1, fixture.Translator.Preloads);
        Assert.AreEqual(0, fixture.Audio.StartCalls);

        await fixture.Session.SetSpeechReadinessAsync(SpeechReadiness.Ready);
        Assert.AreEqual(RecordingState.Recording, fixture.Session.State);
        await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);

        Assert.AreEqual("Good morning everyone", fixture.Output.LastText);
    }

    [TestMethod]
    public async Task Other_triggers_do_not_preload_the_translation()
    {
        using var fixture = SessionFixture.Create();

        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        await fixture.Session.StopAsync(StopReason.User);
        await fixture.Session.PressAsync(DictationTrigger.TaskHotkey, fixture.Target);
        await fixture.Session.StopAsync(StopReason.User);

        Assert.AreEqual(0, fixture.Translator.Preloads);
        Assert.AreEqual(0, fixture.Translator.Inputs.Count);
    }

    [TestMethod]
    public async Task Other_triggers_use_no_translation_even_when_the_transcript_is_german()
    {
        using var fixture = SessionFixture.Create();
        fixture.Speech.Text = German;

        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);

        Assert.AreEqual(German, fixture.Output.LastText);
        Assert.AreEqual(0, fixture.Translator.Inputs.Count);
    }

    [TestMethod]
    public async Task English_dictation_holds_by_default_and_ends_on_release()
    {
        using var fixture = SessionFixture.Create(settings: AppSettings.Default);

        await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);
        Assert.AreEqual(RecordingState.Recording, fixture.Session.State);
        // A second press is not a stop in Hold mode.
        await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);
        Assert.AreEqual(RecordingState.Recording, fixture.Session.State);
        await fixture.Session.ReleaseAsync(DictationTrigger.EnglishDictationHotkey);

        Assert.AreEqual(RecordingState.Idle, fixture.Session.State);
        Assert.AreEqual(1, fixture.Audio.StopCalls);
        Assert.AreEqual(1, fixture.Output.Calls);
    }

    [TestMethod]
    public async Task English_dictation_uses_its_own_toggle_mode_independent_of_the_primary_mode()
    {
        AppSettings settings = AppSettings.Default with { Mode = RecordingMode.Hold, EnglishDictationMode = RecordingMode.Toggle };
        using var fixture = SessionFixture.Create(settings: settings);

        await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);
        await fixture.Session.ReleaseAsync(DictationTrigger.EnglishDictationHotkey);
        Assert.AreEqual(RecordingState.Recording, fixture.Session.State);
        await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);

        Assert.AreEqual(RecordingState.Idle, fixture.Session.State);
        Assert.AreEqual(1, fixture.Output.Calls);
    }

    [TestMethod]
    public async Task The_mode_is_fixed_at_the_press_and_settings_changes_apply_from_the_next_session()
    {
        AppSettings hold = AppSettings.Default with { EnglishDictationMode = RecordingMode.Hold };
        using var fixture = SessionFixture.Create(settings: hold);
        await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);

        fixture.Session.UpdateSettings(hold with { EnglishDictationMode = RecordingMode.Toggle });
        await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);
        Assert.AreEqual(RecordingState.Recording, fixture.Session.State);
        await fixture.Session.ReleaseAsync(DictationTrigger.EnglishDictationHotkey);
        Assert.AreEqual(RecordingState.Idle, fixture.Session.State);

        await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);
        await fixture.Session.ReleaseAsync(DictationTrigger.EnglishDictationHotkey);
        Assert.AreEqual(RecordingState.Recording, fixture.Session.State);
        await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);
        Assert.AreEqual(RecordingState.Idle, fixture.Session.State);
    }

    [TestMethod]
    public async Task While_english_dictation_owns_the_session_other_triggers_are_ignored()
    {
        AppSettings settings = AppSettings.Default with { EnglishDictationMode = RecordingMode.Toggle };
        using var fixture = SessionFixture.Create(settings: settings);
        await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);

        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        await fixture.Session.PressAsync(DictationTrigger.TaskHotkey, fixture.Target);
        await fixture.Session.PressAsync(DictationTrigger.TaskButton, fixture.Target);
        await fixture.Session.ReleaseAsync(DictationTrigger.PrimaryHotkey);
        await fixture.Session.ReleaseAsync(DictationTrigger.TaskHotkey);

        Assert.AreEqual(RecordingState.Recording, fixture.Session.State);
        Assert.AreEqual(1, fixture.Audio.StartCalls);
        Assert.AreEqual(0, fixture.Audio.StopCalls);
        await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);
        Assert.AreEqual(1, fixture.Output.Calls);
        Assert.AreEqual(0, fixture.TaskCaptures.Count);
    }

    [TestMethod]
    public async Task The_english_hotkey_is_ignored_while_another_trigger_owns_the_session()
    {
        using var fixture = SessionFixture.Create(RecordingMode.Toggle);
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);

        await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);
        await fixture.Session.ReleaseAsync(DictationTrigger.EnglishDictationHotkey);

        Assert.AreEqual(RecordingState.Recording, fixture.Session.State);
        Assert.AreEqual(0, fixture.Translator.Preloads);
        Assert.AreEqual(0, fixture.Notices.Count);
        await fixture.Session.StopAsync(StopReason.User);
        Assert.AreEqual(0, fixture.Translator.Inputs.Count);
    }

    [TestMethod]
    public async Task The_english_hotkey_press_while_not_installed_does_not_disturb_a_running_session()
    {
        using var fixture = SessionFixture.Create(RecordingMode.Toggle);
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        fixture.Translator.Installed = false;

        await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);

        Assert.AreEqual(0, fixture.Notices.Count);
        Assert.AreEqual(RecordingState.Recording, fixture.Session.State);
    }

    [TestMethod]
    public async Task A_hold_release_while_the_speech_model_loads_drops_the_pending_english_start()
    {
        using var fixture = SessionFixture.Create(settings: AppSettings.Default, readiness: SpeechReadiness.Loading);

        await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);
        await fixture.Session.ReleaseAsync(DictationTrigger.EnglishDictationHotkey);
        await fixture.Session.SetSpeechReadinessAsync(SpeechReadiness.Ready);

        Assert.IsFalse(fixture.Session.HasPendingStart);
        Assert.AreEqual(RecordingState.Idle, fixture.Session.State);
        Assert.AreEqual(0, fixture.Audio.StartCalls);
    }

    [TestMethod]
    public async Task The_tray_can_stop_an_english_dictation_and_the_result_is_still_translated()
    {
        using var fixture = SessionFixture.Create();
        fixture.Translator.Answer = _ => new TranslationResult.Translated("Hello");
        await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);

        await fixture.Session.PressAsync(DictationTrigger.Tray, fixture.Target);

        Assert.AreEqual("Hello", fixture.Output.LastText);
    }

    [TestMethod]
    public async Task The_next_session_after_english_dictation_is_not_translated()
    {
        using var fixture = SessionFixture.Create(RecordingMode.Toggle);
        fixture.Speech.Text = German;
        fixture.Translator.Answer = _ => new TranslationResult.Translated("Good morning");
        await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);
        await fixture.Session.ReleaseAsync(DictationTrigger.EnglishDictationHotkey);
        await fixture.Session.StopAsync(StopReason.User);

        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);
        await fixture.Session.PressAsync(DictationTrigger.PrimaryHotkey, fixture.Target);

        Assert.AreEqual(German, fixture.Output.LastText);
        Assert.AreEqual(1, fixture.Translator.Inputs.Count);
    }

    [TestMethod]
    public async Task The_meeting_stays_muted_only_for_the_capture_and_translation_runs_after_the_restore()
    {
        using var fixture = SessionFixture.Create(meeting: true);
        fixture.Translator.Answer = _ => new TranslationResult.Translated("Hello");

        await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);
        await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);

        CollectionAssert.AreEqual(
            new[] { "preload", "mute", "start", "stop", "restore", "recognize", "translate", "deliver" },
            fixture.Events);
    }

    [TestMethod]
    public async Task Disposing_during_the_translation_ends_without_a_paste_or_a_notice()
    {
        using var fixture = SessionFixture.Create();
        fixture.Translator.WaitForRelease = true;
        await fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);
        Task stop = fixture.Session.PressAsync(DictationTrigger.EnglishDictationHotkey, fixture.Target);
        await fixture.Translator.Started.Task;

        fixture.Session.Dispose();
        await stop;

        Assert.AreEqual(0, fixture.Output.Calls);
        Assert.AreEqual(0, fixture.Clipboard.Count);
    }

    [TestMethod]
    public async Task English_dictation_without_translation_effects_never_records()
    {
        // A session built without translation effects (for example in older tests) treats translation as missing.
        var effects = new DictationEffects(
            (_, _) => Task.FromResult<DictationNotice?>(null),
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => Task.FromResult(CapturedAudio.FromArray([0.1f])),
            () => Task.CompletedTask,
            (_, _) => Task.FromResult("x"),
            (_, _, _) => Task.FromResult(DeliveryResult.PasteSent));
        using var session = new DictationSession(AppSettings.Default, effects);
        var notices = new List<DictationNotice>();
        session.Notice += notices.Add;

        await session.PressAsync(DictationTrigger.EnglishDictationHotkey, new WindowTarget(1));

        Assert.AreEqual(DictationNoticeKind.TranslationNotInstalled, notices.Single().Kind);
        Assert.AreEqual(RecordingState.Idle, session.State);
    }

    private sealed class SessionFixture : IDisposable
    {
        private readonly List<string> _events;

        private SessionFixture(
            DictationSession session,
            FakeAudio audio,
            FakeSpeech speech,
            FakeOutput output,
            FakeMeeting meeting,
            FakeTranslator translator,
            List<string> events)
        {
            Session = session;
            Audio = audio;
            Speech = speech;
            Output = output;
            Meeting = meeting;
            Translator = translator;
            _events = events;
            session.Notice += Notices.Add;
            session.StartBlocked += Blocked.Add;
        }

        public static SessionFixture Create(
            RecordingMode mode = RecordingMode.Toggle,
            Func<string, CancellationToken, Task>? taskCaptured = null,
            AppSettings? settings = null,
            SpeechReadiness readiness = SpeechReadiness.Ready,
            bool meeting = false)
        {
            var audio = new FakeAudio();
            var speech = new FakeSpeech();
            var output = new FakeOutput();
            var meetingFake = new FakeMeeting();
            var translator = new FakeTranslator();
            var events = new List<string>();
            void Log(string name)
            {
                lock (events)
                    events.Add(name);
            }

            Func<CancellationToken, Task>? muteMeeting = null;
            Func<Task<DictationNotice?>>? restoreMeeting = null;
            if (meeting)
            {
                muteMeeting = async cancellationToken =>
                {
                    Log("mute");
                    await meetingFake.MuteAsync(cancellationToken);
                };
                restoreMeeting = async () =>
                {
                    Log("restore");
                    return await meetingFake.RestoreAsync();
                };
            }

            SessionFixture? fixture = null;
            var effects = new DictationEffects(
                async (microphone, cancellationToken) =>
                {
                    Log("start");
                    await audio.StartAsync(microphone, cancellationToken);
                    return audio.StartNotice;
                },
                async cancellationToken =>
                {
                    Log("pause");
                    await audio.PauseAsync(cancellationToken);
                },
                async cancellationToken =>
                {
                    Log("resume");
                    await audio.ResumeAsync(cancellationToken);
                },
                async cancellationToken =>
                {
                    Log("stop");
                    return await audio.StopAsync(cancellationToken);
                },
                async () =>
                {
                    Log("discard");
                    await audio.DiscardAsync();
                },
                async (samples, cancellationToken) =>
                {
                    Log("recognize");
                    return await speech.RecognizeAsync(samples, cancellationToken);
                },
                async (target, text, cancellationToken) =>
                {
                    Log("deliver");
                    return await output.DeliverAsync(target, text, cancellationToken);
                })
            {
                TaskCapturedAsync = taskCaptured ?? ((string text, CancellationToken _) =>
                {
                    fixture!.TaskCaptures.Add(text);
                    return Task.CompletedTask;
                }),
                TryResumeTaskDraft = () =>
                {
                    if (!fixture!.HasTaskDraft)
                        return false;
                    fixture!.DraftResumes++;
                    return true;
                },
                CurrentForegroundWindow = () => fixture!.Foreground,
                MuteMeetingAsync = muteMeeting,
                RestoreMeetingAsync = restoreMeeting,
                IsTranslationInstalled = () => translator.Installed,
                PreloadTranslation = () =>
                {
                    Log("preload");
                    translator.Preloads++;
                },
                TranslateToEnglishAsync = async (text, cancellationToken) =>
                {
                    Log("translate");
                    return await translator.TranslateAsync(text, cancellationToken);
                },
                CopyTranscriptAsync = (text, _) =>
                {
                    if (fixture!.ClipboardException is { } failure)
                        throw failure;
                    fixture!.Clipboard.Add(text);
                    return Task.CompletedTask;
                }
            };
            fixture = new SessionFixture(
                new DictationSession(
                    settings ?? new AppSettings(mode, AppSettings.Default.Hotkey, null)
                        with { EnglishDictationMode = RecordingMode.Toggle },
                    effects,
                    readiness),
                audio,
                speech,
                output,
                meetingFake,
                translator,
                events);
            return fixture;
        }

        public DictationSession Session { get; }
        public FakeAudio Audio { get; }
        public FakeSpeech Speech { get; }
        public FakeOutput Output { get; }
        public FakeMeeting Meeting { get; }
        public FakeTranslator Translator { get; }
        public List<string> Clipboard { get; } = [];
        public Exception? ClipboardException { get; set; }
        public WindowTarget Target { get; } = new((nint)1234);
        public WindowTarget Foreground { get; } = new((nint)5678);
        public List<DictationNotice> Notices { get; } = [];
        public List<SpeechReadiness> Blocked { get; } = [];
        public List<string> TaskCaptures { get; } = [];
        public bool HasTaskDraft { get; set; }
        public int DraftResumes { get; set; }

        /// <summary>The effects the session ran, in order.</summary>
        public string[] Events
        {
            get
            {
                lock (_events)
                    return [.. _events];
            }
        }

        public void Dispose() => Session.Dispose();
    }

    private sealed class FakeAudio
    {
        private readonly List<float> _samples = [];
        private bool _paused;

        public int StartCalls { get; private set; }
        public int PauseCalls { get; private set; }
        public int ResumeCalls { get; private set; }
        public int StopCalls { get; private set; }
        public int DiscardCalls { get; private set; }
        public Exception? StartException { get; set; }
        /// <summary>What the capture start reports, for example a microphone fallback.</summary>
        public DictationNotice? StartNotice { get; set; }
        public Exception? PauseException { get; set; }
        public Exception? ResumeException { get; set; }
        public Exception? StopException { get; set; }
        public bool WaitForStart { get; set; }
        public TaskCompletionSource StartEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseStart { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void AppendForTest(params float[] samples)
        {
            if (!_paused)
                _samples.AddRange(samples);
        }

        public async Task StartAsync(string? _, CancellationToken cancellationToken)
        {
            StartCalls++;
            StartEntered.TrySetResult();
            if (StartException is not null)
                throw StartException;
            if (WaitForStart)
                await ReleaseStart.Task;
        }

        public Task PauseAsync(CancellationToken cancellationToken)
        {
            PauseCalls++;
            if (PauseException is not null)
                throw PauseException;
            _paused = true;
            return Task.CompletedTask;
        }

        public Task ResumeAsync(CancellationToken cancellationToken)
        {
            ResumeCalls++;
            if (ResumeException is not null)
                throw ResumeException;
            _paused = false;
            return Task.CompletedTask;
        }

        public Task<CapturedAudio> StopAsync(CancellationToken cancellationToken)
        {
            StopCalls++;
            if (StopException is not null)
                throw StopException;
            return Task.FromResult(CapturedAudio.FromArray(_samples.ToArray()));
        }

        public Task DiscardAsync() {
            DiscardCalls++;
            _samples.Clear();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeSpeech
    {
        public int Calls { get; private set; }
        public float[] LastSamples { get; private set; } = [];
        public string Text { get; set; } = "hello";
        public Exception? Exception { get; set; }
        public bool WaitForRelease { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<string> RecognizeAsync(CapturedAudio samples, CancellationToken cancellationToken)
        {
            Calls++;
            LastSamples = samples.Chunks.SelectMany(chunk => chunk).ToArray();
            if (Exception is not null)
                throw Exception;
            if (WaitForRelease)
            {
                Started.SetResult();
                await Release.Task;
            }

            return Text;
        }
    }

    private sealed class FakeOutput
    {
        public int Calls { get; private set; }

        public string? LastText { get; private set; }
        public WindowTarget LastTarget { get; private set; }
        public DeliveryResult Result { get; set; } = DeliveryResult.PasteSent;
        public Exception? Exception { get; set; }
        public bool WaitForRelease { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<DeliveryResult> DeliverAsync(WindowTarget target, string text, CancellationToken cancellationToken)
        {
            Calls++;
            LastText = text;
            LastTarget = target;
            if (Exception is not null)
                throw Exception;
            if (WaitForRelease)
            {
                Started.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
            }
            return Result;
        }
    }

    private sealed class FakeTranslator
    {
        public bool Installed { get; set; } = true;
        public int Preloads { get; set; }
        public List<string> Inputs { get; } = [];
        public Func<string, TranslationResult> Answer { get; set; } =
            text => new TranslationResult.Translated("translated: " + text, TranslationDirection.GermanToEnglish);
        public bool WaitForRelease { get; set; }
        public bool CancellationRequested { get; private set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<TranslationResult> TranslateAsync(string text, CancellationToken cancellationToken)
        {
            Inputs.Add(text);
            if (WaitForRelease)
            {
                Started.TrySetResult();
                using CancellationTokenRegistration registration = cancellationToken.Register(() => CancellationRequested = true);
                await Release.Task.WaitAsync(cancellationToken);
            }

            return Answer(text);
        }
    }

    private sealed class FakeMeeting
    {
        public Exception? MuteException { get; set; }
        public DictationNotice? RestoreWarning { get; set; }
        public bool WaitForMute { get; set; }
        public TaskCompletionSource MuteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseMute { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task MuteAsync(CancellationToken cancellationToken)
        {
            MuteEntered.TrySetResult();
            if (MuteException is not null)
                throw MuteException;
            if (WaitForMute)
                await ReleaseMute.Task;
        }

        public Task<DictationNotice?> RestoreAsync() => Task.FromResult(RestoreWarning);
    }
}
