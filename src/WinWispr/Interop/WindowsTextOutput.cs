using System.Runtime.InteropServices;
using WinWispr.Core;
using WinWispr.Diagnostics;

namespace WinWispr.Interop;

/// <summary>
/// The clipboard as seen without opening it: the window that holds it open (0 when it is closed, or open without a
/// window) and its change counter.
/// </summary>
internal readonly record struct ClipboardState(nint OpenWindow, uint Sequence);

/// <summary>A dictation on the clipboard: the attempts it took to open the clipboard, and its change counter after the set.</summary>
internal readonly record struct ClipboardSet(int Attempts, uint Sequence);

/// <summary>Another program held the clipboard open through every attempt to use it.</summary>
internal sealed class ClipboardBusyException(int attempts)
    : InvalidOperationException("Another program held the clipboard open.")
{
    internal int Attempts { get; } = attempts;
}

// A delivery stays within three seconds of its own: the set gives up after about one, the wait for a quiet clipboard
// after one and a half. Only the time the user still holds the hotkey comes on top.
internal sealed class WindowsTextOutput
{
    internal const int ClipboardOpenAttempts = 60;
    internal static readonly TimeSpan ClipboardRetryInterval = TimeSpan.FromMilliseconds(10);
    internal static readonly TimeSpan ClipboardPollInterval = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// How long the clipboard must stay closed and unchanged before Ctrl+V. Monitors open it right after a change or
    /// after a delay of their own (Ditto waits about 100 ms), and a paste in that moment finds it open.
    /// </summary>
    internal static readonly TimeSpan ClipboardQuietWindow = TimeSpan.FromMilliseconds(150);

    /// <summary>A clipboard that is not quiet by then gets no Ctrl+V; the dictation stays on it.</summary>
    internal static readonly TimeSpan ClipboardSettleTimeout = TimeSpan.FromMilliseconds(1500);

    /// <summary>How long after Ctrl+V a traced delivery records who opens the clipboard.</summary>
    internal static readonly TimeSpan PasteWatch = TimeSpan.FromSeconds(1);

    private const uint GuiInMenuMode = 0x4;
    private const uint GuiSystemMenuMode = 0x8;
    private const uint GuiPopupMenuMode = 0x10;

    private readonly Func<nint> _getForegroundWindow;
    private readonly Func<string, CancellationToken, Task<ClipboardSet>> _setClipboard;
    private readonly Func<int> _sendPaste;
    private readonly Func<bool> _modifiersPressed;
    private readonly Func<ClipboardState> _clipboardState;
    private readonly Func<nint, bool> _inMenuMode;
    private readonly Func<nint, string> _processOf;
    private readonly Func<ClipboardHoldRecorder, Task<IReadOnlyList<ClipboardHold>>> _watchAfterPaste;
    private readonly TimeProvider _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    internal WindowsTextOutput()
        : this(new ClipboardOwnerThread())
    {
    }

    internal WindowsTextOutput(
        Func<nint> getForegroundWindow,
        Func<string, Task> setClipboard,
        Func<int> sendPaste,
        Func<bool>? modifiersPressed = null,
        Func<ClipboardState>? clipboardState = null,
        Func<nint, bool>? inMenuMode = null,
        TimeProvider? clock = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<nint, string>? processOf = null,
        Func<ClipboardHoldRecorder, Task<IReadOnlyList<ClipboardHold>>>? watchAfterPaste = null)
        : this(
            getForegroundWindow,
            async (text, _) =>
            {
                await setClipboard(text).ConfigureAwait(false);
                return new ClipboardSet(1, clipboardState?.Invoke().Sequence ?? 0);
            },
            sendPaste,
            modifiersPressed ?? (() => false),
            clipboardState ?? (() => default),
            inMenuMode ?? (_ => false),
            processOf ?? (_ => "unknown"),
            watchAfterPaste ?? (holds => Task.FromResult(holds.Finish((clock ?? TimeProvider.System).GetTimestamp()))),
            clock ?? TimeProvider.System,
            delay ?? Task.Delay)
    {
    }

    private WindowsTextOutput(ClipboardOwnerThread owner)
        : this(
            GetForegroundWindow,
            (text, cancellationToken) => SetClipboardAsync(
                content => owner.InvokeAsync(window => NativeClipboard.TryWriteText(window, content)),
                text,
                Task.Delay,
                cancellationToken),
            () => NativeKeyboard.SendControlChord(NativeKeyboard.V),
            NativeKeyboard.AreModifiersPressed,
            NativeClipboard.State,
            IsInMenuMode,
            NativeClipboard.ProcessImageName,
            holds => NativeClipboard.WatchAsync(holds, TimeProvider.System, PasteWatch),
            TimeProvider.System,
            Task.Delay)
    {
    }

    private WindowsTextOutput(
        Func<nint> getForegroundWindow,
        Func<string, CancellationToken, Task<ClipboardSet>> setClipboard,
        Func<int> sendPaste,
        Func<bool> modifiersPressed,
        Func<ClipboardState> clipboardState,
        Func<nint, bool> inMenuMode,
        Func<nint, string> processOf,
        Func<ClipboardHoldRecorder, Task<IReadOnlyList<ClipboardHold>>> watchAfterPaste,
        TimeProvider clock,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        _getForegroundWindow = getForegroundWindow;
        _setClipboard = setClipboard;
        _sendPaste = sendPaste;
        _modifiersPressed = modifiersPressed;
        _clipboardState = clipboardState;
        _inMenuMode = inMenuMode;
        _processOf = processOf;
        _watchAfterPaste = watchAfterPaste;
        _clock = clock;
        _delay = delay;
    }

    /// <summary>
    /// Leaves the dictation on the clipboard. SendInput only queues Ctrl+V, and Windows provides
    /// no acknowledgement that the target pasted, so restoring the previous clipboard could make a
    /// busy target paste stale content and lose the dictation. A delivery that <see cref="DictationTimings"/>
    /// measures fills its <see cref="DeliveryTrace"/>.
    /// </summary>
    internal async Task<DeliveryResult> DeliverAsync(
        WindowTarget target,
        string text,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
            return DeliveryResult.NoSpeech;

        cancellationToken.ThrowIfCancellationRequested();
        DeliveryTrace? trace = DeliveryTrace.Current;
        if (trace is not null && target.Hwnd != 0)
            trace.TargetProcess = _processOf(target.Hwnd);
        ClipboardSet set;
        try
        {
            set = await _setClipboard(text, cancellationToken).ConfigureAwait(false);
        }
        catch (ClipboardBusyException busy) when (trace is not null)
        {
            trace.ClipboardSetAttempts = busy.Attempts;
            throw;
        }

        ClipboardHoldRecorder? holds = null;
        if (trace is not null)
        {
            trace.ClipboardSetAttempts = set.Attempts;
            holds = new ClipboardHoldRecorder(_clock, _clock.GetTimestamp(), _processOf);
        }
        try
        {
            return await PasteAsync(target, set.Sequence, trace, holds, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Without a Ctrl+V nothing is watched; the holds seen while waiting for a quiet clipboard are all there is.
            if (trace is not null && holds is not null)
                trace.ClipboardHolds ??= Task.FromResult(holds.Finish(_clock.GetTimestamp()));
        }
    }

    private async Task<DeliveryResult> PasteAsync(
        WindowTarget target,
        uint sequence,
        DeliveryTrace? trace,
        ClipboardHoldRecorder? holds,
        CancellationToken cancellationToken)
    {
        if (target.Hwnd == 0 || cancellationToken.IsCancellationRequested)
            return DeliveryResult.CopiedToClipboard;
        // Let the triggering chord go before sending Ctrl+V; never release the user's keys for them.
        for (int attempt = 0; _modifiersPressed(); attempt++)
        {
            if (attempt >= 100 || _getForegroundWindow() != target.Hwnd)
                return DeliveryResult.CopiedToClipboard;
            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
        // Clipboard monitors (security agents, clipboard history) open the clipboard as soon as it changes. A target
        // whose paste finds it open pastes nothing, so let them finish before sending Ctrl+V.
        if (!await WaitForClipboardQuietAsync(sequence, trace, holds, cancellationToken).ConfigureAwait(false))
            return DeliveryResult.CopiedToClipboard;
        cancellationToken.ThrowIfCancellationRequested();
        if (_getForegroundWindow() != target.Hwnd)
            return DeliveryResult.CopiedToClipboard;
        // A menu bar or menu that holds the keyboard (a lone Alt press, for one) would swallow Ctrl+V without a trace.
        if (_inMenuMode(target.Hwnd))
            return DeliveryResult.TargetMenuOpen;

        long pasteAt = _clock.GetTimestamp();
        try
        {
            if (_sendPaste() != 4)
                return DeliveryResult.CopiedToClipboard;
        }
        catch
        {
            return DeliveryResult.CopiedToClipboard;
        }
        if (trace is not null && holds is not null)
        {
            trace.PasteAtMs = holds.OffsetMs(pasteAt);
            try
            {
                trace.ClipboardHolds = _watchAfterPaste(holds);
            }
            catch (Exception)
            {
                // Diagnostics only: the paste is sent either way.
            }
        }
        return DeliveryResult.PasteSent;
    }

    /// <summary>
    /// Waits until the clipboard has been closed and unchanged for <see cref="ClipboardQuietWindow"/>. It only looks:
    /// opening the clipboard to test it would itself refuse a monitor or the target in that moment. A program that opens
    /// the clipboard without a window cannot be seen this way.
    /// </summary>
    private async Task<bool> WaitForClipboardQuietAsync(
        uint sequence,
        DeliveryTrace? trace,
        ClipboardHoldRecorder? holds,
        CancellationToken cancellationToken)
    {
        long started = _clock.GetTimestamp();
        long quietSince = started;
        while (true)
        {
            await _delay(ClipboardPollInterval, cancellationToken).ConfigureAwait(false);
            ClipboardState state = _clipboardState();
            long now = _clock.GetTimestamp();
            holds?.Observe(now, state.OpenWindow);
            if (state.Sequence != sequence)
            {
                // Another program changed the clipboard, and monitors answer that change as well.
                sequence = state.Sequence;
                quietSince = now;
                if (trace is not null)
                    trace.ClipboardChangedBeforePaste = true;
            }
            else if (state.OpenWindow != 0)
            {
                quietSince = now;
            }
            else if (_clock.GetElapsedTime(quietSince, now) >= ClipboardQuietWindow)
            {
                return true;
            }
            if (_clock.GetElapsedTime(started, now) >= ClipboardSettleTimeout)
                return false;
        }
    }

    /// <summary>
    /// Leaves <paramref name="text"/> on the clipboard (out of the clipboard history) and sends no paste, for a
    /// transcript the user must paste by hand. Throws when the clipboard cannot be set.
    /// </summary>
    internal async Task CopyAsync(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _setClipboard(text, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Puts <paramref name="text"/> on the clipboard with one set. Only opening the clipboard is retried while another
    /// program holds it. Once the text is on the clipboard it is never set again: every set is a change that monitors
    /// answer by opening the clipboard, which would refuse the next attempt in turn.
    /// </summary>
    /// <param name="tryWrite">
    /// One attempt: the change counter after the set, or null when the clipboard was open. It throws when the set
    /// itself failed, which is not retried.
    /// </param>
    internal static async Task<ClipboardSet> SetClipboardAsync(
        Func<string, Task<uint?>> tryWrite,
        string text,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken cancellationToken)
    {
        (uint sequence, int attempts) = await WhenClipboardOpenAsync(() => tryWrite(text), delay, cancellationToken)
            .ConfigureAwait(false);
        return new ClipboardSet(attempts, sequence);
    }

    /// <summary>
    /// Runs one clipboard operation, retrying it only while another program holds the clipboard open: up to
    /// <see cref="ClipboardOpenAttempts"/> attempts, <see cref="ClipboardRetryInterval"/> apart. The selection
    /// clipboard's reads and restore share this rule.
    /// </summary>
    /// <param name="tryOnce">
    /// One attempt: its result, or null when the clipboard was open. It throws when the operation itself failed, which is
    /// not retried.
    /// </param>
    /// <returns>The result and the attempts it took.</returns>
    internal static async Task<(T Value, int Attempts)> WhenClipboardOpenAsync<T>(
        Func<Task<T?>> tryOnce,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken cancellationToken)
        where T : struct
    {
        for (int attempt = 1; ; attempt++)
        {
            if (await tryOnce().ConfigureAwait(false) is T value)
                return (value, attempt);
            if (attempt == ClipboardOpenAttempts)
                throw new ClipboardBusyException(attempt);
            await delay(ClipboardRetryInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    private static bool IsInMenuMode(nint window)
    {
        uint thread = GetWindowThreadProcessId(window, out _);
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        return thread != 0
            && GetGUIThreadInfo(thread, ref info)
            && (info.Flags & (GuiInMenuMode | GuiSystemMenuMode | GuiPopupMenuMode)) != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        internal int Size;
        internal uint Flags;
        internal nint Active;
        internal nint Focus;
        internal nint Capture;
        internal nint MenuOwner;
        internal nint MoveSize;
        internal nint Caret;
        internal int CaretLeft;
        internal int CaretTop;
        internal int CaretRight;
        internal int CaretBottom;
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
}
