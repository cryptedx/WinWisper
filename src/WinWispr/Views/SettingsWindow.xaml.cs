using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Windows.Graphics;
using Windows.System;
using WinWispr.Audio;
using WinWispr.Core;
using WinWispr.Core.Updates;
using WinWispr.Interop;
using WinWispr.Speech;
using WinWispr.Translation;
using WinRT.Interop;

namespace WinWispr.Views;

internal enum ModelUiState
{
    NotInstalled,
    Downloading,
    Loading,
    Ready,
    Cancelled,
    DownloadFailed,
    IntegrityFailed,
    InstallationFailed,
    Unavailable
}

internal readonly record struct ModelUiPresentation(
    string StatusText,
    string ActionText,
    bool ShowProgress,
    bool IsIndeterminate,
    bool ShowCancel,
    bool ShowAction);

internal enum HotkeyCaptureTarget
{
    Primary,
    Board,
    Task,
    EnglishDictation,
    Translate,
    Replay
}

internal sealed partial class SettingsWindow : Window
{
    internal const int SettingsMinimumWidthDip = 560;
    internal static readonly string HotkeyCapturePrompt = "Press a key combination, or Esc to cancel.";
    internal static readonly string HotkeyCaptureCancelledText = "Hotkey capture cancelled.";
    internal static readonly string HotkeyCaptureTimedOutText = "Hotkey capture timed out. Your previous shortcut is unchanged.";
    internal static readonly string HotkeyCaptureUnavailableText = "That shortcut is not available. Your previous shortcut is unchanged.";
    internal const string StartupUpdateFailedText =
        "Could not update Windows startup. Check the current setting and try again.";
    private const int DefaultWidth = 840;
    private const int DefaultHeight = 680;
    private readonly WindowPlacement? _savedPlacement;
    private readonly WindowProc _windowProc;
    private bool _loadingSettings;
    private bool _loadingSpeechModes;
    private bool _speechLoading;
    private bool _modelBusy;
    private SpeechMode _savedSpeechMode;
    private bool _placementApplied;
    private bool _minimumSizeHookInstalled;
    private HotkeyCaptureTarget _hotkeyCaptureTarget;
    private nint _windowHandle;
    private nint _originalWindowProc;

    internal SettingsWindow(WindowPlacement? savedPlacement = null)
    {
        _savedPlacement = savedPlacement;
        _windowProc = WindowProcedure;
        InitializeComponent();
        AppVersionText.Text = $"Version {AppVersion.Display}";
        ModelSizeText.Text = GetModelSizeText();
        SetModelState(ModelUiState.NotInstalled);
        SetTranslationState(TranslationModelState.Missing);
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarContent);
        SystemBackdrop = new MicaBackdrop();
        Activated += SettingsWindow_Activated;
        AppWindow.Closing += (_, args) =>
        {
            RestoreMinimumSizeHook();
            HotkeyCaptureCancelRequested?.Invoke();
            FocusHotkeyCaptureButton();
            PlacementChanged?.Invoke(new WindowPlacement(
                AppWindow.Position.X,
                AppWindow.Position.Y,
                AppWindow.Size.Width,
                AppWindow.Size.Height));
            args.Cancel = true;
            AppWindow.Hide();
        };
        Closed += (_, _) => RestoreMinimumSizeHook();
    }

    internal event Action<HotkeyCaptureTarget>? HotkeyCaptureRequested;
    internal event Action? HotkeyCaptureCancelRequested;
    internal event Action<RecordingMode>? ModeChanged;
    /// <summary>Returns whether the change was saved; on false the previous selection is restored.</summary>
    internal event Func<SpeechMode, bool>? SpeechModeChanged;
    /// <summary>Returns whether the change was saved; on false the toggle is restored.</summary>
    internal event Func<bool, bool>? VoiceCommandsChanged;
    /// <summary>Returns whether the change was saved; on false the toggle is restored.</summary>
    internal event Func<bool, bool>? AutomaticUpdateChecksChanged;
    /// <summary>Checks for an update, or installs the one already found.</summary>
    internal event Action? UpdateRequested;
    internal event Func<string, DictionarySaveResult>? PersonalDictionaryChanged;
    internal event Action<string?>? MicrophoneChanged;
    internal event Action? MicrophoneRefreshRequested;
    internal event Action? DownloadRequested;
    internal event Action? CancelDownloadRequested;
    internal event Action<WindowPlacement>? PlacementChanged;
    internal event Action<RecordingMode>? TaskHotkeyModeChanged;
    internal event Action<RecordingMode>? TaskButtonModeChanged;
    internal event Action<RecordingMode>? EnglishDictationModeChanged;

    internal void SetSettingsWarning(string? warning) => DictionaryStatusText.Text = warning ?? string.Empty;

    internal void LoadSettings(AppSettings settings)
    {
        _loadingSettings = true;
        _savedSpeechMode = settings.SpeechMode;
        SetSpeechModes([]);
        SetHotkey(settings.Hotkey);
        SetHotkey(HotkeyCaptureTarget.Board, settings.BoardHotkey);
        SetHotkey(HotkeyCaptureTarget.Task, settings.TaskHotkey);
        SetHotkey(HotkeyCaptureTarget.EnglishDictation, settings.EnglishDictationHotkey);
        SetHotkey(HotkeyCaptureTarget.Translate, settings.TranslateHotkey);
        SetHotkey(HotkeyCaptureTarget.Replay, settings.ReplayHotkey);
        EnglishDictationModeComboBox.SelectedIndex = settings.EnglishDictationMode == RecordingMode.Toggle ? 0 : 1;
        ModeComboBox.SelectedIndex = settings.Mode == RecordingMode.Toggle ? 0 : 1;
        TaskHotkeyModeComboBox.SelectedIndex = settings.TaskHotkeyMode == RecordingMode.Toggle ? 0 : 1;
        TaskButtonModeComboBox.SelectedIndex = settings.TaskButtonMode == RecordingMode.Toggle ? 0 : 1;
        VoiceCommandsToggle.IsOn = settings.VoiceCommandsEnabled;
        AutomaticUpdatesToggle.IsOn = settings.AutomaticUpdateChecks;
        StartupToggle.IsOn = StartupManager.IsEnabled();
        StartupStatusText.Text = string.Empty;
        _loadingSettings = false;
    }

    internal void LoadDictionary(string text)
    {
        _loadingSettings = true;
        PersonalDictionaryBox.Text = text;
        _loadingSettings = false;
    }

    internal static string FormatDictionaryStatus(DictionarySaveResult result) => result.Status switch
    {
        DictionarySaveStatus.Saved => string.Empty,
        DictionarySaveStatus.Invalid => result.Message + " Your last valid dictionary is still active.",
        _ => result.Message ?? PersonalDictionaryStore.SaveFailedMessage
    };

    internal async Task LoadSpeechDevicesAsync()
    {
        try
        {
            IReadOnlyList<SpeechAdapter> adapters = await Task.Run(SpeechDevices.Enumerate);
            SetSpeechModes(adapters);
        }
        catch
        {
            SetSpeechModes([]);
        }
    }

    private void SetSpeechModes(IReadOnlyList<SpeechAdapter> adapters)
    {
        _loadingSpeechModes = true;
        try
        {
            SpeechModeComboBox.Items.Clear();
            foreach (SpeechMode mode in Enum.GetValues<SpeechMode>())
            {
                string label = mode switch
                {
                    SpeechMode.Automatic => "Automatic",
                    SpeechMode.Cpu => "CPU",
                    _ => SpeechAdapter.Select(mode, adapters)?.Name
                        ?? (mode == SpeechMode.Nvidia ? "NVIDIA GPU (unavailable)" : "Intel GPU (unavailable)")
                };
                SpeechModeComboBox.Items.Add(new ComboBoxItem { Content = label, Tag = mode });
            }
            SpeechModeComboBox.SelectedIndex = (int)_savedSpeechMode;
        }
        finally { _loadingSpeechModes = false; }
    }

    internal void SetSpeechStatus(string status, bool loading, bool pending)
    {
        SpeechStatusText.Text = status;
        _speechLoading = loading;
        SpeechModeComboBox.IsEnabled = !loading && !_modelBusy;
        SpeechSelectionStatusText.Text = pending
            ? "Your selection will apply after the current dictation."
            : string.Empty;
    }

    private void SpeechModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loadingSpeechModes || SpeechModeComboBox.SelectedItem is not ComboBoxItem { Tag: SpeechMode mode })
            return;
        if (SpeechModeChanged?.Invoke(mode) ?? true)
            _savedSpeechMode = mode;
        else
        {
            _loadingSpeechModes = true;
            SpeechModeComboBox.SelectedIndex = (int)_savedSpeechMode;
            _loadingSpeechModes = false;
            SpeechSelectionStatusText.Text = "Could not save the processing device. Check disk access and try again. Your previous selection is unchanged.";
        }
    }

    internal void BeginHotkeyCapture(HotkeyCaptureTarget target = HotkeyCaptureTarget.Primary)
    {
        _hotkeyCaptureTarget = target;
        Button button = GetHotkeyButton(target);
        button.Content = HotkeyCapturePrompt;
        button.IsEnabled = false;
        HotkeyCaptureStatusText.Text = HotkeyCapturePrompt;
        CancelHotkeyButton.Visibility = Visibility.Visible;
        CancelHotkeyButton.IsEnabled = true;
    }

    internal void SetHotkeyCaptureStatus(string text)
    {
        HotkeyCaptureStatusText.Text = text;
        HotkeyCaptureStatusText.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    internal void CompleteHotkeyCapture(string formattedHotkey) => CompleteHotkeyCapture(_hotkeyCaptureTarget, formattedHotkey);

    internal void CompleteHotkeyCapture(HotkeyCaptureTarget target, string formattedHotkey)
    {
        _hotkeyCaptureTarget = target;
        Button button = GetHotkeyButton(target);
        button.Content = formattedHotkey;
        button.IsEnabled = true;
        CancelHotkeyButton.Visibility = Visibility.Collapsed;
        CancelHotkeyButton.IsEnabled = false;
        SetHotkeyCaptureStatus(string.Empty);
        FocusHotkeyCaptureButton();
    }

    internal void SetHotkey(Hotkey hotkey) => SetHotkey(HotkeyCaptureTarget.Primary, hotkey);

    internal void SetHotkey(HotkeyCaptureTarget target, Hotkey hotkey) =>
        CompleteHotkeyCapture(target, FormatHotkeyButtonText(hotkey, capturePending: false));

    internal void ShowMicrophonesLoading()
    {
        MicrophoneStatusText.Text = "Loading microphones...";
        MicrophoneStatusText.Visibility = Visibility.Visible;
        RefreshMicrophonesButton.IsEnabled = false;
        RefreshMicrophonesButton.Visibility = Visibility.Visible;
    }

    /// <summary>Lists the devices of a selection; choosing one raises <see cref="MicrophoneChanged"/>.</summary>
    internal void ShowMicrophones(MicrophoneSelection selection)
    {
        bool wasLoadingSettings = _loadingSettings;
        _loadingSettings = true;
        try
        {
            MicrophoneComboBox.Items.Clear();
            MicrophoneComboBox.Items.Add(new ComboBoxItem { Content = "Windows default", Tag = null });
            foreach (Microphone device in selection.Devices)
                MicrophoneComboBox.Items.Add(new ComboBoxItem { Content = device.Name, Tag = device.Id });
        }
        finally
        {
            _loadingSettings = wasLoadingSettings;
        }

        SetMicrophoneState(selection.State, selection.SelectedIndex);
    }

    internal void SetMicrophoneState(MicrophoneLoadState state, int selectedIndex)
    {
        bool wasLoadingSettings = _loadingSettings;
        _loadingSettings = true;
        try
        {
            MicrophoneComboBox.SelectedIndex = selectedIndex;
        }
        finally
        {
            _loadingSettings = wasLoadingSettings;
        }

        MicrophoneStatusText.Text = GetMicrophoneStatusText(state);
        MicrophoneStatusText.Visibility = state == MicrophoneLoadState.Ready
            ? Visibility.Collapsed
            : Visibility.Visible;
        RefreshMicrophonesButton.IsEnabled = true;
        RefreshMicrophonesButton.Visibility = Visibility.Visible;
        AutomationProperties.SetName(MicrophoneStatusText, MicrophoneStatusText.Text);
    }

    internal void SetModelState(ModelUiState state, double progress = 0)
    {
        _modelBusy = state is ModelUiState.Downloading or ModelUiState.Loading;
        SpeechModeComboBox.IsEnabled = !_speechLoading && !_modelBusy;
        ModelUiPresentation presentation = GetModelUiPresentation(state, progress);
        double clampedProgress = Math.Clamp(progress, 0, 1);
        string statusText = presentation.ShowProgress ? string.Empty : presentation.StatusText;
        string progressText = presentation.ShowProgress ? presentation.StatusText : string.Empty;
        string liveStatusText = presentation.ShowProgress ? progressText : statusText;

        ModelStatusHintText.Text = statusText;
        ModelStatusHintText.Visibility = statusText.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        ModelProgressText.Text = progressText;
        ModelProgressText.Visibility = progressText.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        ModelProgressBar.IsIndeterminate = presentation.IsIndeterminate;
        ModelProgressBar.Value = clampedProgress;
        ModelProgressBar.Visibility = presentation.ShowProgress ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(ModelProgressBar, liveStatusText);

        DownloadButton.Content = presentation.ActionText;
        DownloadButton.IsEnabled = presentation.ShowAction;
        DownloadButton.Visibility = presentation.ShowAction ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(DownloadButton, presentation.ShowAction ? presentation.ActionText : "Download model");

        CancelDownloadButton.IsEnabled = presentation.ShowCancel;
        CancelDownloadButton.Visibility = presentation.ShowCancel ? Visibility.Visible : Visibility.Collapsed;

        AutomationProperties.SetName(ModelStatusHintText, liveStatusText);
        AutomationProperties.SetName(ModelProgressText, liveStatusText);
    }

    internal static ModelUiPresentation GetModelUiPresentation(ModelUiState state, double progress = 0)
    {
        int percent = (int)Math.Round(Math.Clamp(progress, 0, 1) * 100, MidpointRounding.AwayFromZero);
        return state switch
        {
            ModelUiState.NotInstalled => new ModelUiPresentation(
                "Model not installed.",
                "Download model",
                ShowProgress: false,
                IsIndeterminate: false,
                ShowCancel: false,
                ShowAction: true),
            ModelUiState.Downloading => new ModelUiPresentation(
                $"Downloading model... {percent}%",
                string.Empty,
                ShowProgress: true,
                IsIndeterminate: false,
                ShowCancel: true,
                ShowAction: false),
            ModelUiState.Loading => new ModelUiPresentation(
                "Loading model...",
                string.Empty,
                ShowProgress: true,
                IsIndeterminate: true,
                ShowCancel: false,
                ShowAction: false),
            ModelUiState.Ready => new ModelUiPresentation(
                "Ready",
                string.Empty,
                ShowProgress: false,
                IsIndeterminate: false,
                ShowCancel: false,
                ShowAction: false),
            ModelUiState.Cancelled => new ModelUiPresentation(
                "Download cancelled. You can try again.",
                "Try again",
                ShowProgress: false,
                IsIndeterminate: false,
                ShowCancel: false,
                ShowAction: true),
            ModelUiState.DownloadFailed => new ModelUiPresentation(
                "Download failed. Check your connection, then try again.",
                "Try again",
                ShowProgress: false,
                IsIndeterminate: false,
                ShowCancel: false,
                ShowAction: true),
            ModelUiState.IntegrityFailed => new ModelUiPresentation(
                "The downloaded model could not be verified. Please try again.",
                "Try again",
                ShowProgress: false,
                IsIndeterminate: false,
                ShowCancel: false,
                ShowAction: true),
            ModelUiState.InstallationFailed => new ModelUiPresentation(
                "The model could not be installed. Please try again.",
                "Try again",
                ShowProgress: false,
                IsIndeterminate: false,
                ShowCancel: false,
                ShowAction: true),
            ModelUiState.Unavailable => new ModelUiPresentation(
                "Model unavailable. Download it to use dictation.",
                "Download model",
                ShowProgress: false,
                IsIndeterminate: false,
                ShowCancel: false,
                ShowAction: true),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
        };
    }

    internal void SetTranslationState(TranslationModelState state)
    {
        string statusText = GetTranslationStatusText(state);
        TranslationStatusHintText.Text = statusText;
        AutomationProperties.SetName(TranslationStatusHintText, statusText);
    }

    internal static string GetTranslationStatusText(TranslationModelState state) => state switch
    {
        TranslationModelState.Installed =>
            $"Translation is ready. The model uses {FormatInstalledSize(ModelManifest.Translation.Files.Sum(file => file.ExpectedBytes))}.",
        TranslationModelState.Missing =>
            "The translation model is not installed. Install WinWispr Models to add it.",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
    };

    internal static string FormatArchiveSize(long bytes) => $"{Math.Floor(bytes / 1_000_000d):0} MB";

    internal static string FormatInstalledSize(long bytes) => $"about {Math.Ceiling(bytes / 1_000_000d):0} MB";

    internal static string GetMicrophoneStatusText(MicrophoneLoadState state) => state switch
    {
        MicrophoneLoadState.Ready => string.Empty,
        MicrophoneLoadState.NoDevices => "No microphones found. Windows default remains active.",
        MicrophoneLoadState.EnumerationFailed => "Could not list microphones. Windows default remains active.",
        MicrophoneLoadState.SavedDeviceUnavailable => "Saved microphone unavailable. Windows default remains active.",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
    };

    internal void ShowModelSection()
    {
        SettingsNavigation.SelectedItem = SpeechNavItem;
        PageScrollViewer.UpdateLayout();
        ModelSection.StartBringIntoView();
        Activate();
    }

    private void SettingsNavigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        string? tag = (args.SelectedItem as NavigationViewItem)?.Tag as string;
        if (tag is null)
            return;

        GeneralPage.Visibility = tag == "General" ? Visibility.Visible : Visibility.Collapsed;
        DictationPage.Visibility = tag == "Dictation" ? Visibility.Visible : Visibility.Collapsed;
        TaskBoardPage.Visibility = tag == "TaskBoard" ? Visibility.Visible : Visibility.Collapsed;
        SpeechPage.Visibility = tag == "Speech" ? Visibility.Visible : Visibility.Collapsed;
        TranslationPage.Visibility = tag == "Translation" ? Visibility.Visible : Visibility.Collapsed;
        PageScrollViewer.ChangeView(null, 0, null, disableAnimation: true);
    }

    private void SettingsWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        InstallMinimumSizeHook();
        if (_placementApplied)
            return;

        WindowPlacement? savedPlacement = _savedPlacement is WindowPlacement savedValue && savedValue.IsUsable
            ? savedValue
            : null;
        DisplayArea displayArea = savedPlacement is WindowPlacement savedForDisplay
            ? DisplayArea.GetFromPoint(
                new PointInt32(savedForDisplay.X, savedForDisplay.Y),
                DisplayAreaFallback.Nearest)
            : DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
        RectInt32 workArea = displayArea.WorkArea;
        uint dpi = savedPlacement is WindowPlacement restoredForDpi
            ? GetTargetMonitorDpi(new PointInt32(restoredForDpi.X, restoredForDpi.Y))
            : GetDpiForWindow(_windowHandle);
        WindowPlacement placement = savedPlacement is WindowPlacement restored
            ? restored.ConstrainTo(
                workArea.X,
                workArea.Y,
                workArea.Width,
                workArea.Height,
                DipToPixels(SettingsMinimumWidthDip, dpi),
                minimumHeight: 1)
            : WindowPlacement.Centered(
                workArea.X,
                workArea.Y,
                workArea.Width,
                workArea.Height,
                DipToPixels(DefaultWidth, dpi),
                DipToPixels(DefaultHeight, dpi));

        AppWindow.Resize(new SizeInt32(placement.Width, placement.Height));
        AppWindow.Move(new PointInt32(placement.X, placement.Y));
        _placementApplied = true;
    }

    private void HotkeyButton_Click(object sender, RoutedEventArgs args) => HotkeyCaptureRequested?.Invoke(HotkeyCaptureTarget.Primary);

    private void BoardHotkeyButton_Click(object sender, RoutedEventArgs args) => HotkeyCaptureRequested?.Invoke(HotkeyCaptureTarget.Board);

    private void TaskHotkeyButton_Click(object sender, RoutedEventArgs args) => HotkeyCaptureRequested?.Invoke(HotkeyCaptureTarget.Task);

    private void EnglishDictationHotkeyButton_Click(object sender, RoutedEventArgs args) =>
        HotkeyCaptureRequested?.Invoke(HotkeyCaptureTarget.EnglishDictation);

    private void TranslateHotkeyButton_Click(object sender, RoutedEventArgs args) =>
        HotkeyCaptureRequested?.Invoke(HotkeyCaptureTarget.Translate);

    private void ReplayHotkeyButton_Click(object sender, RoutedEventArgs args) =>
        HotkeyCaptureRequested?.Invoke(HotkeyCaptureTarget.Replay);

    private void CancelHotkeyButton_Click(object sender, RoutedEventArgs args) => HotkeyCaptureCancelRequested?.Invoke();

    private void FocusHotkeyCaptureButton() => GetHotkeyButton(_hotkeyCaptureTarget).Focus(FocusState.Programmatic);

    private Button GetHotkeyButton(HotkeyCaptureTarget target) => target switch
    {
        HotkeyCaptureTarget.Board => BoardHotkeyButton,
        HotkeyCaptureTarget.Task => TaskHotkeyButton,
        HotkeyCaptureTarget.EnglishDictation => EnglishDictationHotkeyButton,
        HotkeyCaptureTarget.Translate => TranslateHotkeyButton,
        HotkeyCaptureTarget.Replay => ReplayHotkeyButton,
        _ => HotkeyButton
    };

    private TextBlock GetHotkeyStatusText(HotkeyCaptureTarget target) => target switch
    {
        HotkeyCaptureTarget.Board => BoardHotkeyStatusText,
        HotkeyCaptureTarget.Task => TaskHotkeyStatusText,
        HotkeyCaptureTarget.EnglishDictation => EnglishDictationHotkeyStatusText,
        HotkeyCaptureTarget.Translate => TranslateHotkeyStatusText,
        HotkeyCaptureTarget.Replay => ReplayHotkeyStatusText,
        _ => HotkeyStatusText
    };

    internal const string HotkeyAvailableText = "Available";
    internal const string HotkeyUnavailableText = "Unavailable. Choose another shortcut or free it in the other app.";

    /// <summary>
    /// Shows whether the hotkey is registered and, when it is, whether it blocks a character that AltGr types. The
    /// dictation hotkey shows nothing while it is registered and blocks nothing.
    /// </summary>
    internal void SetHotkeyStatus(HotkeyCaptureTarget target, bool available, Hotkey hotkey, HotkeyCharacterClash? clash)
    {
        TextBlock status = GetHotkeyStatusText(target);
        status.Text = !available ? HotkeyUnavailableText
            : clash is HotkeyCharacterClash blocked ? HotkeyClashText(hotkey, blocked)
            : target == HotkeyCaptureTarget.Primary ? string.Empty
            : HotkeyAvailableText;
        status.Visibility = status.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    internal static string HotkeyClashText(Hotkey hotkey, HotkeyCharacterClash clash) =>
        $"Blocks {clash.Character} on the {clash.LayoutName} keyboard: Windows reports {FormatAltGrChord(hotkey)} as "
        + $"{FormatHotkey(hotkey)}, so WinWispr takes the key press. Choose another hotkey to type {clash.Character}.";

    /// <summary>The hotkeys that block an AltGr character, for the tray notice at startup.</summary>
    internal static (string Title, string Body) HotkeyClashNotice(
        IReadOnlyList<(string Name, Hotkey Hotkey, HotkeyCharacterClash Clash)> clashes)
    {
        string[] characters = [.. clashes.Select(clash => clash.Clash.Character).Distinct()];
        string title = clashes.Count == 1
            ? $"A WinWispr hotkey blocks {characters[0]}"
            : $"WinWispr hotkeys block {string.Join(", ", characters)}";
        // A balloon holds 255 characters.
        string body = string.Join(" ", clashes.Take(2).Select(clash =>
                $"{clash.Name} ({FormatHotkey(clash.Hotkey)}) takes {FormatAltGrChord(clash.Hotkey)}, which types "
                + $"{clash.Clash.Character} on the {clash.Clash.LayoutName} keyboard."))
            + (clashes.Count > 2 ? $" {clashes.Count - 2} more." : "")
            + " Click to choose another hotkey.";
        return (title, body);
    }

    private static string FormatAltGrChord(Hotkey hotkey) =>
        ((hotkey.Modifiers & HotkeyModifiers.Shift) != 0 ? "AltGr + Shift + " : "AltGr + ")
        + FormatVirtualKey(hotkey.VirtualKey);

    /// <summary>Opens the page that holds the hotkey and moves the focus to its button.</summary>
    internal void ShowHotkey(HotkeyCaptureTarget target)
    {
        SettingsNavigation.SelectedItem = target switch
        {
            HotkeyCaptureTarget.Replay => DictationNavItem,
            HotkeyCaptureTarget.Board or HotkeyCaptureTarget.Task => TaskBoardNavItem,
            HotkeyCaptureTarget.EnglishDictation or HotkeyCaptureTarget.Translate => TranslationNavItem,
            _ => GeneralNavItem
        };
        PageScrollViewer.UpdateLayout();
        Button button = GetHotkeyButton(target);
        button.StartBringIntoView();
        Activate();
        button.Focus(FocusState.Programmatic);
    }

    /// <summary>Shows the update state; <paramref name="policyDisabled"/> locks the section for a managed machine.</summary>
    internal void SetUpdateStatus(string status, string buttonText, bool buttonEnabled, bool policyDisabled)
    {
        UpdateStatusText.Text = status;
        UpdateButton.Content = buttonText;
        AutomationProperties.SetName(UpdateButton, buttonText);
        UpdateButton.IsEnabled = buttonEnabled && !policyDisabled;
        AutomaticUpdatesToggle.IsEnabled = !policyDisabled;
    }

    private void UpdateButton_Click(object sender, RoutedEventArgs args) => UpdateRequested?.Invoke();

    private void AutomaticUpdatesToggle_Toggled(object sender, RoutedEventArgs args)
    {
        if (_loadingSettings)
            return;
        if (AutomaticUpdateChecksChanged?.Invoke(AutomaticUpdatesToggle.IsOn) ?? true)
            return;
        _loadingSettings = true;
        try
        {
            AutomaticUpdatesToggle.IsOn = !AutomaticUpdatesToggle.IsOn;
        }
        finally
        {
            _loadingSettings = false;
        }
        UpdateStatusText.Text = "Could not save the update setting. Check disk access and try again.";
    }

    private void VoiceCommandsToggle_Toggled(object sender, RoutedEventArgs args)
    {
        if (_loadingSettings)
            return;
        if (VoiceCommandsChanged?.Invoke(VoiceCommandsToggle.IsOn) ?? true)
            VoiceCommandsStatusText.Text = string.Empty;
        else
        {
            _loadingSettings = true;
            try
            {
                VoiceCommandsToggle.IsOn = !VoiceCommandsToggle.IsOn;
            }
            finally
            {
                _loadingSettings = false;
            }
            VoiceCommandsStatusText.Text = "Could not save voice commands. Check disk access and try again. Your previous setting is unchanged.";
        }
    }

    private void PersonalDictionaryBox_TextChanged(object sender, TextChangedEventArgs args)
    {
        if (_loadingSettings)
            return;
        DictionarySaveResult result = PersonalDictionaryChanged?.Invoke(PersonalDictionaryBox.Text)
            ?? new DictionarySaveResult(DictionarySaveStatus.Saved);
        DictionaryStatusText.Text = FormatDictionaryStatus(result);
    }

    private void ModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loadingSettings || ModeComboBox.SelectedItem is not ComboBoxItem item)
            return;

        ModeChanged?.Invoke(item.Content?.ToString() == "Hold" ? RecordingMode.Hold : RecordingMode.Toggle);
    }

    private void TaskHotkeyModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loadingSettings || TaskHotkeyModeComboBox.SelectedItem is not ComboBoxItem item)
            return;
        TaskHotkeyModeChanged?.Invoke(item.Content?.ToString() == "Hold" ? RecordingMode.Hold : RecordingMode.Toggle);
    }

    private void EnglishDictationModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loadingSettings || EnglishDictationModeComboBox.SelectedItem is not ComboBoxItem item)
            return;
        EnglishDictationModeChanged?.Invoke(item.Content?.ToString() == "Hold" ? RecordingMode.Hold : RecordingMode.Toggle);
    }

    private void TaskButtonModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loadingSettings || TaskButtonModeComboBox.SelectedItem is not ComboBoxItem item)
            return;
        TaskButtonModeChanged?.Invoke(item.Content?.ToString() == "Hold" ? RecordingMode.Hold : RecordingMode.Toggle);
    }

    private void MicrophoneComboBox_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_loadingSettings || MicrophoneComboBox.SelectedItem is not ComboBoxItem item)
            return;

        MicrophoneChanged?.Invoke(item.Tag as string);
    }

    private void RefreshMicrophonesButton_Click(object sender, RoutedEventArgs args) => MicrophoneRefreshRequested?.Invoke();

    private void DownloadButton_Click(object sender, RoutedEventArgs args) => DownloadRequested?.Invoke();

    private void CancelDownloadButton_Click(object sender, RoutedEventArgs args) => CancelDownloadRequested?.Invoke();

    private void StartupToggle_Toggled(object sender, RoutedEventArgs args)
    {
        if (_loadingSettings)
            return;

        bool enabled = StartupToggle.IsOn;
        bool succeeded = StartupManager.TrySetEnabled(enabled);
        ApplyStartupUpdateResult(enabled, succeeded, succeeded ? enabled : StartupManager.IsEnabled());
    }

    internal void ApplyStartupUpdateResult(bool requestedEnabled, bool succeeded, bool? actualEnabled = null)
    {
        if (succeeded)
        {
            StartupStatusText.Text = string.Empty;
            return;
        }

        bool wasLoadingSettings = _loadingSettings;
        _loadingSettings = true;
        try
        {
            StartupToggle.IsOn = actualEnabled ?? !requestedEnabled;
        }
        finally
        {
            _loadingSettings = wasLoadingSettings;
        }

        StartupStatusText.Text = StartupUpdateFailedText;
    }

    private static string FormatHotkey(Hotkey hotkey)
    {
        var parts = new List<string>();
        if ((hotkey.Modifiers & HotkeyModifiers.Control) != 0)
            parts.Add("Ctrl");
        if ((hotkey.Modifiers & HotkeyModifiers.Alt) != 0)
            parts.Add("Alt");
        if ((hotkey.Modifiers & HotkeyModifiers.Shift) != 0)
            parts.Add("Shift");
        if ((hotkey.Modifiers & HotkeyModifiers.Windows) != 0)
            parts.Add("Win");
        parts.Add(FormatVirtualKey(hotkey.VirtualKey));
        return string.Join(" + ", parts);
    }

    private static string FormatVirtualKey(uint virtualKey)
    {
        if (virtualKey is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A)
            return ((char)virtualKey).ToString();

        string? name = Enum.GetName(typeof(VirtualKey), (VirtualKey)virtualKey);
        if (name is null)
            return $"VK {virtualKey}";
        if (name == "Back")
            return "Backspace";
        if (name == "Escape")
            return "Esc";
        if (name == "PageUp")
            return "Page Up";
        if (name == "PageDown")
            return "Page Down";
        const string numberPadPrefix = "NumberPad";
        const string numberPrefix = "Number";
        if (name.StartsWith(numberPadPrefix, StringComparison.Ordinal))
            return $"Num {name[numberPadPrefix.Length..]}";
        if (name.StartsWith(numberPrefix, StringComparison.Ordinal))
            return name[numberPrefix.Length..];
        return name;
    }

    internal static string FormatHotkeyButtonText(Hotkey hotkey, bool capturePending) =>
        capturePending ? HotkeyCapturePrompt : FormatHotkey(hotkey);

    private static string GetModelSizeText()
    {
        long installedBytes = 0;
        foreach (ModelFileSpec file in ModelManifest.Parakeet.Files)
            installedBytes += file.ExpectedBytes;

        return $"Bundle: {FormatArchiveSize(ModelManifest.Parakeet.ArchiveBytes)} · Installed: {FormatInstalledSize(installedBytes)}";
    }

    private void InstallMinimumSizeHook()
    {
        if (_minimumSizeHookInstalled)
            return;

        _windowHandle = WindowNative.GetWindowHandle(this);
        _originalWindowProc = SetWindowLongPtr(
            _windowHandle,
            GwlWndProc,
            Marshal.GetFunctionPointerForDelegate(_windowProc));
        _minimumSizeHookInstalled = _originalWindowProc != 0;
    }

    private void RestoreMinimumSizeHook()
    {
        if (!_minimumSizeHookInstalled || _windowHandle == 0)
            return;

        SetWindowLongPtr(_windowHandle, GwlWndProc, _originalWindowProc);
        _originalWindowProc = 0;
        _minimumSizeHookInstalled = false;
    }

    private nint WindowProcedure(nint handle, uint message, nint wParam, nint lParam)
    {
        nint result = CallWindowProc(_originalWindowProc, handle, message, wParam, lParam);
        if (message == WmGetMinMaxInfo && lParam != 0)
            Marshal.WriteInt32(
                lParam,
                MinTrackSizeWidthOffset,
                DipToPixels(SettingsMinimumWidthDip, GetDpiForWindow(handle)));

        return result;
    }

    private const int GwlWndProc = -4;
    private const int MinTrackSizeWidthOffset = 24;
    private const uint WmGetMinMaxInfo = 0x0024;

    internal static int DipToPixels(double dips, uint dpi) =>
        (int)Math.Round(dips * SelectRestoreDpi(dpi) / 96d);

    internal static uint SelectRestoreDpi(uint targetMonitorDpi) => targetMonitorDpi == 0 ? 96u : targetMonitorDpi;

    private static uint GetTargetMonitorDpi(PointInt32 point)
    {
        nint monitor = MonitorFromPoint(
            new NativePoint { X = point.X, Y = point.Y },
            MonitorDefaultToNearest);
        if (monitor != 0
            && GetDpiForMonitor(monitor, MonitorDpiType.Effective, out uint dpiX, out _) == 0)
            return SelectRestoreDpi(dpiX);

        return SelectRestoreDpi(0);
    }

    private const uint MonitorDefaultToNearest = 2;

    private enum MonitorDpiType
    {
        Effective = 0
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        internal int X;
        internal int Y;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProc(nint handle, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint handle, int index, nint value);

    [DllImport("user32.dll")]
    private static extern nint CallWindowProc(
        nint previousProcedure,
        nint handle,
        uint message,
        nint wParam,
        nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint handle);

    [DllImport("user32.dll")]
    private static extern nint MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(
        nint monitor,
        MonitorDpiType dpiType,
        out uint dpiX,
        out uint dpiY);
}
