using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Audio;
using WinWispr.Core;
using WinWispr.Speech;
using WinWispr.Tests.Speech;

namespace WinWispr.Tests.Views;

/// <summary>
/// Dictates the replayed clip into a real Notepad while another window reads the clipboard on every change, the way
/// security agents and clipboard histories do. A paste that finds the clipboard open pastes nothing; Notepad shows it
/// every time when Ctrl+V follows the clipboard change at once. Needs the logged-in desktop, like
/// <see cref="DictationEndToEndTests"/>.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class PasteDeliveryEndToEndTests
{
    private const int Dictations = 4;
    private static readonly TimeSpan MonitorHold = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan DeliveryLogged = TimeSpan.FromMilliseconds(1500);

    // Shift+Alt+Q: the installed WinWispr holds Shift+Alt+X on the test machine.
    private static readonly Hotkey ShiftAltHotkey = new(HotkeyModifiers.Shift | HotkeyModifiers.Alt, 0x51);

    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    public void Real_application_pastes_into_notepad_while_a_clipboard_monitor_reads_every_change()
    {
        float[] clip = Pcm16Wave.ReadMono16Khz(TrustedSpeechFixtures.Require().GermanClip);
        TimeSpan speaking = TimeSpan.FromSeconds((double)clip.Length / CaptureFormat.SampleRate)
            + TimeSpan.FromMilliseconds(500);
        var outcomes = new List<string>();

        TaskBoardEndToEndTests.WithProfile((root, instance, exit) =>
        {
            DictationEndToEndTests.SeedDictationProfile(root);
            string replayPath = Path.Combine(root, "replay.pcm");
            File.WriteAllBytes(replayPath, Pcm16Wave.ToPcm16(clip));
            string document = Path.Combine(root, "paste-target.txt");
            File.WriteAllText(document, "");

            TaskBoardEndToEndTests.RunApplication(root, instance, exit, ui =>
            {
                DictationEndToEndTests.WaitForTitle(
                    ui, DictationEndToEndTests.Ready, DictationEndToEndTests.ModelLoadTimeout, "speech model loads");
                DictationEndToEndTests.WaitForIdle(ui, "ready status clears");
                ui.PressHotkey(DictationEndToEndTests.BoardHotkey, "hide the board so Notepad keeps the focus");
                ui.WaitForBoardHidden("board is hidden");

                using var monitor = new ClipboardMonitor(MonitorHold);
                using var notepad = new NotepadTarget(document);
                for (int dictation = 1; dictation <= Dictations; dictation++)
                {
                    notepad.Clear();
                    int reads = monitor.Reads;
                    ui.HoldHotkey(DictationEndToEndTests.PrimaryHotkey, $"hold the primary hotkey over Notepad ({dictation})");
                    DictationEndToEndTests.WaitForTitle(
                        ui, DictationEndToEndTests.Recording, DictationEndToEndTests.StepTimeout, "hold press starts recording");
                    Thread.Sleep(speaking);
                    ui.ReleaseHotkey(DictationEndToEndTests.PrimaryHotkey, "release the primary hotkey");
                    DictationEndToEndTests.WaitForIdle(ui, "dictation returns to idle");
                    // DeliveryCompleted is written once the clipboard watch, a second after Ctrl+V, is over.
                    Thread.Sleep(DeliveryLogged);
                    outcomes.Add($"#{dictation} monitor reads={monitor.Reads - reads} "
                        + $"delivery={LastDeliveryResult(root)} pasted={notepad.HasText}");
                }
            },
            launchArgument: "--e2e-dictation",
            environment: new Dictionary<string, string> { ["WINWISPR_E2E_AUDIO_PCM16"] = replayPath });
        });

        Console.WriteLine(string.Join(Environment.NewLine, outcomes));
        File.AppendAllLines(Path.Combine(Path.GetTempPath(), "winwispr-paste-e2e-evidence.txt"),
            [$"{DateTimeOffset.UtcNow:O}", .. outcomes]);
        Assert.IsFalse(outcomes.Any(outcome => outcome.Contains("reads=0", StringComparison.Ordinal)),
            $"The clipboard monitor did not see every dictation: {string.Join("; ", outcomes)}");
        Assert.IsFalse(outcomes.Any(outcome => outcome.EndsWith("pasted=False", StringComparison.Ordinal)),
            $"A dictation was not pasted into Notepad: {string.Join("; ", outcomes)}");
    }

    /// <summary>
    /// The shipped hotkey style, Shift+Alt plus a letter in Toggle mode, pressed Shift first and released Alt first at
    /// both presses. Windows consumes the letter, so without WinWispr's menu mask Windows 11 Notepad sees a lone Alt
    /// press, moves the focus to its title bar, and the paste goes nowhere while the delivery reports PasteSent.
    /// </summary>
    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    public void Real_application_pastes_into_notepad_after_a_shift_alt_toggle_released_alt_first()
    {
        float[] clip = Pcm16Wave.ReadMono16Khz(TrustedSpeechFixtures.Require().GermanClip);
        TimeSpan speaking = TimeSpan.FromSeconds((double)clip.Length / CaptureFormat.SampleRate)
            + TimeSpan.FromMilliseconds(500);
        var outcomes = new List<string>();

        TaskBoardEndToEndTests.WithProfile((root, instance, exit) =>
        {
            DictationEndToEndTests.SeedDictationProfile(root);
            var settingsStore = new AppSettingsStore(Path.Combine(root, "settings.json"));
            settingsStore.Save(settingsStore.Load() with { Hotkey = ShiftAltHotkey, Mode = RecordingMode.Toggle });
            string replayPath = Path.Combine(root, "replay.pcm");
            File.WriteAllBytes(replayPath, Pcm16Wave.ToPcm16(clip));
            string document = Path.Combine(root, "paste-target.txt");
            File.WriteAllText(document, "");

            TaskBoardEndToEndTests.RunApplication(root, instance, exit, ui =>
            {
                DictationEndToEndTests.WaitForTitle(
                    ui, DictationEndToEndTests.Ready, DictationEndToEndTests.ModelLoadTimeout, "speech model loads");
                DictationEndToEndTests.WaitForIdle(ui, "ready status clears");
                ui.PressHotkey(DictationEndToEndTests.BoardHotkey, "hide the board so Notepad keeps the focus");
                ui.WaitForBoardHidden("board is hidden");

                using var notepad = new NotepadTarget(document);
                for (int dictation = 1; dictation <= Dictations; dictation++)
                {
                    notepad.Clear();
                    PressShiftAltReleasingAltFirst();
                    DictationEndToEndTests.WaitForTitle(
                        ui, DictationEndToEndTests.Recording, DictationEndToEndTests.StepTimeout, "toggle press starts recording");
                    Thread.Sleep(speaking);
                    PressShiftAltReleasingAltFirst();
                    DictationEndToEndTests.WaitForIdle(ui, "dictation returns to idle");
                    // DeliveryCompleted is written once the clipboard watch, a second after Ctrl+V, is over.
                    Thread.Sleep(DeliveryLogged);
                    outcomes.Add($"#{dictation} delivery={LastDeliveryResult(root)} pasted={notepad.HasText}");
                }
            },
            launchArgument: "--e2e-dictation",
            environment: new Dictionary<string, string> { ["WINWISPR_E2E_AUDIO_PCM16"] = replayPath });
        });

        Console.WriteLine(string.Join(Environment.NewLine, outcomes));
        File.AppendAllLines(Path.Combine(Path.GetTempPath(), "winwispr-paste-e2e-evidence.txt"),
            [$"{DateTimeOffset.UtcNow:O} Shift+Alt toggle, Alt released first", .. outcomes]);
        Assert.IsFalse(outcomes.Any(outcome => outcome.EndsWith("pasted=False", StringComparison.Ordinal)),
            $"A dictation was not pasted into Notepad: {string.Join("; ", outcomes)}");
    }

    /// <summary>
    /// Shift, Alt and the letter down, then Alt, the letter and Shift up, with scan codes like a keyboard. If a step
    /// fails, every key goes up again, so none stays held on the shared desktop.
    /// </summary>
    private static void PressShiftAltReleasingAltFirst()
    {
        ushort letter = (ushort)ShiftAltHotkey.VirtualKey;
        try
        {
            SendPhysicalKey(0xA0, keyUp: false);
            SendPhysicalKey(0xA4, keyUp: false);
            SendPhysicalKey(letter, keyUp: false);
            Thread.Sleep(60);
            SendPhysicalKey(0xA4, keyUp: true);
            SendPhysicalKey(letter, keyUp: true);
            SendPhysicalKey(0xA0, keyUp: true);
        }
        catch
        {
            foreach (ushort key in new ushort[] { 0xA4, letter, 0xA0 })
            {
                try
                {
                    SendPhysicalKey(key, keyUp: true);
                }
                catch (AssertFailedException)
                {
                }
            }
            throw;
        }
    }

    private static void SendPhysicalKey(ushort virtualKey, bool keyUp)
    {
        var input = new INPUT
        {
            Type = 1,
            Data = new InputUnion
            {
                Keyboard = new KEYBDINPUT
                {
                    VirtualKey = virtualKey,
                    ScanCode = (ushort)MapVirtualKey(virtualKey, 0),
                    Flags = keyUp ? 2u : 0u
                }
            }
        };
        if (SendInput(1, [input], Marshal.SizeOf<INPUT>()) != 1)
            throw new AssertFailedException($"SendInput failed for virtual key 0x{virtualKey:X2}.");
        Thread.Sleep(45);
    }

    private static string LastDeliveryResult(string root)
    {
        string logs = Path.Combine(root, "Logs");
        string? line = Directory.EnumerateFiles(logs)
            .SelectMany(file =>
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd().Split('\n');
            })
            .LastOrDefault(entry => entry.Contains("\"DeliveryCompleted\"", StringComparison.Ordinal)
                || entry.Contains("\"ClipboardUnavailable\"", StringComparison.Ordinal));
        return line?.Trim() ?? "none";
    }

    /// <summary>Opens the clipboard on every change, reads the text and holds the clipboard open for a moment.</summary>
    private sealed class ClipboardMonitor : IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private readonly Thread _thread;
        private int _reads;

        public ClipboardMonitor(TimeSpan hold)
        {
            var ready = new TaskCompletionSource<Dispatcher>();
            _thread = new Thread(() =>
            {
                // HWND_MESSAGE: a message-only window that receives WM_CLIPBOARDUPDATE.
                var source = new HwndSource(new HwndSourceParameters("WinWispr clipboard monitor") { ParentWindow = -3 });
                source.AddHook((nint window, int message, nint wParam, nint lParam, ref bool handled) =>
                {
                    if (message == 0x031D && OpenClipboard(window))
                    {
                        try
                        {
                            _ = GetClipboardData(13);
                            Interlocked.Increment(ref _reads);
                            Thread.Sleep(hold);
                        }
                        finally
                        {
                            CloseClipboard();
                        }
                    }
                    return 0;
                });
                if (AddClipboardFormatListener(source.Handle))
                    ready.SetResult(Dispatcher.CurrentDispatcher);
                else
                    ready.SetException(new AssertFailedException("The clipboard monitor could not listen."));
                Dispatcher.Run();
            });
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.IsBackground = true;
            _thread.Start();
            _dispatcher = ready.Task.GetAwaiter().GetResult();
        }

        public int Reads => Volatile.Read(ref _reads);

        public void Dispose()
        {
            _dispatcher.InvokeShutdown();
            _thread.Join(TimeSpan.FromSeconds(5));
        }
    }

    /// <summary>A Notepad window on an empty file, read through UI Automation.</summary>
    private sealed class NotepadTarget : IDisposable
    {
        private readonly nint _window;

        public NotepadTarget(string document)
        {
            Process.Start("notepad.exe", $"\"{document}\"")?.Dispose();
            string name = Path.GetFileNameWithoutExtension(document);
            Stopwatch timer = Stopwatch.StartNew();
            while (_window == 0 && timer.Elapsed < TimeSpan.FromSeconds(30))
            {
                foreach (Process process in Process.GetProcessesByName("Notepad"))
                {
                    if (process.MainWindowHandle != 0
                        && process.MainWindowTitle.Contains(name, StringComparison.OrdinalIgnoreCase))
                        _window = process.MainWindowHandle;
                    process.Dispose();
                }
                Thread.Sleep(200);
            }
            if (_window == 0)
                throw new AssertFailedException("Notepad did not open the target file.");
        }

        public void Clear()
        {
            BringToFront();
            for (int attempt = 0; attempt < 5 && Text().Length > 0; attempt++)
            {
                SendKeyEvent(0xA2, keyUp: false);
                SendKeyEvent(0x41, keyUp: false);
                SendKeyEvent(0x41, keyUp: true);
                SendKeyEvent(0xA2, keyUp: true);
                SendKeyEvent(0x2E, keyUp: false);
                SendKeyEvent(0x2E, keyUp: true);
                Thread.Sleep(200);
            }
            if (Text().Length > 0)
                throw new AssertFailedException("Notepad could not be cleared.");
        }

        public bool HasText => !string.IsNullOrWhiteSpace(Text());

        public void Dispose()
        {
            _ = GetWindowThreadProcessId(_window, out uint processId);
            try
            {
                using Process process = Process.GetProcessById((int)processId);
                process.Kill();
            }
            catch (ArgumentException)
            {
            }
        }

        private string Text()
        {
            AutomationElement? document = AutomationElement.FromHandle(_window).FindFirst(
                TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document));
            if (document?.TryGetCurrentPattern(TextPattern.Pattern, out object pattern) == true)
                return ((TextPattern)pattern).DocumentRange.GetText(-1);
            throw new AssertFailedException("The Notepad document is not available through UI Automation.");
        }

        private void BringToFront()
        {
            uint current = GetCurrentThreadId();
            uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            bool attached = foregroundThread != 0 && foregroundThread != current
                && AttachThreadInput(current, foregroundThread, true);
            try
            {
                _ = ShowWindow(_window, 9);
                _ = BringWindowToTop(_window);
                _ = SetForegroundWindow(_window);
            }
            finally
            {
                if (attached)
                    AttachThreadInput(current, foregroundThread, false);
            }
            Thread.Sleep(300);
            if (GetForegroundWindow() != _window)
                throw new AssertFailedException("Notepad could not take the foreground.");
        }
    }

    private static void SendKeyEvent(ushort virtualKey, bool keyUp)
    {
        var input = new INPUT
        {
            Type = 1,
            Data = new InputUnion { Keyboard = new KEYBDINPUT { VirtualKey = virtualKey, Flags = keyUp ? 2u : 0u } }
        };
        if (SendInput(1, [input], Marshal.SizeOf<INPUT>()) != 1)
            throw new AssertFailedException($"SendInput failed for virtual key 0x{virtualKey:X2}.");
        Thread.Sleep(30);
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct INPUT
    {
        [FieldOffset(0)]
        internal uint Type;

        [FieldOffset(8)]
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll")]
    private static extern bool AddClipboardFormatListener(nint window);

    [DllImport("user32.dll")]
    private static extern bool OpenClipboard(nint window);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern nint GetClipboardData(uint format);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint handle);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(nint handle);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint handle, int command);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint threadId, uint attachTo, bool attach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
