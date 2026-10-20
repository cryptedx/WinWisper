using WinWispr.Core;

namespace WinWispr.Interop;

internal enum GlobalInputAction
{
    None,
    PressPrimary,
    ReleasePrimary,
    PressTask,
    ReleaseTask,
    PressEnglishDictation,
    ReleaseEnglishDictation,

    /// <summary>The translate hotkey was pressed. It starts a selection translation, never a dictation session.</summary>
    PressTranslate,

    /// <summary>Esc while a selection translation reads or translates.</summary>
    CancelTranslation,
    RequestCancel,
    ConfirmDiscard,
    ReplayLast,
    OpenBoard
}

internal readonly record struct HookDecision(GlobalInputAction Action, bool Suppress);

/// <summary>
/// Translates keyboard edges into input actions. It owns only keyboard concerns (Esc/Y edges,
/// the cancel latch, suppression); what a hotkey press or release means for the dictation
/// session is decided by <see cref="DictationSession"/>.
/// </summary>
internal sealed class GlobalInputRouter
{
    // ponytail: local five-second latch bridges queued UI messages; use a state callback if timing diverges.
    private static readonly TimeSpan CancelRequestWindow = TimeSpan.FromSeconds(5);
    private const uint Escape = 0x1B;
    private const uint Y = 0x59;
    private bool _escapeDown;
    private bool _yDown;
    private DateTimeOffset? _cancelRequestDeadline;

    internal static HookDecision OnRegisteredHotkey() => new(GlobalInputAction.PressPrimary, Suppress: false);

    internal static HookDecision OnTaskHotkey() => new(GlobalInputAction.PressTask, Suppress: false);

    internal static HookDecision OnEnglishDictationHotkey() => new(GlobalInputAction.PressEnglishDictation, Suppress: false);

    internal static HookDecision OnTranslateHotkey() => new(GlobalInputAction.PressTranslate, Suppress: false);

    internal static HookDecision OnReplayHotkey(RecordingState state) =>
        state == RecordingState.Idle ? new(GlobalInputAction.ReplayLast, false) : default;

    internal static HookDecision OnBoardHotkey() => new(GlobalInputAction.OpenBoard, false);

    /// <summary>
    /// Whether a key-down completes one of the registered hotkeys while Alt or a Windows key is held. Windows consumes
    /// the press of the hotkey's key, so the focused application can see a lone Alt or Windows press: on its release
    /// a menu bar takes the keyboard, or Windows 11 Notepad moves the focus to its title bar, and the paste that
    /// follows goes nowhere. The caller then taps the menu mask while the modifier is still down.
    /// </summary>
    internal static bool NeedsMenuMask(uint virtualKey, HotkeyModifiers held, IEnumerable<Hotkey?> hotkeys) =>
        (held & (HotkeyModifiers.Alt | HotkeyModifiers.Windows)) != 0
        && hotkeys.Any(hotkey => hotkey is Hotkey registered
            && registered.VirtualKey == virtualKey
            && registered.Modifiers == held);

    /// <summary>Every key-up of a hotkey's key is a release; the dictation hotkeys may share a key.</summary>
    internal static IEnumerable<GlobalInputAction> OnHotkeyKeyUp(
        uint virtualKey, uint? primaryVirtualKey, uint? taskVirtualKey, uint? englishDictationVirtualKey = null)
    {
        if (virtualKey == primaryVirtualKey)
            yield return GlobalInputAction.ReleasePrimary;
        if (virtualKey == taskVirtualKey)
            yield return GlobalInputAction.ReleaseTask;
        if (virtualKey == englishDictationVirtualKey)
            yield return GlobalInputAction.ReleaseEnglishDictation;
    }

    internal HookDecision OnKeyDown(uint virtualKey, RecordingState state, bool translating = false)
    {
        bool cancelPending = IsCancelPending(state);
        // A selection translation never runs during a dictation session, so the two Esc meanings cannot collide.
        if (virtualKey == Escape && translating && state == RecordingState.Idle)
        {
            bool first = !_escapeDown;
            _escapeDown = true;
            return new(first ? GlobalInputAction.CancelTranslation : GlobalInputAction.None, Suppress: true);
        }

        if (virtualKey == Escape && state is RecordingState.Starting or RecordingState.Recording or RecordingState.CancelPending)
        {
            bool first = !_escapeDown;
            _escapeDown = true;
            _cancelRequestDeadline = DateTimeOffset.UtcNow + CancelRequestWindow;
            return new(first ? GlobalInputAction.RequestCancel : GlobalInputAction.None, Suppress: true);
        }

        if (virtualKey == Y && cancelPending)
        {
            bool first = !_yDown;
            _yDown = true;
            if (first)
                _cancelRequestDeadline = null;
            return new(first ? GlobalInputAction.ConfirmDiscard : GlobalInputAction.None, Suppress: true);
        }

        return default;
    }

    private bool IsCancelPending(RecordingState state)
    {
        if (state == RecordingState.CancelPending)
            return true;
        if (state is not (RecordingState.Starting or RecordingState.Recording))
        {
            _cancelRequestDeadline = null;
            return false;
        }

        if (_cancelRequestDeadline is DateTimeOffset deadline && deadline > DateTimeOffset.UtcNow)
            return true;

        _cancelRequestDeadline = null;
        return false;
    }

    internal HookDecision OnKeyUp(uint virtualKey, RecordingState state)
    {
        if (virtualKey == Escape && _escapeDown)
        {
            _escapeDown = false;
            return new(GlobalInputAction.None, Suppress: true);
        }

        if (virtualKey == Y && _yDown)
        {
            _yDown = false;
            return new(GlobalInputAction.None, Suppress: true);
        }

        return default;
    }
}

internal sealed class HotkeyRegistration
{
    private const HotkeyModifiers ValidModifiers = HotkeyModifiers.Alt
        | HotkeyModifiers.Control
        | HotkeyModifiers.Shift
        | HotkeyModifiers.Windows;
    private readonly Func<Hotkey, bool> _tryRegister;
    private readonly Action<Hotkey> _unregister;
    private readonly Action<Hotkey> _persist;

    internal HotkeyRegistration(
        Hotkey? active,
        Func<Hotkey, bool> tryRegister,
        Action<Hotkey> unregister,
        Action<Hotkey> persist)
    {
        Active = active;
        _tryRegister = tryRegister;
        _unregister = unregister;
        _persist = persist;
    }

    internal Hotkey? Active { get; private set; }

    internal static bool IsValid(Hotkey hotkey) =>
        hotkey.Modifiers != HotkeyModifiers.None
        && (hotkey.Modifiers & ~ValidModifiers) == 0
        && hotkey.VirtualKey is > 0 and <= byte.MaxValue
        && hotkey.VirtualKey is not 0x1B and not 0x59 and not NativeKeyboard.MenuMask;

    internal bool TryReplace(Hotkey candidate)
    {
        if (!IsValid(candidate))
            return false;

        Hotkey? previous = Active;
        if (previous is Hotkey registered)
            _unregister(registered);
        if (_tryRegister(candidate))
        {
            try
            {
                _persist(candidate);
                Active = candidate;
                return true;
            }
            catch
            {
                _unregister(candidate);
                if (previous is Hotkey fallback)
                    Active = _tryRegister(fallback) ? fallback : null;
                return false;
            }
        }

        _unregister(candidate);
        if (previous is Hotkey restore)
            Active = _tryRegister(restore) ? restore : null;
        return false;
    }
}
