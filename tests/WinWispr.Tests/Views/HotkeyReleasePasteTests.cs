using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Core;
using WinWispr.Interop;

namespace WinWispr.Tests.Views;

/// <summary>
/// Presses an Alt hotkey over real editors, releases its keys in the orders people do, and then sends the Ctrl+V that
/// WinWispr sends. Windows consumes the hotkey's key, so without WinWispr's menu mask the editor sees a lone Alt press
/// and release: Notepad++ enters its menu bar and Windows 11 Notepad moves the focus to its title bar, and the paste
/// goes nowhere although the caret still shows in the text. The hotkeys are registered through
/// <see cref="GlobalInput"/>, WinWispr's own hook and registration. A control run registers the same chords without
/// the hook and only records how the editors react to them. Writes the matrix to the evidence file. Needs the
/// logged-in desktop, like <see cref="DictationEndToEndTests"/>.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class HotkeyReleasePasteTests
{
    private const int Trials = 2;
    private const uint ProbeKey = 0x51;
    private static readonly string Evidence = Path.Combine(Path.GetTempPath(), "winwispr-hotkey-release-evidence.txt");

    // K = the hotkey's key, A = Alt, S = Shift, C = Ctrl, in release order; a comma separates two presses of a
    // Toggle session. "hold:" holds the key with auto-repeat first, as Hold mode does. "burst:" sends the whole chord
    // in one SendInput call, as macro tools and remote sessions do; the mask can then still come after Alt's release,
    // so bursts are only recorded. Unmasked, Windows 11 Notepad lost the paste when Alt went up first or before Ctrl,
    // and Notepad++ when Alt went up first.
    private static readonly string[] ShiftAltSequences =
        ["KAS", "KSA", "AKS", "ASK", "SAK", "AKS,AKS", "burst:AKS", "hold:AKS"];

    private static readonly string[] ControlAltSequences = ["KAC", "AKC", "CAK", "burst:KAC"];

    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    public void Ctrl_v_pastes_after_every_release_order_of_an_alt_hotkey()
    {
        File.WriteAllText(Evidence, $"{DateTimeOffset.UtcNow:O}{Environment.NewLine}");
        var lost = new List<string>();
        int controlLost = 0;
        foreach (string targetName in new[] { "notepad", "notepadpp" })
        {
            Target? target = null;
            try
            {
                target = Target.Create(targetName);
            }
            catch (Exception exception) when (targetName == "notepadpp")
            {
                Record($"# target={targetName} unavailable: {exception.GetType().Name}: {exception.Message}");
                continue;
            }

            using (target)
            {
                foreach (bool masked in new[] { true, false })
                {
                    List<string> shiftAlt = Run(target, targetName, HotkeyModifiers.Shift | HotkeyModifiers.Alt,
                        ShiftAltSequences, masked);
                    List<string> controlAlt = Run(target, targetName, HotkeyModifiers.Control | HotkeyModifiers.Alt,
                        ControlAltSequences, masked);
                    if (masked)
                        lost.AddRange([.. shiftAlt, .. controlAlt]);
                    else
                        controlLost += shiftAlt.Count + controlAlt.Count;
                }
            }
        }

        Assert.AreEqual(0, lost.Count, "Pastes lost after a WinWispr hotkey: " + string.Join("; ", lost));
        if (controlLost == 0)
            Assert.Inconclusive("Without the mask no paste was lost either, so the editors no longer react to a lone Alt.");
    }

    private static List<string> Run(Target target, string targetName, HotkeyModifiers modifiers, string[] sequences, bool masked)
    {
        var lost = new List<string>();
        var hotkey = new Hotkey(modifiers, ProbeKey);
        using HotkeyOwner owner = (masked ? new WinWisprHotkey(hotkey) : (HotkeyOwner)new PlainHotkey(hotkey)).Start();
        string arm = masked ? "WinWispr" : "control";
        Record($"# target={targetName} chord={modifiers} arm={arm} registered={owner.Registered}");
        if (!owner.Registered)
            throw new AssertFailedException($"{modifiers}+Q could not be registered; another program holds it.");
        foreach (string sequence in sequences)
        {
            int pasted = 0;
            for (int trial = 1; trial <= Trials; trial++)
            {
                target.Reset();
                int pressesBefore = owner.Presses;
                string[] presses = sequence.Split(',');
                for (int press = 0; press < presses.Length; press++)
                {
                    PressChord(modifiers, presses[press]);
                    if (press < presses.Length - 1)
                        Thread.Sleep(1000);
                }
                Thread.Sleep(600);
                string afterHotkey = GuiSnapshot();
                string focused = FocusedElement();
                string token = $"probe{trial}x{Environment.TickCount64 % 100000}";
                SetClipboardText(token);
                Thread.Sleep(150);
                bool sameForeground = GetForegroundWindow() == target.Window;
                int sent = NativeKeyboard.SendControlChord(NativeKeyboard.V);
                Thread.Sleep(900);
                bool ok = target.Text().Contains(token, StringComparison.Ordinal);
                if (ok)
                    pasted++;
                else if (!sequence.StartsWith("burst:", StringComparison.Ordinal))
                    lost.Add($"{targetName} {modifiers} {sequence} #{trial}");
                if (owner.Presses - pressesBefore != presses.Length)
                    lost.Add($"{targetName} {modifiers} {sequence} #{trial}: {owner.Presses - pressesBefore} of {presses.Length} presses arrived");
                Record($"seq={sequence} trial={trial} pasted={ok} presses={owner.Presses - pressesBefore} sent={sent} "
                    + $"fgSame={sameForeground} afterHotkey=[{afterHotkey}] focused=[{focused}] afterPaste=[{GuiSnapshot()}]");
            }
            Record($"## target={targetName} chord={modifiers} arm={arm} seq={sequence} pasted {pasted}/{Trials}");
        }
        return lost;
    }

    private static void Record(string line) => File.AppendAllLines(Evidence, [line]);

    /// <summary>
    /// Presses Shift or Ctrl before Alt, then the key, and releases the keys in the given order. Every key it pressed
    /// goes up again if a step fails, so no key stays held on the shared desktop.
    /// </summary>
    private static void PressChord(HotkeyModifiers chord, string press)
    {
        bool burst = press.StartsWith("burst:", StringComparison.Ordinal);
        bool hold = press.StartsWith("hold:", StringComparison.Ordinal);
        string releaseOrder = press[(press.IndexOf(':') + 1)..];
        var modifiers = new List<(char Letter, ushort VirtualKey)>();
        if ((chord & HotkeyModifiers.Control) != 0)
            modifiers.Add(('C', 0xA2));
        if ((chord & HotkeyModifiers.Shift) != 0)
            modifiers.Add(('S', 0xA0));
        if ((chord & HotkeyModifiers.Alt) != 0)
            modifiers.Add(('A', 0xA4));
        ushort[] downs = [.. modifiers.Select(modifier => modifier.VirtualKey), (ushort)ProbeKey];
        ushort[] ups = [.. releaseOrder.Select(letter =>
            letter == 'K' ? (ushort)ProbeKey : modifiers.First(modifier => modifier.Letter == letter).VirtualKey)];
        try
        {
            if (burst)
            {
                SendKeys([.. downs.Select(key => PhysicalKey(key, keyUp: false)),
                    .. ups.Select(key => PhysicalKey(key, keyUp: true))]);
                return;
            }
            foreach (ushort key in downs)
            {
                SendKeys([PhysicalKey(key, keyUp: false)]);
                Thread.Sleep(45);
            }
            // A held key repeats its key-down about every 33 ms after the typematic delay.
            for (int repeat = 0; hold && repeat < 20; repeat++)
            {
                Thread.Sleep(repeat == 0 ? 500 : 33);
                SendKeys([PhysicalKey((ushort)ProbeKey, keyUp: false)]);
            }
            Thread.Sleep(90);
            foreach (ushort key in ups)
            {
                SendKeys([PhysicalKey(key, keyUp: true)]);
                Thread.Sleep(45);
            }
        }
        catch
        {
            foreach (ushort key in downs)
            {
                try
                {
                    SendKeys([PhysicalKey(key, keyUp: true)]);
                }
                catch (AssertFailedException)
                {
                }
            }
            throw;
        }
    }

    // Like a keyboard: the virtual key together with its scan code.
    private static INPUT PhysicalKey(ushort virtualKey, bool keyUp) =>
        Key(virtualKey, (ushort)MapVirtualKey(virtualKey, 0), keyUp ? 2u : 0u);

    private static void TapKey(ushort virtualKey)
    {
        SendKeys([Key(virtualKey, 0, 0)]);
        Thread.Sleep(30);
        SendKeys([Key(virtualKey, 0, 2)]);
        Thread.Sleep(30);
    }

    private static void ControlChord(ushort virtualKey) =>
        SendKeys([Key(0x11, 0, 0), Key(virtualKey, 0, 0), Key(virtualKey, 0, 2), Key(0x11, 0, 2)]);

    private static INPUT Key(ushort virtualKey, ushort scanCode, uint flags) => new()
    {
        Type = 1,
        Data = new InputUnion { Keyboard = new KEYBDINPUT { VirtualKey = virtualKey, ScanCode = scanCode, Flags = flags } }
    };

    private static void SendKeys(INPUT[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length)
            throw new AssertFailedException("SendInput failed.");
    }

    private static string GuiSnapshot()
    {
        uint thread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var info = new GUITHREADINFO { Size = Marshal.SizeOf<GUITHREADINFO>() };
        if (!GetGUIThreadInfo(thread, ref info))
            return "unavailable";
        var flags = new List<string>();
        if ((info.Flags & 0x1) != 0) flags.Add("CARET");
        if ((info.Flags & 0x4) != 0) flags.Add("INMENUMODE");
        if ((info.Flags & 0x8) != 0) flags.Add("SYSMENU");
        if ((info.Flags & 0x10) != 0) flags.Add("POPUPMENU");
        return $"flags={string.Join('|', flags)} focus={ClassOf(info.Focus)} menuOwner={ClassOf(info.MenuOwner)}";
    }

    private static string FocusedElement()
    {
        try
        {
            AutomationElement? element = AutomationElement.FocusedElement;
            if (element is null)
                return "none";
            string name = element.Current.Name ?? "";
            return $"{element.Current.ControlType.ProgrammaticName}:{element.Current.ClassName}:{(name.Length > 30 ? name[..30] : name)}";
        }
        catch (Exception exception)
        {
            return "error:" + exception.GetType().Name;
        }
    }

    private static string ClassOf(nint window)
    {
        if (window == 0)
            return "-";
        var name = new StringBuilder(256);
        _ = GetClassName(window, name, name.Capacity);
        return name.ToString();
    }

    private static void SetClipboardText(string text)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            if (OpenClipboard(0))
            {
                try
                {
                    _ = EmptyClipboard();
                    byte[] bytes = Encoding.Unicode.GetBytes(text + "\0");
                    nint memory = GlobalAlloc(0x0002, (nuint)bytes.Length);
                    Marshal.Copy(bytes, 0, GlobalLock(memory), bytes.Length);
                    _ = GlobalUnlock(memory);
                    if (SetClipboardData(13, memory) == 0)
                        throw new AssertFailedException("SetClipboardData failed.");
                    return;
                }
                finally
                {
                    _ = CloseClipboard();
                }
            }
            Thread.Sleep(20);
        }
        throw new AssertFailedException("The clipboard stayed busy.");
    }

    private abstract class Target : IDisposable
    {
        internal nint Window { get; init; }

        internal static Target Create(string name) => name switch
        {
            "notepad" => new NotepadTarget(),
            "notepadpp" => new NotepadPlusPlusTarget(),
            _ => throw new ArgumentException(name)
        };

        internal abstract string Text();

        protected abstract AutomationElement Editor();

        /// <summary>Leaves any menu or access-key mode, puts the caret back into the editor and empties it.</summary>
        internal void Reset()
        {
            TapKey(0x1B);
            TapKey(0x1B);
            BringToFront(Window);
            Thread.Sleep(250);
            try
            {
                Editor().SetFocus();
            }
            catch (Exception)
            {
            }
            Thread.Sleep(200);
            for (int attempt = 0; attempt < 5 && Text().Length > 0; attempt++)
            {
                ControlChord(0x41);
                Thread.Sleep(60);
                TapKey(0x2E);
                Thread.Sleep(200);
            }
            if (GetForegroundWindow() != Window)
                throw new AssertFailedException("The editor lost the foreground to " + ClassOf(GetForegroundWindow()));
            Thread.Sleep(300);
        }

        protected static nint WaitForWindow(string processName, Func<Process, bool> match)
        {
            Stopwatch timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(40))
            {
                foreach (Process candidate in Process.GetProcessesByName(processName))
                {
                    using (candidate)
                    {
                        if (candidate.MainWindowHandle != 0 && match(candidate))
                            return candidate.MainWindowHandle;
                    }
                }
                Thread.Sleep(250);
            }
            throw new AssertFailedException(processName + " did not open its window.");
        }

        public virtual void Dispose()
        {
            _ = GetWindowThreadProcessId(Window, out uint processId);
            try
            {
                using Process process = Process.GetProcessById((int)processId);
                process.Kill(entireProcessTree: true);
            }
            catch (ArgumentException)
            {
            }
        }
    }

    private sealed class NotepadTarget : Target
    {
        internal NotepadTarget()
        {
            string file = Path.Combine(Path.GetTempPath(), $"hotkey-probe-{Environment.ProcessId}.txt");
            File.WriteAllText(file, "");
            Process.Start("notepad.exe", $"\"{file}\"")?.Dispose();
            string name = Path.GetFileNameWithoutExtension(file);
            Window = WaitForWindow("Notepad", process => process.MainWindowTitle.Contains(name, StringComparison.OrdinalIgnoreCase));
            Thread.Sleep(1000);
        }

        protected override AutomationElement Editor() => AutomationElement.FromHandle(Window).FindFirst(
                TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document))
            ?? throw new AssertFailedException("Notepad has no document.");

        internal override string Text() => Editor().TryGetCurrentPattern(TextPattern.Pattern, out object pattern)
            ? ((TextPattern)pattern).DocumentRange.GetText(-1)
            : throw new AssertFailedException("Notepad's document has no text pattern.");
    }

    private sealed class NotepadPlusPlusTarget : Target
    {
        internal NotepadPlusPlusTarget()
        {
            string file = Path.Combine(Path.GetTempPath(), $"hotkey-probe-pp-{Environment.ProcessId}.txt");
            File.WriteAllText(file, "");
            Process.Start(@"C:\Program Files\Notepad++\notepad++.exe", $"-multiInst -nosession \"{file}\"")?.Dispose();
            string name = Path.GetFileName(file);
            Window = WaitForWindow("notepad++", process => process.MainWindowTitle.Contains(name, StringComparison.OrdinalIgnoreCase));
            Thread.Sleep(1000);
        }

        private nint Scintilla() => FindWindowEx(Window, 0, "Scintilla", null);

        protected override AutomationElement Editor() => AutomationElement.FromHandle(Scintilla());

        internal override string Text()
        {
            nint editor = Scintilla();
            int length = (int)SendMessage(editor, 0x000E, 0, 0);
            var buffer = new StringBuilder(length + 1);
            _ = SendMessageText(editor, 0x000D, length + 1, buffer);
            return buffer.ToString();
        }
    }

    /// <summary>Owns a registered chord on a thread with a message loop and counts its presses.</summary>
    private abstract class HotkeyOwner : IDisposable
    {
        private Thread? _thread;
        private uint _threadId;
        private int _presses;

        internal HotkeyOwner Start()
        {
            using var ready = new ManualResetEventSlim();
            Exception? failure = null;
            _thread = new Thread(() =>
            {
                _threadId = GetCurrentThreadId();
                _ = PeekMessage(out _, 0, 0, 0, 0);
                try
                {
                    Registered = Register();
                }
                catch (Exception exception)
                {
                    // Thrown on this thread it would end the test host; the test thread rethrows it.
                    failure = exception;
                    return;
                }
                finally
                {
                    ready.Set();
                }
                while (GetMessage(out MSG message, 0, 0, 0) > 0)
                {
                    if (message.Window == 0 && message.Message == 0x0312)
                        CountPress();
                    _ = DispatchMessage(ref message);
                }
                Unregister();
            })
            {
                IsBackground = true
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            ready.Wait();
            if (failure is not null)
                throw new AssertFailedException("The hotkey owner could not register its chord.", failure);
            return this;
        }

        internal bool Registered { get; private set; }

        internal int Presses => Volatile.Read(ref _presses);

        protected void CountPress() => Interlocked.Increment(ref _presses);

        /// <summary>Runs on the owner's thread.</summary>
        protected abstract bool Register();

        /// <summary>Runs on the owner's thread.</summary>
        protected abstract void Unregister();

        public void Dispose()
        {
            _ = PostThreadMessage(_threadId, 0x0012, 0, 0);
            _thread?.Join(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>The chord registered through WinWispr's <see cref="GlobalInput"/>, with its keyboard hook.</summary>
    private sealed class WinWisprHotkey(Hotkey hotkey) : HotkeyOwner
    {
        private GlobalInput? _input;

        protected override bool Register()
        {
            _input = new GlobalInput(() => RecordingState.Idle, action =>
            {
                if (action == GlobalInputAction.PressPrimary)
                    CountPress();
            });
            return _input.Start(hotkey);
        }

        protected override void Unregister() => _input?.Dispose();
    }

    /// <summary>The chord registered with RegisterHotKey alone, as WinWispr did before it masked the Alt release.</summary>
    private sealed class PlainHotkey(Hotkey hotkey) : HotkeyOwner
    {
        protected override bool Register()
        {
            uint modifiers = 0x4000;
            if ((hotkey.Modifiers & HotkeyModifiers.Alt) != 0)
                modifiers |= 0x1;
            if ((hotkey.Modifiers & HotkeyModifiers.Control) != 0)
                modifiers |= 0x2;
            if ((hotkey.Modifiers & HotkeyModifiers.Shift) != 0)
                modifiers |= 0x4;
            return RegisterHotKey(0, 1, modifiers, hotkey.VirtualKey);
        }

        protected override void Unregister() => _ = UnregisterHotKey(0, 1);
    }

    private static void BringToFront(nint window)
    {
        uint current = GetCurrentThreadId();
        uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        bool attached = foregroundThread != 0 && foregroundThread != current && AttachThreadInput(current, foregroundThread, true);
        try
        {
            _ = ShowWindow(window, 9);
            _ = BringWindowToTop(window);
            _ = SetForegroundWindow(window);
        }
        finally
        {
            if (attached)
                _ = AttachThreadInput(current, foregroundThread, false);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        internal uint Type;
        internal InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        internal KEYBDINPUT Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        internal ushort VirtualKey;
        internal ushort ScanCode;
        internal uint Flags;
        internal uint Time;
        internal nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        internal int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GUITHREADINFO
    {
        internal int Size;
        internal uint Flags;
        internal nint Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        internal RECT CaretRect;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        internal nint Window;
        internal uint Message;
        internal nint WParam, LParam;
        internal uint Time;
        internal int X, Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(nint window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint window, int command);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint thread, uint attachTo, bool attach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder name, int capacity);

    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(nint owner);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    private static extern nint SetClipboardData(uint format, nint memory);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalLock(nint memory);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(nint memory);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(nint window, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG message, nint window, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG message, nint window, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint thread, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref MSG message);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowEx(nint parent, nint after, string className, string? title);

    [DllImport("user32.dll")]
    private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageText(nint window, uint message, nint wParam, StringBuilder text);
}
