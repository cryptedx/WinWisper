using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System.Reflection;
using WinWispr.Audio;
using WinWispr.Core;
using WinWispr.Interop;
using WinWispr.Views;
using WinWispr.Speech;
using WinWispr.Translation;

namespace WinWispr.SettingsUiAcceptance;

// Started by the WindowsAcceptance tests SettingsWindowTests.Native_*, which set the marker path and the scope.
// Run them in the interactive session: ./eng/deploy-windows-vm.sh --check --test-filter "FullyQualifiedName~SettingsWindowTests.Native_"

internal static class Program
{
    private const string MarkerPathEnvironmentVariable = "WINWISPR_UI_MARKER_PATH";

    private sealed class HelperApplication(string markerPath, string failureDiagnosticPath) : global::WinWispr.App
    {
        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            try
            {
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                if (Environment.GetEnvironmentVariable("WINWISPR_UI_SCOPE") == "overlay")
                    await OverlayStartupAssertions.RunAsync();
                else if (Environment.GetEnvironmentVariable("WINWISPR_UI_SCOPE") == "overlay-gallery")
                    await OverlayGallery.RunAsync();
                else if (Environment.GetEnvironmentVariable("WINWISPR_UI_SCOPE") == "board")
                    await RunTaskBoardAssertions();
                else
                {
                    await AssertApplicationRoutingAsync();
                    await RunIsolatedWindowAssertions();
                }
                File.WriteAllText(markerPath, "PASS");
                Environment.Exit(0);
            }
            catch (Exception exception)
            {
                File.WriteAllText(failureDiagnosticPath, exception.ToString());
                Console.Error.WriteLine(exception);
                Console.Error.Flush();
                Environment.Exit(1);
            }
        }
    }

    [STAThread]
    private static void Main()
    {
        string markerPath = Environment.GetEnvironmentVariable(MarkerPathEnvironmentVariable)
            ?? throw new InvalidOperationException($"Expected environment variable {MarkerPathEnvironmentVariable}.");
        string failureDiagnosticPath = $"{markerPath}.FAIL.txt";
        File.Delete(markerPath);
        File.Delete(failureDiagnosticPath);
        AssertEqual(true, AreDpiAwarenessContextsEqual(GetThreadDpiAwarenessContext(), (nint)(-4)), "Per-monitor V2 DPI awareness");
        Application.Start(initializationParams =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new HelperApplication(markerPath, failureDiagnosticPath);
        });
    }

    [DllImport("user32.dll")]
    private static extern nint GetThreadDpiAwarenessContext();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(nint first, nint second);

    private static async Task AssertDictationTextSettings(SettingsWindow window)
    {
        var root = (FrameworkElement)window.Content;
        var dispatcher = window.DispatcherQueue;
        var dictionary = (TextBox)root.FindName("PersonalDictionaryBox");
        var dictionaryStatus = (TextBlock)root.FindName("DictionaryStatusText");
        var commands = (ToggleSwitch)root.FindName("VoiceCommandsToggle");
        var commandsStatus = (TextBlock)root.FindName("VoiceCommandsStatusText");
        Task OnUiAsync(Action action)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!dispatcher.TryEnqueue(() =>
            {
                try { action(); done.SetResult(); }
                catch (Exception error) { done.SetException(error); }
            }))
                done.SetException(new InvalidOperationException("UI dispatcher unavailable"));
            return done.Task;
        }

        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        RoutedEventHandler loadedHandler = (_, _) => loaded.TrySetResult();
        root.Loaded += loadedHandler;
        window.Activate();
        if (root.IsLoaded)
            loaded.TrySetResult();
        await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await OnUiAsync(() => root.Loaded -= loadedHandler);

        async Task ChangeDictionaryAsync(string value, Action assert)
        {
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            TextChangedEventHandler handler = (_, _) => changed.TrySetResult();
            await OnUiAsync(() =>
            {
                dictionary.TextChanged += handler;
                dictionary.Text = value;
            });
            try
            {
                await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await OnUiAsync(assert);
            }
            finally
            {
                await OnUiAsync(() => dictionary.TextChanged -= handler);
            }
        }

        string saved = "";
        bool enabled = false;
        // Mirrors PersonalDictionaryStore.Replace: only valid text is saved.
        Func<string, DictionarySaveResult> saveDictionary = value =>
        {
            if (!PersonalDictionary.TryParse(value, out _, out string? error))
                return new DictionarySaveResult(DictionarySaveStatus.Invalid, error);
            saved = value;
            return new DictionarySaveResult(DictionarySaveStatus.Saved);
        };
        Func<bool, bool> saveCommands = value => { enabled = value; return true; };
        await OnUiAsync(() =>
        {
            window.PersonalDictionaryChanged += saveDictionary;
            window.VoiceCommandsChanged += saveCommands;
            window.LoadSettings(global::WinWispr.Core.AppSettings.Default);
            AssertEqual(false, commands.IsOn, "Commands initially disabled");
            commands.IsOn = true;
            AssertEqual(true, enabled, "Command toggle dispatches change");
        });
        await ChangeDictionaryAsync("Win Whisper = WinWispr", () =>
            AssertEqual(dictionary.Text, saved, "Valid dictionary dispatches change"));
        await ChangeDictionaryAsync("invalid", () =>
        {
            AssertEqual("Win Whisper = WinWispr", saved, "Invalid dictionary retains saved value");
            AssertEqual(true, dictionaryStatus.Text.Length > 0, "Invalid dictionary explains correction");
        });
        await ChangeDictionaryAsync("", () =>
        {
            AssertEqual("", saved, "Dictionary can be cleared");
            AssertEqual("", dictionaryStatus.Text, "Error clears after correction");
            window.VoiceCommandsChanged -= saveCommands;
            window.VoiceCommandsChanged += _ => false;
            commands.IsOn = false;
            AssertEqual(true, commands.IsOn, "Failed save restores command toggle");
            AssertEqual(true, commandsStatus.Text.Length > 0, "Command save failure is visible");
            window.PersonalDictionaryChanged -= saveDictionary;
            window.PersonalDictionaryChanged += _ =>
                new DictionarySaveResult(DictionarySaveStatus.Failed, PersonalDictionaryStore.SaveFailedMessage);
        });
        await ChangeDictionaryAsync("a = b", () =>
        {
            AssertEqual(true, dictionaryStatus.Text.Length > 0, "Dictionary save failure is visible");
            window.SetHotkeyStatus(HotkeyCaptureTarget.Replay, false, AppSettings.Default.ReplayHotkey, null);
            AssertEqual(SettingsWindow.HotkeyUnavailableText, ((TextBlock)root.FindName("ReplayHotkeyStatusText")).Text,
                "Replay hotkey conflict is visible");
        });
    }

    private static void AssertSpeechProcessingSettings(SettingsWindow window)
    {
        var root = (FrameworkElement)window.Content;
        var selection = (ComboBox)root.FindName("SpeechModeComboBox");
        var status = (TextBlock)root.FindName("SpeechStatusText");
        var pending = (TextBlock)root.FindName("SpeechSelectionStatusText");
        window.LoadSettings(AppSettings.Default with { SpeechMode = SpeechMode.Nvidia });
        AssertEqual(2, selection.SelectedIndex, "Saved processing mode is restored");
        SpeechMode? chosen = null;
        Func<SpeechMode, bool> changed = mode => { chosen = mode; return true; };
        window.SpeechModeChanged += changed;
        selection.SelectedIndex = 3;
        AssertEqual<SpeechMode?>(SpeechMode.Intel, chosen, "Intel selection is dispatched");
        window.SetSpeechStatus("Active: CPU", loading: false, pending: true);
        AssertEqual("Active: CPU", status.Text, "Active backend is independent of selection");
        AssertEqual(true, pending.Text.Contains("current dictation"), "Deferred switch is explained");
        window.SetSpeechStatus("Loading processing device...", loading: true, pending: false);
        AssertEqual(false, selection.IsEnabled, "Selection cannot race a running load");
        window.SetSpeechStatus("Active: DirectML · Intel Graphics", loading: false, pending: false);
        AssertEqual(true, selection.IsEnabled, "Selection reenabled after load");
        Func<SpeechMode, bool> failed = _ => false;
        window.SpeechModeChanged -= changed;
        window.SpeechModeChanged += failed;
        selection.SelectedIndex = 1;
        AssertEqual(3, selection.SelectedIndex, "Failed save restores previous selection");
        AssertEqual(true, pending.Text.Contains("Could not save"), "Save failure is visible");
        window.SpeechModeChanged -= failed;
        window.LoadSettings(AppSettings.Default);
        window.SetSpeechStatus("Not active", loading: false, pending: false);
    }

    private static async Task AssertApplicationRoutingAsync()
    {
        var app = (global::WinWispr.App)Application.Current;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        FieldInfo Field(string name) => typeof(global::WinWispr.App).GetField(name, flags)
            ?? throw new InvalidOperationException($"Missing app field {name}.");
        string? activeMicrophone = "not started";
        AppSettings saved = AppSettings.Default with { MicrophoneId = "disconnected", Mode = RecordingMode.Hold };
        using var session = new DictationSession(saved, new DictationEffects(
            (microphone, _) => { activeMicrophone = microphone; return Task.FromResult<DictationNotice?>(null); },
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => Task.FromResult(CapturedAudio.FromArray([])),
            () => Task.CompletedTask,
            (_, _) => Task.FromResult(string.Empty),
            (_, _, _) => Task.FromResult(DeliveryResult.NoSpeech)));
        Field("_settings").SetValue(app, saved);
        Field("_session").SetValue(app, session);

        // The session always asks for the saved microphone; the capture falls back when it is not connected.
        app.ApplySettings(saved with { SettingsWindowPlacement = new WindowPlacement(10, 10, 640, 760) });
        await session.PressAsync(DictationTrigger.Tray, new WindowTarget(1));
        AssertEqual<string?>("disconnected", activeMicrophone, "Saving window placement keeps the saved microphone");
        await session.StopAsync(StopReason.User);
        app.ApplySettings(saved with { MicrophoneId = "new microphone" });
        await session.PressAsync(DictationTrigger.Tray, new WindowTarget(1));
        AssertEqual("new microphone", activeMicrophone, "An explicit microphone selection reaches the session");
        await session.StopAsync(StopReason.User);
        // Hold release, pending model starts, and task-button ownership are covered by DictationSessionTests.
    }

    private static async Task RunIsolatedWindowAssertions()
    {
        AssertOverlayLayout();

        SettingsWindow? window = null;
        try
        {
            window = new SettingsWindow();
            AssertSpeechProcessingSettings(window);
            AssertEqual(
                Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level1,
                Microsoft.UI.Xaml.Automation.AutomationProperties.GetHeadingLevel(window.SettingsPageTitle),
                "Settings page heading level");
            AssertEqual(
                Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2,
                Microsoft.UI.Xaml.Automation.AutomationProperties.GetHeadingLevel(window.HotkeyModeHeading),
                "Hotkey section heading level");
            AssertEqual(
                Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2,
                Microsoft.UI.Xaml.Automation.AutomationProperties.GetHeadingLevel(window.MicrophoneHeading),
                "Microphone section heading level");
            AssertEqual(
                Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2,
                Microsoft.UI.Xaml.Automation.AutomationProperties.GetHeadingLevel(window.ModelHeading),
                "Model section heading level");
            AssertEqual(
                Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2,
                Microsoft.UI.Xaml.Automation.AutomationProperties.GetHeadingLevel(window.StartupHeading),
                "Startup section heading level");
            AssertEqual(
                Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2,
                Microsoft.UI.Xaml.Automation.AutomationProperties.GetHeadingLevel(window.TranslationHeading),
                "Translation section heading level");
            AssertEqual("On", window.StartupToggle.OnContent?.ToString(), "Startup toggle on content");
            AssertEqual("Off", window.StartupToggle.OffContent?.ToString(), "Startup toggle off content");
            var root = (FrameworkElement)window.Content;
            ScrollViewer settingsScrollViewer = window.PageScrollViewer;
            AssertEqual(ScrollMode.Disabled, settingsScrollViewer.HorizontalScrollMode, "Settings horizontal scrolling");
            AssertEqual(ScrollMode.Enabled, settingsScrollViewer.VerticalScrollMode, "Settings vertical scrolling");
            AssertEqual(true, settingsScrollViewer.IsVerticalRailEnabled, "Settings vertical scroll rail");
            var modelSection = (Microsoft.UI.Xaml.Controls.StackPanel)(root.FindName("ModelSection")
                ?? throw new InvalidOperationException("Expected ModelSection in the Settings namescope."));
            AssertEqual(
                "Local speech model",
                ((Microsoft.UI.Xaml.Controls.TextBlock)modelSection.Children[1]).Text,
                "Model user-facing label");
            AssertEqual(
                Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw,
                Microsoft.UI.Xaml.Automation.AutomationProperties.GetAccessibilityView(window.TitleBarIcon),
                "Title bar icon accessibility view");
            AssertModelSizeAccessibleNameIncludesSizes(window);
            AssertSavedMicrophoneFallbackDoesNotChangeSavedPreference(window);
            AssertStartupFailureRestoresToggle(window);
            AssertMicrophoneState(
                window,
                MicrophoneLoadState.Ready,
                expectedStatusText: string.Empty,
                expectedStatusVisibility: Visibility.Collapsed);
            AssertMicrophoneState(
                window,
                MicrophoneLoadState.NoDevices,
                expectedStatusText: "No microphones found. Windows default remains active.",
                expectedStatusVisibility: Visibility.Visible);
            AssertMicrophoneState(
                window,
                MicrophoneLoadState.EnumerationFailed,
                expectedStatusText: "Could not list microphones. Windows default remains active.",
                expectedStatusVisibility: Visibility.Visible);
            AssertMicrophoneState(
                window,
                MicrophoneLoadState.SavedDeviceUnavailable,
                expectedStatusText: "Saved microphone unavailable. Windows default remains active.",
                expectedStatusVisibility: Visibility.Visible);
            AssertModelState(
                window,
                ModelUiState.Loading,
                progress: 0d,
                expectedStatusText: string.Empty,
                expectedStatusVisibility: Visibility.Collapsed,
                expectedProgressText: "Loading model...",
                expectedProgressVisibility: Visibility.Visible,
                expectedProgressBarVisibility: Visibility.Visible,
                expectedIsIndeterminate: true,
                expectedProgressValue: 0d,
                expectedActionText: string.Empty,
                expectedActionVisibility: Visibility.Collapsed,
                expectedActionEnabled: false,
                expectedCancelVisibility: Visibility.Collapsed,
                expectedCancelEnabled: false);
            AssertModelState(
                window,
                ModelUiState.Downloading,
                progress: 0.5d,
                expectedStatusText: string.Empty,
                expectedStatusVisibility: Visibility.Collapsed,
                expectedProgressText: "Downloading model... 50%",
                expectedProgressVisibility: Visibility.Visible,
                expectedProgressBarVisibility: Visibility.Visible,
                expectedIsIndeterminate: false,
                expectedProgressValue: 0.5d,
                expectedActionText: string.Empty,
                expectedActionVisibility: Visibility.Collapsed,
                expectedActionEnabled: false,
                expectedCancelVisibility: Visibility.Visible,
                expectedCancelEnabled: true);
            AssertModelState(
                window,
                ModelUiState.DownloadFailed,
                progress: 0d,
                expectedStatusText: "Download failed. Check your connection, then try again.",
                expectedStatusVisibility: Visibility.Visible,
                expectedProgressText: string.Empty,
                expectedProgressVisibility: Visibility.Collapsed,
                expectedProgressBarVisibility: Visibility.Collapsed,
                expectedIsIndeterminate: false,
                expectedProgressValue: 0d,
                expectedActionText: "Try again",
                expectedActionVisibility: Visibility.Visible,
                expectedActionEnabled: true,
                expectedCancelVisibility: Visibility.Collapsed,
                expectedCancelEnabled: false);
            window.LoadSettings(AppSettings.Default);
            AssertEqual("Alt + Shift + E", window.EnglishDictationHotkeyButton.Content?.ToString() ?? string.Empty,
                "English dictation hotkey default");
            AssertEqual(1, window.EnglishDictationModeComboBox.SelectedIndex, "English dictation defaults to Hold");
            RecordingMode? changedEnglishMode = null;
            Action<RecordingMode> recordEnglishMode = mode => changedEnglishMode = mode;
            window.EnglishDictationModeChanged += recordEnglishMode;
            window.EnglishDictationModeComboBox.SelectedIndex = 0;
            window.EnglishDictationModeChanged -= recordEnglishMode;
            AssertEqual<RecordingMode?>(RecordingMode.Toggle, changedEnglishMode, "English dictation mode change is reported");
            window.LoadSettings(AppSettings.Default with { EnglishDictationMode = RecordingMode.Toggle });
            AssertEqual<RecordingMode?>(RecordingMode.Toggle, changedEnglishMode, "Loading settings does not report a change");
            AssertEqual(0, window.EnglishDictationModeComboBox.SelectedIndex, "English dictation Toggle is shown");
            window.BeginHotkeyCapture(HotkeyCaptureTarget.EnglishDictation);
            AssertEqual(false, window.EnglishDictationHotkeyButton.IsEnabled, "English dictation capture disables its button");
            window.SetHotkey(HotkeyCaptureTarget.EnglishDictation, AppSettings.Default.EnglishDictationHotkey);
            AssertEqual(true, window.EnglishDictationHotkeyButton.IsEnabled, "English dictation capture ends");
            window.SetHotkeyStatus(HotkeyCaptureTarget.EnglishDictation, false, AppSettings.Default.EnglishDictationHotkey, null);
            AssertEqual(SettingsWindow.HotkeyUnavailableText, window.EnglishDictationHotkeyStatusText.Text, "English dictation unavailable status");
            window.SetHotkeyStatus(HotkeyCaptureTarget.EnglishDictation, true, AppSettings.Default.EnglishDictationHotkey, null);
            AssertEqual(SettingsWindow.HotkeyAvailableText, window.EnglishDictationHotkeyStatusText.Text, "English dictation available status");
            AssertEqual("Alt + Shift + U", window.TranslateHotkeyButton.Content?.ToString() ?? string.Empty,
                "Translate hotkey default");
            AssertEqual("Alt + Shift + V", window.ReplayHotkeyButton.Content?.ToString() ?? string.Empty,
                "Replay hotkey default");
            window.BeginHotkeyCapture(HotkeyCaptureTarget.Translate);
            AssertEqual(false, window.TranslateHotkeyButton.IsEnabled, "Translate capture disables its button");
            window.SetHotkey(HotkeyCaptureTarget.Translate, AppSettings.Default.TranslateHotkey);
            AssertEqual(true, window.TranslateHotkeyButton.IsEnabled, "Translate capture ends");
            window.SetHotkeyStatus(HotkeyCaptureTarget.Translate, false, AppSettings.Default.TranslateHotkey, null);
            AssertEqual(SettingsWindow.HotkeyUnavailableText, window.TranslateHotkeyStatusText.Text, "Translate unavailable status");
            window.SetHotkeyStatus(HotkeyCaptureTarget.Translate, true, AppSettings.Default.TranslateHotkey, null);
            AssertEqual(SettingsWindow.HotkeyAvailableText, window.TranslateHotkeyStatusText.Text, "Translate available status");
            var legacyTranslate = new Hotkey(HotkeyModifiers.Control | HotkeyModifiers.Alt, 0x45);
            var euro = new HotkeyCharacterClash("€", "German (Germany)");
            window.SetHotkeyStatus(HotkeyCaptureTarget.Translate, true, legacyTranslate, euro);
            AssertEqual(SettingsWindow.HotkeyClashText(legacyTranslate, euro), window.TranslateHotkeyStatusText.Text,
                "Translate clash status");
            window.SetHotkeyStatus(HotkeyCaptureTarget.Primary, true, AppSettings.Default.Hotkey, null);
            AssertEqual(Visibility.Collapsed, window.HotkeyStatusText.Visibility, "A free dictation hotkey shows no status");
            window.ShowHotkey(HotkeyCaptureTarget.Replay);
            AssertEqual(Visibility.Visible, window.DictationPage.Visibility, "The clash notice opens the replay hotkey's page");
            window.ShowHotkey(HotkeyCaptureTarget.Translate);
            AssertEqual(Visibility.Visible, window.TranslationPage.Visibility, "The clash notice opens the translate hotkey's page");
            AssertEqual(Visibility.Collapsed, window.DictationPage.Visibility, "Only the hotkey's page shows");
            AssertTranslationState(
                window,
                TranslationModelState.Missing,
                "The translation model is not installed. Install WinWispr Models to add it.");
            AssertTranslationState(
                window,
                TranslationModelState.Installed,
                "Translation is ready. The model uses about 218 MB.");
            // The translation model state never touches the speech model's controls.
            AssertEqual("Try again", window.DownloadButton.Content?.ToString() ?? string.Empty, "Speech model action after translation states");
            await AssertDictationTextSettings(window);
            AssertSettingsNavigation(window);
            // Translation is the longest page, so it still scrolls at the default size.
            window.SettingsNavigation.SelectedItem = window.TranslationNavItem;
            await AssertScrollSurfaceAsync(window, settingsScrollViewer, "Settings");
            await AssertTaskCaptureScrollingAsync(window.DispatcherQueue);
        }
        finally
        {
            window?.AppWindow.Hide();
        }
    }

    private static async Task AssertTaskCaptureScrollingAsync(DispatcherQueue dispatcher)
    {
        BoardTag work = new() { Name = "Work", Color = "green" };
        BoardTag home = new() { Name = "Home", Color = "blue" };
        var capture = new TaskCaptureWindow(
            Guid.NewGuid(),
            new TaskDraftParseResult("Captured text", "Description", "New task", null, null, [], [work.Id], []),
            [work, home],
            dispatcher,
            (_, _, _, _, _, _) => true,
            (_, _, _, _, _) => { },
            _ => true);
        try
        {
            var root = (FrameworkElement)capture.Content;
            capture.ShowCapture();
            await Until(() => root.IsLoaded, "Task capture loaded");
            Button workTag = Descendants<Button>(root).Single(button => button.Content?.ToString() == "Work");
            AssertEqual("Add tag Work", Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(workTag), "Existing tag is available in new task");
            ((IInvokeProvider)new ButtonAutomationPeer(workTag).GetPattern(PatternInterface.Invoke)!).Invoke();
            await Until(() => Descendants<Button>(root).Any(button => Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(button) == "Remove tag Work"), "Existing tag can be selected");
            AssertEqual(false, Descendants<Button>(root).Any(button => button.Content?.ToString() == "+ Work"), "Selected tag is not duplicated as a suggestion");
            capture.UpdateTags([work, home, new BoardTag { Name = "Later", Color = "purple" }]);
            AssertEqual(true, Descendants<Button>(root).Any(button => Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(button) == "Add tag Later"), "Reopened task uses current tags");
            AssertEqual(true, Descendants<AutoSuggestBox>(root).Single().ItemsSource is IEnumerable<string> items && items.Contains("Later"), "Tag completion uses current tags");
            AssertEqual(true, root.ActualWidth >= 500, "Task capture retains its DIP width at high DPI");
            var scroll = ((Grid)root).Children.OfType<ScrollViewer>().Single(view => view.VerticalScrollMode == ScrollMode.Enabled);
            AssertEqual(ScrollMode.Disabled, scroll.HorizontalScrollMode, "Task capture horizontal scrolling");
            AssertEqual(ScrollMode.Enabled, scroll.VerticalScrollMode, "Task capture vertical scrolling");
            AssertEqual(true, scroll.IsVerticalRailEnabled, "Task capture vertical scroll rail");
            await AssertScrollSurfaceAsync(capture, scroll, "Task capture");
        }
        finally
        {
            capture.Dispose();
        }

        var emptyCapture = new TaskCaptureWindow(
            Guid.NewGuid(),
            new TaskDraftParseResult("Captured text", "Description", "New task", null, null, [], [], []),
            [],
            dispatcher,
            (_, _, _, _, _, _) => true,
            (_, _, _, _, _) => { },
            _ => true);
        try
        {
            var emptyRoot = (FrameworkElement)emptyCapture.Content;
            emptyCapture.ShowCapture();
            await Until(() => emptyRoot.IsLoaded, "Empty task capture loaded");
            emptyCapture.UpdateTags([new BoardTag { Name = "First", Color = "amber" }]);
            // The tag section was hidden until now; its buttons reach the visual tree with the next layout pass.
            await Until(() => Descendants<Button>(emptyRoot).Any(button => Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(button) == "Add tag First"), "Empty task capture shows tags added later");
        }
        finally
        {
            emptyCapture.Dispose();
        }
    }

    private static void AssertSettingsNavigation(SettingsWindow window)
    {
        StackPanel[] pages = [window.GeneralPage, window.DictationPage, window.TaskBoardPage, window.SpeechPage, window.TranslationPage];
        AssertEqual(5, window.SettingsNavigation.MenuItems.Count, "Settings navigation items");
        AssertEqual(Visibility.Visible, window.GeneralPage.Visibility, "General page is shown first");
        for (int i = 0; i < pages.Length; i++)
        {
            window.SettingsNavigation.SelectedItem = window.SettingsNavigation.MenuItems[i];
            for (int j = 0; j < pages.Length; j++)
                AssertEqual(i == j ? Visibility.Visible : Visibility.Collapsed, pages[j].Visibility, $"Settings page {j} after selecting item {i}");
        }
        window.SettingsNavigation.SelectedItem = window.SettingsNavigation.MenuItems[0];
        window.ShowModelSection();
        AssertEqual<object?>(window.SpeechNavItem, window.SettingsNavigation.SelectedItem, "Show model selects the Speech model page");
        AssertEqual(Visibility.Visible, window.SpeechPage.Visibility, "Show model shows the Speech model page");
    }

    private static async Task AssertScrollSurfaceAsync(Window window, ScrollViewer scroll, string name)
    {
        window.Activate();
        await Until(() => scroll.ScrollableHeight > 0, $"{name} content is scrollable");
        scroll.ChangeView(null, 0, null, disableAnimation: true);
        await Until(() => scroll.VerticalOffset <= 0.5, $"{name} scroll reset");
        if (Environment.GetEnvironmentVariable("WINWISPR_UI_MANUAL_SCROLL") == "1")
        {
            var root = (FrameworkElement)window.Content;
            root.AddHandler(UIElement.PointerPressedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, e) => Console.WriteLine($"PRESS {name} source={e.OriginalSource.GetType().Name} point={e.GetCurrentPoint(root).Position}")), true);
            root.AddHandler(UIElement.PointerWheelChangedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, e) => Console.WriteLine($"WHEEL {name} source={e.OriginalSource.GetType().Name} point={e.GetCurrentPoint(root).Position} delta={e.GetCurrentPoint(root).Properties.MouseWheelDelta}")), true);
            Console.WriteLine($"READY: {name}; scroll down over empty space, then back to the top.");
            for (int i = 0; i < 2400 && scroll.VerticalOffset <= 0.5; i++)
                await Task.Delay(100);
            AssertEqual(true, scroll.VerticalOffset > 0.5, $"{name} manual wheel down");
            for (int i = 0; i < 2400 && scroll.VerticalOffset > 0.5; i++)
                await Task.Delay(100);
            AssertEqual(true, scroll.VerticalOffset <= 0.5, $"{name} manual wheel up");
        }
    }

    private static async Task RunTaskBoardAssertions()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        string folder = Path.Combine(AppContext.BaseDirectory, "task-board-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "tasks.json");
        var store = new TaskBoardStore(path);
        TaskBoardDocument document = store.Load();
        BoardTask original = TaskBoardOperations.CreateTask(document, "Original", "saved", "", null, [], DateTimeOffset.UtcNow);
        store.Save(document);
        var dispatcher = DispatcherQueue.GetForCurrentThread();
        var board = new TaskBoardWindow(store, document, null, 400, dispatcher);
        var root = (FrameworkElement)board.Content;
        object? Call(string name, params object?[] args) => typeof(TaskBoardWindow).GetMethod(name, flags)!.Invoke(board, args);
        T Field<T>(string name) => (T)typeof(TaskBoardWindow).GetField(name, flags)!.GetValue(board)!;
        void Set(string name, object? value) => typeof(TaskBoardWindow).GetField(name, flags)!.SetValue(board, value);
        TaskBoardRecoveryFile Recovery() => System.Text.Json.JsonSerializer.Deserialize<TaskBoardRecoveryFile>(File.ReadAllText(store.RecoveryPath))!;
        board.ActivateBoard();
        SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcher));
        await Until(() => root.IsLoaded, "Board loaded");
        XamlRoot xamlRoot = root.XamlRoot ?? throw new InvalidOperationException("Expected board XamlRoot.");
        board.OpenTask(original.Id);
        Set("_editingDescription", true);
        Call("BuildDrawer", original);
        root.UpdateLayout();
        TextBox savedDescription = Descendants<TextBox>(root).Single(box => Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(box) == "Description in Markdown");
        savedDescription.Text = "saved appended";
        Call("CloseDrawer");
        Call("CloseDrawer");
        AssertEqual("saved appended", new TaskBoardStore(path).Load().Tasks.Single(task => task.Id == original.Id).Description, "Description edit is durable after closing the drawer");
        board.OpenTask(original.Id);
        await Until(() => Descendants<Button>(root).Any(button => Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(button) == "Edit description"), "Description preview is visible after reopening");
        Button editDescriptionBeforeTags = Descendants<Button>(root).Single(button => Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(button) == "Edit description");
        ((IInvokeProvider)new ButtonAutomationPeer(editDescriptionBeforeTags).GetPattern(PatternInterface.Invoke)!).Invoke();
        root.UpdateLayout();
        TextBox reopenedDescriptionBeforeTags = Descendants<TextBox>(root).Single(box => Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(box) == "Description in Markdown");
        AssertEqual("saved appended", reopenedDescriptionBeforeTags.Text, "Description edit remains in the editor after reopening");
        Call("CloseDrawer");
        Call("CloseDrawer");
        ContentDialog? tagManager = null;
        Call("ShowTagManager");
        await Until(() =>
        {
            tagManager = VisualTreeHelper.GetOpenPopupsForXamlRoot(xamlRoot)
                .Select(popup => popup.Child)
                .OfType<ContentDialog>()
                .FirstOrDefault(dialog => string.Equals(dialog.Title?.ToString(), "Manage tags", StringComparison.Ordinal));
            return tagManager is not null;
        }, "Tag manager opened");
        DependencyObject tagManagerContent = tagManager!.Content as DependencyObject ?? throw new InvalidOperationException("Expected tag manager content.");
        TextBox managedTagName = Descendants<TextBox>(tagManagerContent).Single(textBox => Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(textBox) == "New tag name");
        Button addManagedTag = Descendants<Button>(tagManagerContent).Single(button => Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(button) == "Add tag");
        managedTagName.Text = "Managed";
        ((IInvokeProvider)new ButtonAutomationPeer(addManagedTag).GetPattern(PatternInterface.Invoke)!).Invoke();
        AssertEqual(true, document.Tags.Any(tag => tag.Name == "Managed"), "Tag manager adds a tag");
        AssertEqual(true, new TaskBoardStore(path).Load().Tags.Any(tag => tag.Name == "Managed"), "Managed tag is durable");
        tagManager!.Hide();
        board.ToggleBoard();
        await Until(() => !board.IsVisible, "Board toggle hides the visible board");
        board.ToggleBoard();
        await Until(() => board.IsVisible, "Board toggle shows the hidden board");
        var savedDraft = new TaskCaptureDraft { OriginalTranscript = "Saved draft" };
        document.Draft = savedDraft;
        AssertEqual(true, (bool)Call("TrySave")!, "Saved task draft is durable");
        AssertEqual("Saved", Field<TextBlock>("_footerSaveStatus").Text, "Saved task draft does not show pending edits");
        // A saved draft is part of the saved board, not an unsaved change: it must not hold back a recovery import.
        AssertEqual(false, board.HasPendingDurableData, "Saved task draft is not an unsaved change");
        AssertEqual(savedDraft.Id, new TaskBoardStore(path).Load().Draft?.Id, "Saved task draft remains recoverable");
        AssertEqual(true, board.DiscardTaskDraft(savedDraft.Id), "Saved task draft can be discarded");
        AssertEqual(Visibility.Collapsed, Field<Grid>("_taskSavedHost").Visibility, "Closed save notice does not cover board controls");
        AssertEqual(0, Grid.GetColumn(Field<TextBox>("_searchBox")), "Search field occupies the first toolbar column");
        AssertEqual(1, Grid.GetColumn(Field<StackPanel>("_toolbarActions")), "Sort and task actions occupy the second toolbar column");
        AssertEqual((byte)255, ((SolidColorBrush)Field<Border>("_drawer").Background).Color.A, "Details drawer uses an opaque surface");
        board.OpenTask(original.Id);
        Set("_editingDescription", true);
        Call("BuildDrawer", original);
        root.UpdateLayout();
        TextBox description = Descendants<TextBox>(root).Single(box => Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(box) == "Description in Markdown");
        string invalid = new('x', 100_001);
        description.Text = invalid;
        await Until(() => File.Exists(store.RecoveryPath) && Recovery().PendingDescriptions.GetValueOrDefault(original.Id) == invalid,
            "Invalid description is saved by the debounce timer");
        Call("CloseDrawer");
        Call("CloseDrawer");
        Call("StartQuickTask");
        TextBox quick = (TextBox)Field<Border>("_quickEntryHost").Child;
        quick.Text = "Quick task";
        await Until(() => Recovery().PendingQuickEntry == "Quick task", "Quick entry is recoverable before submission");
        AssertEqual(true, (bool)Call("SubmitQuickTask")!, "Quick task saved");
        var reopened = new TaskBoardStore(path);
        AssertEqual(2, reopened.Load().Tasks.Count, "Exactly one quick task committed");
        AssertEqual(invalid, reopened.RecoveryPendingDescriptions[original.Id], "Quick task preserves unrelated pending description");
        AssertEqual<string?>(null, reopened.RecoveryPendingQuickEntry, "Committed quick input is removed from recovery");

        Field<Dictionary<Guid, string>>("_descriptionEdits").Clear();
        Call("TrySave");
        store.SaveRecoveryTo(Path.Combine(folder, "export.json"), document, new Dictionary<Guid, string>());
        Set("_exportedRecoveryIdentity", Call("CurrentRecoveryIdentity"));
        File.WriteAllText(store.RecoveryPath, "locked recovery fixture");
        using (var primaryLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        using (var recoveryLock = new FileStream(store.RecoveryPath, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            original.DueDate = new DateOnly(2028, 3, 5);
            AssertEqual(false, (bool)Call("PersistTask")!, "Metadata edit reports the locked save");
            AssertEqual(false, board.TryPrepareForShutdown(), "Old export cannot authorize exit with new metadata");
        }
        AssertEqual(true, board.TryPrepareForShutdown(), "Retry saves after locks are released");
        AssertEqual(original.DueDate, new TaskBoardStore(path).Load().Tasks.Single(task => task.Id == original.Id).DueDate, "Retried metadata survives restart");
        using (var primaryLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            original.Status = BoardTaskStatus.Doing;
            AssertEqual(false, (bool)Call("TrySave")!, "Locked primary reports failure");
            AssertEqual(true, board.SaveRecoveryCopy(), "Metadata fallback saved");
            AssertEqual(true, board.SaveRecoveryCopy(), "Repeated fallback remains durable");
            AssertEqual(true, File.Exists(store.RecoveryPath), "Repeated fallback does not delete pending metadata");
        }
        AssertEqual(BoardTaskStatus.Doing, new TaskBoardStore(path).Load().Tasks.Single(task => task.Id == original.Id).Status, "Metadata-only recovery survives restart");
        Call("TrySave");

        int releases = 0;
        board.TaskCaptureReleased += () => releases++;
        board.SetTaskButtonMode(RecordingMode.Hold);
        AssertEqual(true, (bool)Call("BeginTaskButtonHold")!, "Button begins Hold");
        board.SetTaskButtonMode(RecordingMode.Toggle);
        Call("EndTaskButtonHold");
        Call("EndTaskButtonHold");
        AssertEqual(1, releases, "Mode change and duplicate release finish the original Hold once");

        Call("CloseDrawer");
        Call("CloseDrawer");
        document.View.ShowArchived = true;
        original.Status = BoardTaskStatus.Done;
        original.ArchivedUtc = DateTimeOffset.UtcNow;
        Call("RefreshBoard");
        root.UpdateLayout();
        Button OriginalCard() => Descendants<Button>(root).Single(button => button.Tag is Guid id && id == original.Id);
        AssertEqual(false, OriginalCard().CanDrag, "Archived card cannot be dragged");
        original.ArchivedUtc = null;
        Call("RefreshBoard");
        root.UpdateLayout();
        AssertEqual(true, OriginalCard().CanDrag, "Restored card regains drag handling");
        var measurements = new List<object>();
        foreach (int size in new[] { 1000, 5000 })
        {
            document.Tasks.Clear();
            for (int index = 0; index < size; index++)
                document.Tasks.Add(new BoardTask { Title = $"Task {index:D5}", ManualOrder = index });
            var opened = System.Diagnostics.Stopwatch.StartNew();
            Call("RefreshBoard");
            root.UpdateLayout();
            opened.Stop();
            await Task.Delay(100);
            ListView list = Field<Dictionary<BoardTaskStatus, ListView>>("_lists")[BoardTaskStatus.ToDo];
            int realized = Descendants<ContentControl>(list).Count(host => host.Name == "TaskCardHost");
            AssertEqual(true, realized > 0 && realized < 200, "Only visible cards are realized");
            ScrollViewer scroll = Descendants<ScrollViewer>(list).First();
            scroll.ChangeView(null, 800, null, true);
            await Task.Delay(100);
            double offset = scroll.VerticalOffset;
            object column = Field<Grid>("_boardGrid").Children[0];
            var refresh = new List<double>();
            for (int sample = 0; sample < 10; sample++)
            {
                var timer = System.Diagnostics.Stopwatch.StartNew();
                Call("RefreshBoard");
                root.UpdateLayout();
                timer.Stop();
                refresh.Add(timer.Elapsed.TotalMilliseconds);
            }
            AssertEqual(true, ReferenceEquals(column, Field<Grid>("_boardGrid").Children[0]), "Column survives refresh");
            AssertEqual(offset, scroll.VerticalOffset, 1, "Refresh preserves scroll offset");
            var searchTimes = new List<double>();
            for (int sample = 0; sample < 10; sample++)
            {
                document.View.Search = sample % 2 == 0 ? "Task 00" : "Task";
                var timer = System.Diagnostics.Stopwatch.StartNew();
                Call("RefreshBoard");
                root.UpdateLayout();
                searchTimes.Add(timer.Elapsed.TotalMilliseconds);
            }
            document.View.Search = "";
            Call("RefreshBoard");
            var saveTimer = System.Diagnostics.Stopwatch.StartNew();
            Task save = (Task)Call("SaveScheduledAsync")!;
            double inputMs = saveTimer.Elapsed.TotalMilliseconds;
            await save;
            measurements.Add(new { size, realized, openMs = opened.Elapsed.TotalMilliseconds,
                refreshP95Ms = refresh.Order().ElementAt(9), searchP95Ms = searchTimes.Order().ElementAt(9),
                inputMs, saveMs = saveTimer.Elapsed.TotalMilliseconds,
                memoryBytes = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64 });
            AssertEqual(true, inputMs < 100, $"Snapshot dispatch stays below 100ms: {inputMs:F2}ms at {size} tasks");
            AssertEqual(true, searchTimes.Order().ElementAt(9) < 250, $"Search and layout stay below 250ms: {searchTimes.Max():F2}ms at {size} tasks");
            AssertEqual(size, new TaskBoardStore(path).Load().Tasks.Count, "Background snapshot is durable");
        }
        string metrics = Environment.GetEnvironmentVariable(MarkerPathEnvironmentVariable)! + ".board.json";
        File.WriteAllText(metrics, System.Text.Json.JsonSerializer.Serialize(new
        {
            Host = Environment.MachineName, Runtime = Environment.Version.ToString(), Measurements = measurements,
            Scenarios = "invalid input debounce; quick entry recovery; export exit protection; retry; Hold origin/mode; virtualization; stable scroll"
        }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"TASK_BOARD_EVIDENCE={metrics}");
        board.TryPrepareForShutdown();
        board.AppWindow.Hide();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) yield return match;
            foreach (T descendant in Descendants<T>(child)) yield return descendant;
        }
    }

    private static async Task Until(Func<bool> condition, string message)
    {
        for (int attempt = 0; attempt < 200; attempt++)
        {
            try { if (condition()) return; }
            catch (IOException) { }
            await Task.Delay(25);
        }
        throw new InvalidOperationException(message);
    }

    private static void AssertOverlayLayout()
    {
        OverlayWindow? window = null;
        try
        {
            window = new OverlayWindow();
            var surface = (Border)(((FrameworkElement)window.Content).FindName("Surface")
                ?? throw new InvalidOperationException("Expected Surface in the overlay namescope."));

            AssertEqual(224d, surface.Width, "Overlay width");
            AssertEqual(48d, surface.Height, "Overlay height");
            AssertEqual(surface.Height / 2, surface.CornerRadius.TopLeft, 0.01d, "Overlay semicircle radius");
            AssertEqual(1d, surface.BorderThickness.Left, 0.01d, "Overlay border thickness");
            var backgroundBrush = (SolidColorBrush)surface.Background;
            var borderBrush = (SolidColorBrush)surface.BorderBrush;
            if (borderBrush.Color.A == 0 ||
                borderBrush.Color.R <= backgroundBrush.Color.R ||
                borderBrush.Color.G <= backgroundBrush.Color.G ||
                borderBrush.Color.B <= backgroundBrush.Color.B)
            {
                throw new InvalidOperationException("Overlay border must be visible and lighter than its background.");
            }

            AssertRenderedText(surface, "Transcribe", "Hover text");
            AssertRenderedText(surface, "Delete recording?", "Delete text");
            AssertRenderedText(surface, "Processing", "Status text");
            window.ShowStatus(OverlayStatus.Progress("Preparing…"));
            var statusSpinner = (ProgressRing)(surface.FindName("StatusSpinner")
                ?? throw new InvalidOperationException("Expected StatusSpinner in the overlay namescope."));
            AssertEqual(true, statusSpinner.IsActive, "Status spinner is active");
            AssertOverlayActions(window, surface);
        }
        finally
        {
            window?.AppWindow.Hide();
        }
    }

    private static void AssertRenderedText(Border surface, string text, string message)
    {
        var recordingPanel = (Grid)(surface.FindName("RecordingPanel")
            ?? throw new InvalidOperationException("Expected RecordingPanel in the overlay namescope."));
        var hoverPanel = (Grid)(surface.FindName("HoverPanel")
            ?? throw new InvalidOperationException("Expected HoverPanel in the overlay namescope."));
        var cancelPanel = (Grid)(surface.FindName("CancelPanel")
            ?? throw new InvalidOperationException("Expected CancelPanel in the overlay namescope."));
        var statusPanel = (StackPanel)(surface.FindName("StatusPanel")
            ?? throw new InvalidOperationException("Expected StatusPanel in the overlay namescope."));
        var statusText = (TextBlock)(surface.FindName("StatusText")
            ?? throw new InvalidOperationException("Expected StatusText in the overlay namescope."));

        recordingPanel.Visibility = Visibility.Collapsed;
        hoverPanel.Visibility = text == "Transcribe" ? Visibility.Visible : Visibility.Collapsed;
        cancelPanel.Visibility = text == "Delete recording?" ? Visibility.Visible : Visibility.Collapsed;
        statusPanel.Visibility = text == "Processing" ? Visibility.Visible : Visibility.Collapsed;
        statusText.Visibility = Visibility.Visible;

        surface.Measure(new Windows.Foundation.Size(surface.Width, surface.Height));
        surface.Arrange(new Windows.Foundation.Rect(0, 0, surface.Width, surface.Height));
        surface.UpdateLayout();

        TextBlock textBlock = FindText(surface, text)
            ?? throw new InvalidOperationException($"Expected overlay text '{text}'.");

        AssertEqual(14d, textBlock.FontSize, $"{message} font size");
        AssertBetween(textBlock.ActualHeight, 14d, 22d, $"{message} rendered height");
        AssertEqual(VerticalAlignment.Center, textBlock.VerticalAlignment, $"{message} vertical alignment");
        if (text == "Transcribe")
        {
            var button = (Button)(surface.FindName("TranscribeButton")
                ?? throw new InvalidOperationException("Expected TranscribeButton in the overlay namescope."));
            AssertEqual(HorizontalAlignment.Stretch, button.HorizontalAlignment, $"{message} button width");
            AssertEqual(HorizontalAlignment.Stretch, button.HorizontalContentAlignment, $"{message} horizontal alignment");
            AssertEqual(VerticalAlignment.Center, button.VerticalContentAlignment, $"{message} content alignment");
            AssertEqual(HorizontalAlignment.Stretch, textBlock.HorizontalAlignment, $"{message} text width");
            AssertEqual(TextAlignment.Center, textBlock.TextAlignment, $"{message} text alignment");
            AssertEqual(button.ActualHeight / 2, button.CornerRadius.TopLeft, 0.01d, $"{message} semicircle radius");
        }
        else if (text == "Processing")
        {
            // The status text shares a centred column with its detail line, so it fills the column and centres
            // its own text; both lines stay centred under each other.
            AssertEqual(HorizontalAlignment.Stretch, textBlock.HorizontalAlignment, $"{message} horizontal alignment");
            AssertEqual(TextAlignment.Center, textBlock.TextAlignment, $"{message} text alignment");
        }
        else
        {
            AssertEqual(HorizontalAlignment.Center, textBlock.HorizontalAlignment, $"{message} horizontal alignment");
        }
    }

    private static void AssertOverlayActions(OverlayWindow window, Border surface)
    {
        int transcribeRequests = 0;
        int deleteRequests = 0;
        int discardRequests = 0;
        window.SetActions(
            () => transcribeRequests++,
            () => deleteRequests++,
            () => discardRequests++);

        InvokeButton(surface, "TranscribeButton");
        AssertEqual(1, transcribeRequests, "Transcribe action");
        AssertEqual(0, deleteRequests, "Transcribe must not open delete confirmation");

        InvokeButton(surface, "StopButton");
        AssertEqual(1, deleteRequests, "Stop action");
        AssertEqual(0, discardRequests, "Stop must not discard before confirmation");

        InvokeButton(surface, "CancelButton");
        AssertEqual(1, discardRequests, "Confirmed delete action");
    }

    private static void InvokeButton(Border surface, string name)
    {
        var button = (Button)(surface.FindName(name)
            ?? throw new InvalidOperationException($"Expected {name} in the overlay namescope."));
        var peer = new ButtonAutomationPeer(button);
        if (peer.GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
            throw new InvalidOperationException($"Expected {name} to expose the invoke pattern.");
        invoke.Invoke();
    }

    private static TextBlock? FindText(DependencyObject parent, string text)
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, index);
            if (child is TextBlock textBlock && textBlock.Text == text)
                return textBlock;

            TextBlock? match = FindText(child, text);
            if (match is not null)
                return match;
        }

        return null;
    }

    private static void AssertModelState(
        SettingsWindow window,
        ModelUiState state,
        double progress,
        string expectedStatusText,
        Visibility expectedStatusVisibility,
        string expectedProgressText,
        Visibility expectedProgressVisibility,
        Visibility expectedProgressBarVisibility,
        bool expectedIsIndeterminate,
        double expectedProgressValue,
        string expectedActionText,
        Visibility expectedActionVisibility,
        bool expectedActionEnabled,
        Visibility expectedCancelVisibility,
        bool expectedCancelEnabled)
    {
        window.SetModelState(state, progress);

        AssertEqual(expectedStatusText, window.ModelStatusHintText.Text, $"{state} status text");
        AssertEqual(expectedStatusVisibility, window.ModelStatusHintText.Visibility, $"{state} status visibility");
        AssertEqual(expectedProgressText, window.ModelProgressText.Text, $"{state} progress text");
        AssertEqual(expectedProgressVisibility, window.ModelProgressText.Visibility, $"{state} progress visibility");
        AssertEqual(expectedProgressBarVisibility, window.ModelProgressBar.Visibility, $"{state} progress bar visibility");
        AssertEqual(expectedIsIndeterminate, window.ModelProgressBar.IsIndeterminate, $"{state} progress mode");
        AssertEqual(expectedProgressValue, window.ModelProgressBar.Value, 0.0001d, $"{state} progress value");
        AssertEqual(expectedActionText, window.DownloadButton.Content?.ToString() ?? string.Empty, $"{state} action text");
        AssertEqual(expectedActionVisibility, window.DownloadButton.Visibility, $"{state} action visibility");
        AssertEqual(expectedActionEnabled, window.DownloadButton.IsEnabled, $"{state} action enabled");
        AssertEqual(expectedCancelVisibility, window.CancelDownloadButton.Visibility, $"{state} cancel visibility");
        AssertEqual(expectedCancelEnabled, window.CancelDownloadButton.IsEnabled, $"{state} cancel enabled");
    }

    private static void AssertTranslationState(SettingsWindow window, TranslationModelState state, string expectedStatusText)
    {
        window.SetTranslationState(state);

        AssertEqual(expectedStatusText, window.TranslationStatusHintText.Text, $"{state} translation status text");
        AssertEqual(Visibility.Visible, window.TranslationStatusHintText.Visibility, $"{state} translation status visibility");
    }

    private static void AssertMicrophoneState(
        SettingsWindow window,
        MicrophoneLoadState state,
        string expectedStatusText,
        Visibility expectedStatusVisibility)
    {
        window.SetMicrophoneState(state, selectedIndex: 0);

        AssertEqual(expectedStatusText, window.MicrophoneStatusText.Text, $"{state} status text");
        AssertEqual(expectedStatusVisibility, window.MicrophoneStatusText.Visibility, $"{state} status visibility");
        AssertEqual(Visibility.Visible, window.RefreshMicrophonesButton.Visibility, $"{state} refresh visibility");
        AssertEqual(true, window.RefreshMicrophonesButton.IsEnabled, $"{state} refresh enabled");
    }

    private static void AssertModelSizeAccessibleNameIncludesSizes(SettingsWindow window)
    {
        const string expectedName = "Bundle: 487 MB · Installed: about 671 MB";
        var root = (FrameworkElement)window.Content;
        var modelSizeText = (Microsoft.UI.Xaml.Controls.TextBlock)(root.FindName("ModelSizeText")
            ?? throw new InvalidOperationException("Expected ModelSizeText in the Settings namescope."));
        modelSizeText.Text = expectedName;

        var peer = new Microsoft.UI.Xaml.Automation.Peers.TextBlockAutomationPeer(modelSizeText);
        AssertEqual(expectedName, peer.GetName(), "Model size accessible name");
    }

    private static void AssertSavedMicrophoneFallbackDoesNotChangeSavedPreference(SettingsWindow window)
    {
        int changes = 0;
        string? changedMicrophoneId = null;
        window.MicrophoneChanged += id =>
        {
            changes++;
            changedMicrophoneId = id;
        };
        Microphone[] devices = [new("mic-1", "Headset"), new("mic-id", "Test microphone")];

        window.ShowMicrophones(MicrophoneSelection.For("disconnected", devices));

        AssertEqual(0, changes, "Saved microphone fallback must not raise a microphone change");
        AssertEqual(3, window.MicrophoneComboBox.Items.Count, "Windows default plus the listed devices");
        AssertEqual(0, window.MicrophoneComboBox.SelectedIndex, "A missing saved microphone shows the Windows default");
        AssertEqual(
            "Saved microphone unavailable. Windows default remains active.",
            window.MicrophoneStatusText.Text,
            "Missing saved microphone status");

        window.ShowMicrophones(MicrophoneSelection.For("mic-id", devices));
        AssertEqual(0, changes, "Listing the saved microphone must not raise a microphone change");
        AssertEqual(2, window.MicrophoneComboBox.SelectedIndex, "The saved microphone is selected");

        window.MicrophoneComboBox.SelectedIndex = 1;

        AssertEqual(1, changes, "User microphone selection must raise exactly one change");
        AssertEqual("mic-1", changedMicrophoneId, "User microphone selection id");
    }

    private static void AssertStartupFailureRestoresToggle(SettingsWindow window)
    {
        window.ApplyStartupUpdateResult(requestedEnabled: false, succeeded: false);

        AssertEqual(true, window.StartupToggle.IsOn, "Failed startup update must restore the previous toggle");
        AssertEqual(
            SettingsWindow.StartupUpdateFailedText,
            window.StartupStatusText.Text,
            "Failed startup update status text");
        window.ApplyStartupUpdateResult(requestedEnabled: false, succeeded: false, actualEnabled: false);
        AssertEqual(false, window.StartupToggle.IsOn, "Partial startup update must show the actual disabled state");
        window.ApplyStartupUpdateResult(requestedEnabled: true, succeeded: false, actualEnabled: true);
        AssertEqual(true, window.StartupToggle.IsOn, "Partial startup update must show the actual enabled state");
    }

    private static void AssertEqual<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"{message}: expected '{expected}', got '{actual}'.");
    }

    private static void AssertEqual(double expected, double actual, double delta, string message)
    {
        if (Math.Abs(expected - actual) > delta)
            throw new InvalidOperationException($"{message}: expected '{expected}' +/- {delta}, got '{actual}'.");
    }

    private static void AssertBetween(double actual, double minimum, double maximum, string message)
    {
        if (actual < minimum || actual > maximum)
            throw new InvalidOperationException($"{message}: expected {minimum}–{maximum}, got '{actual}'.");
    }
}
