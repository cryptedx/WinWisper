using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Audio;
using WinWispr.Core;
using WinWispr.Speech;
using WinWispr.Tests.Speech;

namespace WinWispr.Tests.Views;

/// <summary>
/// Drives the real application's dictation session through injected global hotkeys and
/// observes it through the overlay's window title. It needs the logged-in desktop session and the
/// pinned model archive (see <see cref="TrustedSpeechFixtures"/>), not an installed app. Audio is
/// replayed instead of recorded: silence for the session transitions, and the archive's German
/// clip for the tests that assert a transcript.
/// </summary>
[TestClass]
[DoNotParallelize]
public sealed class DictationEndToEndTests
{
    internal const string Busy = "WinWispr: Preparing…";
    internal const string Recording = "WinWispr: Recording";
    internal const string Processing = "WinWispr: Processing";
    internal const string CancelPending = "WinWispr: Delete recording?";
    internal const string Ready = "WinWispr: Ready";
    private static readonly string[] ActiveTitles = [Busy, Recording, Processing, CancelPending];
    private static readonly string[] FailureTitles =
    [
        "WinWispr: Microphone unavailable",
        "WinWispr: Model could not be loaded",
        "WinWispr: Model unavailable",
        "WinWispr: Restart WinWispr"
    ];
    internal static readonly TimeSpan ModelLoadTimeout = TimeSpan.FromMinutes(3);
    // Generous on purpose: recognition of a short clip took 17 s on this machine under load.
    internal static readonly TimeSpan StepTimeout = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan TranscriptTimeout = TimeSpan.FromSeconds(90);
    internal static readonly Hotkey PrimaryHotkey = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x78);
    internal static readonly Hotkey TaskHotkey = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x79);
    internal static readonly Hotkey BoardHotkey = new(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x76);
    internal const ushort Escape = 0x1B;
    private const ushort Y = 0x59;

    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    public void Real_application_hold_toggle_cancel_and_ownership_follow_the_dictation_session()
    {
        TaskBoardEndToEndTests.WithProfile((root, instance, exit) =>
        {
            SeedDictationProfile(root);
            // Silence replaces the microphone: the test needs neither a capture device nor Teams, and it
            // cannot mute the meeting of whoever uses this machine.
            string silencePath = Path.Combine(root, "silence.pcm");
            File.WriteAllBytes(silencePath, new byte[CaptureFormat.SampleRate * sizeof(short) * 10]);
            TaskBoardEndToEndTests.RunApplication(root, instance, exit, ui =>
            {
                WaitForTitle(ui, Ready, ModelLoadTimeout, "speech model loads in the E2E profile");
                WaitForIdle(ui, "ready status clears");

                // Hold: recording lasts while the primary hotkey is held and ends on release.
                ui.HoldHotkey(PrimaryHotkey, "hold primary hotkey");
                WaitForTitle(ui, Recording, StepTimeout, "hold press starts recording");
                StaysAt(ui, Recording, TimeSpan.FromSeconds(1), "held key keeps recording");
                ui.ReleaseHotkey(PrimaryHotkey, "release primary hotkey");
                WaitForIdle(ui, "hold release processes and returns to idle");

                // Toggle + Esc then Y: the task hotkey's session is discarded without processing.
                ui.PressHotkey(TaskHotkey, "press task hotkey to start");
                WaitForTitle(ui, Recording, StepTimeout, "task toggle press starts recording");
                StaysAt(ui, Recording, TimeSpan.FromSeconds(1), "toggle ignores the key release");
                ui.PressVirtualKey(Escape, "request cancel");
                WaitForTitle(ui, CancelPending, StepTimeout, "Esc opens the cancel window");
                ui.PressVirtualKey(Y, "confirm discard");
                WaitForIdle(ui, "Y discards the recording");
                Assert.IsFalse(ui.HasWindow(Processing), "A discarded recording must not be processed.");

                // Toggle + Esc then timeout: the same session resumes; a foreign hotkey is ignored.
                ui.PressHotkey(TaskHotkey, "press task hotkey to start again");
                WaitForTitle(ui, Recording, StepTimeout, "task toggle starts a second recording");
                ui.PressVirtualKey(Escape, "request cancel and let it time out");
                WaitForTitle(ui, CancelPending, StepTimeout, "Esc opens the cancel window again");
                WaitForTitle(ui, Recording, StepTimeout, "cancel window times out and recording resumes");
                ui.PressHotkey(PrimaryHotkey, "press the primary hotkey during the task session");
                StaysAt(ui, Recording, TimeSpan.FromSeconds(1.5), "foreign hotkey does not end the task session");
                ui.PressHotkey(TaskHotkey, "press task hotkey to stop");
                WaitForIdle(ui, "owner press processes and returns to idle");
            },
            launchArgument: "--e2e-dictation",
            environment: new Dictionary<string, string> { ["WINWISPR_E2E_AUDIO_PCM16"] = silencePath });
        });
    }

    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    public void Real_application_turns_replayed_speech_into_a_task_draft()
    {
        WithReplayedSpeech((ui, root, speaking) =>
        {
            // The board's dictate button is invoked through UI Automation, so this test sends no keys and
            // does not depend on which window is in front.
            ui.InvokeButton("Dictate a task", "start the task dictation from the board");
            WaitForTitle(ui, Recording, StepTimeout, "the button starts recording");
            Thread.Sleep(speaking);
            ui.InvokeButton("Dictate a task", "stop the task dictation from the board");
            TaskCaptureDraft draft = WaitForDraft(root, TranscriptTimeout);
            Console.WriteLine($"Replayed speech became the task draft: {draft.OriginalTranscript}");

            TaskBoardEndToEndTests.UiDriver capture = ui.WaitForWindow("New task");
            Assert.IsFalse(
                string.IsNullOrWhiteSpace(capture.GetValue("captured-task-text", "read the captured transcript")),
                "The task capture window shows no transcript.");
            capture.InvokeId("capture-discard", "open the discard confirmation");
            capture.InvokeDescendantName("discard-draft-dialog", "Discard", "confirm discarding the draft");
            capture.WaitForWindowGone("New task", "capture window closes");
        });
    }

    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    public void Real_application_dictates_replayed_speech_into_a_text_target()
    {
        WithReplayedSpeech((ui, root, speaking) =>
        {
            // The paste needs a real foreground window, so this test sends keys and needs the desktop to itself.
            // With the board hidden, the hotkeys no longer pull the focus to it.
            ui.PressHotkey(BoardHotkey, "hide the board so the text target keeps the focus");
            ui.WaitForBoardHidden("board is hidden");

            using var target = new TextTarget();
            target.BringToFront();
            ui.HoldHotkey(PrimaryHotkey, "hold the primary hotkey over the text target");
            WaitForTitle(ui, Recording, StepTimeout, "hold press starts recording");
            Thread.Sleep(speaking);
            ui.ReleaseHotkey(PrimaryHotkey, "release the primary hotkey");
            var overlayStates = new List<string>();
            string text;
            try
            {
                text = target.WaitForText(TranscriptTimeout, () =>
                {
                    foreach (string title in ui.VisibleTitles())
                        if (title.StartsWith("WinWispr: ", StringComparison.Ordinal) && !overlayStates.Contains(title))
                            overlayStates.Add(title);
                });
            }
            catch (AssertFailedException failure)
            {
                throw new AssertFailedException(
                    $"{failure.Message} Overlay states seen while waiting: {string.Join("; ", overlayStates)}. "
                    + $"Clipboard holds: '{target.ClipboardText()}'.");
            }

            Console.WriteLine($"Replayed speech was dictated as: {text}");
            Assert.IsFalse(string.IsNullOrWhiteSpace(text), "The replayed speech was not pasted into the text target.");
            WaitForIdle(ui, "text dictation returns to idle");
        });
    }

    // Starts the real app with the pinned speech clip replacing the microphone, waits until it is ready,
    // and hands the scenario the driver, the profile root, and how long the clip lasts.
    private static void WithReplayedSpeech(Action<TaskBoardEndToEndTests.UiDriver, string, TimeSpan> scenario)
    {
        float[] clip = Pcm16Wave.ReadMono16Khz(TrustedSpeechFixtures.Require().GermanClip);
        // The clip plays in real time from the start of the capture, so hold a little longer than it lasts.
        TimeSpan speaking = TimeSpan.FromSeconds((double)clip.Length / CaptureFormat.SampleRate)
            + TimeSpan.FromMilliseconds(500);

        TaskBoardEndToEndTests.WithProfile((root, instance, exit) =>
        {
            SeedDictationProfile(root);
            string replayPath = Path.Combine(root, "replay.pcm");
            File.WriteAllBytes(replayPath, Pcm16Wave.ToPcm16(clip));
            TaskBoardEndToEndTests.RunApplication(root, instance, exit, ui =>
            {
                WaitForTitle(ui, Ready, ModelLoadTimeout, "speech model loads in the E2E profile");
                WaitForIdle(ui, "ready status clears");
                scenario(ui, root, speaking);
            },
            launchArgument: "--e2e-dictation",
            environment: new Dictionary<string, string> { ["WINWISPR_E2E_AUDIO_PCM16"] = replayPath });
        });
    }

    internal static void SeedDictationProfile(string root)
    {
        // The pinned model, not whatever this machine has installed, so the E2E needs no installation.
        string installedModels = TrustedSpeechFixtures.Require().ModelsRoot;
        string activePath = Path.Combine(installedModels, "active.json");

        // Hard links keep the E2E profile isolated without copying the 650 MB model.
        string models = Path.Combine(root, "Models");
        foreach (string directory in Directory.EnumerateDirectories(
            Path.Combine(installedModels, "installs"), "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(models, Path.GetRelativePath(installedModels, directory)));
        foreach (string file in Directory.EnumerateFiles(
            Path.Combine(installedModels, "installs"), "*", SearchOption.AllDirectories))
        {
            string link = Path.Combine(models, Path.GetRelativePath(installedModels, file));
            if (!CreateHardLink(link, file, nint.Zero))
                throw new AssertFailedException($"Could not link {file}: Win32 error {Marshal.GetLastWin32Error()}.");
        }
        File.Copy(activePath, Path.Combine(models, "active.json"));

        var settingsStore = new AppSettingsStore(Path.Combine(root, "settings.json"));
        settingsStore.Save(settingsStore.Load() with
        {
            Hotkey = PrimaryHotkey,
            Mode = RecordingMode.Hold,
            TaskHotkey = TaskHotkey,
            TaskHotkeyMode = RecordingMode.Toggle,
            BoardHotkey = BoardHotkey,
            SpeechMode = SpeechMode.Cpu
        });
    }

    internal static void WaitForTitle(TaskBoardEndToEndTests.UiDriver ui, string title, TimeSpan timeout, string step)
    {
        Stopwatch timer = Stopwatch.StartNew();
        var seen = new List<string>();
        while (timer.Elapsed < timeout)
        {
            FailIfExited(ui, step);
            List<string> titles = ui.VisibleTitles();
            FailOnError(titles, step);
            if (titles.Contains(title))
                return;
            foreach (string other in titles.Where(t => t.StartsWith("WinWispr: ", StringComparison.Ordinal) && !seen.Contains(t)))
                seen.Add(other);
            Thread.Sleep(50);
        }
        throw new AssertFailedException(
            $"{step}: '{title}' did not appear within {timeout.TotalSeconds:0} s. Overlay states seen: "
            + $"{(seen.Count == 0 ? "none" : string.Join("; ", seen))}. Windows: {ui.VisibleWindows()}");
    }

    internal static void FailIfExited(TaskBoardEndToEndTests.UiDriver ui, string step)
    {
        if (ui.ExitDescription() is { } exit)
            throw new AssertFailedException(
                $"{step}: the application {exit}."
                + (exit.EndsWith("code -1", StringComparison.Ordinal)
                    ? " Exit code -1 is how a process ends when another program terminates it (for example a "
                      + "deployment stopping every WinWispr.exe on this machine); the app has no such exit itself."
                    : string.Empty));
    }

    internal static void StaysAt(TaskBoardEndToEndTests.UiDriver ui, string title, TimeSpan duration, string step)
    {
        Stopwatch timer = Stopwatch.StartNew();
        while (timer.Elapsed < duration)
        {
            FailIfExited(ui, step);
            List<string> titles = ui.VisibleTitles();
            FailOnError(titles, step);
            if (!titles.Contains(title))
                throw new AssertFailedException($"{step}: '{title}' ended early. Windows: {ui.VisibleWindows()}");
            Thread.Sleep(50);
        }
    }

    internal static void WaitForIdle(TaskBoardEndToEndTests.UiDriver ui, string step)
    {
        Stopwatch timer = Stopwatch.StartNew();
        Stopwatch idle = Stopwatch.StartNew();
        while (timer.Elapsed < StepTimeout)
        {
            FailIfExited(ui, step);
            List<string> titles = ui.VisibleTitles();
            FailOnError(titles, step);
            if (titles.Any(title => ActiveTitles.Contains(title)))
                idle.Restart();
            else if (idle.Elapsed >= TimeSpan.FromSeconds(1))
                return;
            Thread.Sleep(50);
        }
        throw new AssertFailedException($"{step}: the session did not return to idle. Windows: {ui.VisibleWindows()}");
    }

    internal static void FailOnError(List<string> titles, string step)
    {
        string? failure = titles.FirstOrDefault(title => FailureTitles.Contains(title));
        if (failure is not null)
            throw new AssertFailedException($"{step}: the application reported '{failure}'.");
    }

    private static TaskCaptureDraft WaitForDraft(string root, TimeSpan timeout)
    {
        string path = Path.Combine(root, "tasks.json");
        Stopwatch timer = Stopwatch.StartNew();
        while (timer.Elapsed < timeout)
        {
            try
            {
                TaskCaptureDraft? draft = new TaskBoardStore(path).Load().Draft;
                if (draft is not null && !string.IsNullOrWhiteSpace(draft.OriginalTranscript))
                    return draft;
            }
            catch (IOException)
            {
            }

            Thread.Sleep(200);
        }

        throw new AssertFailedException($"No task draft with a transcript appeared within {timeout.TotalSeconds:0} s.");
    }

    /// <summary>A window in the test process that receives the dictation as a real paste target.</summary>
    internal sealed class TextTarget : IDisposable
    {
        private readonly Dispatcher _dispatcher;
        private readonly Window _window;
        private readonly TextBox _box;
        private readonly Thread _thread;

        public TextTarget()
        {
            var ready = new TaskCompletionSource<(Dispatcher, Window, TextBox)>();
            _thread = new Thread(() =>
            {
                try
                {
                    var box = new TextBox { AcceptsReturn = true, FontSize = 24, TextWrapping = TextWrapping.Wrap };
                    var window = new Window
                    {
                        Title = "WinWispr dictation target",
                        Width = 720,
                        Height = 320,
                        Left = 40,
                        Top = 40,
                        Topmost = true,
                        Content = box
                    };
                    window.Show();
                    ready.SetResult((Dispatcher.CurrentDispatcher, window, box));
                }
                catch (Exception exception)
                {
                    ready.SetException(exception);
                    return;
                }

                Dispatcher.Run();
            }) { IsBackground = true, Name = "WinWispr.E2E.TextTarget" };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            (_dispatcher, _window, _box) = ready.Task.GetAwaiter().GetResult();
        }

        public string Text => _dispatcher.Invoke(() => _box.Text);

        // A transcript on the clipboard but not in the box means the recognition worked and the paste did not land.
        public string ClipboardText()
        {
            try
            {
                return _dispatcher.Invoke(() => Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty);
            }
            catch (Exception exception) when (exception is COMException or InvalidOperationException)
            {
                return $"[clipboard unavailable: {exception.Message}]";
            }
        }

        public void BringToFront()
        {
            nint handle = _dispatcher.Invoke(() => new WindowInteropHelper(_window).Handle);
            for (int attempt = 0; attempt < 20 && GetForegroundWindow() != handle; attempt++)
            {
                ForceForeground(handle);
                Thread.Sleep(100);
            }

            _dispatcher.Invoke(() =>
            {
                _window.Activate();
                _box.Focus();
            });
            if (GetForegroundWindow() != handle)
                throw new AssertFailedException("The text target could not take the foreground.");
        }

        public string WaitForText(TimeSpan timeout, Action? poll = null)
        {
            Stopwatch timer = Stopwatch.StartNew();
            while (timer.Elapsed < timeout)
            {
                poll?.Invoke();
                string text = Text;
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
                Thread.Sleep(100);
            }

            throw new AssertFailedException($"The text target received nothing within {timeout.TotalSeconds:0} s.");
        }

        /// <summary>Replaces the text and selects all of it, or only the first <paramref name="selectLength"/> characters.</summary>
        public void SetText(string text, int? selectLength = null) => _dispatcher.Invoke(() =>
        {
            _box.Text = text;
            _box.Select(0, selectLength ?? text.Length);
            _box.Focus();
        });

        /// <summary>Puts the caret at the start with nothing selected.</summary>
        public void ClearSelection() => _dispatcher.Invoke(() => _box.Select(0, 0));

        public string SelectedText => _dispatcher.Invoke(() => _box.SelectedText);

        public void SetClipboardText(string text)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    _dispatcher.Invoke(() => Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text), true));
                    return;
                }
                catch (Exception exception) when (attempt < 20 && exception is COMException or InvalidOperationException)
                {
                    Thread.Sleep(100);
                }
            }
        }

        /// <summary>Null while the clipboard holds no text.</summary>
        public string? ClipboardTextOrNull()
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    return _dispatcher.Invoke(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);
                }
                catch (Exception exception) when (attempt < 20 && exception is COMException or InvalidOperationException)
                {
                    Thread.Sleep(100);
                }
            }
        }

        public void Dispose()
        {
            _dispatcher.InvokeShutdown();
            _thread.Join(TimeSpan.FromSeconds(5));
        }

        private static void ForceForeground(nint handle)
        {
            uint targetThread = GetWindowThreadProcessId(handle, out _);
            uint currentThread = GetCurrentThreadId();
            uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            bool attachedToForeground = foregroundThread != 0 && foregroundThread != currentThread
                && AttachThreadInput(currentThread, foregroundThread, true);
            bool attachedToTarget = targetThread != 0 && targetThread != currentThread
                && AttachThreadInput(currentThread, targetThread, true);
            try
            {
                _ = ShowWindow(handle, 9);
                _ = BringWindowToTop(handle);
                _ = SetForegroundWindow(handle);
            }
            finally
            {
                if (attachedToTarget)
                    _ = AttachThreadInput(currentThread, targetThread, false);
                if (attachedToForeground)
                    _ = AttachThreadInput(currentThread, foregroundThread, false);
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(nint handle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint handle, int command);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint threadId, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, nint securityAttributes);
}
