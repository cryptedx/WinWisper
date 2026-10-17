using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Interop;

namespace WinWispr.Tests.Interop;

/// <summary>
/// The selection reader against a fake clipboard: when it sends Ctrl+C, how it notices that the copy happened, and
/// that the previous clipboard comes back on every path.
/// </summary>
[TestClass]
public sealed class SelectionReaderTests
{
    private static readonly SelectionReaderTiming FastTiming = new(
        ModifierPoll: TimeSpan.FromMilliseconds(1),
        ModifierPollLimit: 20,
        CopyPoll: TimeSpan.FromMilliseconds(1),
        CopyTimeout: TimeSpan.FromMilliseconds(60),
        Settle: TimeSpan.FromMilliseconds(2));

    [TestMethod]
    public async Task The_copied_text_is_returned_and_the_previous_clipboard_is_restored()
    {
        var clipboard = new FakeClipboard { Content = "previous content" };
        clipboard.OnCopy = () => clipboard.Write("the selection");
        var reader = Reader(clipboard);

        string? text = await reader.ReadAsync(CancellationToken.None);

        Assert.AreEqual("the selection", text);
        Assert.AreEqual("previous content", clipboard.Content, "the user's clipboard must come back");
        Assert.AreEqual(1, clipboard.Restores);
    }

    [TestMethod]
    public async Task A_selection_equal_to_the_current_clipboard_text_still_counts_because_the_counter_decides()
    {
        var clipboard = new FakeClipboard { Content = "same text" };
        clipboard.OnCopy = () => clipboard.Write("same text");
        var reader = Reader(clipboard);

        string? text = await reader.ReadAsync(CancellationToken.None);

        Assert.AreEqual("same text", text);
        Assert.AreEqual("same text", clipboard.Content);
    }

    [TestMethod]
    public async Task Nothing_selected_returns_null_and_leaves_the_clipboard_alone()
    {
        var clipboard = new FakeClipboard { Content = "previous content" };
        var reader = Reader(clipboard);

        string? text = await reader.ReadAsync(CancellationToken.None);

        Assert.IsNull(text);
        Assert.AreEqual(1, clipboard.Copies, "Ctrl+C was sent");
        Assert.AreEqual("previous content", clipboard.Content);
        Assert.AreEqual(0, clipboard.Writes, "nothing was written, so nothing needs restoring");
        Assert.AreEqual(0, clipboard.Restores);
        Assert.AreEqual(0, clipboard.TextReads, "no copy happened, so the clipboard text is not the selection");
    }

    [TestMethod]
    public async Task A_previously_empty_clipboard_is_emptied_again_after_the_read()
    {
        var clipboard = new FakeClipboard { Content = null };
        clipboard.OnCopy = () => clipboard.Write("the selection");
        var reader = Reader(clipboard);

        Assert.AreEqual("the selection", await reader.ReadAsync(CancellationToken.None));

        Assert.IsNull(clipboard.Content);
        Assert.AreEqual(1, clipboard.Restores);
    }

    [TestMethod]
    public async Task A_copy_that_holds_no_text_returns_null_and_still_restores()
    {
        var clipboard = new FakeClipboard { Content = "previous content" };
        clipboard.OnCopy = () => clipboard.Write(null);
        var reader = Reader(clipboard);

        Assert.IsNull(await reader.ReadAsync(CancellationToken.None));

        Assert.AreEqual("previous content", clipboard.Content);
    }

    [TestMethod]
    public async Task Ctrl_c_is_sent_only_after_the_user_has_released_the_hotkeys_modifier_keys()
    {
        var clipboard = new FakeClipboard { Content = "previous content" };
        int checks = 0;
        int checksWhenCopied = -1;
        clipboard.OnCopy = () =>
        {
            checksWhenCopied = checks;
            clipboard.Write("the selection");
        };
        var reader = new SelectionReader(clipboard, () => ++checks <= 3, clipboard.SendCopy, FastTiming);

        Assert.AreEqual("the selection", await reader.ReadAsync(CancellationToken.None));

        Assert.AreEqual(4, checksWhenCopied, "three polls saw a held modifier, the fourth saw none");
    }

    [TestMethod]
    public async Task Modifiers_that_stay_held_fail_the_read_without_sending_anything_or_touching_the_clipboard()
    {
        var clipboard = new FakeClipboard { Content = "previous content" };
        var reader = new SelectionReader(clipboard, () => true, clipboard.SendCopy, FastTiming);

        await Assert.ThrowsExactlyAsync<TimeoutException>(() => reader.ReadAsync(CancellationToken.None));

        Assert.AreEqual(0, clipboard.Copies, "the reader must never release the user's keys or copy with them held");
        Assert.AreEqual(0, clipboard.Captures);
        Assert.AreEqual("previous content", clipboard.Content);
    }

    [TestMethod]
    public async Task A_cancellation_after_the_copy_still_restores_the_previous_clipboard()
    {
        using var cancellation = new CancellationTokenSource();
        var clipboard = new FakeClipboard { Content = "previous content" };
        clipboard.OnCopy = () => clipboard.Write("the selection");
        clipboard.OnReadText = () => cancellation.Cancel();
        var reader = Reader(clipboard);

        await Assert.ThrowsAsync<OperationCanceledException>(() => reader.ReadAsync(cancellation.Token));

        Assert.AreEqual("previous content", clipboard.Content);
    }

    [TestMethod]
    public async Task A_cancellation_before_anything_happens_touches_nothing()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var clipboard = new FakeClipboard { Content = "previous content" };
        var reader = Reader(clipboard);

        await Assert.ThrowsAsync<OperationCanceledException>(() => reader.ReadAsync(cancellation.Token));

        Assert.AreEqual(0, clipboard.Copies);
        Assert.AreEqual("previous content", clipboard.Content);
    }

    [TestMethod]
    public async Task A_failure_while_reading_the_copy_still_restores_the_previous_clipboard()
    {
        var clipboard = new FakeClipboard { Content = "previous content" };
        clipboard.OnCopy = () => clipboard.Write("the selection");
        clipboard.OnReadText = () => throw new InvalidOperationException("clipboard busy");
        var reader = Reader(clipboard);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reader.ReadAsync(CancellationToken.None));

        Assert.AreEqual("previous content", clipboard.Content);
    }

    [TestMethod]
    public async Task A_copy_that_could_not_be_sent_fails_without_restoring_anything()
    {
        var clipboard = new FakeClipboard { Content = "previous content", InputsSent = 0 };
        var reader = Reader(clipboard);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reader.ReadAsync(CancellationToken.None));

        Assert.AreEqual("previous content", clipboard.Content);
        Assert.AreEqual(0, clipboard.Restores);
    }

    [TestMethod]
    public async Task A_clipboard_that_cannot_be_captured_fails_before_ctrl_c_is_sent()
    {
        var clipboard = new FakeClipboard { Content = "previous content", FailCapture = true };
        var reader = Reader(clipboard);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reader.ReadAsync(CancellationToken.None));

        Assert.AreEqual(0, clipboard.Copies, "never overwrite a clipboard that cannot be put back");
    }

    [TestMethod]
    public async Task A_restore_that_fails_does_not_lose_the_selection()
    {
        var clipboard = new FakeClipboard { Content = "previous content", FailRestore = true };
        clipboard.OnCopy = () => clipboard.Write("the selection");
        var reader = Reader(clipboard);

        Assert.AreEqual("the selection", await reader.ReadAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task An_application_that_writes_the_clipboard_in_several_steps_is_read_after_the_last_one()
    {
        var clipboard = new FakeClipboard { Content = "previous content" };
        clipboard.OnCopy = () =>
        {
            clipboard.Write(string.Empty);
            _ = Task.Run(async () =>
            {
                await Task.Delay(1);
                clipboard.Write("the complete selection");
            });
        };
        var reader = new SelectionReader(
            clipboard, () => false, clipboard.SendCopy, FastTiming with { Settle = TimeSpan.FromMilliseconds(30) });

        Assert.AreEqual("the complete selection", await reader.ReadAsync(CancellationToken.None));
    }

    private static SelectionReader Reader(FakeClipboard clipboard) =>
        new(clipboard, () => false, clipboard.SendCopy, FastTiming);

    /// <summary>A clipboard with a change counter, the way Windows keeps one.</summary>
    private sealed class FakeClipboard : ISelectionClipboard
    {
        private uint _sequence = 100;

        internal string? Content { get; set; }
        internal Action? OnCopy { get; set; }
        internal Action? OnReadText { get; set; }
        internal int InputsSent { get; init; } = 4;
        internal bool FailCapture { get; init; }
        internal bool FailRestore { get; init; }
        internal int Copies { get; private set; }
        internal int Captures { get; private set; }
        internal int Restores { get; private set; }
        internal int Writes { get; private set; }
        internal int TextReads { get; private set; }

        public uint SequenceNumber => Volatile.Read(ref _sequence);

        internal void Write(string? text)
        {
            Content = text;
            Writes++;
            Interlocked.Increment(ref _sequence);
        }

        internal int SendCopy()
        {
            Copies++;
            if (InputsSent == 4)
                OnCopy?.Invoke();
            return InputsSent;
        }

        public Task<object?> CaptureAsync(CancellationToken cancellationToken)
        {
            Captures++;
            if (FailCapture)
                throw new InvalidOperationException("cannot capture");
            return Task.FromResult<object?>(Content is null ? null : new Snapshot(Content));
        }

        public Task RestoreAsync(object? snapshot, CancellationToken cancellationToken)
        {
            // A restore must not depend on the caller's cancellation: it runs on the cancel path.
            Assert.IsFalse(cancellationToken.IsCancellationRequested);
            Restores++;
            if (FailRestore)
                throw new InvalidOperationException("cannot restore");
            Content = (snapshot as Snapshot)?.Text;
            Interlocked.Increment(ref _sequence);
            return Task.CompletedTask;
        }

        public Task<string?> ReadTextAsync(CancellationToken cancellationToken)
        {
            TextReads++;
            OnReadText?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(string.IsNullOrEmpty(Content) ? null : Content);
        }

        private sealed record Snapshot(string Text);
    }
}
