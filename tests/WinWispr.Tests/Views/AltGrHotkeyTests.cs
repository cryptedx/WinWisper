using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;
using WinWispr.Core;
using WinWispr.Interop;

namespace WinWispr.Tests.Views;

/// <summary>
/// Types AltGr characters into a text box on the German layout while WinWispr's hotkeys are registered through
/// <see cref="GlobalInput"/>. Windows reports AltGr as Ctrl+Alt: a keyboard's right Alt arrives together with a left Ctrl
/// that Windows adds itself, so a registered Ctrl+Alt chord catches AltGr plus its key, and the character never reaches
/// the application: with the defaults of 1.11, Ctrl+Alt+E (translate) swallowed € (AltGr+E). A control run registers
/// nothing and only checks that the injected AltGr types the characters; the 1.11 run only records what those defaults
/// swallow, and the test is inconclusive if they swallow nothing. The keys are pressed with scan codes, right Alt as an
/// extended key, like a keyboard. Writes the matrix to the evidence file. Needs the logged-in desktop, like
/// <see cref="DictationEndToEndTests"/>.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class AltGrHotkeyTests
{
    private const ushort RightAlt = 0xA5;
    private const ushort LeftControl = 0xA2;
    private const ushort GermanLanguage = 0x0407;
    private static readonly string Evidence = Path.Combine(Path.GetTempPath(), "winwispr-altgr-evidence.txt");

    // On the German layout: Q is @, E is €, M is µ, 7 is {, 0 is }.
    private static readonly ushort[] SampleKeys = [0x51, 0x45, 0x4D, 0x37, 0x30];

    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    public void AltGr_characters_arrive_while_the_default_hotkeys_are_registered()
    {
        File.WriteAllText(Evidence, $"{DateTimeOffset.UtcNow:O}{Environment.NewLine}");
        Hotkey[] current = Hotkeys(AppSettings.Default);
        Hotkey[] legacyDefaults = Hotkeys(AppSettings.ExistingInstallationDefaults);
        Record("# current defaults: " + string.Join(", ", current.Select(Describe)));
        Record("# 1.11 defaults: " + string.Join(", ", legacyDefaults.Select(Describe)));

        using var installed = InstalledApp.StepAside([.. current, .. legacyDefaults], Record);
        WaitForNotificationsToClose();
        using var target = new DictationEndToEndTests.TextTarget();
        TakeForeground(target);
        nint window = GetForegroundWindow();
        nint layout = GetKeyboardLayout(GetWindowThreadProcessId(window, out _));
        Record($"# layout=0x{layout:X} heldBefore={NativeKeyboard.HeldModifiers()}");
        if ((ushort)layout != GermanLanguage)
            Assert.Inconclusive($"The text box uses layout 0x{layout:X}, not German (0x0407).");

        var keys = new Dictionary<ushort, string>();
        foreach (ushort key in SampleKeys.Concat(current.Select(hotkey => (ushort)hotkey.VirtualKey))
                     .Concat(legacyDefaults.Select(hotkey => (ushort)hotkey.VirtualKey)).Distinct())
        {
            string character = AltGrCharacter(key, layout);
            Record($"# AltGr+{(char)key} types [{character}] on this layout");
            if (character.Length > 0)
                keys[key] = character;
        }
        Assert.IsTrue(keys.ContainsKey(0x45), "AltGr+E types no character on this layout, so the € case cannot be checked.");

        Dictionary<ushort, string> control = Run(target, "control", keys, null);
        Assert.AreEqual(0, control.Count,
            "Injected AltGr did not type its characters even with no hotkey registered: " + string.Join("; ", control.Values));
        Dictionary<ushort, string> legacy = Run(target, "1.11-defaults", keys, legacyDefaults);
        Dictionary<ushort, string> swallowed = Run(target, "current-defaults", keys, current);
        Assert.AreEqual(0, swallowed.Count, "AltGr characters lost to WinWispr's default hotkeys: " + string.Join("; ", swallowed.Values)
            + (legacy.Count == 0 ? "" : $" (with the 1.11 defaults: {string.Join("; ", legacy.Values)})"));
        if (legacy.Count == 0)
            Assert.Inconclusive("The 1.11 defaults swallowed no AltGr character either, so Windows no longer reports AltGr as Ctrl+Alt.");

        // Settings warns about exactly the hotkeys that swallowed a character.
        KeyboardLayout german = KeyboardLayout.Installed().Single(candidate => candidate.Name == "German (Germany)");
        ushort[] predicted = [.. legacyDefaults.Where(hotkey => KeyboardLayout.FindClash(hotkey, [german]) is not null)
            .Select(hotkey => (ushort)hotkey.VirtualKey).Distinct().Order()];
        Record("# Settings warns for AltGr+" + string.Join(", AltGr+", predicted.Select(key => (char)key)));
        CollectionAssert.AreEqual(legacy.Keys.Order().ToArray(), predicted,
            "The clash check in Settings disagrees with the characters the 1.11 defaults swallowed.");
    }

    /// <summary>
    /// Starts the real application with the translate hotkey Ctrl+Alt+E stored, as an installation from 1.11 has it, on
    /// the German layout. The tray notice at startup reports the clash and records it, so it shows only once.
    /// </summary>
    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    public void The_real_application_reports_a_stored_ctrl_alt_e_once_at_startup()
    {
        TaskBoardEndToEndTests.WithProfile((root, instance, exit) =>
        {
            string path = Path.Combine(root, "settings.json");
            var store = new AppSettingsStore(path);
            store.Save(store.Load() with { TranslateHotkey = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x45) });
            AppSettings saved = store.Load();
            Assert.AreEqual("", saved.HotkeyClashesShown);
            using var installed = InstalledApp.StepAside(Hotkeys(saved), Record);
            const string clash = "3+69:€:German (Germany)";

            try
            {
                TaskBoardEndToEndTests.RunApplication(root, instance, exit, ui =>
                    ui.Until(() => ShownClashes(path).Contains(clash), "the startup notice records the clash it reported"));
            }
            finally
            {
                Record("# settings after the real application: " + File.ReadAllText(path));
                // The notice would stay on the idle desktop and keep the foreground from the next desktop test.
                Stopwatch timer = Stopwatch.StartNew();
                while (CloseWinWisprNotifications() > 0 && timer.Elapsed < TimeSpan.FromSeconds(10))
                    Thread.Sleep(500);
                WaitForNotificationsToClose();
            }

            CollectionAssert.AreEqual(new[] { clash }, ShownClashes(path));
        });
    }

    private static string[] ShownClashes(string path)
    {
        try
        {
            return AppSettingsStore.Parse(File.ReadAllText(path)).HotkeyClashesShown
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException)
        {
            return [];
        }
    }

    /// <summary>Primary, board, task, English dictation, translate and replay, in the order <c>GlobalInput.Start</c> takes them.</summary>
    private static Hotkey[] Hotkeys(AppSettings settings) =>
    [
        settings.Hotkey, settings.BoardHotkey, settings.TaskHotkey, settings.EnglishDictationHotkey,
        settings.TranslateHotkey, settings.ReplayHotkey
    ];

    /// <summary>Brings the text box to the front, and names the window that keeps the foreground when it cannot.</summary>
    private static void TakeForeground(DictationEndToEndTests.TextTarget target)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                target.BringToFront();
                return;
            }
            catch (AssertFailedException) when (attempt < 3)
            {
                Record($"# foreground attempt {attempt} failed; the foreground is {DescribeForeground()}");
                WaitForNotificationsToClose();
                // A click activates a window whatever holds the foreground, as it does for a user.
                ClickInto(FindWindow(null, "WinWispr dictation target"));
            }
            catch (AssertFailedException exception)
            {
                throw new AssertFailedException($"{exception.Message} Foreground: {DescribeForeground()}.", exception);
            }
        }
    }

    /// <summary>
    /// Waits while a notification holds the foreground, such as the clash notice the real application shows: the shared
    /// desktop has no user, Windows keeps a notification on screen while the user is idle, and it keeps the foreground
    /// from the next desktop test. Closes WinWispr's notifications and taps the unassigned key 0xE8 (applications
    /// ignore it; WinWispr's menu mask uses it) so that Windows counts the desktop as active.
    /// </summary>
    private static void WaitForNotificationsToClose()
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (IsNotificationInFront() && timer.Elapsed < TimeSpan.FromSeconds(15))
        {
            CloseWinWisprNotifications();
            SendKeys([PhysicalKey(NativeKeyboard.MenuMask, keyUp: false), PhysicalKey(NativeKeyboard.MenuMask, keyUp: true)]);
            Thread.Sleep(1000);
        }
        if (IsNotificationInFront())
            Record($"# a notification still holds the foreground: {DescribeForeground()}");
    }

    /// <summary>
    /// Invokes the close button of every notification on screen that names WinWispr, and returns how many it found.
    /// Other programs' notifications stay.
    /// </summary>
    private static int CloseWinWisprNotifications()
    {
        int found = 0;
        foreach (nint window in NotificationWindows())
        {
            try
            {
                AutomationElement notification = AutomationElement.FromHandle(window);
                if (!notification.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition)
                        .Cast<AutomationElement>().Any(element => (element.Current.Name ?? "").Contains("WinWispr", StringComparison.Ordinal)))
                    continue;
                found++;
                AutomationElement? close = notification.FindAll(TreeScope.Descendants,
                        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
                    .Cast<AutomationElement>()
                    .FirstOrDefault(button => (button.Current.AutomationId ?? "").Contains("Dismiss", StringComparison.OrdinalIgnoreCase));
                if (close?.TryGetCurrentPattern(InvokePattern.Pattern, out object pattern) == true)
                {
                    ((InvokePattern)pattern).Invoke();
                    Record($"# closed a WinWispr notification ({close.Current.Name})");
                }
            }
            catch (Exception exception) when (exception is ElementNotAvailableException or InvalidOperationException
                or System.Runtime.InteropServices.COMException)
            {
                Record($"# a notification could not be read: {exception.GetType().Name}");
            }
        }
        return found;
    }

    /// <summary>The visible windows of the shell's notification host.</summary>
    private static List<nint> NotificationWindows()
    {
        var windows = new List<nint>();
        _ = EnumWindows((window, _) =>
        {
            var className = new StringBuilder(64);
            _ = GetClassName(window, className, className.Capacity);
            if (IsWindowVisible(window) && className.ToString() == "Windows.UI.Core.CoreWindow" && IsShellNotificationHost(window))
                windows.Add(window);
            return true;
        }, 0);
        return windows;
    }

    private static bool IsShellNotificationHost(nint window)
    {
        _ = GetWindowThreadProcessId(window, out uint processId);
        try
        {
            using Process owner = Process.GetProcessById((int)processId);
            return owner.ProcessName == "ShellExperienceHost";
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Clicks the middle of the window with the mouse, in physical pixels of the virtual screen.</summary>
    private static void ClickInto(nint window)
    {
        if (window == 0)
            return;
        nint previous = SetThreadDpiAwarenessContext(-4);
        try
        {
            if (!GetWindowRect(window, out RECT bounds))
                return;
            int left = GetSystemMetrics(76), top = GetSystemMetrics(77);
            int width = Math.Max(1, GetSystemMetrics(78) - 1), height = Math.Max(1, GetSystemMetrics(79) - 1);
            int x = (bounds.Left + bounds.Right) / 2, y = (bounds.Top + bounds.Bottom) / 2;
            var move = new MOUSEINPUT
            {
                Dx = (int)Math.Clamp((x - left) * 65535L / width, 0, 65535),
                Dy = (int)Math.Clamp((y - top) * 65535L / height, 0, 65535),
                Flags = 0x0001 | 0x8000 | 0x4000
            };
            foreach (MOUSEINPUT mouse in new[] { move, new MOUSEINPUT { Flags = 0x0002 }, new MOUSEINPUT { Flags = 0x0004 } })
            {
                var input = new INPUT { Type = 0, Data = new InputUnion { Mouse = mouse } };
                if (SendInput(1, [input], Marshal.SizeOf<INPUT>()) != 1)
                    throw new AssertFailedException("SendInput failed for mouse input.");
                Thread.Sleep(50);
            }
            Record($"# clicked into the text box at {x},{y}");
        }
        finally
        {
            _ = SetThreadDpiAwarenessContext(previous);
        }
    }

    private static bool IsNotificationInFront() => IsShellNotificationHost(GetForegroundWindow());

    private static string DescribeForeground()
    {
        nint window = GetForegroundWindow();
        if (window == 0)
            return "no window";
        var title = new StringBuilder(256);
        _ = GetWindowText(window, title, title.Capacity);
        var className = new StringBuilder(256);
        _ = GetClassName(window, className, className.Capacity);
        _ = GetWindowThreadProcessId(window, out uint processId);
        string process;
        try
        {
            using Process owner = Process.GetProcessById((int)processId);
            process = owner.ProcessName;
        }
        catch (ArgumentException)
        {
            process = "exited";
        }
        return $"'{title}' ({className}) of {process} (pid {processId})";
    }

    /// <summary>Types every AltGr character once and returns the ones that did not arrive alone, by key.</summary>
    private static Dictionary<ushort, string> Run(
        DictationEndToEndTests.TextTarget target, string arm, Dictionary<ushort, string> keys, Hotkey[]? hotkeys)
    {
        var lost = new Dictionary<ushort, string>();
        using Registration? registration = hotkeys is null ? null : Registration.Start(hotkeys);
        Record($"# arm={arm} registered={registration?.Describe() ?? "none"}");
        foreach ((ushort key, string expected) in keys)
        {
            target.SetText("");
            target.BringToFront();
            Thread.Sleep(200);
            int actionsBefore = registration?.Actions ?? 0;
            bool syntheticControl = TypeAltGr(key);
            Thread.Sleep(400);
            string text = target.Text;
            int actions = (registration?.Actions ?? 0) - actionsBefore;
            bool arrived = text == expected;
            if (!arrived || actions != 0)
                lost[key] = $"AltGr+{(char)key} [{expected}]: typed [{text}], WinWispr actions {actions}";
            Record($"arm={arm} key={(char)key} expected=[{expected}] typed=[{text}] arrived={arrived} actions={actions} "
                + $"leftCtrlWithRightAlt={syntheticControl}");
        }
        Record($"## arm={arm} lost {lost.Count}/{keys.Count}");
        return lost;
    }

    /// <summary>
    /// Presses right Alt alone, as a keyboard does, then the key, and releases both in reverse order. Returns whether
    /// Windows added the left Ctrl of AltGr. Every key it pressed goes up again if a step fails.
    /// </summary>
    private static bool TypeAltGr(ushort key)
    {
        try
        {
            SendKeys([PhysicalKey(RightAlt, keyUp: false)]);
            Thread.Sleep(45);
            bool syntheticControl = (GetAsyncKeyState(LeftControl) & 0x8000) != 0;
            SendKeys([PhysicalKey(key, keyUp: false)]);
            Thread.Sleep(45);
            SendKeys([PhysicalKey(key, keyUp: true)]);
            Thread.Sleep(45);
            SendKeys([PhysicalKey(RightAlt, keyUp: true)]);
            Thread.Sleep(45);
            return syntheticControl;
        }
        catch
        {
            foreach (ushort held in new[] { key, RightAlt })
            {
                try
                {
                    SendKeys([PhysicalKey(held, keyUp: true)]);
                }
                catch (AssertFailedException)
                {
                }
            }
            throw;
        }
    }

    private static string AltGrCharacter(ushort key, nint layout)
    {
        var state = new byte[256];
        state[0x11] = state[0x12] = state[LeftControl] = state[RightAlt] = 0x80;
        var buffer = new StringBuilder(8);
        // 0x4: leave the keyboard state, dead keys included, as it is.
        int count = ToUnicodeEx(key, MapVirtualKeyEx(key, 0, layout), state, buffer, buffer.Capacity, 0x4, layout);
        return count > 0 && !char.IsControl(buffer[0]) ? buffer.ToString(0, count) : "";
    }

    private static string Describe(Hotkey hotkey) => $"{hotkey.Modifiers.ToString().Replace(", ", "+")}+{(char)hotkey.VirtualKey}";

    private static void Record(string line) => File.AppendAllLines(Evidence, [line]);

    // Like a keyboard: the virtual key together with its scan code; right Alt is an extended key.
    private static INPUT PhysicalKey(ushort virtualKey, bool keyUp) => new()
    {
        Type = 1,
        Data = new InputUnion
        {
            Keyboard = new KEYBDINPUT
            {
                VirtualKey = virtualKey,
                ScanCode = (ushort)MapVirtualKey(virtualKey, 0),
                Flags = (keyUp ? 2u : 0u) | (virtualKey == RightAlt ? 1u : 0u)
            }
        }
    };

    private static void SendKeys(INPUT[] inputs)
    {
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>()) != inputs.Length)
            throw new AssertFailedException("SendInput failed.");
    }

    /// <summary>Hotkeys registered through WinWispr's <see cref="GlobalInput"/> on a thread with a message loop.</summary>
    private sealed class Registration : IDisposable
    {
        private readonly Thread _thread;
        private uint _threadId;
        private int _actions;
        private GlobalInput? _input;

        private Registration(Hotkey[] hotkeys)
        {
            using var ready = new ManualResetEventSlim();
            Exception? failure = null;
            _thread = new Thread(() =>
            {
                _threadId = GetCurrentThreadId();
                _ = PeekMessage(out _, 0, 0, 0, 0);
                try
                {
                    // Every key-up of a hotkey's key posts its release, a typed letter's too; only a press is a catch.
                    _input = new GlobalInput(() => RecordingState.Idle, action =>
                    {
                        if (action is not (GlobalInputAction.ReleasePrimary or GlobalInputAction.ReleaseTask
                            or GlobalInputAction.ReleaseEnglishDictation))
                            Interlocked.Increment(ref _actions);
                    });
                    Started = _input.Start(hotkeys[0], hotkeys[1], hotkeys[2], hotkeys[3], hotkeys[4], hotkeys[5]);
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
                    _ = DispatchMessage(ref message);
                _input?.Dispose();
            })
            {
                IsBackground = true
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            ready.Wait();
            if (failure is not null)
                throw new AssertFailedException("GlobalInput could not start.", failure);
        }

        internal static Registration Start(Hotkey[] hotkeys)
        {
            var registration = new Registration(hotkeys);
            if (!registration.AllAvailable)
            {
                string description = registration.Describe();
                registration.Dispose();
                throw new AssertFailedException("Another program holds a hotkey the test registers: " + description);
            }
            return registration;
        }

        private bool Started { get; set; }

        private bool AllAvailable => Started && _input!.BoardHotkeyAvailable && _input.TaskHotkeyAvailable
            && _input.EnglishDictationHotkeyAvailable && _input.TranslateHotkeyAvailable && _input.ReplayHotkeyAvailable;

        /// <summary>The hotkey presses WinWispr received, releases not counted.</summary>
        internal int Actions => Volatile.Read(ref _actions);

        internal string Describe() => $"primary={Started} board={_input!.BoardHotkeyAvailable} task={_input.TaskHotkeyAvailable} "
            + $"english={_input.EnglishDictationHotkeyAvailable} translate={_input.TranslateHotkeyAvailable} "
            + $"replay={_input.ReplayHotkeyAvailable}";

        public void Dispose()
        {
            _ = PostThreadMessage(_threadId, 0x0012, 0, 0);
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>
    /// Closes the installed WinWispr for the test, because it holds chords the test registers or takes them while it
    /// starts, and starts it again afterwards, as its logon entry would: also when it was not running but that entry
    /// starts it, the state of the shared desktop after a logon. Another WinWispr that holds a chord (a development
    /// build) fails the test instead.
    /// </summary>
    private sealed class InstalledApp : IDisposable
    {
        private static readonly string Installed = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "WinWispr", "WinWispr.exe");

        private readonly string? _path;
        private readonly Action<string> _record;

        private InstalledApp(string? path, Action<string> record)
        {
            _path = path;
            _record = record;
        }

        internal static InstalledApp StepAside(Hotkey[] hotkeys, Action<string> record)
        {
            string installed = Installed;
            var others = new List<string>();
            bool stopped = false;
            foreach (Process process in Process.GetProcessesByName("WinWispr"))
            {
                using (process)
                {
                    string path = TryGetPath(process);
                    if (string.Equals(path, installed, StringComparison.OrdinalIgnoreCase))
                    {
                        process.Kill();
                        process.WaitForExit(10_000);
                        stopped = true;
                        record($"# stopped the installed WinWispr (pid {process.Id}); it starts again after the test");
                    }
                    else
                    {
                        others.Add($"pid {process.Id} {path}");
                    }
                }
            }
            var app = new InstalledApp(stopped || StartsAtLogon() ? installed : null, record);
            Thread.Sleep(500);
            Hotkey[] taken = [.. hotkeys.Distinct().Where(hotkey => !IsFree(hotkey))];
            if (taken.Length > 0)
            {
                app.Dispose();
                Assert.Fail("Another program holds " + string.Join(", ", taken.Select(Describe))
                    + (others.Count > 0 ? "; running WinWispr processes: " + string.Join(", ", others) : "") + ".");
            }
            return app;
        }

        public void Dispose()
        {
            if (_path is null || IsRunning())
                return;
            // Explorer starts it as the logon entry would, outside the test's process tree; it once did not, so a
            // direct start follows. The next test must find it running, or it would take its chords mid-test.
            Process.Start("explorer.exe", $"\"{_path}\"")?.Dispose();
            if (!WaitUntilRunning())
            {
                _record("# Explorer did not start the installed WinWispr; starting it directly");
                Process.Start(new ProcessStartInfo(_path) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(_path)! })?.Dispose();
            }
            _record(WaitUntilRunning()
                ? "# the installed WinWispr runs again"
                : "# the installed WinWispr did not start again; start it on the VM");
            Thread.Sleep(2000);
        }

        private static bool WaitUntilRunning()
        {
            Stopwatch timer = Stopwatch.StartNew();
            while (timer.Elapsed < TimeSpan.FromSeconds(20))
            {
                if (IsRunning())
                    return true;
                Thread.Sleep(250);
            }
            return false;
        }

        private static bool StartsAtLogon()
        {
            using RegistryKey? run = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            return File.Exists(Installed)
                && run?.GetValue("WinWispr") is string command
                && command.Contains(Installed, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsRunning() => Process.GetProcessesByName("WinWispr").Any(process =>
        {
            using (process)
                return string.Equals(TryGetPath(process), Installed, StringComparison.OrdinalIgnoreCase);
        });

        private static bool IsFree(Hotkey hotkey)
        {
            uint modifiers = (uint)hotkey.Modifiers; // HotkeyModifiers uses the native MOD_ values.
            if (!RegisterHotKey(0, 0x7A47, modifiers, hotkey.VirtualKey))
                return false;
            _ = UnregisterHotKey(0, 0x7A47);
            return true;
        }

        private static string TryGetPath(Process process)
        {
            try
            {
                return process.MainModule?.FileName ?? "";
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return "";
            }
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

        [FieldOffset(0)]
        internal MOUSEINPUT Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        internal int Dx;
        internal int Dy;
        internal uint MouseData;
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
    private struct KEYBDINPUT
    {
        internal ushort VirtualKey;
        internal ushort ScanCode;
        internal uint Flags;
        internal uint Time;
        internal nint ExtraInfo;
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
    private static extern uint MapVirtualKeyEx(uint code, uint mapType, nint layout);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(
        uint virtualKey, uint scanCode, byte[] keyState, StringBuilder buffer, int bufferSize, uint flags, nint layout);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern nint GetKeyboardLayout(uint threadId);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint window, StringBuilder text, int capacity);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindow(string? className, string title);

    private delegate bool EnumWindowsProc(nint window, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out RECT bounds);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern nint SetThreadDpiAwarenessContext(nint context);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(nint window, StringBuilder name, int capacity);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

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
}
