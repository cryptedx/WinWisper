using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using WinWispr.Diagnostics;
using WinWispr.Interop;

namespace WinWispr.Tests.Interop;

/// <summary>
/// WinWispr's Win32 clipboard writer, the selection clipboard's capture and restore, and the watch on the real clipboard
/// of the logged-in desktop. They replace the clipboard content, so they run with the desktop tests, not in the default
/// check.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class NativeClipboardTests
{
    private const string Dictation = "Grüße aus WinWispr – one set ✓";
    private const uint UnicodeText = 13;
    private static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(30);

    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    public async Task One_set_puts_the_text_and_the_exclusion_formats_on_the_clipboard_as_one_change()
    {
        var owner = new ClipboardOwnerThread();
        using var listener = new ClipboardListener();

        ClipboardSet set = await SetAsync(owner);
        Thread.Sleep(300);

        Assert.AreEqual(1, set.Attempts);
        Assert.AreEqual(1, listener.Updates, "Clipboard monitors see exactly one change.");
        Assert.AreEqual(set.Sequence, GetClipboardSequenceNumber());
        _ = GetWindowThreadProcessId(GetClipboardOwner(), out uint ownerProcess);
        Assert.AreEqual((uint)Environment.ProcessId, ownerProcess);
        WithOpenClipboard(() =>
        {
            Assert.AreEqual(Dictation, Read(UnicodeText, (content, _) => Marshal.PtrToStringUni(content)));
            foreach (string name in NativeClipboard.ExclusionFormats)
            {
                Assert.AreEqual(
                    ((nuint)sizeof(uint), 0),
                    Read(RegisterClipboardFormat(name), (content, size) => (size, Marshal.ReadInt32(content))),
                    name);
            }
        });
    }

    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    public async Task A_set_waits_for_a_program_that_holds_the_clipboard_open()
    {
        var owner = new ClipboardOwnerThread();
        using var release = new ManualResetEventSlim();
        using var opened = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            if (OpenClipboard(0))
            {
                opened.Set();
                release.Wait(TimeSpan.FromSeconds(5));
                CloseClipboard();
            }
        })
        { IsBackground = true };
        holder.Start();
        Assert.IsTrue(opened.Wait(TimeSpan.FromSeconds(5)), "The holder could not open the clipboard.");

        Task<ClipboardSet> set = SetAsync(owner);
        Thread.Sleep(150);
        bool doneWhileHeld = set.IsCompleted;
        release.Set();
        ClipboardSet done = await set;
        holder.Join(TimeSpan.FromSeconds(5));

        Assert.IsFalse(doneWhileHeld);
        Assert.IsGreaterThan(1, done.Attempts);
        WithOpenClipboard(() => Assert.AreEqual(Dictation, Read(UnicodeText, (content, _) => Marshal.PtrToStringUni(content))));
    }

    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    public async Task Another_program_takes_the_clipboard_over_without_waiting_for_the_owner()
    {
        // Emptying the clipboard sends its owner WM_DESTROYCLIPBOARD; an owner thread that does not pump would hang it.
        var owner = new ClipboardOwnerThread();
        await SetAsync(owner);

        Stopwatch timer = Stopwatch.StartNew();
        WithOpenClipboard(() => Assert.IsTrue(EmptyClipboard()));

        Assert.IsLessThan(TimeSpan.FromSeconds(1), timer.Elapsed);
    }

    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    public async Task The_state_and_the_watch_see_a_window_that_holds_the_clipboard_open_without_opening_it()
    {
        var owner = new ClipboardOwnerThread();
        TimeProvider clock = TimeProvider.System;
        var holds = new ClipboardHoldRecorder(clock, clock.GetTimestamp(), NativeClipboard.ProcessImageName);
        Task<IReadOnlyList<ClipboardHold>> watch = NativeClipboard.WatchAsync(holds, clock, TimeSpan.FromMilliseconds(400));
        Thread.Sleep(100);

        nint window = await owner.InvokeAsync(handle => OpenClipboard(handle) ? handle : 0);
        Assert.AreNotEqual((nint)0, window, "The owner window could not open the clipboard.");
        nint seen;
        try
        {
            seen = NativeClipboard.State().OpenWindow;
            Thread.Sleep(50);
        }
        finally
        {
            await owner.InvokeAsync(_ => CloseClipboard());
        }

        Assert.AreEqual(window, seen);
        Assert.AreEqual((nint)0, NativeClipboard.State().OpenWindow);
        string ownProcess = Path.GetFileName(Environment.ProcessPath)!;
        ClipboardHold hold = (await watch).Single(candidate =>
            string.Equals(candidate.Process, ownProcess, StringComparison.OrdinalIgnoreCase));
        Assert.IsInRange(80, 250, hold.AtMs);
        Assert.IsInRange(40, 150, hold.ForMs);
    }

    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    public async Task A_selection_restore_puts_text_html_rtf_a_bitmap_files_and_a_web_link_back_as_one_change()
    {
        DirectoryInfo folder = Directory.CreateTempSubdirectory("WinWispr-clipboard-");
        try
        {
            string file = Path.Combine(folder.FullName, "Grüße.txt");
            File.WriteAllText(file, "a file on the clipboard");
            using var winRt = new StaThread();
            // A WinRT package uses the formats an application's copy uses: CF_UNICODETEXT, HTML Format, Rich Text
            // Format, CF_BITMAP, CF_HDROP and UniformResourceLocatorW.
            WinRtView original = await winRt.RunAsync(async () =>
            {
                await PutWinRtPackageAsync(file);
                return await ReadWinRtAsync();
            }).WaitAsync(StepTimeout);
            Assert.AreEqual(Dictation, original.Text);
            Assert.AreEqual(file, original.Files);
            Assert.AreEqual("2x1:FF0000,0000FF", original.Pixels);

            var selection = new WindowsSelectionClipboard();
            object? snapshot = await selection.CaptureAsync(CancellationToken.None).WaitAsync(StepTimeout);
            await SetAsync(new ClipboardOwnerThread()).WaitAsync(StepTimeout);
            using var listener = new ClipboardListener();
            await selection.RestoreAsync(snapshot, CancellationToken.None).WaitAsync(StepTimeout);
            Thread.Sleep(300);

            Assert.AreEqual(1, listener.Updates, "Clipboard monitors see exactly one change.");
            Assert.AreEqual(original, await winRt.RunAsync(ReadWinRtAsync).WaitAsync(StepTimeout));
            WithOpenClipboard(() =>
            {
                foreach (string name in NativeClipboard.ExclusionFormats)
                {
                    Assert.AreEqual(
                        ((nuint)sizeof(uint), 0),
                        Read(RegisterClipboardFormat(name), (content, size) => (size, Marshal.ReadInt32(content))),
                        name);
                }
            });
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    public async Task A_selection_restore_puts_back_a_bitmap_that_a_program_put_as_a_dibv5()
    {
        var owner = new ClipboardOwnerThread();
        await WindowsTextOutput.WhenClipboardOpenAsync(
            () => owner.InvokeAsync(window => NativeClipboard.TryWrite(window, [new ClipboardFormatData(NativeClipboard.DibV5, TwoPixelDibV5())])),
            Task.Delay,
            CancellationToken.None).WaitAsync(StepTimeout);
        using var winRt = new StaThread();
        Assert.AreEqual("2x1:FF0000,0000FF", await winRt.RunAsync(() => ReadWinRtPixelsAsync(Clipboard.GetContent())).WaitAsync(StepTimeout));

        var selection = new WindowsSelectionClipboard();
        object? snapshot = await selection.CaptureAsync(CancellationToken.None).WaitAsync(StepTimeout);
        await SetAsync(owner).WaitAsync(StepTimeout);
        await selection.RestoreAsync(snapshot, CancellationToken.None).WaitAsync(StepTimeout);

        Assert.AreEqual("2x1:FF0000,0000FF", await winRt.RunAsync(() => ReadWinRtPixelsAsync(Clipboard.GetContent())).WaitAsync(StepTimeout));
        byte[] expected = TwoPixelDibV5();
        WithOpenClipboard(() => CollectionAssert.AreEqual(
            expected,
            Read(NativeClipboard.DibV5, (content, size) =>
            {
                // The memory block can be larger than its content.
                Assert.IsGreaterThanOrEqualTo((nuint)expected.Length, size);
                var bytes = new byte[expected.Length];
                Marshal.Copy(content, bytes, 0, bytes.Length);
                return bytes;
            })));
    }

    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    public async Task A_selection_restore_of_an_empty_clipboard_empties_it_as_one_change()
    {
        WithOpenClipboard(() => Assert.IsTrue(EmptyClipboard()));
        var selection = new WindowsSelectionClipboard();
        object? snapshot = await selection.CaptureAsync(CancellationToken.None).WaitAsync(StepTimeout);
        await SetAsync(new ClipboardOwnerThread()).WaitAsync(StepTimeout);
        using var listener = new ClipboardListener();

        await selection.RestoreAsync(snapshot, CancellationToken.None).WaitAsync(StepTimeout);
        Thread.Sleep(300);

        Assert.IsNull(snapshot);
        Assert.AreEqual(1, listener.Updates, "Clipboard monitors see exactly one change.");
        Assert.AreEqual(0, CountClipboardFormats());
    }

    private static Task<ClipboardSet> SetAsync(ClipboardOwnerThread owner) =>
        WindowsTextOutput.SetClipboardAsync(
            text => owner.InvokeAsync(window => NativeClipboard.TryWriteText(window, text)),
            Dictation,
            Task.Delay,
            CancellationToken.None);

    // Opens the clipboard without a window, retrying while a program such as VMware Tools reads it.
    private static void WithOpenClipboard(Action action)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (!OpenClipboard(0))
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(2))
                throw new AssertFailedException("The clipboard stayed open.");
            Thread.Sleep(10);
        }
        try
        {
            action();
        }
        finally
        {
            CloseClipboard();
        }
    }

    // Reads a format of the open clipboard while its memory is locked.
    private static T Read<T>(uint format, Func<nint, nuint, T> read)
    {
        nint memory = GetClipboardData(format);
        Assert.AreNotEqual((nint)0, memory, $"Clipboard format {format} is missing.");
        nint content = GlobalLock(memory);
        try
        {
            return read(content, GlobalSize(memory));
        }
        finally
        {
            GlobalUnlock(memory);
        }
    }

    // Text, HTML, RTF, a red and a blue pixel, a file and a web link, the six formats a selection capture keeps.
    private static async Task PutWinRtPackageAsync(string file)
    {
        var package = new DataPackage();
        package.SetText(Dictation);
        package.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat("<p><b>Grüße</b> aus WinWispr</p>"));
        package.SetRtf(@"{\rtf1\ansi Gr\'fc\'dfe aus {\b WinWispr}}");
        package.SetWebLink(new Uri("https://example.com/winwispr?greeting=gr%C3%BC%C3%9Fe"));
        package.SetStorageItems([await StorageFile.GetFileFromPathAsync(file)]);
        var image = new InMemoryRandomAccessStream();
        BitmapEncoder encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, image);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, 2, 1, 96, 96, [0, 0, 255, 255, 255, 0, 0, 255]);
        await encoder.FlushAsync();
        package.SetBitmap(RandomAccessStreamReference.CreateFromStream(image));
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Clipboard.SetContent(package);
                Clipboard.Flush();
                return;
            }
            catch (COMException) when (attempt < 20)
            {
                // A program such as VMware Tools reads the clipboard for a moment.
                await Task.Delay(100);
            }
        }
    }

    private static async Task<WinRtView> ReadWinRtAsync()
    {
        DataPackageView view = Clipboard.GetContent();
        string[] standard =
        [
            StandardDataFormats.Text, StandardDataFormats.Html, StandardDataFormats.Rtf,
            StandardDataFormats.Bitmap, StandardDataFormats.StorageItems, StandardDataFormats.WebLink
        ];
        foreach (string format in standard)
            Assert.IsTrue(view.Contains(format), $"The clipboard lacks {format}.");

        IReadOnlyList<IStorageItem> files = await view.GetStorageItemsAsync();
        return new WinRtView(
            await view.GetTextAsync(),
            await view.GetHtmlFormatAsync(),
            await view.GetRtfAsync(),
            await view.GetWebLinkAsync(),
            string.Join("|", files.Select(item => item.Path)),
            await ReadWinRtPixelsAsync(view));
    }

    // "2x1:FF0000,0000FF" for a red and a blue pixel.
    private static async Task<string> ReadWinRtPixelsAsync(DataPackageView view)
    {
        RandomAccessStreamReference bitmap = await view.GetBitmapAsync();
        using IRandomAccessStreamWithContentType stream = await bitmap.OpenReadAsync();
        BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
        PixelDataProvider pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage);
        IEnumerable<string> rgb = pixels.DetachPixelData().Chunk(4).Select(bgra => $"{bgra[2]:X2}{bgra[1]:X2}{bgra[0]:X2}");
        return $"{decoder.PixelWidth}x{decoder.PixelHeight}:{string.Join(",", rgb)}";
    }

    // A red and a blue pixel as a CF_DIBV5 the way a program writes one: a BITMAPV5HEADER with its color and alpha
    // masks and the sRGB color space, then the pixels, without the masks a second time.
    private static byte[] TwoPixelDibV5()
    {
        var dib = new byte[124 + 8];
        using var header = new BinaryWriter(new MemoryStream(dib));
        header.Write(124);
        header.Write(2);
        header.Write(1);
        header.Write((short)1);
        header.Write((short)32);
        header.Write(3); // BI_BITFIELDS
        header.Write(8);
        header.Seek(40, SeekOrigin.Begin);
        header.Write(0x00FF0000);
        header.Write(0x0000FF00);
        header.Write(0x000000FF);
        header.Write(unchecked((int)0xFF000000));
        header.Write(0x73524742); // LCS_sRGB
        header.Seek(108, SeekOrigin.Begin);
        header.Write(4); // LCS_GM_IMAGES
        header.Seek(124, SeekOrigin.Begin);
        header.Write(new byte[] { 0, 0, 255, 255, 255, 0, 0, 255 });
        return dib;
    }

    /// <summary>The clipboard as WinRT's DataPackageView reads it, in the six formats a selection capture keeps.</summary>
    private sealed record WinRtView(string Text, string Html, string Rtf, Uri WebLink, string Files, string Pixels);

    /// <summary>An STA thread with a dispatcher, where WinRT's clipboard runs as it does on the UI thread.</summary>
    private sealed class StaThread : IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private readonly Thread _thread;

        public StaThread()
        {
            var ready = new TaskCompletionSource<Dispatcher>();
            _thread = new Thread(() =>
            {
                ready.SetResult(Dispatcher.CurrentDispatcher);
                Dispatcher.Run();
            });
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.IsBackground = true;
            _thread.Start();
            _dispatcher = ready.Task.GetAwaiter().GetResult();
        }

        public Task<T> RunAsync<T>(Func<Task<T>> work) => _dispatcher.InvokeAsync(work).Task.Unwrap();

        public void Dispose()
        {
            _dispatcher.InvokeShutdown();
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>Counts WM_CLIPBOARDUPDATE, the way clipboard monitors learn about changes.</summary>
    private sealed class ClipboardListener : IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private readonly Thread _thread;
        private int _updates;

        public ClipboardListener()
        {
            var ready = new TaskCompletionSource<Dispatcher>();
            _thread = new Thread(() =>
            {
                // HWND_MESSAGE: a message-only window that receives WM_CLIPBOARDUPDATE.
                var source = new HwndSource(new HwndSourceParameters("WinWispr clipboard listener") { ParentWindow = -3 });
                source.AddHook((nint window, int message, nint wParam, nint lParam, ref bool handled) =>
                {
                    if (message == 0x031D)
                        Interlocked.Increment(ref _updates);
                    return 0;
                });
                if (AddClipboardFormatListener(source.Handle))
                    ready.SetResult(Dispatcher.CurrentDispatcher);
                else
                    ready.SetException(new AssertFailedException("The clipboard listener could not listen."));
                Dispatcher.Run();
            });
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.IsBackground = true;
            _thread.Start();
            _dispatcher = ready.Task.GetAwaiter().GetResult();
        }

        public int Updates => Volatile.Read(ref _updates);

        public void Dispose()
        {
            _dispatcher.InvokeShutdown();
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    [DllImport("user32.dll")]
    private static extern bool AddClipboardFormatListener(nint window);

    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(nint window);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    private static extern nint GetClipboardData(uint format);

    [DllImport("user32.dll")]
    private static extern int CountClipboardFormats();

    [DllImport("user32.dll")]
    private static extern nint GetClipboardOwner();

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string name);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalLock(nint memory);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(nint memory);

    [DllImport("kernel32.dll")]
    private static extern nuint GlobalSize(nint memory);
}
