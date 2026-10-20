using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Core;
using WinWispr.Diagnostics;
using WinWispr.Interop;

namespace WinWispr.Tests.Interop;

[TestClass]
public sealed class WindowsTextOutputTests
{
    [TestMethod]
    public async Task Sent_paste_leaves_the_dictation_on_the_clipboard_even_if_cancelled()
    {
        using var cancellation = new CancellationTokenSource();
        string clipboard = "previous";
        var output = new WindowsTextOutput(() => 42,
            text => { clipboard = text; return Task.CompletedTask; },
            () =>
            {
                Assert.AreEqual("dictation", clipboard);
                cancellation.Cancel();
                return 4;
            });

        Assert.AreEqual(DeliveryResult.PasteSent,
            await output.DeliverAsync(new WindowTarget(42), "dictation", cancellation.Token));
        Assert.AreEqual("dictation", clipboard);
    }

    [TestMethod]
    public async Task Copy_puts_the_text_on_the_clipboard_without_sending_a_paste()
    {
        string clipboard = "previous";
        var output = new WindowsTextOutput(() => 42,
            text => { clipboard = text; return Task.CompletedTask; },
            () => { Assert.Fail("A copy must not paste."); return 0; });

        await output.CopyAsync("transcript", CancellationToken.None);

        Assert.AreEqual("transcript", clipboard);
    }

    [TestMethod]
    public async Task Copy_reports_an_unusable_clipboard_to_the_caller()
    {
        var output = new WindowsTextOutput(() => 42, _ => throw new InvalidOperationException(), () => 4);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => output.CopyAsync("transcript", CancellationToken.None));
    }

    [TestMethod]
    public async Task Busy_target_that_reads_the_clipboard_late_still_pastes_the_dictation()
    {
        // SendInput only queues Ctrl+V; a busy target can read the clipboard well after DeliverAsync returns.
        string clipboard = "previous";
        Task<string>? pastedByTarget = null;
        var output = new WindowsTextOutput(() => 42,
            text => { clipboard = text; return Task.CompletedTask; },
            () =>
            {
                pastedByTarget = Task.Delay(700).ContinueWith(_ => clipboard);
                return 4;
            });

        await output.DeliverAsync(new WindowTarget(42), "dictation", CancellationToken.None);

        Assert.AreEqual("dictation", await pastedByTarget!);
    }

    [TestMethod]
    [DataRow(42, 3)]
    [DataRow(99, 4)]
    [DataRow(0, 4)]
    public async Task Fallback_keeps_the_dictation_on_the_clipboard(int foreground, int inputCount)
    {
        string? clipboard = null;
        var output = new WindowsTextOutput(() => foreground,
            text => { clipboard = text; return Task.CompletedTask; },
            () => inputCount);
        Assert.AreEqual(DeliveryResult.CopiedToClipboard,
            await output.DeliverAsync(new WindowTarget(foreground == 0 ? 0 : 42), "text", CancellationToken.None));
        Assert.AreEqual("text", clipboard);
    }

    [TestMethod]
    public async Task Paste_waits_for_hotkey_release_and_rechecks_target()
    {
        int checks = 0;
        bool pasted = false;
        nint foreground = 42;
        var output = new WindowsTextOutput(() => foreground, _ => Task.CompletedTask,
            () => { pasted = true; return 4; }, () => ++checks == 1);
        Assert.AreEqual(DeliveryResult.PasteSent,
            await output.DeliverAsync(new WindowTarget(42), "text", CancellationToken.None));
        Assert.IsTrue(pasted);
        Assert.IsTrue(checks > 1);

        pasted = false;
        checks = 0;
        output = new WindowsTextOutput(() => foreground, _ => Task.CompletedTask,
            () => { pasted = true; return 4; }, () =>
            {
                if (++checks == 2)
                    foreground = 99;
                return checks == 1;
            });
        Assert.AreEqual(DeliveryResult.CopiedToClipboard,
            await output.DeliverAsync(new WindowTarget(42), "text", CancellationToken.None));
        Assert.IsFalse(pasted);
    }

    [TestMethod]
    public async Task Paste_waits_until_the_clipboard_has_been_closed_for_the_quiet_window()
    {
        // A monitor that opens the clipboard on every change makes a paste in that moment find it open and paste nothing.
        var time = new VirtualTime();
        TimeSpan? pastedAt = null;
        var output = new WindowsTextOutput(() => 42, _ => Task.CompletedTask,
            () => { pastedAt = time.Elapsed; return 4; },
            clipboardState: () => new ClipboardState(time.Elapsed < Ms(40) ? 7 : 0, 1),
            clock: time,
            delay: time.Delay);

        Assert.AreEqual(DeliveryResult.PasteSent,
            await output.DeliverAsync(new WindowTarget(42), "text", CancellationToken.None));
        Assert.AreEqual(Ms(30) + WindowsTextOutput.ClipboardQuietWindow, pastedAt, "Last seen open at 30 ms.");
    }

    [TestMethod]
    public async Task A_monitor_that_opens_the_clipboard_late_still_comes_before_the_paste()
    {
        // Ditto, for one, waits about 100 ms after a change before it reads the clipboard.
        var time = new VirtualTime();
        TimeSpan? pastedAt = null;
        var output = new WindowsTextOutput(() => 42, _ => Task.CompletedTask,
            () => { pastedAt = time.Elapsed; return 4; },
            clipboardState: () => new ClipboardState(time.Elapsed >= Ms(110) && time.Elapsed < Ms(150) ? 7 : 0, 1),
            clock: time,
            delay: time.Delay);

        await output.DeliverAsync(new WindowTarget(42), "text", CancellationToken.None);

        Assert.AreEqual(Ms(140) + WindowsTextOutput.ClipboardQuietWindow, pastedAt);
    }

    [TestMethod]
    public async Task A_clipboard_change_by_another_program_restarts_the_wait_and_is_traced()
    {
        var time = new VirtualTime();
        DeliveryTrace trace = DeliveryTrace.Begin();
        TimeSpan? pastedAt = null;
        var output = new WindowsTextOutput(() => 42, _ => Task.CompletedTask,
            () => { pastedAt = time.Elapsed; return 4; },
            clipboardState: () => new ClipboardState(0, time.Elapsed < Ms(50) ? 1u : 2u),
            clock: time,
            delay: time.Delay);

        await output.DeliverAsync(new WindowTarget(42), "text", CancellationToken.None);

        Assert.AreEqual(Ms(50) + WindowsTextOutput.ClipboardQuietWindow, pastedAt);
        Assert.IsTrue(trace.ClipboardChangedBeforePaste);
    }

    [TestMethod]
    public async Task A_clipboard_that_stays_open_falls_back_to_copy_without_a_paste()
    {
        var time = new VirtualTime();
        bool pasted = false;
        var output = new WindowsTextOutput(() => 42, _ => Task.CompletedTask,
            () => { pasted = true; return 4; },
            clipboardState: () => new ClipboardState(7, 1),
            clock: time,
            delay: time.Delay);

        Assert.AreEqual(DeliveryResult.CopiedToClipboard,
            await output.DeliverAsync(new WindowTarget(42), "text", CancellationToken.None));
        Assert.IsFalse(pasted);
        Assert.AreEqual(WindowsTextOutput.ClipboardSettleTimeout, time.Elapsed);
    }

    [TestMethod]
    public void A_delivery_gives_up_within_three_seconds_of_its_own()
    {
        // Each wait can last a whole 15.6 ms timer tick; the user's own hotkey hold comes on top.
        TimeSpan tick = TimeSpan.FromMilliseconds(16);
        TimeSpan settingGivesUp = (WindowsTextOutput.ClipboardOpenAttempts - 1) * Max(WindowsTextOutput.ClipboardRetryInterval, tick);
        TimeSpan settleGivesUp = WindowsTextOutput.ClipboardSettleTimeout + Max(WindowsTextOutput.ClipboardPollInterval, tick);

        Assert.IsTrue(settingGivesUp + settleGivesUp < TimeSpan.FromSeconds(3), $"{settingGivesUp} + {settleGivesUp}");
    }

    [TestMethod]
    public async Task Setting_the_clipboard_retries_only_while_another_program_holds_it_open()
    {
        int writes = 0;
        var delays = new List<TimeSpan>();

        ClipboardSet set = await WindowsTextOutput.SetClipboardAsync(
            text =>
            {
                Assert.AreEqual("dictation", text);
                return Task.FromResult(++writes < 4 ? null : (uint?)77);
            },
            "dictation",
            (delay, _) => { delays.Add(delay); return Task.CompletedTask; },
            CancellationToken.None);

        Assert.AreEqual(new ClipboardSet(4, 77), set);
        CollectionAssert.AreEqual(Enumerable.Repeat(WindowsTextOutput.ClipboardRetryInterval, 3).ToArray(), delays);
    }

    [TestMethod]
    public async Task A_set_that_fails_after_opening_the_clipboard_is_never_repeated()
    {
        // Setting again would be a second change, which every monitor answers by opening the clipboard again.
        int writes = 0;

        await Assert.ThrowsExactlyAsync<COMException>(() => WindowsTextOutput.SetClipboardAsync(
            _ => { writes++; throw new COMException("set failed"); },
            "dictation",
            (_, _) => Task.CompletedTask,
            CancellationToken.None));
        Assert.AreEqual(1, writes);
    }

    [TestMethod]
    public async Task A_clipboard_held_open_through_every_attempt_reports_busy_with_the_attempts()
    {
        int writes = 0;

        ClipboardBusyException busy = await Assert.ThrowsExactlyAsync<ClipboardBusyException>(
            () => WindowsTextOutput.SetClipboardAsync(
                _ => { writes++; return Task.FromResult<uint?>(null); },
                "dictation",
                (_, _) => Task.CompletedTask,
                CancellationToken.None));

        Assert.AreEqual(WindowsTextOutput.ClipboardOpenAttempts, busy.Attempts);
        Assert.AreEqual(WindowsTextOutput.ClipboardOpenAttempts, writes);
    }

    [TestMethod]
    public async Task Cancellation_stops_the_retries_between_attempts()
    {
        using var cancellation = new CancellationTokenSource();
        int writes = 0;

        await Assert.ThrowsAsync<OperationCanceledException>(() => WindowsTextOutput.SetClipboardAsync(
            _ => { writes++; cancellation.Cancel(); return Task.FromResult<uint?>(null); },
            "dictation",
            Task.Delay,
            cancellation.Token));
        Assert.AreEqual(1, writes);
    }

    [TestMethod]
    public async Task A_traced_delivery_records_its_attempts_target_paste_and_clipboard_holds()
    {
        var time = new VirtualTime();
        DeliveryTrace trace = DeliveryTrace.Begin();
        var output = new WindowsTextOutput(() => 42, _ => Task.CompletedTask, () => 4,
            clipboardState: () => new ClipboardState(time.Elapsed >= Ms(20) && time.Elapsed < Ms(50) ? 7 : 0, 1),
            clock: time,
            delay: time.Delay,
            processOf: window => window == 42 ? "notepad.exe" : "monitor.exe",
            watchAfterPaste: holds =>
            {
                // The target opens the clipboard to paste.
                holds.Observe(time.GetTimestamp(), 42);
                time.Advance(Ms(3));
                return Task.FromResult(holds.Finish(time.GetTimestamp()));
            });

        Assert.AreEqual(DeliveryResult.PasteSent,
            await output.DeliverAsync(new WindowTarget(42), "dictation", CancellationToken.None));

        Assert.AreEqual(1, trace.ClipboardSetAttempts);
        Assert.AreEqual("notepad.exe", trace.TargetProcess);
        Assert.AreEqual(190, trace.PasteAtMs, "Last seen open at 40 ms, then the quiet window.");
        Assert.IsFalse(trace.ClipboardChangedBeforePaste);
        CollectionAssert.AreEqual(
            new[] { new ClipboardHold("monitor.exe", 20, 30), new ClipboardHold("notepad.exe", 190, 3) },
            (await trace.ClipboardHolds!).ToArray());
    }

    [TestMethod]
    public async Task A_traced_delivery_without_a_paste_keeps_the_holds_seen_while_waiting()
    {
        var time = new VirtualTime();
        DeliveryTrace trace = DeliveryTrace.Begin();
        var output = new WindowsTextOutput(() => 99, _ => Task.CompletedTask, () => 4,
            clipboardState: () => new ClipboardState(time.Elapsed < Ms(30) ? 7 : 0, 1),
            clock: time,
            delay: time.Delay,
            processOf: _ => "monitor.exe",
            watchAfterPaste: _ => throw new AssertFailedException("Nothing is watched without a paste."));

        Assert.AreEqual(DeliveryResult.CopiedToClipboard,
            await output.DeliverAsync(new WindowTarget(42), "dictation", CancellationToken.None));

        Assert.IsNull(trace.PasteAtMs);
        CollectionAssert.AreEqual(new[] { new ClipboardHold("monitor.exe", 10, 20) }, (await trace.ClipboardHolds!).ToArray());
    }

    [TestMethod]
    public async Task A_traced_delivery_records_the_attempts_of_a_busy_clipboard()
    {
        DeliveryTrace trace = DeliveryTrace.Begin();
        var output = new WindowsTextOutput(() => 42, _ => throw new ClipboardBusyException(60), () => 4);

        await Assert.ThrowsExactlyAsync<ClipboardBusyException>(
            () => output.DeliverAsync(new WindowTarget(42), "dictation", CancellationToken.None));

        Assert.AreEqual(60, trace.ClipboardSetAttempts);
        Assert.IsNull(trace.ClipboardHolds);
    }

    [TestMethod]
    public async Task An_untraced_delivery_looks_up_no_process_and_watches_nothing()
    {
        // A selection translation's delivery is not measured.
        var time = new VirtualTime();
        var output = new WindowsTextOutput(() => 42, _ => Task.CompletedTask, () => 4,
            clipboardState: () => new ClipboardState(time.Elapsed < Ms(30) ? 7 : 0, 1),
            processOf: _ => throw new AssertFailedException("No process lookup without a trace."),
            watchAfterPaste: _ => throw new AssertFailedException("No watch without a trace."),
            clock: time,
            delay: time.Delay);

        Assert.AreEqual(DeliveryResult.PasteSent,
            await output.DeliverAsync(new WindowTarget(42), "text", CancellationToken.None));
    }

    [TestMethod]
    public async Task A_target_in_menu_mode_keeps_the_dictation_on_the_clipboard_without_a_paste()
    {
        // A menu bar that holds the keyboard swallows Ctrl+V; a notice is better than a silent loss.
        bool pasted = false;
        nint askedFor = 0;
        var output = new WindowsTextOutput(() => 42, _ => Task.CompletedTask,
            () => { pasted = true; return 4; },
            inMenuMode: window => { askedFor = window; return true; });

        Assert.AreEqual(DeliveryResult.TargetMenuOpen,
            await output.DeliverAsync(new WindowTarget(42), "text", CancellationToken.None));
        Assert.IsFalse(pasted);
        Assert.AreEqual((nint)42, askedFor);
    }

    [TestMethod]
    public void Dictation_clipboard_content_is_kept_out_of_history_roaming_and_monitors()
    {
        CollectionAssert.AreEquivalent(
            new[] { "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard", "ExcludeClipboardContentFromMonitorProcessing" },
            NativeClipboard.ExclusionFormats);
    }

    [TestMethod]
    public void Native_input_layout_matches_the_x64_win32_abi()
    {
        Type? input = typeof(NativeKeyboard).GetNestedType("INPUT", BindingFlags.NonPublic);

        Assert.IsNotNull(input);
        Assert.AreEqual(40, Marshal.SizeOf(input!));
    }

    [TestMethod]
    public async Task Clipboard_is_set_before_same_window_paste()
    {
        var calls = new List<string>();
        nint target = 42;
        var output = new WindowsTextOutput(
            getForegroundWindow: () => { calls.Add("foreground"); return target; },
            setClipboard: text => { calls.Add($"clipboard:{text}"); return Task.CompletedTask; },
            sendPaste: () => { calls.Add("paste"); return 4; });

        DeliveryResult result = await output.DeliverAsync(new WindowTarget(target), "Hallo world", CancellationToken.None);

        Assert.AreEqual(DeliveryResult.PasteSent, result);
        CollectionAssert.AreEqual(new[] { "clipboard:Hallo world", "foreground", "paste" }, calls);
    }

    [TestMethod]
    public async Task Changed_window_keeps_clipboard_and_does_not_send_input()
    {
        string? clipboard = null;
        int pasteCalls = 0;
        var output = new WindowsTextOutput(
            getForegroundWindow: () => 99,
            setClipboard: text => { clipboard = text; return Task.CompletedTask; },
            sendPaste: () => { pasteCalls++; return 4; });

        DeliveryResult result = await output.DeliverAsync(new WindowTarget(42), "kept", CancellationToken.None);

        Assert.AreEqual(DeliveryResult.CopiedToClipboard, result);
        Assert.AreEqual("kept", clipboard);
        Assert.AreEqual(0, pasteCalls);
    }

    [TestMethod]
    public async Task Empty_or_whitespace_text_does_not_touch_clipboard()
    {
        int clipboardCalls = 0;
        var output = new WindowsTextOutput(
            getForegroundWindow: () => 42,
            setClipboard: _ => { clipboardCalls++; return Task.CompletedTask; },
            sendPaste: () => 4);

        Assert.AreEqual(DeliveryResult.NoSpeech, await output.DeliverAsync(new WindowTarget(42), " ", CancellationToken.None));
        Assert.AreEqual(0, clipboardCalls);
    }

    [TestMethod]
    public async Task Zero_target_still_copies_without_foreground_check()
    {
        int foregroundCalls = 0;
        var output = new WindowsTextOutput(
            getForegroundWindow: () => { foregroundCalls++; return 42; },
            setClipboard: _ => Task.CompletedTask,
            sendPaste: () => 4);

        DeliveryResult result = await output.DeliverAsync(default, "kept", CancellationToken.None);

        Assert.AreEqual(DeliveryResult.CopiedToClipboard, result);
        Assert.AreEqual(0, foregroundCalls);
    }

    [TestMethod]
    public async Task Partial_send_input_falls_back_to_clipboard()
    {
        var output = new WindowsTextOutput(
            getForegroundWindow: () => 42,
            setClipboard: _ => Task.CompletedTask,
            sendPaste: () => 3);

        Assert.AreEqual(
            DeliveryResult.CopiedToClipboard,
            await output.DeliverAsync(new WindowTarget(42), "kept", CancellationToken.None));
    }

    [TestMethod]
    public async Task Paste_exception_after_commit_keeps_clipboard_and_returns_copy_result()
    {
        string? clipboard = null;
        var output = new WindowsTextOutput(
            getForegroundWindow: () => 42,
            setClipboard: text => { clipboard = text; return Task.CompletedTask; },
            sendPaste: () => throw new InvalidOperationException("SendInput failed"));

        DeliveryResult result = await output.DeliverAsync(new WindowTarget(42), "kept", CancellationToken.None);

        Assert.AreEqual(DeliveryResult.CopiedToClipboard, result);
        Assert.AreEqual("kept", clipboard);
    }

    [TestMethod]
    public async Task Clipboard_failure_is_returned_to_the_controller()
    {
        var output = new WindowsTextOutput(
            getForegroundWindow: () => 42,
            setClipboard: _ => Task.FromException(new InvalidOperationException("clipboard")),
            sendPaste: () => 4);

        try
        {
            await output.DeliverAsync(new WindowTarget(42), "text", CancellationToken.None);
            Assert.Fail("Clipboard failure should be returned to the caller.");
        }
        catch (InvalidOperationException)
        {
        }
    }

    [TestMethod]
    public async Task Cancellation_before_commit_does_not_touch_clipboard()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        int clipboardCalls = 0;
        var output = new WindowsTextOutput(
            getForegroundWindow: () => 42,
            setClipboard: _ => { clipboardCalls++; return Task.CompletedTask; },
            sendPaste: () => 4);

        try
        {
            await output.DeliverAsync(new WindowTarget(42), "text", cancellation.Token);
            Assert.Fail("Cancellation should stop delivery before the clipboard commit.");
        }
        catch (OperationCanceledException)
        {
        }
        Assert.AreEqual(0, clipboardCalls);
    }

    [TestMethod]
    public async Task Cancellation_after_commit_keeps_clipboard_and_skips_paste()
    {
        using var cancellation = new CancellationTokenSource();
        int pasteCalls = 0;
        var output = new WindowsTextOutput(
            getForegroundWindow: () => 42,
            setClipboard: _ =>
            {
                cancellation.Cancel();
                return Task.CompletedTask;
            },
            sendPaste: () => { pasteCalls++; return 4; });

        DeliveryResult result = await output.DeliverAsync(new WindowTarget(42), "kept", cancellation.Token);

        Assert.AreEqual(DeliveryResult.CopiedToClipboard, result);
        Assert.AreEqual(0, pasteCalls);
    }

    private static TimeSpan Ms(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    private static TimeSpan Max(TimeSpan first, TimeSpan second) => first > second ? first : second;

    /// <summary>A clock that moves only when the code under test waits, so the waits take no real time.</summary>
    private sealed class VirtualTime : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        internal TimeSpan Elapsed => TimeSpan.FromTicks(_ticks);

        internal void Advance(TimeSpan duration) => _ticks += duration.Ticks;

        internal Task Delay(TimeSpan duration, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Advance(duration);
            return Task.CompletedTask;
        }
    }
}
