using System.Runtime.InteropServices;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Interop;

namespace WinWispr.Tests.Interop;

/// <summary>
/// The selection clipboard's capture, restore and text read against a fake Win32 clipboard: what it asks for, what it
/// puts back, and that it retries only opening the clipboard. <see cref="NativeClipboardTests"/> covers the same path
/// on the real clipboard.
/// </summary>
[TestClass]
public sealed class WindowsSelectionClipboardTests
{
    private static readonly uint Html = NativeClipboard.Register("HTML Format");
    private static readonly uint Private = NativeClipboard.Register("WinWispr test application format");

    [TestMethod]
    public async Task A_capture_asks_for_text_html_rtf_a_bitmap_files_and_a_web_link()
    {
        var clipboard = new FakeClipboard();

        await clipboard.Selection.CaptureAsync(CancellationToken.None);

        CollectionAssert.AreEqual(
            new[]
            {
                NativeClipboard.UnicodeText,
                NativeClipboard.Register("HTML Format"),
                NativeClipboard.Register("Rich Text Format"),
                NativeClipboard.Dib,
                NativeClipboard.DibV5,
                NativeClipboard.FileDrop,
                NativeClipboard.Register("Shell IDList Array"),
                NativeClipboard.Register("UniformResourceLocatorW")
            },
            clipboard.Requested.Single().ToArray());
    }

    [TestMethod]
    public async Task A_restore_puts_every_captured_format_back_in_one_set_and_in_the_clipboard_order()
    {
        var clipboard = new FakeClipboard();
        clipboard.Hold(Html, "<b>previous</b>");
        clipboard.Hold(NativeClipboard.UnicodeText, "previous");
        object? snapshot = await clipboard.Selection.CaptureAsync(CancellationToken.None);
        clipboard.Hold(NativeClipboard.UnicodeText, "the selection", replace: true);

        await clipboard.Selection.RestoreAsync(snapshot, CancellationToken.None);

        Assert.AreEqual(1, clipboard.Writes);
        Assert.AreEqual(0, clipboard.Empties);
        CollectionAssert.AreEqual(new[] { Html, NativeClipboard.UnicodeText }, clipboard.Content.Select(data => data.Format).ToArray());
        Assert.AreEqual("<b>previous</b>", Decode(clipboard.Content[0]));
        Assert.AreEqual("previous", Decode(clipboard.Content[1]));
    }

    [TestMethod]
    public async Task Only_the_first_bitmap_format_is_put_back_as_windows_derives_the_other_from_it()
    {
        foreach ((uint first, uint second) in new[] { (NativeClipboard.Dib, NativeClipboard.DibV5), (NativeClipboard.DibV5, NativeClipboard.Dib) })
        {
            var clipboard = new FakeClipboard();
            clipboard.Hold(first, "the bitmap the program put there");
            clipboard.Hold(NativeClipboard.UnicodeText, "previous");
            clipboard.Hold(second, "the bitmap Windows derived");
            object? snapshot = await clipboard.Selection.CaptureAsync(CancellationToken.None);
            clipboard.Hold(NativeClipboard.UnicodeText, "the selection", replace: true);

            await clipboard.Selection.RestoreAsync(snapshot, CancellationToken.None);

            CollectionAssert.AreEqual(new[] { first, NativeClipboard.UnicodeText }, clipboard.Content.Select(data => data.Format).ToArray());
            Assert.AreEqual("the bitmap the program put there", Decode(clipboard.Content[0]));
        }
    }

    [TestMethod]
    public async Task A_previously_empty_clipboard_is_emptied_again_without_writing_anything()
    {
        var clipboard = new FakeClipboard();
        object? snapshot = await clipboard.Selection.CaptureAsync(CancellationToken.None);
        clipboard.Hold(NativeClipboard.UnicodeText, "the selection");

        await clipboard.Selection.RestoreAsync(snapshot, CancellationToken.None);

        Assert.IsNull(snapshot);
        Assert.AreEqual(1, clipboard.Empties);
        Assert.AreEqual(0, clipboard.Writes);
        Assert.IsEmpty(clipboard.Content);
    }

    [TestMethod]
    public async Task A_clipboard_that_held_nothing_restorable_is_left_as_the_copy_made_it()
    {
        var clipboard = new FakeClipboard();
        clipboard.Hold(Private, "application data");
        object? snapshot = await clipboard.Selection.CaptureAsync(CancellationToken.None);
        clipboard.Hold(NativeClipboard.UnicodeText, "the selection", replace: true);

        await clipboard.Selection.RestoreAsync(snapshot, CancellationToken.None);

        Assert.IsNotNull(snapshot);
        Assert.AreEqual(0, clipboard.Writes);
        Assert.AreEqual(0, clipboard.Empties);
        Assert.AreEqual("the selection", Decode(clipboard.Content.Single()));
    }

    [TestMethod]
    public async Task A_restore_retries_only_while_another_program_holds_the_clipboard_open()
    {
        var clipboard = new FakeClipboard();
        clipboard.Hold(NativeClipboard.UnicodeText, "previous");
        object? snapshot = await clipboard.Selection.CaptureAsync(CancellationToken.None);
        clipboard.HeldOpenFor = 3;

        await clipboard.Selection.RestoreAsync(snapshot, CancellationToken.None);

        Assert.AreEqual(4, clipboard.WriteAttempts);
        Assert.AreEqual(1, clipboard.Writes);
        CollectionAssert.AreEqual(Enumerable.Repeat(WindowsTextOutput.ClipboardRetryInterval, 3).ToArray(), clipboard.Delays);
    }

    [TestMethod]
    public async Task A_restore_that_fails_after_opening_the_clipboard_is_never_repeated()
    {
        // Setting again would be a second change, which every monitor answers by opening the clipboard again.
        var clipboard = new FakeClipboard();
        clipboard.Hold(NativeClipboard.UnicodeText, "previous");
        object? snapshot = await clipboard.Selection.CaptureAsync(CancellationToken.None);
        clipboard.FailWrite = true;

        await Assert.ThrowsExactlyAsync<COMException>(() => clipboard.Selection.RestoreAsync(snapshot, CancellationToken.None));

        Assert.AreEqual(1, clipboard.WriteAttempts);
    }

    [TestMethod]
    public async Task A_clipboard_held_open_through_every_attempt_fails_the_restore_as_busy()
    {
        var clipboard = new FakeClipboard();
        object? snapshot = await clipboard.Selection.CaptureAsync(CancellationToken.None);
        clipboard.HeldOpenFor = int.MaxValue;

        ClipboardBusyException busy = await Assert.ThrowsExactlyAsync<ClipboardBusyException>(
            () => clipboard.Selection.RestoreAsync(snapshot, CancellationToken.None));

        Assert.AreEqual(WindowsTextOutput.ClipboardOpenAttempts, busy.Attempts);
        Assert.AreEqual(WindowsTextOutput.ClipboardOpenAttempts, clipboard.EmptyAttempts);
    }

    [TestMethod]
    public async Task A_capture_waits_for_a_program_that_holds_the_clipboard_open_and_fails_as_busy_after_every_attempt()
    {
        var clipboard = new FakeClipboard();
        clipboard.Hold(NativeClipboard.UnicodeText, "previous");
        clipboard.HeldOpenFor = 2;

        Assert.IsNotNull(await clipboard.Selection.CaptureAsync(CancellationToken.None));
        Assert.AreEqual(3, clipboard.ReadAttempts);

        clipboard.HeldOpenFor = int.MaxValue;
        await Assert.ThrowsExactlyAsync<ClipboardBusyException>(() => clipboard.Selection.CaptureAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task Reading_text_asks_for_unicode_text_only_and_stops_at_its_first_nul()
    {
        var clipboard = new FakeClipboard();
        clipboard.Hold(NativeClipboard.UnicodeText, "the selection\0\0stale");

        Assert.AreEqual("the selection", await clipboard.Selection.ReadTextAsync(CancellationToken.None));
        CollectionAssert.AreEqual(new[] { NativeClipboard.UnicodeText }, clipboard.Requested.Single().ToArray());
    }

    [TestMethod]
    public async Task A_clipboard_without_text_reads_as_null()
    {
        var clipboard = new FakeClipboard();
        clipboard.Hold(Html, "<b>no text</b>");

        Assert.IsNull(await clipboard.Selection.ReadTextAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task A_cancelled_read_does_not_touch_the_clipboard()
    {
        var clipboard = new FakeClipboard();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => clipboard.Selection.CaptureAsync(cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => clipboard.Selection.ReadTextAsync(cancellation.Token));

        Assert.AreEqual(0, clipboard.ReadAttempts);
    }

    private static string Decode(ClipboardFormatData data) => Encoding.Unicode.GetString(data.Content).TrimEnd('\0');

    /// <summary>A Win32 clipboard that holds formats in order and can be held open by another program.</summary>
    private sealed class FakeClipboard
    {
        private uint _sequence = 100;

        internal FakeClipboard()
        {
            Selection = new WindowsSelectionClipboard(
                formats => Task.FromResult(Read(formats)),
                formats => Task.FromResult(Write(formats)),
                () => Task.FromResult(Empty()),
                () => _sequence,
                (delay, _) =>
                {
                    Delays.Add(delay);
                    return Task.CompletedTask;
                });
        }

        internal WindowsSelectionClipboard Selection { get; }
        internal List<ClipboardFormatData> Content { get; } = [];
        internal List<IReadOnlyCollection<uint>> Requested { get; } = [];
        internal List<TimeSpan> Delays { get; } = [];
        internal int HeldOpenFor { get; set; }
        internal bool FailWrite { get; set; }
        internal int ReadAttempts { get; private set; }
        internal int WriteAttempts { get; private set; }
        internal int EmptyAttempts { get; private set; }
        internal int Writes { get; private set; }
        internal int Empties { get; private set; }

        internal void Hold(uint format, string text, bool replace = false)
        {
            if (replace)
                Content.Clear();
            Content.Add(new ClipboardFormatData(format, Encoding.Unicode.GetBytes(text + '\0')));
            _sequence++;
        }

        private bool HeldOpen()
        {
            if (HeldOpenFor <= 0)
                return false;
            HeldOpenFor--;
            return true;
        }

        private ClipboardContent? Read(IReadOnlyCollection<uint> formats)
        {
            ReadAttempts++;
            Requested.Add(formats);
            return HeldOpen()
                ? null
                : new ClipboardContent(Content.Count == 0, Content.Where(data => formats.Contains(data.Format)).ToList());
        }

        private uint? Write(IReadOnlyList<ClipboardFormatData> formats)
        {
            WriteAttempts++;
            if (HeldOpen())
                return null;
            if (FailWrite)
                throw new COMException("set failed");
            Writes++;
            Content.Clear();
            Content.AddRange(formats);
            return ++_sequence;
        }

        private uint? Empty()
        {
            EmptyAttempts++;
            if (HeldOpen())
                return null;
            Empties++;
            Content.Clear();
            return ++_sequence;
        }
    }
}
