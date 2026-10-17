using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using WinWispr.Core;
using WinWispr.Interop;

namespace WinWispr.Tests.Interop;

[TestClass]
public sealed class TeamsMicrophoneTests
{
    [TestMethod]
    public async Task Timing_failure_does_not_lose_mute_ownership()
    {
        bool muted = false;
        int toggles = 0;
        var service = new TeamsMicrophone(
            () => new TeamsMicrophone.Target(() => muted, () => { muted = !muted; toggles++; }),
            (_, _) => throw new IOException("Diagnostics unavailable"));
        await service.BeginAsync(CancellationToken.None);
        Assert.IsTrue(muted);
        Assert.IsNull(await service.EndAsync());
        Assert.IsFalse(muted);
        Assert.AreEqual(2, toggles);
    }

    [TestMethod]
    public async Task Unconfirmed_restore_returns_a_teams_warning()
    {
        bool? muted = false;
        var service = new TeamsMicrophone(
            () => new TeamsMicrophone.Target(() => muted, () => muted = muted is null ? null : !muted.Value));
        await service.BeginAsync(CancellationToken.None);
        Assert.AreEqual(true, muted);
        muted = null;

        DictationNotice? warning = await service.EndAsync();

        Assert.AreEqual(
            new DictationNotice(DictationNoticeKind.TeamsMicrophone, "Check Teams microphone; automatic restore was not confirmed"),
            warning);
    }

    [TestMethod]
    public async Task Discovery_is_repeated_after_a_previous_negative_result()
    {
        bool meeting = false;
        bool muted = false;
        int discoveries = 0;
        var phases = new List<WinWispr.Diagnostics.TechnicalEvent>();
        var target = new TeamsMicrophone.Target(() => muted, () => muted = !muted);
        var service = new TeamsMicrophone(
            () => { discoveries++; return meeting ? target : null; },
            (phase, elapsed) => { Assert.IsTrue(elapsed >= 0); phases.Add(phase); });
        await service.BeginAsync(CancellationToken.None);
        Assert.IsNull(await service.EndAsync());
        meeting = true;
        await service.BeginAsync(CancellationToken.None);
        Assert.IsTrue(muted);
        Assert.IsNull(await service.EndAsync());
        Assert.IsFalse(muted);
        Assert.AreEqual(2, discoveries);
        Assert.AreEqual(2, phases.Count(phase => phase == WinWispr.Diagnostics.TechnicalEvent.TeamsDiscovery));
        Assert.AreEqual(1, phases.Count(phase => phase == WinWispr.Diagnostics.TechnicalEvent.TeamsMuteConfirmation));
    }

    [TestMethod]
    public async Task Slow_discovery_aborts_dictation_before_audio_starts()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new TeamsMicrophone(
            () =>
            {
                entered.SetResult();
                release.Task.GetAwaiter().GetResult();
                finished.SetResult();
                return null;
            });

        Stopwatch started = Stopwatch.StartNew();
        Task begin = service.BeginAsync(CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<MeetingMuteException>(() => begin);
        started.Stop();

        release.SetResult();
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.IsTrue(started.Elapsed < TimeSpan.FromSeconds(1), $"Discovery blocked startup for {started.Elapsed}.");
    }

    [TestMethod]
    public async Task Stale_cached_meeting_does_not_block_a_new_dictation()
    {
        bool available = true;
        bool muted = false;
        var target = new TeamsMicrophone.Target(() => available ? muted : null, () => muted = !muted);
        var service = new TeamsMicrophone(() => available ? target : null);

        await service.BeginAsync(CancellationToken.None);
        Assert.IsNull(await service.EndAsync());
        available = false;

        await service.BeginAsync(CancellationToken.None);
        Assert.IsFalse(muted);
    }

    [TestMethod]
    public async Task Restore_waits_for_a_mute_that_is_still_being_confirmed()
    {
        using var toggleEntered = new ManualResetEventSlim();
        using var releaseToggle = new ManualResetEventSlim();
        bool muted = false;
        var target = new TeamsMicrophone.Target(() => muted, () =>
        {
            toggleEntered.Set();
            releaseToggle.Wait(TimeSpan.FromSeconds(5));
            muted = !muted;
        });
        var service = new TeamsMicrophone(() => target);

        Task begin = service.BeginAsync(CancellationToken.None);
        Assert.IsTrue(toggleEntered.Wait(TimeSpan.FromSeconds(5)));
        Task<DictationNotice?> end = service.EndAsync();
        await Task.Delay(100);
        Assert.IsFalse(end.IsCompleted, "The restore must wait for the mute it is going to undo.");

        releaseToggle.Set();
        await begin.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsNull(await end.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsFalse(muted, "The restore ran before the mute was owned and left the meeting muted.");
    }

    [TestMethod]
    public void Focus_is_restored_only_when_the_toggle_activated_Teams()
    {
        Assert.IsTrue(TeamsMicrophone.ShouldRestoreFocus(10, 20, 20));
        Assert.IsFalse(TeamsMicrophone.ShouldRestoreFocus(10, 30, 20)); // User chose another app.
        Assert.IsFalse(TeamsMicrophone.ShouldRestoreFocus(10, 10, 20));
        Assert.IsFalse(TeamsMicrophone.ShouldRestoreFocus(20, 20, 20));
        Assert.IsFalse(TeamsMicrophone.ShouldRestoreFocus(0, 20, 20));
        Assert.IsFalse(TeamsMicrophone.ShouldRestoreFocus(10, 0, 0));
    }

    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    public async Task Teams_mute_and_restore_preserve_Notepad_focus_and_selection()
    {
        if (Environment.GetEnvironmentVariable("WINWISPR_TEAMS_FOCUS_TEST") != "1")
            Assert.Inconclusive("Requires an interactive Notepad document and an unmuted Teams test meeting.");
        await Task.Run(async () =>
        {
            using Process notepad = Process.GetProcessesByName("Notepad").Single();
            AutomationElement window = AutomationElement.RootElement.FindFirst(TreeScope.Children,
                new PropertyCondition(AutomationElement.ProcessIdProperty, notepad.Id));
            Assert.IsNotNull(window);
            nint handle = window.Current.NativeWindowHandle;
            SetForegroundWindow(handle);
            await Task.Delay(200);
            Assert.AreEqual(handle, GetForegroundWindow());
            AutomationElement editor = AutomationElement.FocusedElement;
            Assert.AreEqual(notepad.Id, editor.Current.ProcessId);
            var text = (TextPattern)editor.GetCurrentPattern(TextPattern.Pattern);
            var selection = text.GetSelection();
            int[] focusedId = editor.GetRuntimeId();
            TeamsMicrophone.Target target = TeamsMicrophone.FindMeeting()
                ?? throw new AssertFailedException("No Teams meeting.");
            Assert.AreEqual(false, target.ReadMuted(), "This focus test requires an initially unmuted test meeting.");
            var teams = new TeamsMicrophone();
            void AssertEditorFocus()
            {
                Assert.AreEqual(handle, GetForegroundWindow());
                CollectionAssert.AreEqual(focusedId, AutomationElement.FocusedElement.GetRuntimeId());
                var currentSelection = text.GetSelection();
                Assert.AreEqual(selection.Length, currentSelection.Length);
                for (int index = 0; index < selection.Length; index++)
                {
                    Assert.AreEqual(0, selection[index].CompareEndpoints(
                        System.Windows.Automation.Text.TextPatternRangeEndpoint.Start, currentSelection[index],
                        System.Windows.Automation.Text.TextPatternRangeEndpoint.Start));
                    Assert.AreEqual(0, selection[index].CompareEndpoints(
                        System.Windows.Automation.Text.TextPatternRangeEndpoint.End, currentSelection[index],
                        System.Windows.Automation.Text.TextPatternRangeEndpoint.End));
                }
            }
            try
            {
                for (int cycle = 0; cycle < 2; cycle++)
                {
                    await teams.BeginAsync(CancellationToken.None);
                    Assert.AreEqual(true, target.ReadMuted());
                    AssertEditorFocus();
                    Assert.IsNull(await teams.EndAsync());
                    Assert.AreEqual(false, target.ReadMuted());
                    AssertEditorFocus();
                }
            }
            finally
            {
                await teams.EndAsync();
            }
        });
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint window);

    [TestMethod]
    public void Compact_view_of_the_same_meeting_uses_the_main_window_in_either_order()
    {
        foreach (string prefix in new[] { "Kompakte Besprechungsansicht | ", "Compact meeting view | " })
        {
            var compact = (9916, prefix + "Test2 | Microsoft Teams");
            var main = (9916, "Test2 | Microsoft Teams");
            Assert.AreEqual(1, TeamsMicrophone.SelectMeetingWindow([compact, main]));
            Assert.AreEqual(0, TeamsMicrophone.SelectMeetingWindow([main, compact]));
            Assert.AreEqual(0, TeamsMicrophone.SelectMeetingWindow([compact]));
        }
        Assert.AreEqual(-1, TeamsMicrophone.SelectMeetingWindow([]));
    }

    [TestMethod]
    public void Different_meetings_or_processes_remain_ambiguous()
    {
        Assert.Throws<MeetingMuteException>(() => TeamsMicrophone.SelectMeetingWindow([
            (9916, "Test2 | Microsoft Teams"), (9916, "Test3 | Microsoft Teams")]));
        Assert.Throws<MeetingMuteException>(() => TeamsMicrophone.SelectMeetingWindow([
            (9916, "Test2 | Microsoft Teams"), (9999, "Kompakte Besprechungsansicht | Test2 | Microsoft Teams")]));
        Assert.Throws<MeetingMuteException>(() => TeamsMicrophone.SelectMeetingWindow([
            (9916, "Test2 | Microsoft Teams"), (9916, "Kompakte Besprechungsansicht | Test3 | Microsoft Teams")]));
    }

    [TestMethod]
    public async Task Only_a_confirmed_owned_mute_is_restored_once()
    {
        foreach (bool initiallyMuted in new[] { false, true })
        {
            bool muted = initiallyMuted;
            int toggles = 0;
            var target = new TeamsMicrophone.Target(() => muted, () => { muted = !muted; toggles++; });
            var service = new TeamsMicrophone(() => target);
            await service.BeginAsync(CancellationToken.None);
            Assert.IsTrue(muted);
            Assert.IsNull(await service.EndAsync());
            Assert.IsNull(await service.EndAsync());
            Assert.AreEqual(initiallyMuted, muted);
            Assert.AreEqual(initiallyMuted ? 0 : 2, toggles);
        }
    }

    [TestMethod]
    public async Task Unknown_or_changed_meeting_never_causes_a_restore_toggle()
    {
        bool? state = null;
        int toggles = 0;
        var target = new TeamsMicrophone.Target(() => state, () => { state = !state; toggles++; });
        var service = new TeamsMicrophone(() => target);
        await Assert.ThrowsAsync<MeetingMuteException>(() => service.BeginAsync(CancellationToken.None));
        Assert.IsNull(await service.EndAsync());
        Assert.AreEqual(0, toggles);
        state = false;
        await service.BeginAsync(CancellationToken.None);
        state = null; // The saved meeting or microphone disappeared.
        DictationNotice? warning = await service.EndAsync();
        Assert.AreEqual(1, toggles);
        Assert.AreEqual(DictationNoticeKind.TeamsMicrophone, warning?.Kind);
    }

    [TestMethod]
    public async Task Manual_unmute_and_no_meeting_are_left_untouched()
    {
        bool muted = false;
        int toggles = 0;
        var target = new TeamsMicrophone.Target(() => muted, () => { muted = !muted; toggles++; });
        var service = new TeamsMicrophone(() => target);
        await service.BeginAsync(CancellationToken.None);
        muted = false;
        Assert.IsNull(await service.EndAsync());
        Assert.AreEqual(1, toggles);
        var absent = new TeamsMicrophone(() => null);
        await absent.BeginAsync(CancellationToken.None);
        Assert.IsNull(await absent.EndAsync());
    }

    [TestMethod]
    public void Only_known_microphone_labels_are_accepted()
    {
        Assert.AreEqual(false, TeamsMicrophone.ParseMuted("Mikrofon stummschalten"));
        Assert.AreEqual(true, TeamsMicrophone.ParseMuted("Mikrofon wieder aktivieren"));
        Assert.AreEqual(false, TeamsMicrophone.ParseMuted("Mute mic"));
        Assert.AreEqual(true, TeamsMicrophone.ParseMuted("Unmute mic"));
        Assert.IsNull(TeamsMicrophone.ParseMuted("Microphone unavailable"));
    }

    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    public async Task Teams_live_mute_and_restore_preserve_initial_state()
    {
        if (Environment.GetEnvironmentVariable("WINWISPR_TEAMS_LIVE_TEST") != "1")
            Assert.Inconclusive("Requires an explicitly enabled interactive Teams test meeting.");
        if (Environment.GetEnvironmentVariable("WINWISPR_TEAMS_REQUIRE_COMPACT") == "1")
        {
            bool compactVisible = await Task.Run(() => System.Windows.Automation.AutomationElement.RootElement
                .FindAll(System.Windows.Automation.TreeScope.Children, System.Windows.Automation.Condition.TrueCondition)
                .Cast<System.Windows.Automation.AutomationElement>()
                .Any(window => window.Current.Name.StartsWith("Kompakte Besprechungsansicht | ", StringComparison.Ordinal)
                    && !window.Current.IsOffscreen));
            Assert.IsTrue(compactVisible, "The compact Teams view must be visible for this regression check.");
        }
        TeamsMicrophone.Target target = await Task.Run(TeamsMicrophone.FindMeeting)
            ?? throw new AssertFailedException("No active Teams meeting found.");
        bool initiallyMuted = await Task.Run(target.ReadMuted)
            ?? throw new AssertFailedException("The initial Teams microphone state is unknown.");
        Console.WriteLine($"Teams live check: initially muted = {initiallyMuted}");
        var teams = new TeamsMicrophone();
        try
        {
            await teams.BeginAsync(CancellationToken.None);
            Assert.AreEqual(true, await Task.Run(target.ReadMuted));
            Assert.IsNull(await teams.EndAsync());
            Assert.AreEqual(initiallyMuted, await Task.Run(target.ReadMuted));
            if (!initiallyMuted)
                await Task.Run(target.Toggle);
            await Task.Delay(500);
            Assert.AreEqual(true, await Task.Run(target.ReadMuted));
            // A mute the user made is not owned, so it is left alone.
            await teams.BeginAsync(CancellationToken.None);
            Assert.IsNull(await teams.EndAsync());
            Assert.AreEqual(true, await Task.Run(target.ReadMuted));
        }
        finally
        {
            await teams.EndAsync();
            if (!initiallyMuted && await Task.Run(target.ReadMuted) == true)
                await Task.Run(target.Toggle);
        }
    }
}
