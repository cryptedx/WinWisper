using System.Runtime.InteropServices;
using WinWispr.Core;

namespace WinWispr.Interop;

internal sealed partial class GlobalInput : IDisposable
{
    internal static readonly Hotkey ReplayHotkey = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x56);
    internal bool ReplayHotkeyAvailable { get; private set; }
    internal bool BoardHotkeyAvailable { get; private set; }
    internal bool TaskHotkeyAvailable { get; private set; }
    internal bool EnglishDictationHotkeyAvailable { get; private set; }
    internal bool TranslateHotkeyAvailable { get; private set; }

    private const int BoardHotkeyId = 3;
    private const int TaskHotkeyId = 4;
    private const int EnglishDictationHotkeyId = 5;
    private const int TranslateHotkeyId = 6;
    private const int WhKeyboardLl = 13;
    private const uint WmKeyDown = 0x0100;
    private const uint WmKeyUp = 0x0101;
    private const uint WmSysKeyDown = 0x0104;
    private const uint WmSysKeyUp = 0x0105;
    private const uint WmHotkey = 0x0312;
    private const uint WmAppInput = 0x8001;
    private const uint WmAppCapture = 0x8002;
    private const uint WmAppCaptureCancel = 0x8003;
    private const uint WmQuit = 0x0012;
    private const uint PmNoRemove = 0x0000;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWindows = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private const nint HookSuppress = 1;
    private readonly LowLevelKeyboardProc _keyboardProc;
    private readonly NativeMessageWindow _messageWindow;
    private readonly GlobalInputRouter _router;
    private readonly Func<RecordingState> _state;
    private readonly Func<bool> _isTranslating;
    private readonly Action<GlobalInputAction> _postAction;
    private readonly object _inputGate = new();
    private Hotkey? _activeHotkey;
    private Hotkey? _activeBoardHotkey;
    private Hotkey? _activeTaskHotkey;
    private Hotkey? _activeEnglishDictationHotkey;
    private Hotkey? _activeTranslateHotkey;
    private nint _keyboardHook;
    private Thread? _keyboardHookThread;
    private uint _keyboardHookThreadId;
    private int _keyboardHookStartResult;
    private int _keyboardHookStopRequested;
    private bool _disposed;
    private TaskCompletionSource<Hotkey?>? _capture;
    private CancellationTokenRegistration _captureCancellation;
    private HotkeyModifiers _captureModifiers;

    /// <param name="isTranslating">
    /// Whether a selection translation reads or translates right now. Esc is recognized globally (and swallowed)
    /// only then, so it never disturbs other applications otherwise. Called on the keyboard hook thread.
    /// </param>
    internal GlobalInput(Func<RecordingState> state, Action<GlobalInputAction> postAction, Func<bool>? isTranslating = null)
    {
        _state = state;
        _isTranslating = isTranslating ?? (() => false);
        _postAction = postAction;
        _router = new GlobalInputRouter();
        _messageWindow = new NativeMessageWindow(OnWindowMessage);
        _keyboardProc = KeyboardProcedure;
    }

    internal bool Start(Hotkey hotkey) => Start(hotkey, default, default);

    internal bool Start(
        Hotkey hotkey,
        Hotkey boardHotkey,
        Hotkey taskHotkey,
        Hotkey englishDictationHotkey = default,
        Hotkey translateHotkey = default)
    {
        lock (_inputGate)
        {
            if (_disposed || !HotkeyRegistration.IsValid(hotkey) || _activeHotkey is not null)
                return false;
        }

        if (_keyboardHookThread is null && !StartKeyboardHook())
            return false;

        ReplayHotkeyAvailable = hotkey != ReplayHotkey
            && RegisterHotKey(_messageWindow.Handle, 2, ToNativeModifiers(ReplayHotkey.Modifiers), ReplayHotkey.VirtualKey);
        BoardHotkeyAvailable = IsUsableOptionalHotkey(boardHotkey, hotkey, ReplayHotkey)
            && RegisterHotKey(_messageWindow.Handle, 3, ToNativeModifiers(boardHotkey.Modifiers), boardHotkey.VirtualKey);
        TaskHotkeyAvailable = IsUsableOptionalHotkey(taskHotkey, hotkey, ReplayHotkey, boardHotkey)
            && RegisterHotKey(_messageWindow.Handle, 4, ToNativeModifiers(taskHotkey.Modifiers), taskHotkey.VirtualKey);
        EnglishDictationHotkeyAvailable = IsUsableOptionalHotkey(englishDictationHotkey, hotkey, ReplayHotkey, boardHotkey, taskHotkey)
            && RegisterHotKey(
                _messageWindow.Handle, EnglishDictationHotkeyId,
                ToNativeModifiers(englishDictationHotkey.Modifiers), englishDictationHotkey.VirtualKey);
        TranslateHotkeyAvailable = IsUsableOptionalHotkey(
                translateHotkey, hotkey, ReplayHotkey, boardHotkey, taskHotkey, englishDictationHotkey)
            && RegisterHotKey(
                _messageWindow.Handle, TranslateHotkeyId,
                ToNativeModifiers(translateHotkey.Modifiers), translateHotkey.VirtualKey);
        // The optional hotkeys stay usable when another program holds the primary chord;
        // the caller asks the user for a new primary hotkey through TryReplace.
        bool primaryRegistered = RegisterNative(hotkey);

        lock (_inputGate)
        {
            if (!_disposed)
            {
                _activeHotkey = primaryRegistered ? hotkey : null;
                _activeBoardHotkey = BoardHotkeyAvailable ? boardHotkey : null;
                _activeTaskHotkey = TaskHotkeyAvailable ? taskHotkey : null;
                _activeEnglishDictationHotkey = EnglishDictationHotkeyAvailable ? englishDictationHotkey : null;
                _activeTranslateHotkey = TranslateHotkeyAvailable ? translateHotkey : null;
                return primaryRegistered;
            }
        }

        if (primaryRegistered)
            UnregisterNative(hotkey);
        UnregisterHotKey(_messageWindow.Handle, TranslateHotkeyId);
        UnregisterHotKey(_messageWindow.Handle, EnglishDictationHotkeyId);
        UnregisterHotKey(_messageWindow.Handle, 4);
        UnregisterHotKey(_messageWindow.Handle, 3);
        UnregisterHotKey(_messageWindow.Handle, 2);
        ReplayHotkeyAvailable = false;
        BoardHotkeyAvailable = false;
        TaskHotkeyAvailable = false;
        EnglishDictationHotkeyAvailable = false;
        TranslateHotkeyAvailable = false;
        return false;
    }

    internal bool TryReplace(Hotkey hotkey, Action<Hotkey> persist)
    {
        if (hotkey == ReplayHotkey)
            return false;

        Hotkey? active;
        lock (_inputGate)
        {
            if (_disposed)
                return false;
            active = _activeHotkey;
        }

        if (_keyboardHookThread is null && !StartKeyboardHook())
            return false;

        var transaction = new HotkeyRegistration(
            active,
            RegisterNative,
            UnregisterNative,
            persist);
        bool replaced = transaction.TryReplace(hotkey);
        lock (_inputGate)
            _activeHotkey = transaction.Active;
        return replaced;
    }

    internal bool TryReplaceBoard(Hotkey hotkey, Action<Hotkey> persist) =>
        TryReplaceOptional(hotkey, BoardHotkeyId, ref _activeBoardHotkey, persist);

    internal bool TryReplaceTask(Hotkey hotkey, Action<Hotkey> persist) =>
        TryReplaceOptional(hotkey, TaskHotkeyId, ref _activeTaskHotkey, persist);

    internal bool TryReplaceEnglishDictation(Hotkey hotkey, Action<Hotkey> persist) =>
        TryReplaceOptional(hotkey, EnglishDictationHotkeyId, ref _activeEnglishDictationHotkey, persist);

    internal bool TryReplaceTranslate(Hotkey hotkey, Action<Hotkey> persist) =>
        TryReplaceOptional(hotkey, TranslateHotkeyId, ref _activeTranslateHotkey, persist);

    internal Task<Hotkey?> CaptureHotkeyAsync(CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<Hotkey?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_inputGate)
        {
            if (_disposed || _capture is not null)
                return Task.FromResult<Hotkey?>(null);
            _capture = completion;
            _captureModifiers = HotkeyModifiers.None;
        }

        _captureCancellation = cancellationToken.Register(
            static state =>
            {
                var input = (GlobalInput)state!;
                PostMessage(input._messageWindow.Handle, WmAppCaptureCancel, 0, 0);
            },
            this);
        if (cancellationToken.IsCancellationRequested)
            PostMessage(_messageWindow.Handle, WmAppCaptureCancel, 0, 0);
        return completion.Task;
    }

    public void Dispose()
    {
        lock (_inputGate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        StopKeyboardHookThread();

        Hotkey? activeHotkey;
        lock (_inputGate)
        {
            activeHotkey = _activeHotkey;
            _activeHotkey = null;
        }

        if (activeHotkey is Hotkey hotkey)
            UnregisterNative(hotkey);
        UnregisterHotKey(_messageWindow.Handle, 2);
        UnregisterHotKey(_messageWindow.Handle, 3);
        UnregisterHotKey(_messageWindow.Handle, 4);
        UnregisterHotKey(_messageWindow.Handle, EnglishDictationHotkeyId);
        UnregisterHotKey(_messageWindow.Handle, TranslateHotkeyId);
        ReplayHotkeyAvailable = false;
        BoardHotkeyAvailable = false;
        TaskHotkeyAvailable = false;
        EnglishDictationHotkeyAvailable = false;
        TranslateHotkeyAvailable = false;
        CompleteCapture(null);
        _messageWindow.Dispose();
    }

    private nint OnWindowMessage(uint message, nint wParam, nint lParam)
    {
        if (message == WmHotkey)
        {
            HookDecision decision;
                lock (_inputGate)
                    decision = _capture is not null ? default : wParam switch
                    {
                        2 => GlobalInputRouter.OnReplayHotkey(_state()),
                        3 => GlobalInputRouter.OnBoardHotkey(),
                        4 => GlobalInputRouter.OnTaskHotkey(),
                        EnglishDictationHotkeyId => GlobalInputRouter.OnEnglishDictationHotkey(),
                        TranslateHotkeyId => GlobalInputRouter.OnTranslateHotkey(),
                        _ => GlobalInputRouter.OnRegisteredHotkey()
                    };
            PostAction(decision.Action);
            return 1;
        }

        if (message == WmAppInput)
        {
            _postAction((GlobalInputAction)wParam);
            return 1;
        }

        if (message == WmAppCapture)
        {
            CompleteCapture(new Hotkey((HotkeyModifiers)lParam, unchecked((uint)wParam.ToInt64())));
            return 1;
        }

        if (message == WmAppCaptureCancel)
        {
            CompleteCapture(null);
            return 1;
        }

        return 0;
    }

    private nint KeyboardProcedure(int code, nint wParam, nint lParam)
    {
        if (code < 0)
            return CallNextHookEx(0, code, wParam, lParam);

        HookDecision decision = default;
        IEnumerable<GlobalInputAction> releases = [];
        bool suppress = false;
        lock (_inputGate)
        {
            if (_disposed)
                return CallNextHookEx(0, code, wParam, lParam);

            KbdLlHookStruct data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            uint keyboardMessage = unchecked((uint)wParam.ToInt64());
            bool keyDown = keyboardMessage is WmKeyDown or WmSysKeyDown;
            bool keyUp = keyboardMessage is WmKeyUp or WmSysKeyUp;

            if (_capture is not null)
            {
                if (keyDown && data.VirtualKey == 0x1B)
                {
                    PostMessage(_messageWindow.Handle, WmAppCaptureCancel, 0, 0);
                    suppress = true;
                }
                else if (keyDown && TryGetModifierVirtualKey(data.VirtualKey, out HotkeyModifiers modifierDown))
                {
                    _captureModifiers |= modifierDown;
                }
                else if (keyDown)
                {
                    PostMessage(
                        _messageWindow.Handle,
                        WmAppCapture,
                        unchecked((nint)data.VirtualKey),
                        unchecked((nint)(uint)_captureModifiers));
                    suppress = true;
                }
                else if (keyUp && TryGetModifierVirtualKey(data.VirtualKey, out HotkeyModifiers modifierUp))
                {
                    _captureModifiers &= ~modifierUp;
                }
            }
            else
            {
                if (keyDown)
                    decision = _router.OnKeyDown(
                        data.VirtualKey, _state(), data.VirtualKey == 0x1B && _isTranslating());
                else if (keyUp)
                {
                    decision = _router.OnKeyUp(data.VirtualKey, _state());
                    if (!decision.Suppress)
                        releases = GlobalInputRouter.OnHotkeyKeyUp(
                            data.VirtualKey,
                            _activeHotkey?.VirtualKey,
                            _activeTaskHotkey?.VirtualKey,
                            _activeEnglishDictationHotkey?.VirtualKey).ToArray();
                }

                suppress = decision.Suppress;
            }
        }

        if (decision.Action != GlobalInputAction.None)
            PostAction(decision.Action);
        foreach (GlobalInputAction release in releases)
            PostAction(release);
        return suppress ? HookSuppress : CallNextHookEx(0, code, wParam, lParam);
    }

    private void PostAction(GlobalInputAction action)
    {
        if (action != GlobalInputAction.None)
            PostMessage(_messageWindow.Handle, WmAppInput, (nint)action, 0);
    }

    private void CompleteCapture(Hotkey? hotkey)
    {
        TaskCompletionSource<Hotkey?>? completion;
        CancellationTokenRegistration cancellation;
        lock (_inputGate)
        {
            completion = _capture;
            if (completion is null)
                return;
            _capture = null;
            cancellation = _captureCancellation;
            _captureCancellation = default;
            _captureModifiers = HotkeyModifiers.None;
        }

        cancellation.Dispose();
        completion.TrySetResult(hotkey is Hotkey candidate && HotkeyRegistration.IsValid(candidate)
            ? candidate
            : null);
    }

    private bool StartKeyboardHook()
    {
        Volatile.Write(ref _keyboardHookStartResult, 0);
        Volatile.Write(ref _keyboardHookStopRequested, 0);
        var ready = new ManualResetEventSlim(false);
        var thread = new Thread(() => KeyboardHookThreadProcedure(ready))
        {
            IsBackground = true,
            Name = "WinWispr.KeyboardHook"
        };
        _keyboardHookThread = thread;
        thread.Start();

        if (!ready.Wait(TimeSpan.FromSeconds(5))
            || Volatile.Read(ref _keyboardHookStartResult) != 1)
        {
            StopKeyboardHookThread();
            return false;
        }

        return true;
    }

    private void StopKeyboardHookThread()
    {
        Thread? thread = _keyboardHookThread;
        if (thread is null)
            return;

        Volatile.Write(ref _keyboardHookStopRequested, 1);
        uint threadId = Volatile.Read(ref _keyboardHookThreadId);
        if (threadId != 0)
            PostThreadMessage(threadId, WmQuit, 0, 0);
        if (thread != Thread.CurrentThread)
            thread.Join();

        _keyboardHookThread = null;
    }

    private void KeyboardHookThreadProcedure(ManualResetEventSlim ready)
    {
        try
        {
            Volatile.Write(ref _keyboardHookThreadId, GetCurrentThreadId());
            PeekMessage(out NativeMessage message, 0, 0, 0, PmNoRemove);
            _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, GetModuleHandle(null), 0);
            Volatile.Write(ref _keyboardHookStartResult, _keyboardHook == 0 ? -1 : 1);
            ready.Set();

            if (_keyboardHook == 0 || Volatile.Read(ref _keyboardHookStopRequested) != 0)
                return;

            while (GetMessage(out message, 0, 0, 0) > 0)
            {
            }
        }
        catch
        {
            Volatile.Write(ref _keyboardHookStartResult, -1);
            if (!ready.IsSet)
                ready.Set();
        }
        finally
        {
            if (_keyboardHook != 0)
            {
                UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = 0;
            }

            Volatile.Write(ref _keyboardHookThreadId, 0);
        }
    }

    internal static bool TryGetModifierVirtualKey(uint virtualKey, out HotkeyModifiers modifier)
    {
        modifier = virtualKey switch
        {
            0x10 or 0xA0 or 0xA1 => HotkeyModifiers.Shift,
            0x11 or 0xA2 or 0xA3 => HotkeyModifiers.Control,
            0x12 or 0xA4 or 0xA5 => HotkeyModifiers.Alt,
            0x5B or 0x5C => HotkeyModifiers.Windows,
            _ => HotkeyModifiers.None
        };
        return modifier != HotkeyModifiers.None;
    }

    private bool RegisterNative(Hotkey hotkey) =>
        RegisterHotKey(_messageWindow.Handle, 1, ToNativeModifiers(hotkey.Modifiers), hotkey.VirtualKey);

    private void UnregisterNative(Hotkey hotkey) =>
        UnregisterHotKey(_messageWindow.Handle, 1);

    private bool TryReplaceOptional(
        Hotkey candidate,
        int id,
        ref Hotkey? active,
        Action<Hotkey> persist)
    {
        Hotkey? primary;
        lock (_inputGate)
        {
            if (_disposed || !HotkeyRegistration.IsValid(candidate)
                || candidate == ReplayHotkey
                || candidate == _activeHotkey
                || (id != BoardHotkeyId && candidate == _activeBoardHotkey)
                || (id != TaskHotkeyId && candidate == _activeTaskHotkey)
                || (id != EnglishDictationHotkeyId && candidate == _activeEnglishDictationHotkey)
                || (id != TranslateHotkeyId && candidate == _activeTranslateHotkey))
                return false;
            primary = active;
        }

        if (primary is Hotkey previous)
            UnregisterOptional(previous, id);
        if (!RegisterOptional(candidate, id))
        {
            bool restored = false;
            if (primary is Hotkey restore)
                restored = RegisterOptional(restore, id);
            SetOptionalAvailability(id, restored);
            lock (_inputGate)
                active = restored ? primary : null;
            return false;
        }

        try
        {
            persist(candidate);
        }
        catch
        {
            UnregisterOptional(candidate, id);
            bool restored = primary is Hotkey restore && RegisterOptional(restore, id);
            SetOptionalAvailability(id, restored);
            lock (_inputGate)
                active = restored ? primary : null;
            return false;
        }
        lock (_inputGate)
            active = candidate;
        SetOptionalAvailability(id, true);
        return true;
    }

    private void SetOptionalAvailability(int id, bool available)
    {
        switch (id)
        {
            case BoardHotkeyId:
                BoardHotkeyAvailable = available;
                break;
            case TaskHotkeyId:
                TaskHotkeyAvailable = available;
                break;
            case TranslateHotkeyId:
                TranslateHotkeyAvailable = available;
                break;
            default:
                EnglishDictationHotkeyAvailable = available;
                break;
        }
    }

    private bool RegisterOptional(Hotkey hotkey, int id) =>
        RegisterHotKey(_messageWindow.Handle, id, ToNativeModifiers(hotkey.Modifiers), hotkey.VirtualKey);

    private void UnregisterOptional(Hotkey hotkey, int id) => UnregisterHotKey(_messageWindow.Handle, id);

    private static bool IsUsableOptionalHotkey(Hotkey candidate, Hotkey primary, Hotkey replay, params Hotkey[] others) =>
        HotkeyRegistration.IsValid(candidate)
        && candidate != primary
        && candidate != replay
        && !others.Contains(candidate);

    private static uint ToNativeModifiers(HotkeyModifiers modifiers)
    {
        uint result = ModNoRepeat;
        if ((modifiers & HotkeyModifiers.Alt) != 0)
            result |= ModAlt;
        if ((modifiers & HotkeyModifiers.Control) != 0)
            result |= ModControl;
        if ((modifiers & HotkeyModifiers.Shift) != 0)
            result |= ModShift;
        if ((modifiers & HotkeyModifiers.Windows) != 0)
            result |= ModWindows;
        return result;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        internal uint VirtualKey;
        internal uint ScanCode;
        internal uint Flags;
        internal uint Time;
        internal nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        internal nint Window;
        internal uint Message;
        internal nint WParam;
        internal nint LParam;
        internal uint Time;
        internal int PointX;
        internal int PointY;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint LowLevelKeyboardProc(int code, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(nint handle, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(nint handle, int id);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    private static extern nint SetWindowsHookEx(
        int hookType,
        LowLevelKeyboardProc procedure,
        nint module,
        uint threadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
    private static extern bool PostMessage(nint handle, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PeekMessage(
        out NativeMessage message,
        nint handle,
        uint minimumMessage,
        uint maximumMessage,
        uint removeMessage);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetMessage(
        out NativeMessage message,
        nint handle,
        uint minimumMessage,
        uint maximumMessage);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(
        uint threadId,
        uint message,
        nint wParam,
        nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? moduleName);
}
