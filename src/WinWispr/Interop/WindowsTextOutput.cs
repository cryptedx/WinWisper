using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;
using WinWispr.Core;

namespace WinWispr.Interop;

internal sealed class WindowsTextOutput
{
    private const int ClipboardAttempts = 40;
    private const int ClipboardSettlePolls = 40;
    private const int ClipboardQuietPolls = 2;
    private const uint GuiInMenuMode = 0x4;
    private const uint GuiSystemMenuMode = 0x8;
    private const uint GuiPopupMenuMode = 0x10;
    private static readonly TimeSpan ClipboardPollInterval = TimeSpan.FromMilliseconds(25);

    private readonly Func<nint> _getForegroundWindow;
    private readonly Func<string, CancellationToken, Task> _setClipboard;
    private readonly Func<int> _sendPaste;
    private readonly Func<bool> _modifiersPressed;
    private readonly Func<bool> _clipboardFree;
    private readonly Func<nint, bool> _inMenuMode;

    internal WindowsTextOutput(DispatcherQueue dispatcher)
        : this(
            GetForegroundWindow,
            (text, cancellationToken) => SetClipboardAsync(dispatcher, text, cancellationToken),
            () => NativeKeyboard.SendControlChord(NativeKeyboard.V),
            NativeKeyboard.AreModifiersPressed,
            IsClipboardFree,
            IsInMenuMode)
    {
    }

    internal WindowsTextOutput(
        Func<nint> getForegroundWindow,
        Func<string, Task> setClipboard,
        Func<int> sendPaste,
        Func<bool>? modifiersPressed = null,
        Func<bool>? clipboardFree = null,
        Func<nint, bool>? inMenuMode = null)
        : this(
            getForegroundWindow,
            (text, _) => setClipboard(text),
            sendPaste,
            modifiersPressed ?? (() => false),
            clipboardFree ?? (() => true),
            inMenuMode ?? (_ => false))
    {
    }

    private WindowsTextOutput(
        Func<nint> getForegroundWindow,
        Func<string, CancellationToken, Task> setClipboard,
        Func<int> sendPaste,
        Func<bool> modifiersPressed,
        Func<bool> clipboardFree,
        Func<nint, bool> inMenuMode)
    {
        _getForegroundWindow = getForegroundWindow;
        _setClipboard = setClipboard;
        _sendPaste = sendPaste;
        _modifiersPressed = modifiersPressed;
        _clipboardFree = clipboardFree;
        _inMenuMode = inMenuMode;
    }

    /// <summary>
    /// Leaves the dictation on the clipboard. SendInput only queues Ctrl+V, and Windows provides
    /// no acknowledgement that the target pasted, so restoring the previous clipboard could make a
    /// busy target paste stale content and lose the dictation.
    /// </summary>
    internal async Task<DeliveryResult> DeliverAsync(
        WindowTarget target,
        string text,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
            return DeliveryResult.NoSpeech;

        cancellationToken.ThrowIfCancellationRequested();
        await _setClipboard(text, cancellationToken).ConfigureAwait(false);

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
        if (!await WaitForClipboardQuietAsync(cancellationToken).ConfigureAwait(false))
            return DeliveryResult.CopiedToClipboard;
        cancellationToken.ThrowIfCancellationRequested();
        if (_getForegroundWindow() != target.Hwnd)
            return DeliveryResult.CopiedToClipboard;
        // A menu bar or menu that holds the keyboard (a lone Alt press, for one) would swallow Ctrl+V without a trace.
        if (_inMenuMode(target.Hwnd))
            return DeliveryResult.TargetMenuOpen;

        try
        {
            if (_sendPaste() != 4)
                return DeliveryResult.CopiedToClipboard;
        }
        catch
        {
            return DeliveryResult.CopiedToClipboard;
        }
        return DeliveryResult.PasteSent;
    }

    private async Task<bool> WaitForClipboardQuietAsync(CancellationToken cancellationToken)
    {
        for (int poll = 0, quiet = 0; poll < ClipboardSettlePolls; poll++)
        {
            await Task.Delay(ClipboardPollInterval, cancellationToken).ConfigureAwait(false);
            quiet = _clipboardFree() ? quiet + 1 : 0;
            if (quiet == ClipboardQuietPolls)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Leaves <paramref name="text"/> on the clipboard (out of the clipboard history) and sends no paste, for a
    /// transcript the user must paste by hand. Throws when the clipboard cannot be set.
    /// </summary>
    internal Task CopyAsync(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _setClipboard(text, cancellationToken);
    }

    private static async Task SetClipboardAsync(
        DispatcherQueue dispatcher,
        string text,
        CancellationToken cancellationToken)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                await dispatcher.InvokeAsync(
                    () =>
                    {
                        var package = new DataPackage();
                        package.SetText(text);
                        if (!Clipboard.SetContentWithOptions(package, CreateClipboardOptions()))
                            throw new InvalidOperationException("Could not set clipboard content.");
                        Clipboard.Flush();
                    },
                    cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (Exception exception) when (attempt < ClipboardAttempts
                && exception is COMException or InvalidOperationException)
            {
                // A clipboard monitor holds the clipboard open for a moment, and setting it is refused meanwhile.
                await Task.Delay(ClipboardPollInterval, cancellationToken).ConfigureAwait(false);
            }
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

    private static bool IsClipboardFree()
    {
        if (!OpenClipboard(0))
            return false;
        CloseClipboard();
        return true;
    }

    internal static ClipboardContentOptions CreateClipboardOptions() => new()
    {
        IsAllowedInHistory = false,
        IsRoamable = false
    };

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

    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(nint owner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();
}
