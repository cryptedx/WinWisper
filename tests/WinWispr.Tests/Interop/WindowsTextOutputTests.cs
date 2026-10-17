using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Core;
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
    public async Task Paste_waits_until_clipboard_monitors_have_released_the_clipboard()
    {
        // A monitor that opens the clipboard on every change makes a paste in that moment find it open and paste nothing.
        int probes = 0;
        int probesAtPaste = 0;
        var output = new WindowsTextOutput(() => 42, _ => Task.CompletedTask,
            () => { probesAtPaste = probes; return 4; },
            clipboardFree: () => ++probes > 3);

        Assert.AreEqual(DeliveryResult.PasteSent,
            await output.DeliverAsync(new WindowTarget(42), "text", CancellationToken.None));
        Assert.AreEqual(5, probesAtPaste, "Three busy probes, then two free ones in a row.");
    }

    [TestMethod]
    public async Task A_clipboard_that_stays_open_falls_back_to_copy_without_a_paste()
    {
        bool pasted = false;
        var output = new WindowsTextOutput(() => 42, _ => Task.CompletedTask,
            () => { pasted = true; return 4; },
            clipboardFree: () => false);

        Assert.AreEqual(DeliveryResult.CopiedToClipboard,
            await output.DeliverAsync(new WindowTarget(42), "text", CancellationToken.None));
        Assert.IsFalse(pasted);
    }

    [TestMethod]
    public void Dictation_clipboard_content_is_not_historic_or_roamable()
    {
        var options = WindowsTextOutput.CreateClipboardOptions();

        Assert.IsFalse(options.IsAllowedInHistory);
        Assert.IsFalse(options.IsRoamable);
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
}
