using System.Diagnostics;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Audio;
using WinWispr.Core;
using WinWispr.Speech;
using WinWispr.Translation;
using WinWispr.Views;

namespace WinWispr.Tests.Views;

[TestClass]
public sealed class SettingsWindowTests
{
    private const string SettingsUiAcceptanceMarkerEnvironmentVariable = "WINWISPR_UI_MARKER_PATH";

    [TestMethod]
    public void Hotkey_button_has_a_visible_capture_prompt_while_waiting_for_input()
    {
        Assert.AreEqual(
            "Press a key combination, or Esc to cancel.",
            SettingsWindow.FormatHotkeyButtonText(AppSettings.Default.Hotkey, capturePending: true));
    }

    [TestMethod]
    public void Dictionary_status_explains_each_save_result()
    {
        Assert.AreEqual("", SettingsWindow.FormatDictionaryStatus(new(DictionarySaveStatus.Saved)));
        Assert.AreEqual(
            "Check line 2: fix it. Your last valid dictionary is still active.",
            SettingsWindow.FormatDictionaryStatus(new(DictionarySaveStatus.Invalid, "Check line 2: fix it.")));
        Assert.AreEqual(
            PersonalDictionaryStore.ChangedOnDiskMessage,
            SettingsWindow.FormatDictionaryStatus(new(DictionarySaveStatus.Locked, PersonalDictionaryStore.ChangedOnDiskMessage)));
        Assert.AreEqual(
            PersonalDictionaryStore.SaveFailedMessage,
            SettingsWindow.FormatDictionaryStatus(new(DictionarySaveStatus.Failed)));
    }

    [TestMethod]
    public void Hotkey_capture_failure_copy_is_exact()
    {
        Assert.AreEqual("Hotkey capture cancelled.", SettingsWindow.HotkeyCaptureCancelledText);
        Assert.AreEqual(
            "Hotkey capture timed out. Your previous shortcut is unchanged.",
            SettingsWindow.HotkeyCaptureTimedOutText);
        Assert.AreEqual(
            "That shortcut is not available. Your previous shortcut is unchanged.",
            SettingsWindow.HotkeyCaptureUnavailableText);
    }

    [TestMethod]
    public void Hotkey_button_formats_standard_virtual_keys_by_name()
    {
        (uint VirtualKey, string Name)[] keys =
        [
            (0x30, "0"),
            (0x46, "F"),
            (0x70, "F1"),
            (0x25, "Left"),
            (0x20, "Space"),
            (0x0D, "Enter"),
            (0x08, "Backspace")
        ];

        foreach ((uint virtualKey, string name) in keys)
        {
            Assert.AreEqual(
                $"Alt + {name}",
                SettingsWindow.FormatHotkeyButtonText(
                    new Hotkey(HotkeyModifiers.Alt, virtualKey),
                    capturePending: false),
                $"Virtual key {virtualKey}");
        }
    }

    [TestMethod]
    [DataRow((int)ModelUiState.NotInstalled, 0d, "Model not installed.", "Download model", false, false, false, true)]
    [DataRow((int)ModelUiState.Downloading, -0.1d, "Downloading model... 0%", "", true, false, true, false)]
    [DataRow((int)ModelUiState.Downloading, 0d, "Downloading model... 0%", "", true, false, true, false)]
    [DataRow((int)ModelUiState.Downloading, 0.5d, "Downloading model... 50%", "", true, false, true, false)]
    [DataRow((int)ModelUiState.Downloading, 1d, "Downloading model... 100%", "", true, false, true, false)]
    [DataRow((int)ModelUiState.Downloading, 1.5d, "Downloading model... 100%", "", true, false, true, false)]
    [DataRow((int)ModelUiState.Loading, 0d, "Loading model...", "", true, true, false, false)]
    [DataRow((int)ModelUiState.Ready, 0d, "Ready", "", false, false, false, false)]
    [DataRow((int)ModelUiState.Cancelled, 0d, "Download cancelled. You can try again.", "Try again", false, false, false, true)]
    [DataRow((int)ModelUiState.DownloadFailed, 0d, "Download failed. Check your connection, then try again.", "Try again", false, false, false, true)]
    [DataRow((int)ModelUiState.IntegrityFailed, 0d, "The downloaded model could not be verified. Please try again.", "Try again", false, false, false, true)]
    [DataRow((int)ModelUiState.InstallationFailed, 0d, "The model could not be installed. Please try again.", "Try again", false, false, false, true)]
    [DataRow((int)ModelUiState.Unavailable, 0d, "Model unavailable. Download it to use dictation.", "Download model", false, false, false, true)]
    public void Model_ui_presentation_matches_the_state_contract(
        int state,
        double progress,
        string statusText,
        string actionText,
        bool showProgress,
        bool isIndeterminate,
        bool showCancel,
        bool showAction)
    {
        ModelUiPresentation presentation = SettingsWindow.GetModelUiPresentation((ModelUiState)state, progress);

        Assert.AreEqual(statusText, presentation.StatusText);
        Assert.AreEqual(actionText, presentation.ActionText);
        Assert.AreEqual(showProgress, presentation.ShowProgress);
        Assert.AreEqual(isIndeterminate, presentation.IsIndeterminate);
        Assert.AreEqual(showCancel, presentation.ShowCancel);
        Assert.AreEqual(showAction, presentation.ShowAction);
    }

    [TestMethod]
    [DataRow((int)SpeechRuntimeState.Loading, (int)ModelUiState.Loading)]
    [DataRow((int)SpeechRuntimeState.Ready, (int)ModelUiState.Ready)]
    [DataRow((int)SpeechRuntimeState.Missing, (int)ModelUiState.Unavailable)]
    [DataRow((int)SpeechRuntimeState.Failed, (int)ModelUiState.InstallationFailed)]
    public void Speech_runtime_state_mapping_matches_the_settings_model_contract(int speechState, int expectedUiState)
    {
        Assert.AreEqual(
            (ModelUiState)expectedUiState,
            App.MapSpeechRuntimeStateToModelUiState((SpeechRuntimeState)speechState));
    }

    [TestMethod]
    public void Archive_and_installed_size_copy_is_derived_from_the_manifest()
    {
        Assert.AreEqual("487 MB", SettingsWindow.FormatArchiveSize(ModelManifest.Parakeet.ArchiveBytes));
        Assert.AreEqual(
            "about 671 MB",
            SettingsWindow.FormatInstalledSize(ModelManifest.Parakeet.Files.Sum(file => file.ExpectedBytes)));
        Assert.AreEqual(
            "Bundle: 487 MB · Installed: about 671 MB",
            $"Bundle: {SettingsWindow.FormatArchiveSize(ModelManifest.Parakeet.ArchiveBytes)} · Installed: {SettingsWindow.FormatInstalledSize(ModelManifest.Parakeet.Files.Sum(file => file.ExpectedBytes))}");
    }

    [TestMethod]
    [DataRow((int)TranslationModelState.Installed, "Translation is ready. The model uses about 218 MB.")]
    [DataRow((int)TranslationModelState.Missing, "The translation model is not installed. Install WinWispr Models to add it.")]
    public void Translation_status_text_matches_the_state(int state, string statusText)
    {
        Assert.AreEqual(statusText, SettingsWindow.GetTranslationStatusText((TranslationModelState)state));
    }

    [TestMethod]
    public void Translation_size_copy_is_derived_from_the_manifest()
    {
        Assert.AreEqual(217_948_232L, ModelManifest.Translation.Files.Sum(file => file.ExpectedBytes));
        Assert.AreEqual(
            "about 218 MB",
            SettingsWindow.FormatInstalledSize(ModelManifest.Translation.Files.Sum(file => file.ExpectedBytes)));
    }

    [TestMethod]
    public void Settings_markup_has_a_translation_section_without_download_controls()
    {
        string markupPath = Path.Combine(FindRepositoryRoot(), "src", "WinWispr", "Views", "SettingsWindow.xaml");
        XDocument document = XDocument.Load(markupPath);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Named(string name) => document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == name);

        XElement heading = Named("TranslationHeading");
        Assert.AreEqual("Translation", (string?)heading.Attribute("Text"));
        Assert.AreEqual("Level2", (string?)heading.Attribute("AutomationProperties.HeadingLevel"));
        Assert.AreEqual("Polite", (string?)Named("TranslationStatusHintText").Attribute("AutomationProperties.LiveSetting"));
        // The model ships with the app: nothing in the section downloads, cancels or removes it.
        XElement section = Named("TranslationSection");
        Assert.IsFalse(section.Descendants().Any(element => ((string?)element.Attribute(x + "Name"))?.StartsWith("TranslationDownload", StringComparison.Ordinal) == true
            || ((string?)element.Attribute(x + "Name"))?.StartsWith("TranslationRemove", StringComparison.Ordinal) == true
            || ((string?)element.Attribute(x + "Name"))?.StartsWith("TranslationProgress", StringComparison.Ordinal) == true));
        Assert.IsFalse(section.ToString().Contains("huggingface", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void The_translation_section_holds_the_english_dictation_hotkey_and_mode()
    {
        string markupPath = Path.Combine(FindRepositoryRoot(), "src", "WinWispr", "Views", "SettingsWindow.xaml");
        XDocument document = XDocument.Load(markupPath);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Named(string name) => document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == name);
        XElement section = Named("TranslationSection");

        foreach (string name in new[] { "EnglishDictationHotkeyButton", "EnglishDictationHotkeyStatusText", "EnglishDictationModeComboBox" })
            Assert.IsTrue(section.Descendants().Any(element => (string?)element.Attribute(x + "Name") == name),
                $"{name} belongs in the Translation section");
        Assert.AreEqual("English dictation hotkey", (string?)Named("EnglishDictationHotkeyButton").Attribute("AutomationProperties.Name"));
        Assert.AreEqual("English dictation mode", (string?)Named("EnglishDictationModeComboBox").Attribute("AutomationProperties.Name"));
        Assert.AreEqual("Polite", (string?)Named("EnglishDictationHotkeyStatusText").Attribute("AutomationProperties.LiveSetting"));
        CollectionAssert.AreEqual(
            new[] { "Toggle", "Hold" },
            Named("EnglishDictationModeComboBox").Elements().Select(item => (string?)item.Attribute("Content")).ToArray());
        Assert.AreEqual("1", (string?)Named("EnglishDictationModeComboBox").Attribute("SelectedIndex"), "Hold is the default");
        string markup = File.ReadAllText(markupPath);
        StringAssert.Contains(markup, "without a preview");
        StringAssert.Contains(markup, "Terminology and idioms can be translated wrongly");
    }

    [TestMethod]
    public void The_translation_section_holds_the_translate_hotkey_with_its_status_and_explains_the_preview()
    {
        string markupPath = Path.Combine(FindRepositoryRoot(), "src", "WinWispr", "Views", "SettingsWindow.xaml");
        XDocument document = XDocument.Load(markupPath);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Named(string name) => document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == name);
        XElement section = Named("TranslationSection");

        foreach (string name in new[] { "TranslateHotkeyButton", "TranslateHotkeyStatusText" })
            Assert.IsTrue(section.Descendants().Any(element => (string?)element.Attribute(x + "Name") == name),
                $"{name} belongs in the Translation section");
        Assert.AreEqual("Translate hotkey", (string?)Named("TranslateHotkeyButton").Attribute("AutomationProperties.Name"));
        Assert.AreEqual("Ctrl + Alt + E", (string?)Named("TranslateHotkeyButton").Attribute("Content"));
        Assert.AreEqual("Polite", (string?)Named("TranslateHotkeyStatusText").Attribute("AutomationProperties.LiveSetting"));
        string markup = File.ReadAllText(markupPath);
        StringAssert.Contains(markup, "Select text in any application and press this hotkey");
        StringAssert.Contains(markup, "your selection is not changed");
        StringAssert.Contains(markup, "Terminology and idioms can be translated wrongly");
    }

    [TestMethod]
    public void The_translation_section_says_the_model_is_included_and_needs_no_download()
    {
        string markup = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "WinWispr", "Views", "SettingsWindow.xaml"));

        StringAssert.Contains(markup, "The model is included with WinWispr and runs without network access.");
        Assert.IsFalse(markup.Contains("huggingface", StringComparison.OrdinalIgnoreCase), "nothing is downloaded from Hugging Face");
        Assert.IsFalse(markup.Contains("optional download", StringComparison.OrdinalIgnoreCase), "the model is not an optional download");
    }

    [TestMethod]
    public void Settings_markup_has_the_required_copy_and_accessibility_names()
    {
        string markup = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "src", "WinWispr", "Views", "SettingsWindow.xaml"));

        StringAssert.Contains(markup, "Changes are saved automatically.");
        StringAssert.Contains(markup, "WinWispr stays active in the system tray when you close this window.");
        StringAssert.Contains(markup, "Toggle: press once to start and again to stop. Hold: keep the hotkey pressed while speaking.");
        StringAssert.Contains(markup, "The model is included in the app. Speech recognition runs locally; audio and transcripts are not saved to disk, and no network access is required after installation.");
        StringAssert.Contains(markup, "AutomationProperties.Name=\"Cancel hotkey capture\"");
        StringAssert.Contains(markup, "AutomationProperties.Name=\"Refresh microphones\"");
        StringAssert.Contains(markup, "AutomationProperties.Name=\"Download model\"");
        StringAssert.Contains(markup, "AutomationProperties.Name=\"Cancel model download\"");
        StringAssert.Contains(markup, "AutomationProperties.LiveSetting=\"Polite\"");
        StringAssert.Contains(markup, "x:Name=\"SettingsPageTitle\"");
        StringAssert.Contains(markup, "Text=\"Settings\"");
        StringAssert.Contains(markup, "AutomationProperties.HeadingLevel=\"Level1\"");
        StringAssert.Contains(markup, "AutomationProperties.HeadingLevel=\"Level2\"");
        StringAssert.Contains(markup, "x:Name=\"TitleBarIcon\"");
        StringAssert.Contains(markup, "AutomationProperties.AccessibilityView=\"Raw\"");
        StringAssert.Contains(markup, "Parakeet TDT 0.6B V3 INT8 turns your voice into text.");

        XDocument document = XDocument.Parse(markup);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (string headingName in new[] { "HotkeyModeHeading", "MicrophoneHeading", "ModelHeading", "StartupHeading" })
        {
            XElement heading = document.Descendants().Single(element =>
                (string?)element.Attribute(x + "Name") == headingName);
            Assert.AreEqual("Level2", (string?)heading.Attribute("{http://schemas.microsoft.com/winfx/2006/xaml/}AutomationProperties.HeadingLevel")
                ?? (string?)heading.Attribute("AutomationProperties.HeadingLevel"));
        }
        XElement refreshButton = document.Descendants().Single(element =>
            (string?)element.Attribute(x + "Name") == "RefreshMicrophonesButton");
        Assert.AreNotEqual("Collapsed", (string?)refreshButton.Attribute("Visibility"));
    }

    [TestMethod]
    public void Settings_sections_are_grouped_into_navigation_pages()
    {
        string markupPath = Path.Combine(FindRepositoryRoot(), "src", "WinWispr", "Views", "SettingsWindow.xaml");
        XDocument document = XDocument.Load(markupPath);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement Named(string name) => document.Descendants().Single(element => (string?)element.Attribute(x + "Name") == name);
        bool Contains(string page, string name) => Named(page).Descendants().Any(element => (string?)element.Attribute(x + "Name") == name);

        Assert.IsTrue(Contains("GeneralPage", "HotkeyModeSection"));
        Assert.IsTrue(Contains("GeneralPage", "StartupSection"));
        Assert.IsTrue(Contains("GeneralPage", "AppVersionText"));
        Assert.IsTrue(Contains("DictationPage", "PersonalDictionaryBox"));
        Assert.IsTrue(Contains("DictationPage", "MicrophoneSection"));
        Assert.IsTrue(Contains("TaskBoardPage", "TaskBoardSection"));
        Assert.IsTrue(Contains("SpeechPage", "SpeechModeComboBox"));
        Assert.IsTrue(Contains("SpeechPage", "ModelSection"));
        Assert.IsTrue(Contains("TranslationPage", "TranslationSection"));
        Assert.IsNull(Named("GeneralPage").Attribute("Visibility"), "General is the first visible page");
        foreach (string page in new[] { "DictationPage", "TaskBoardPage", "SpeechPage", "TranslationPage" })
            Assert.AreEqual("Collapsed", (string?)Named(page).Attribute("Visibility"), $"{page} starts hidden");

        XElement navigation = Named("SettingsNavigation");
        Assert.AreEqual("Left", (string?)navigation.Attribute("PaneDisplayMode"));
        CollectionAssert.AreEqual(
            new[] { "General", "Dictation", "Task board", "Speech model", "Translation" },
            navigation.Descendants().Where(element => element.Name.LocalName == "NavigationViewItem")
                .Select(item => (string?)item.Attribute("Content")).ToArray());
    }

    [TestMethod]
    public void Settings_action_layout_stacks_at_the_560_dip_minimum()
    {
        string markupPath = Path.Combine(FindRepositoryRoot(), "src", "WinWispr", "Views", "SettingsWindow.xaml");
        XDocument document = XDocument.Load(markupPath);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement microphoneActions = document.Descendants().Single(element =>
            (string?)element.Attribute(x + "Name") == "MicrophoneActionsPanel");
        XElement modelActions = document.Descendants().Single(element =>
            (string?)element.Attribute(x + "Name") == "ModelActionsPanel");
        XElement microphone = document.Descendants().Single(element =>
            (string?)element.Attribute(x + "Name") == "MicrophoneComboBox");
        XElement scroller = document.Descendants().Single(element =>
            element.Name.LocalName == "ScrollViewer");

        Assert.AreEqual("Vertical", (string?)microphoneActions.Attribute("Orientation"));
        Assert.AreEqual("Vertical", (string?)modelActions.Attribute("Orientation"));
        Assert.IsNull(microphone.Attribute("Width"));
        Assert.AreEqual("Disabled", (string?)scroller.Attribute("HorizontalScrollBarVisibility"));
        Assert.AreEqual("Disabled", (string?)scroller.Attribute("HorizontalScrollMode"));
        Assert.AreEqual("Enabled", (string?)scroller.Attribute("VerticalScrollMode"));
        Assert.AreEqual("True", (string?)scroller.Attribute("IsVerticalRailEnabled"));
    }

    [TestMethod]
    public void Settings_dip_sizes_are_scaled_at_150_percent_dpi()
    {
        Assert.AreEqual(840, SettingsWindow.DipToPixels(560, 144));
        Assert.AreEqual(960, SettingsWindow.DipToPixels(640, 144));
        Assert.AreEqual(1140, SettingsWindow.DipToPixels(760, 144));
        Assert.AreEqual(560, SettingsWindow.DipToPixels(560, 0));
    }

    [TestMethod]
    public void Restored_placement_uses_target_monitor_dpi_for_the_minimum_width()
    {
        uint targetMonitorDpi = SettingsWindow.SelectRestoreDpi(144);

        Assert.AreEqual(144u, targetMonitorDpi);
        Assert.AreEqual(840, SettingsWindow.DipToPixels(560, targetMonitorDpi));
        Assert.AreEqual(96u, SettingsWindow.SelectRestoreDpi(0));
    }

    [TestMethod]
    [DataRow((int)MicrophoneLoadState.Ready, "")]
    [DataRow((int)MicrophoneLoadState.NoDevices, "No microphones found. Windows default remains active.")]
    [DataRow((int)MicrophoneLoadState.EnumerationFailed, "Could not list microphones. Windows default remains active.")]
    [DataRow((int)MicrophoneLoadState.SavedDeviceUnavailable, "Saved microphone unavailable. Windows default remains active.")]
    public void Microphone_status_copy_matches_the_state_contract(int state, string expected)
    {
        Assert.AreEqual(expected, SettingsWindow.GetMicrophoneStatusText((MicrophoneLoadState)state));
    }

    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    public void Native_windows_bind_settings_controls_and_overlay_layout()
        => RunNativeWindowHelper("settings");

    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    public void Native_overlay_startup_renders_content_without_taking_focus()
        => RunNativeWindowHelper("overlay");

    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    public void Native_overlay_gallery_captures_every_state()
        => RunNativeWindowHelper("overlay-gallery");

    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    public void Native_task_board_recovery_hold_and_virtualization()
        => RunNativeWindowHelper("board");

    private static void RunNativeWindowHelper(string scope)
    {
        string helperPath = FindSettingsUiAcceptanceExecutable();
        string helperDirectory = Path.GetDirectoryName(helperPath)!;
        string successMarkerPath = Path.Combine(helperDirectory, $"SETTINGS_UI_ACCEPTANCE.{Guid.NewGuid():N}.PASS");
        string failureDiagnosticPath = $"{successMarkerPath}.FAIL.txt";
        File.Delete(successMarkerPath);
        File.Delete(failureDiagnosticPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = helperPath,
            WorkingDirectory = helperDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.Environment[SettingsUiAcceptanceMarkerEnvironmentVariable] = successMarkerPath;
        startInfo.Environment["WINWISPR_UI_SCOPE"] = scope;
        if (scope is "overlay" or "overlay-gallery")
            startInfo.Environment["WINWISPR_OVERLAY_EVIDENCE"] = Path.Combine(
                FindRepositoryRoot(), "artifacts", "dev-check", scope == "overlay" ? "overlay-startup" : "overlay-gallery", Guid.NewGuid().ToString("N"));

        using var process = new Process { StartInfo = startInfo };
        try
        {
            process.Start();
            Task<string> standardOutputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> standardErrorTask = process.StandardError.ReadToEndAsync();
            int timeoutMs = scope == "board" ? 120_000 : 60_000;
            bool exited = process.WaitForExit(timeoutMs);
            if (!exited)
            {
                process.Kill(entireProcessTree: true);
                _ = process.WaitForExit(5_000);
                string timedOutOutput = TryGetCompletedOutput(standardOutputTask, 1_000);
                string timedOutError = TryGetCompletedOutput(standardErrorTask, 1_000);
                Assert.Fail(
                    $"The isolated {scope} UI acceptance helper timed out after {timeoutMs / 1000} seconds.{Environment.NewLine}stdout:{Environment.NewLine}{timedOutOutput}{Environment.NewLine}stderr:{Environment.NewLine}{timedOutError}");
            }

            string standardOutput = TryGetCompletedOutput(standardOutputTask, 5_000);
            string standardError = TryGetCompletedOutput(standardErrorTask, 5_000);
            string failureDiagnostic = File.Exists(failureDiagnosticPath)
                ? File.ReadAllText(failureDiagnosticPath)
                : string.Empty;

            Assert.AreEqual(
                0,
                process.ExitCode,
                $"The isolated Settings UI acceptance helper failed.{Environment.NewLine}stdout:{Environment.NewLine}{standardOutput}{Environment.NewLine}stderr:{Environment.NewLine}{standardError}{Environment.NewLine}diagnostic:{Environment.NewLine}{failureDiagnostic}");
            Assert.IsTrue(
                File.Exists(successMarkerPath),
                $"The isolated Settings UI acceptance helper exited successfully but did not create {successMarkerPath}.{Environment.NewLine}stderr:{Environment.NewLine}{standardError}{Environment.NewLine}diagnostic:{Environment.NewLine}{failureDiagnostic}");
        }
        finally
        {
            File.Delete(successMarkerPath);
            File.Delete(failureDiagnosticPath);
        }
    }

    private static string TryGetCompletedOutput(Task<string> outputTask, int timeoutMilliseconds)
    {
        if (outputTask.Wait(timeoutMilliseconds))
            return outputTask.GetAwaiter().GetResult();

        return "[output unavailable before timeout]";
    }

    private static string FindSettingsUiAcceptanceExecutable()
    {
        string repositoryRoot = FindRepositoryRoot();
        string artifactsBin = Path.Combine(repositoryRoot, "artifacts", "dev-check", "bin");
        string artifactsTestRoot = Path.Combine(artifactsBin, "WinWispr.Tests") + Path.DirectorySeparatorChar;
        if (AppContext.BaseDirectory.StartsWith(artifactsTestRoot, StringComparison.OrdinalIgnoreCase))
        {
            string helper = Path.Combine(artifactsBin, "WinWispr.SettingsUiAcceptance",
                Path.GetRelativePath(artifactsTestRoot, AppContext.BaseDirectory), "WinWispr.SettingsUiAcceptance.exe");
            Assert.IsTrue(File.Exists(helper), $"Expected the matching Settings UI acceptance helper at {helper}.");
            return helper;
        }
        string helperBuildRoot = Path.Combine(repositoryRoot, "tests", "WinWispr.SettingsUiAcceptance", "bin");
        string buildSuffix = Path.GetRelativePath(
            Path.Combine(repositoryRoot, "tests", "WinWispr.Tests", "bin"),
            AppContext.BaseDirectory);
        string helperPath = Path.Combine(helperBuildRoot, buildSuffix, "WinWispr.SettingsUiAcceptance.exe");
        Assert.IsTrue(File.Exists(helperPath), $"Expected the matching Settings UI acceptance helper at {helperPath}.");
        return helperPath;
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "WinWispr.sln")))
                return current.FullName;
            current = current.Parent;
        }

        throw new AssertFailedException($"Could not locate WinWispr.sln from {AppContext.BaseDirectory}.");
    }
}
