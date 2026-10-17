using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using WinWispr.Core;
using WinWispr.Core.Updates;
using WinWispr.Diagnostics;
using WinWispr.Interop;
using WinWispr.Views;

namespace WinWispr;

/// <summary>The update check: finds a newer release, offers it, and on request hands it to Setup and restarts.</summary>
public partial class App
{
    private static readonly TimeSpan FirstUpdateCheckDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(24);
    private UpdateService _updates = null!;
    private InstallScope _installScope;
    private DispatcherQueueTimer? _updateTimer;
    private Version? _notifiedUpdateVersion;
    private string? _readySetupPath;
    private bool _installingUpdate;

    private void InitializeUpdates()
    {
        _updates = new UpdateService(
            UpdateService.CreateHttpClient(),
            AppVersion.Current,
            Path.Combine(AppDataPaths.Root, "Updates"),
            path => AuthenticodeSignature.IsSignedBy(path, UpdateService.SigningThumbprint),
            UpdateInstallation.IsDisabledByPolicy);
        _installScope = UpdateInstallation.DetectScope(AppContext.BaseDirectory, UpdateInstallation.ReadInstallLocation);
        _updates.Changed += RefreshUpdateUi;
        _settingsWindow.UpdateRequested += () => _ = RequestUpdateAsync(fromTray: false);
        _settingsWindow.AutomaticUpdateChecksChanged += enabled =>
        {
            if (!TrySaveSettings(_settings with { AutomaticUpdateChecks = enabled }))
                return false;
            ScheduleUpdateChecks();
            return true;
        };
        RefreshUpdateUi();
        ShowPendingUpdateOutcome();
        ScheduleUpdateChecks();
    }

    /// <summary>After an update WinWispr starts again; say whether the new version arrived.</summary>
    private void ShowPendingUpdateOutcome()
    {
        if (UpdateInstallation.TakePending(_updates.UpdatesRoot) is not { } target)
            return;
        if (AppVersion.Current >= target)
            _tray.ShowNotice("WinWispr was updated", $"Version {AppVersion.Display} is installed.");
        else
            _tray.ShowNotice(
                "The update did not complete",
                $"WinWispr {AppVersion.Display} is still installed. The Setup log is in {_updates.UpdatesRoot}.",
                warning: true);
    }

    // E2E runs never ask GitHub on their own, unless the run tests the update itself.
    private bool AutomaticUpdateChecksAllowed =>
        _settings.AutomaticUpdateChecks
        && !_updates.IsDisabledByPolicy
        && (!AppDataPaths.IsTaskBoardE2e
            || Environment.GetEnvironmentVariable("WINWISPR_UPDATE_TEST_CURRENT_VERSION") is not null);

    private void ScheduleUpdateChecks()
    {
        if (!AutomaticUpdateChecksAllowed)
        {
            _updateTimer?.Stop();
            return;
        }
        if (_updateTimer is null)
        {
            _updateTimer = _dispatcher.CreateTimer();
            _updateTimer.Tick += (timer, _) =>
            {
                timer.Interval = UpdateCheckInterval;
                if (AutomaticUpdateChecksAllowed)
                    _ = CheckForUpdatesAsync(manual: false, fromTray: false);
                else
                    timer.Stop();
            };
        }
        if (_updateTimer.IsRunning)
            return;
        _updateTimer.Interval = FirstUpdateCheckDelay;
        _updateTimer.Start();
    }

    /// <summary>The update action of the tray, its balloon and Settings: install a known update, else check.</summary>
    private async Task RequestUpdateAsync(bool fromTray)
    {
        if (_installingUpdate || _updates.State is UpdateState.Checking or UpdateState.Downloading)
            return;
        if (_updates.Available is not null && _updates.State is UpdateState.Available or UpdateState.Ready or UpdateState.Failed)
            await InstallUpdateAsync();
        else
            await CheckForUpdatesAsync(manual: true, fromTray);
    }

    private async Task CheckForUpdatesAsync(bool manual, bool fromTray)
    {
        UpdateState state = await _updates.CheckAsync(_shutdownCancellation.Token);
        if (_shutdownCancellation.IsCancellationRequested)
            return;
        switch (state)
        {
            case UpdateState.Available when _updates.Available is { } release:
                if (manual || _notifiedUpdateVersion != release.Version)
                {
                    _notifiedUpdateVersion = release.Version;
                    _tray.ShowUpdateAvailable(release.DisplayVersion);
                }
                break;
            case UpdateState.UpToDate when fromTray:
                _tray.ShowNotice("WinWispr is up to date", $"Version {AppVersion.Display} is the latest release.");
                break;
            case UpdateState.Failed:
                _ = _log.WriteAsync(TechnicalEvent.UpdateCheckFailed, exception: _updates.LastException);
                if (fromTray)
                    _tray.ShowNotice("Could not check for updates", _updates.FailureMessage ?? string.Empty, warning: true);
                break;
            case UpdateState.DisabledByPolicy when fromTray:
                _tray.ShowNotice("Updates are managed", "Your administrator turned off update checks for WinWispr.");
                break;
        }
    }

    private async Task InstallUpdateAsync()
    {
        if (_updates.Available is not { } release)
            return;
        if (_installScope == InstallScope.Portable)
        {
            // Setup did not install this folder, so it cannot replace it; the release page has the Portable ZIP.
            try
            {
                Process.Start(new ProcessStartInfo(release.PageUri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
                ShowOverlayStatus(OverlayStatus.Result("Could not open the download page", release.PageUri.AbsoluteUri));
            }
            return;
        }
        if (IsBusyForUpdate())
            return;

        _installingUpdate = true;
        try
        {
            string? setup = _updates.State == UpdateState.Ready && File.Exists(_readySetupPath) ? _readySetupPath : null;
            if (setup is null)
            {
                ShowOverlayStatus(OverlayStatus.Progress($"Downloading WinWispr {release.DisplayVersion}"));
                setup = await _updates.DownloadAsync(_shutdownCancellation.Token);
                if (_shutdownCancellation.IsCancellationRequested)
                    return;
                if (setup is null)
                {
                    _ = _log.WriteAsync(TechnicalEvent.UpdateDownloadFailed, exception: _updates.LastException);
                    ShowOverlayStatus(OverlayStatus.Result("Update not installed", _updates.FailureMessage));
                    return;
                }
                _readySetupPath = setup;
            }
            // A dictation may have started while the Setup downloaded; the downloaded Setup waits for the next click.
            if (IsBusyForUpdate())
                return;

            string log = Path.Combine(Path.GetDirectoryName(setup)!, "setup.log");
            string command = UpdateInstallation.BuildHelperCommand(
                Environment.ProcessId,
                setup,
                UpdateInstallation.BuildSetupArguments(_installScope, AppContext.BaseDirectory, log),
                Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "WinWispr.exe"),
                elevate: _installScope == InstallScope.AllUsers);
            UpdateInstallation.WritePending(_updates.UpdatesRoot, release);
            ShowOverlayStatus(OverlayStatus.Progress($"Installing WinWispr {release.DisplayVersion}"));
            Shutdown(beforeExit: () => StartUpdateHelper(command));
            if (!_shutdownCancellation.IsCancellationRequested)
                UpdateInstallation.TakePending(_updates.UpdatesRoot); // Shutdown was refused; nothing is installed.
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            _ = _log.WriteAsync(TechnicalEvent.UpdateStartFailed, exception: exception);
            ShowOverlayStatus(OverlayStatus.Result("Update not installed", "The update could not be started."));
        }
        finally
        {
            _installingUpdate = false;
            RefreshUpdateUi();
        }
    }

    private bool IsBusyForUpdate()
    {
        if (!_session.IsActive && !_selection.IsTranslating)
            return false;
        ShowOverlayStatus(OverlayStatus.Result("Finish the dictation, then update"));
        return true;
    }

    /// <summary>Runs after WinWispr let go of its single-instance lock, just before the process exits.</summary>
    private void StartUpdateHelper(string command)
    {
        try
        {
            Process.Start(UpdateInstallation.CreateHelperStartInfo(command))?.Dispose();
            _log.WriteAsync(TechnicalEvent.UpdateStarted).Wait(TimeSpan.FromMilliseconds(250));
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException)
        {
            UpdateInstallation.TakePending(_updates.UpdatesRoot);
            try
            {
                _log.WriteAsync(TechnicalEvent.UpdateStartFailed, exception: exception).Wait(TimeSpan.FromMilliseconds(250));
                // Nothing will run Setup, so come back as the version that is installed.
                if (Environment.ProcessPath is { } executable)
                    Process.Start(new ProcessStartInfo(executable) { UseShellExecute = false })?.Dispose();
            }
            catch (Exception restartFailure) when (restartFailure is Win32Exception or InvalidOperationException or IOException)
            {
                Debug.WriteLine($"WinWispr restart after a failed update unavailable: {restartFailure.GetType().Name}");
            }
        }
    }

    private void RefreshUpdateUi()
    {
        UpdateRelease? release = _updates.Available;
        string? version = release?.DisplayVersion;
        string install = _installScope == InstallScope.Portable ? $"Download version {version}" : $"Install update {version}";
        (string status, string button, bool enabled) = _updates.State switch
        {
            UpdateState.Checking => ("Checking for updates…", "Check for updates", false),
            UpdateState.UpToDate => ($"WinWispr {AppVersion.Display} is the latest version.", "Check for updates", true),
            UpdateState.Available => (_installScope == InstallScope.Portable
                ? $"Version {version} is available. This Portable copy is updated by extracting the new ZIP over it."
                : $"Version {version} is available. WinWispr restarts to install it.", install, true),
            UpdateState.Downloading => ($"Downloading version {version}… {_updates.DownloadProgress:P0}", install, false),
            UpdateState.Ready => ($"Version {version} is downloaded and verified.", install, true),
            UpdateState.Failed => (_updates.FailureMessage ?? "The update check failed.",
                release is null ? "Check for updates" : install, true),
            UpdateState.DisabledByPolicy => ("Your administrator manages updates for WinWispr.", "Check for updates", false),
            _ => (string.Empty, "Check for updates", true)
        };
        if (_installingUpdate)
            enabled = false;
        _settingsWindow.SetUpdateStatus(status, button, enabled, _updates.IsDisabledByPolicy);
        _tray.UpdateMenuLabel = release is not null
            && _updates.State is UpdateState.Available or UpdateState.Ready or UpdateState.Failed
                ? install
                : null;
    }
}
