using System.Runtime.InteropServices;
using WinWispr.Diagnostics;

namespace WinWispr.Interop;

/// <summary>One clipboard format and a copy of its memory block.</summary>
internal sealed record ClipboardFormatData(uint Format, byte[] Content);

/// <summary>A read of the clipboard: whether it held no format at all, and the wanted formats it held, in its order.</summary>
internal readonly record struct ClipboardContent(bool IsEmpty, IReadOnlyList<ClipboardFormatData> Formats);

/// <summary>The Win32 clipboard calls behind <see cref="WindowsTextOutput"/> and <see cref="WindowsSelectionClipboard"/>.</summary>
internal static class NativeClipboard
{
    /// <summary>CF_UNICODETEXT; Windows derives CF_TEXT and CF_OEMTEXT from it.</summary>
    internal const uint UnicodeText = 13;

    /// <summary>CF_DIB, a bitmap; Windows derives CF_DIBV5 and CF_BITMAP from it, and it from either.</summary>
    internal const uint Dib = 8;

    /// <summary>CF_HDROP, a list of file paths.</summary>
    internal const uint FileDrop = 15;

    /// <summary>CF_DIBV5, a bitmap; Windows derives CF_DIB and CF_BITMAP from it, and it from either.</summary>
    internal const uint DibV5 = 17;

    private const uint GlobalMoveable = 0x0002;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint HighResolutionTimer = 0x2;
    private const uint TimerAllAccess = 0x1F0003;
    private const string UnknownProcess = "unknown";

    /// <summary>
    /// Registered formats, each holding a DWORD 0, that keep what WinWispr puts on the clipboard (a dictation, a restored
    /// clipboard) out of Windows clipboard history, off the cloud clipboard and out of clipboard monitors that honor the
    /// exclusion. WinRT's IsAllowedInHistory = false and
    /// IsRoamable = false write the last two the same way.
    /// </summary>
    internal static readonly string[] ExclusionFormats =
    [
        "ExcludeClipboardContentFromMonitorProcessing",
        "CanIncludeInClipboardHistory",
        "CanUploadToCloudClipboard"
    ];

    private static readonly Lazy<uint[]> ExclusionFormatIds = new(() => ExclusionFormats.Select(Register).ToArray());
    private static readonly byte[] ZeroDword = new byte[sizeof(uint)];

    /// <summary>
    /// Puts <paramref name="text"/> and the exclusion formats on the clipboard in one open, so monitors see one change.
    /// Run it on the thread that owns <paramref name="owner"/>.
    /// </summary>
    /// <returns>The clipboard's change counter after the set, or null when another program holds the clipboard open.</returns>
    internal static uint? TryWriteText(nint owner, string text) =>
        TryWrite(owner, [new ClipboardFormatData(UnicodeText, MemoryMarshal.AsBytes((text + '\0').AsSpan()).ToArray())]);

    /// <summary>
    /// Replaces the clipboard with <paramref name="formats"/>, in their order, and the exclusion formats in one open, so
    /// monitors see one change. Run it on the thread that owns <paramref name="owner"/>.
    /// </summary>
    /// <returns>The clipboard's change counter after the set, or null when another program holds the clipboard open.</returns>
    internal static uint? TryWrite(nint owner, IReadOnlyList<ClipboardFormatData> formats)
    {
        if (!OpenClipboard(owner))
            return null;
        try
        {
            if (!EmptyClipboard())
                throw LastError();
            foreach (uint format in ExclusionFormatIds.Value)
                Set(format, Allocate(ZeroDword));
            foreach (ClipboardFormatData format in formats)
                Set(format.Format, Allocate(format.Content));
        }
        finally
        {
            CloseClipboard();
        }
        return GetClipboardSequenceNumber();
    }

    /// <summary>Empties the clipboard in one open. Run it on the thread that owns <paramref name="owner"/>.</summary>
    /// <returns>The clipboard's change counter afterwards, or null when another program holds the clipboard open.</returns>
    internal static uint? TryEmpty(nint owner)
    {
        if (!OpenClipboard(owner))
            return null;
        try
        {
            if (!EmptyClipboard())
                throw LastError();
        }
        finally
        {
            CloseClipboard();
        }
        return GetClipboardSequenceNumber();
    }

    /// <summary>
    /// Copies the formats of <paramref name="wanted"/> that the clipboard holds, in the clipboard's order, in one open
    /// that changes nothing. A format Windows derives from another one counts as held. A program that renders its
    /// content only on request renders it now. Run it on the thread that owns <paramref name="owner"/>.
    /// </summary>
    /// <returns>What the clipboard holds, or null when another program holds the clipboard open.</returns>
    internal static ClipboardContent? TryRead(nint owner, IReadOnlyCollection<uint> wanted)
    {
        if (!OpenClipboard(owner))
            return null;
        try
        {
            var formats = new List<ClipboardFormatData>();
            bool empty = true;
            for (uint format = EnumClipboardFormats(0); format != 0; format = EnumClipboardFormats(format))
            {
                empty = false;
                if (wanted.Contains(format) && Copy(format) is { Length: > 0 } content)
                    formats.Add(new ClipboardFormatData(format, content));
            }
            return new ClipboardContent(empty, formats);
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>The clipboard's state, read without opening it.</summary>
    internal static ClipboardState State() => new(GetOpenClipboardWindow(), GetClipboardSequenceNumber());

    /// <summary>The image file name of the process that owns <paramref name="window"/>, such as <c>notepad.exe</c>.</summary>
    internal static string ProcessImageName(nint window)
    {
        _ = GetWindowThreadProcessId(window, out uint processId);
        nint process = processId == 0 ? 0 : OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == 0)
            return UnknownProcess;
        try
        {
            var path = new char[1024];
            int length = path.Length;
            return QueryFullProcessImageName(process, 0, path, ref length)
                ? Path.GetFileName(new string(path, 0, length))
                : UnknownProcess;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>
    /// Records which window holds the clipboard open, about every millisecond for <paramref name="duration"/>, on a
    /// thread of its own, and completes with every hold <paramref name="holds"/> has seen.
    /// </summary>
    internal static Task<IReadOnlyList<ClipboardHold>> WatchAsync(
        ClipboardHoldRecorder holds,
        TimeProvider clock,
        TimeSpan duration)
    {
        var completion = new TaskCompletionSource<IReadOnlyList<ClipboardHold>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                using var tick = new MillisecondTick();
                long started = clock.GetTimestamp();
                long now = started;
                while (clock.GetElapsedTime(started, now) < duration)
                {
                    holds.Observe(now, GetOpenClipboardWindow());
                    tick.Wait();
                    now = clock.GetTimestamp();
                }
                completion.TrySetResult(holds.Finish(now));
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        })
        {
            IsBackground = true,
            Name = "WinWispr.ClipboardWatch"
        };
        thread.Start();
        return completion.Task;
    }

    /// <summary>The format id of a registered clipboard format such as <c>HTML Format</c>.</summary>
    internal static uint Register(string name)
    {
        uint format = RegisterClipboardFormat(name);
        return format != 0 ? format : throw LastError();
    }

    // The clipboard owns the memory once SetClipboardData accepts it.
    private static void Set(uint format, nint memory)
    {
        if (SetClipboardData(format, memory) != 0)
            return;
        Exception failure = LastError();
        GlobalFree(memory);
        throw failure;
    }

    // A format that cannot be copied fails the read: content that is not copied cannot be put back. An empty block
    // holds nothing to put back.
    private static unsafe byte[] Copy(uint format)
    {
        nint memory = GetClipboardData(format);
        if (memory == 0)
            throw new InvalidOperationException($"Could not read clipboard format {format}.");
        int size = checked((int)GlobalSize(memory));
        if (size == 0)
            return [];
        nint pointer = GlobalLock(memory);
        if (pointer == 0)
            throw new InvalidOperationException($"Could not read clipboard format {format}.");
        try
        {
            return new ReadOnlySpan<byte>((void*)pointer, size).ToArray();
        }
        finally
        {
            GlobalUnlock(memory);
        }
    }

    private static unsafe nint Allocate(ReadOnlySpan<byte> content)
    {
        nint memory = GlobalAlloc(GlobalMoveable, (nuint)content.Length);
        if (memory == 0)
            throw new OutOfMemoryException("Could not allocate the clipboard content.");
        nint pointer = GlobalLock(memory);
        if (pointer == 0)
        {
            Exception failure = LastError();
            GlobalFree(memory);
            throw failure;
        }
        content.CopyTo(new Span<byte>((void*)pointer, content.Length));
        GlobalUnlock(memory);
        return memory;
    }

    private static Exception LastError() =>
        Marshal.GetExceptionForHR(Marshal.GetHRForLastWin32Error())
        ?? new InvalidOperationException("A clipboard call failed.");

    /// <summary>A one-millisecond wait; Thread.Sleep(1) lasts a whole 15.6 ms timer tick by default.</summary>
    private sealed class MillisecondTick : IDisposable
    {
        private readonly nint _timer = CreateWaitableTimerEx(0, null, HighResolutionTimer, TimerAllAccess);

        internal void Wait()
        {
            long due = -TimeSpan.TicksPerMillisecond;
            if (_timer == 0 || !SetWaitableTimer(_timer, ref due, 0, 0, 0, false) || WaitForSingleObject(_timer, 50) != 0)
                Thread.Sleep(1);
        }

        public void Dispose()
        {
            if (_timer != 0)
                CloseHandle(_timer);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(nint owner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetClipboardData(uint format, nint memory);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint EnumClipboardFormats(uint format);

    [DllImport("user32.dll", EntryPoint = "RegisterClipboardFormatW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint RegisterClipboardFormat(string name);

    [DllImport("user32.dll")]
    private static extern nint GetOpenClipboardWindow();

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint memory);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(nint memory);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalFree(nint memory);

    [DllImport("kernel32.dll")]
    private static extern nuint GlobalSize(nint memory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(nint process, uint flags, [Out] char[] path, ref int length);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", CharSet = CharSet.Unicode)]
    private static extern nint CreateWaitableTimerEx(nint attributes, string? name, uint flags, uint access);

    [DllImport("kernel32.dll")]
    private static extern bool SetWaitableTimer(
        nint timer,
        ref long dueTime,
        int period,
        nint completionRoutine,
        nint argument,
        bool resume);

    [DllImport("kernel32.dll")]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
}
