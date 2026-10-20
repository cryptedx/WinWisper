using System.Text.RegularExpressions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using WinWispr.Audio;
using WinWispr.Core;
using WinWispr.Diagnostics;
using WinWispr.Interop;
using WinWispr.Speech;
using WinWispr.Translation;
using WinWispr.Views;

namespace WinWispr;

public partial class App : Application
{
    private const int WaveformHistoryPeakCount = 32;
    private const string ModelLoadFailedOverlayText = "Model could not be loaded";
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private readonly Queue<float> _peaks = new();
    private readonly float[] _peakSnapshot = new float[WaveformHistoryPeakCount];
    private DispatcherQueue _dispatcher = null!;
    private DispatcherQueueTimer _waveformTimer = null!;
    private Mutex? _singleInstance;
    private EventWaitHandle? _e2eExitEvent;
    private RegisteredWaitHandle? _e2eExitRegistration;
    private AppSettings _settings = null!;
    private TechnicalLog _log = null!;
    private ModelInstaller _modelInstaller = null!;
    private BundledTranslationModel _translationModel = null!;
    private Translator _translator = null!;
    private SpeechRuntime _speech = null!;
    private TtsSession? _tts;
    private AudioCapture _audio = null!;
    private DictationTimings _timings = null!;
    private WindowsTextOutput _textOutput = null!;
    private DictationSession _session = null!;
    private SelectionTranslation _selection = null!;
    private TranslationPreviewWindow? _preview;
    private WindowTarget _selectionTarget;
    private readonly WindowActivator _windowActivator = new();
    private OverlayWindow _overlay = null!;
    private SettingsWindow _settingsWindow = null!;
    private TaskBoardStore _taskBoardStore = null!;
    private TaskBoardDocument _taskBoardDocument = null!;
    private TaskBoardWindow _taskBoardWindow = null!;
    private TaskCaptureWindow? _taskCaptureWindow;
    private GlobalInput _globalInput = null!;
    private TrayIcon _tray = null!;
    private CancellationTokenSource? _downloadCancellation;
    private CancellationTokenSource? _overlayHideCancellation;
    private CancellationTokenSource? _hotkeyCaptureCancellation;
    private bool _hotkeyCaptureCancelRequested;
    private HotkeyCaptureTarget _hotkeyClashTarget;
    private string? _activeModelDirectory;
    private bool _initialized;
    private bool _pendingSpeechMode;
    private bool _changingSpeechMode;
    private readonly MicrophoneCatalog _microphoneCatalog = new();

    public App()
    {
        UnhandledException += (_, args) => WriteLifecycleLog(TechnicalEvent.UnhandledException, args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteLifecycleLog(TechnicalEvent.UnhandledException, args.ExceptionObject as Exception);
        try
        {
            InitializeComponent();
        }
        catch (Exception exception)
        {
            WriteLifecycleLog(TechnicalEvent.StartupFailed, exception);
            throw;
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            WriteLifecycleLog(TechnicalEvent.StartupStarted);
            OnLaunchedCore(args);
            WriteLifecycleLog(TechnicalEvent.StartupReady);
        }
        catch (Exception exception)
        {
            WriteLifecycleLog(TechnicalEvent.StartupFailed, exception);
            throw;
        }
    }

    private void OnLaunchedCore(LaunchActivatedEventArgs args)
    {
        if (!TryAcquireSingleInstance())
        {
            Environment.Exit(0);
            return;
        }

        InitializeComposition();

        string[] commandLineArguments = Environment.GetCommandLineArgs();
        bool e2eTaskBoardLaunch = args.Arguments.Contains("--e2e-taskboard", StringComparison.Ordinal)
            || commandLineArguments.Any(argument => string.Equals(argument, "--e2e-taskboard", StringComparison.Ordinal)
                || string.Equals(argument, "--e2e-taskboard-due", StringComparison.Ordinal));
        bool e2eDictationLaunch = commandLineArguments.Any(
            argument => string.Equals(argument, "--e2e-dictation", StringComparison.Ordinal));
        if (AppDataPaths.IsTaskBoardE2e && (e2eTaskBoardLaunch || e2eDictationLaunch))
        {
            StartE2eExitListener();
            _taskBoardWindow.ActivateBoard();
            if (commandLineArguments.Any(argument => string.Equals(argument, "--e2e-taskboard-due", StringComparison.Ordinal)))
                _taskBoardWindow.ShowDueTasks();
            // Dictation E2E runs the real startup below, including the speech model load.
            if (!e2eDictationLaunch)
                return;
        }

        if (_activeModelDirectory is not null)
        {
            ShowOverlayStatus(OverlayStatus.Progress("Loading model"));
            _ = LoadModelAsync();
        }
        else if (_modelInstaller.HasBundledModelArchive())
        {
            _ = InstallBundledModelAsync();
        }
    }

    private void WriteLifecycleLog(TechnicalEvent technicalEvent, Exception? exception = null)
    {
        try
        {
            _log ??= new TechnicalLog(
                Path.Combine(AppDataPaths.Root, "Logs"),
                TimeProvider.System);
            Task pending = _log.WriteAsync(technicalEvent, exception: exception);
            // The process ends after these events, so give the queued write a moment to land.
            if (technicalEvent is TechnicalEvent.StartupFailed or TechnicalEvent.UnhandledException)
                pending.Wait(TimeSpan.FromMilliseconds(250));
        }
        catch (Exception loggingFailure)
        {
            System.Diagnostics.Debug.WriteLine(
                $"WinWispr lifecycle diagnostic unavailable: {loggingFailure.GetType().Name}");
        }
    }

    private void InitializeComposition()
    {
        if (_initialized)
            return;
        _initialized = true;

        _dispatcher = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("WinWispr must start on the UI dispatcher.");
        _settings = AppSettingsStore.Current.Load();
        // Load the dictionary before any settings save: that save drops the legacy field it migrates from.
        PersonalDictionaryStore.Current.Load();
        _log ??= new TechnicalLog(
            System.IO.Path.Combine(AppDataPaths.Root, "Logs"),
            TimeProvider.System);
        _timings = new DictationTimings(_log, TimeProvider.System);
        _modelInstaller = ModelInstaller.CreateDefault();
        _speech = SpeechRuntime.CreateNative(_ => _modelInstaller.GetActiveModelDirectory(), _settings.SpeechMode);
        var teamsMicrophone = new TeamsMicrophone(timing: _timings.Record);
        // Only the E2E profile can replace the microphone with a recorded clip.
        string? replayAudio = AppDataPaths.E2eReplayAudioPath;
        _audio = replayAudio is not null
            ? new AudioCapture(buffer => ReplayCaptureDevice.FromPcm16File(buffer, replayAudio))
            : AudioCapture.CreateNative(_microphoneCatalog, timing: _timings.Record);
        _textOutput = new WindowsTextOutput();
        _overlay = new OverlayWindow();
        _settingsWindow = new SettingsWindow(_settings.SettingsWindowPlacement);
        _settingsWindow.LoadSettings(_settings);
        _settingsWindow.LoadDictionary(PersonalDictionaryStore.Current.Text);
        _settingsWindow.SetSettingsWarning(LoadWarnings());
        _ = _settingsWindow.LoadSpeechDevicesAsync();
        _taskBoardStore = new TaskBoardStore(System.IO.Path.Combine(AppDataPaths.Root, "tasks.json"));
        _taskBoardDocument = _taskBoardStore.Load();
        _taskBoardWindow = new TaskBoardWindow(
            _taskBoardStore,
            _taskBoardDocument,
            _settings.TaskBoardWindowPlacement,
            _settings.TaskDrawerWidth,
            _dispatcher,
            _settings.TaskButtonMode);
        _taskBoardWindow.TaskCaptureRequested += () => PressTrigger(DictationTrigger.TaskButton);
        _taskBoardWindow.TaskDraftResumeRequested += ResumeTaskDraft;
        _taskBoardWindow.TaskCapturePressed += () => PressTrigger(DictationTrigger.TaskButton);
        _taskBoardWindow.TaskCaptureReleased += () => _ = _session.ReleaseAsync(DictationTrigger.TaskButton);
        _taskBoardWindow.PlacementChanged += placement => SaveBoardSettings(_settings with { TaskBoardWindowPlacement = placement });
        _taskBoardWindow.DrawerWidthChanged += width => SaveBoardSettings(_settings with { TaskDrawerWidth = width });
        _activeModelDirectory = _modelInstaller.GetActiveModelDirectory();
        _settingsWindow.SetModelState(_activeModelDirectory is null ? ModelUiState.NotInstalled : ModelUiState.Ready);
        // The translation model ships with the app; whether it is complete comes from its files, never from settings.json.
        _translationModel = BundledTranslationModel.CreateDefault();
        _settingsWindow.SetTranslationState(_translationModel.State);
        if (_translationModel.IsInstalled && !AppDataPaths.IsTaskBoardE2e)
            _ = Task.Run(() => BundledTranslationModel.RemoveDownloadedCopy(AppDataPaths.Root));
        // Nothing loads until a translation (or the English dictation hotkey) needs a direction.
        _translator = new Translator(() => _translationModel.InstalledDirectory, OnTranslationFailureObserved);

        _session = new DictationSession(
            _settings,
            _timings.Wrap(new DictationEffects(
                StartCaptureAsync,
                cancellationToken => _audio.PauseAsync(cancellationToken),
                cancellationToken => _audio.ResumeAsync(cancellationToken),
                cancellationToken => _audio.StopAsync(cancellationToken),
                () => _audio.DiscardAsync(),
                (samples, cancellationToken) => _speech.RecognizeAsync(samples, cancellationToken),
                (target, text, cancellationToken) => _textOutput.DeliverAsync(target, text, cancellationToken))
            {
                TaskCapturedAsync = HandleTaskTranscriptAsync,
                TaskCapturedRawAsync = HandleTaskTranscriptRawAsync,
                TryResumeTaskDraft = TryResumeTaskDraft,
                CurrentForegroundWindow = () => new WindowTarget(GetForegroundWindow()),
                // A replayed clip never reaches a meeting, so there is nothing to protect and nothing to mute.
                MuteMeetingAsync = replayAudio is null ? teamsMicrophone.BeginAsync : null,
                RestoreMeetingAsync = replayAudio is null ? teamsMicrophone.EndAsync : null,
                IsTranslationInstalled = () => _translationModel.IsInstalled,
                PreloadTranslation = () => _translator.Preload(TranslationDirection.GermanToEnglish),
                TranslateToEnglishAsync = TranslateDictationAsync,
                CopyTranscriptAsync = (text, cancellationToken) => _textOutput.CopyAsync(text, cancellationToken)
            }),
            CurrentSpeechReadiness());
        _session.UpdateDictionary(PersonalDictionaryStore.Current.Dictionary);

        // A selection translation is its own coordinator, never a dictation session. It needs the session only to
        // stay out of the way while a dictation is active.
        var clipboardForSelection = new WindowsSelectionClipboard();
        var selectionReader = new SelectionReader(
            clipboardForSelection,
            NativeKeyboard.AreModifiersPressed,
            () => NativeKeyboard.SendControlChord(NativeKeyboard.C));
        _selection = new SelectionTranslation(new SelectionTranslationEffects(
            ReadSelectionAsync: selectionReader.ReadAsync,
            TranslateAsync: TranslateSelectionAsync,
            ShowPreviewAsync: ShowTranslationPreviewAsync,
            CopyAsync: (text, cancellationToken) => _textOutput.CopyAsync(text, cancellationToken),
            ShowNotice: OnTranslationNotice,
            IsDictationActive: () => _session.IsActive,
            IsTranslationInstalled: () => _translationModel.IsInstalled,
            CaptureTarget: () => _selectionTarget = new WindowTarget(GetForegroundWindow()),
            TranslateInDirectionAsync: TranslateSwappedAsync,
            ReactivateAsync: _windowActivator.ActivateAsync,
            DeliverAsync: (target, text, cancellationToken) => _textOutput.DeliverAsync(target, text, cancellationToken))
        {
            ReadClipboardTextAsync = clipboardForSelection.ReadTextAsync,
            PrepareTranslation = _translator.PreloadForSelection,
            LogFailure = (technicalEvent, exception) => _ = _log.WriteAsync(technicalEvent, exception: exception),
            PhaseChanged = OnSelectionPhaseChanged
        });

        _waveformTimer = _dispatcher.CreateTimer();
        _waveformTimer.Interval = TimeSpan.FromMilliseconds(16);
        _waveformTimer.Tick += (_, _) =>
        {
            if (_session.State != RecordingState.Recording)
            {
                _waveformTimer.Stop();
                return;
            }

            int peakCount = CopyPeaks();
            _overlay.SetWaveform(_peakSnapshot.AsSpan(0, peakCount));
        };

        _overlay.SetActions(
            () => _ = _session.StopAsync(StopReason.User),
            () => _ = _session.RequestCancelAsync(CancellationToken.None),
            () => _ = _session.ConfirmDiscardAsync(CancellationToken.None));
        _settingsWindow.HotkeyCaptureRequested += CaptureHotkeyAsync;
        _settingsWindow.HotkeyCaptureCancelRequested += CancelHotkeyCapture;
        // Voice commands and speech mode restore their own control and explain the failure.
        _settingsWindow.VoiceCommandsChanged += enabled => TrySaveSettings(_settings with { VoiceCommandsEnabled = enabled });
        _settingsWindow.PersonalDictionaryChanged += SaveDictionary;
        _settingsWindow.SpeechModeChanged += mode => TrySaveSettings(_settings with { SpeechMode = mode });
        _settingsWindow.ModeChanged += mode => SaveSettingsOrWarn(_settings with { Mode = mode });
        _settingsWindow.TaskHotkeyModeChanged += mode => SaveSettingsOrWarn(_settings with { TaskHotkeyMode = mode });
        _settingsWindow.TaskButtonModeChanged += mode => SaveSettingsOrWarn(_settings with { TaskButtonMode = mode });
        _settingsWindow.EnglishDictationModeChanged += mode =>
            SaveSettingsOrWarn(_settings with { EnglishDictationMode = mode });
        _settingsWindow.MicrophoneChanged += microphoneId => SaveSettingsOrWarn(_settings with { MicrophoneId = microphoneId });
        _settingsWindow.MicrophoneRefreshRequested += () => _ = LoadMicrophonesAsync();
        _settingsWindow.PlacementChanged += placement => SaveSettingsOrWarn(
            _settings with { SettingsWindowPlacement = placement });
        _settingsWindow.DownloadRequested += () => _ = DownloadModelAsync();
        _settingsWindow.CancelDownloadRequested += () => _downloadCancellation?.Cancel();
        _session.StateChanged += OnSessionStateChanged;
        _session.StartBlocked += OnStartBlocked;
        if (Environment.GetCommandLineArgs().Contains("--tts-prototype", StringComparer.Ordinal))
        {
            var playback = new TtsPlayback();
            _tts = new TtsSession(TtsNative.Load, playback.PlayAsync, playback.Pause, playback.Resume, playback.Stop);
            _session.StateChanged += _tts.OnDictationStateChanged;
            TtsPrototype.Start(_tts, _shutdownCancellation.Token);
        }
        _session.Notice += OnSessionNotice;
        _speech.StateChanged += OnSpeechStateChanged;
        _audio.PeakChanged += OnAudioPeakChanged;
        _audio.LimitReached += () => _dispatcher.TryEnqueue(
            () => _ = _session.StopAsync(StopReason.Limit));
        _audio.Failed += () => _dispatcher.TryEnqueue(
            () => _ = _session.StopAsync(StopReason.User));
        _ = LoadMicrophonesAsync();

        _globalInput = new GlobalInput(
            () => _session.State,
            action =>
            {
                long posted = System.Diagnostics.Stopwatch.GetTimestamp();
                _dispatcher.TryEnqueue(() =>
                {
                    if (action is GlobalInputAction.PressPrimary or GlobalInputAction.PressTask
                            or GlobalInputAction.PressEnglishDictation
                        && _session.State == RecordingState.Idle)
                        _timings.Record(TechnicalEvent.HotkeyDispatch, System.Diagnostics.Stopwatch.GetElapsedTime(posted).TotalMilliseconds);
                    HandleInput(action);
                });
            },
            () => _selection.IsTranslating);
        if (!_globalInput.Start(
                _settings.Hotkey, _settings.BoardHotkey, _settings.TaskHotkey, _settings.EnglishDictationHotkey,
                _settings.TranslateHotkey, _settings.ReplayHotkey))
        {
            _settingsWindow.SetHotkey(_settings.Hotkey);
            _settingsWindow.Activate();
        }

        var hotkeyClashes = RefreshHotkeyStatuses();

        _tray = new TrayIcon(
            (command, target) => _dispatcher.TryEnqueue(() => HandleTray(command, target)),
            LoadTrayMicrophonesAsync,
            SelectTrayMicrophone);
        InitializeUpdates();
        // One balloon at a time: the daily summary waits for the next hotkey press while the clash notice shows.
        if (!ShowHotkeyClashNoticeIfNeeded(hotkeyClashes))
            ShowDailySummaryIfNeeded();
        if (LoadWarnings() is not null)
            _settingsWindow.Activate();
    }

    private async Task LoadMicrophonesAsync()
    {
        _settingsWindow.ShowMicrophonesLoading();
        MicrophoneSelection selection = await _microphoneCatalog.LoadAsync(_settings.MicrophoneId);
        _settingsWindow.ShowMicrophones(selection);
    }

    // A saved microphone that is not connected leaves the preference alone: this session records from the
    // Windows default, says so when it ends, and settings and tray show it from a fresh listing.
    private async Task<DictationNotice?> StartCaptureAsync(string? microphoneId, CancellationToken cancellationToken)
    {
        MicrophoneChoice choice = await _microphoneCatalog.ChooseForCaptureAsync(microphoneId, cancellationToken)
            .ConfigureAwait(false);
        await _audio.StartAsync(choice.SavedMissing ? null : microphoneId, cancellationToken).ConfigureAwait(false);
        if (!choice.SavedMissing)
            return null;

        _ = _log.WriteAsync(TechnicalEvent.MicrophoneFallback, recordingState: RecordingState.Starting);
        RunOnUi(() => _ = LoadMicrophonesAsync());
        return new DictationNotice(DictationNoticeKind.MicrophoneFallback);
    }

    private Task<MicrophoneSelection> LoadTrayMicrophonesAsync() => _microphoneCatalog.LoadAsync(_settings.MicrophoneId);

    private void SelectTrayMicrophone(string? microphoneId)
    {
        if (SaveSettingsOrWarn(_settings with { MicrophoneId = microphoneId }))
            _ = LoadMicrophonesAsync();
    }

    private async Task LoadModelAsync()
    {
        try
        {
            await _speech.LoadInstalledAsync(_shutdownCancellation.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await _log.WriteAsync(TechnicalEvent.ModelLoadFailed, modelState: _speech.State, exception: exception);
        }
    }

    private async Task InstallBundledModelAsync()
    {
        await RunOnUiAsync(() => _settingsWindow.SetModelState(ModelUiState.Loading));
        ModelInstallStatus result = await _modelInstaller.InstallBundledAsync(
            progress: null,
            _shutdownCancellation.Token);
        switch (result)
        {
            case ModelInstallStatus.Installed:
                _activeModelDirectory = _modelInstaller.GetActiveModelDirectory();
                await RunOnUiAsync(() => _settingsWindow.SetModelState(ModelUiState.Loading));
                await LoadModelAsync();
                break;
            case ModelInstallStatus.IntegrityFailed:
                await _log.WriteAsync(TechnicalEvent.ModelIntegrityFailed);
                await RunOnUiAsync(() => _settingsWindow.SetModelState(ModelUiState.IntegrityFailed));
                break;
            case ModelInstallStatus.ExtractionFailed:
                await _log.WriteAsync(TechnicalEvent.ModelDownloadFailed);
                await RunOnUiAsync(() => _settingsWindow.SetModelState(ModelUiState.InstallationFailed));
                break;
            default:
                await _log.WriteAsync(TechnicalEvent.ModelDownloadFailed);
                await RunOnUiAsync(() => _settingsWindow.SetModelState(ModelUiState.InstallationFailed));
                break;
        }
    }

    private async Task DownloadModelAsync()
    {
        if (_downloadCancellation is not null)
            return;

        using var cancellation = new CancellationTokenSource();
        _downloadCancellation = cancellation;
        await RunOnUiAsync(() => _settingsWindow.SetModelState(ModelUiState.Downloading));
        try
        {
            var progress = new Progress<double>(value =>
                _dispatcher.TryEnqueue(() => _settingsWindow.SetModelState(ModelUiState.Downloading, value)));
            ModelInstallStatus result = await _modelInstaller.InstallAsync(progress, cancellation.Token);
            switch (result)
            {
                case ModelInstallStatus.Installed:
                    await RunOnUiAsync(() => _settingsWindow.SetModelState(ModelUiState.Loading));
                    await _speech.LoadInstalledAsync(_shutdownCancellation.Token).ConfigureAwait(false);
                    break;
                case ModelInstallStatus.Cancelled:
                    await RunOnUiAsync(() => _settingsWindow.SetModelState(ModelUiState.Cancelled));
                    break;
                case ModelInstallStatus.IntegrityFailed:
                    await _log.WriteAsync(TechnicalEvent.ModelIntegrityFailed);
                    await RunOnUiAsync(() => _settingsWindow.SetModelState(ModelUiState.IntegrityFailed));
                    break;
                case ModelInstallStatus.ExtractionFailed:
                    await _log.WriteAsync(TechnicalEvent.ModelDownloadFailed);
                    await RunOnUiAsync(() => _settingsWindow.SetModelState(ModelUiState.InstallationFailed));
                    break;
                default:
                    await _log.WriteAsync(TechnicalEvent.ModelDownloadFailed);
                    await RunOnUiAsync(() => _settingsWindow.SetModelState(ModelUiState.DownloadFailed));
                    break;
            }
        }
        catch (Exception exception)
        {
            await _log.WriteAsync(TechnicalEvent.ModelDownloadFailed, exception: exception);
            await RunOnUiAsync(() => _settingsWindow.SetModelState(ModelUiState.DownloadFailed));
        }
        finally
        {
            _downloadCancellation = null;
        }
    }

    private async void CaptureHotkeyAsync(HotkeyCaptureTarget target)
    {
        if (_hotkeyCaptureCancellation is not null)
            return;

        _settingsWindow.BeginHotkeyCapture(target);
        CancellationTokenSource captureCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _shutdownCancellation.Token);
        _hotkeyCaptureCancellation = captureCancellation;
        _hotkeyCaptureCancelRequested = false;
        captureCancellation.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            Hotkey? candidate = await _globalInput.CaptureHotkeyAsync(captureCancellation.Token);
            if (candidate is not Hotkey hotkey)
            {
                bool timedOut = captureCancellation.IsCancellationRequested && !_hotkeyCaptureCancelRequested;
                if (!timedOut)
                    captureCancellation.Cancel();
                await RunOnUiAsync(() =>
                {
                    _settingsWindow.SetHotkey(target, GetHotkey(target));
                    _settingsWindow.SetHotkeyCaptureStatus(
                        timedOut
                            ? SettingsWindow.HotkeyCaptureTimedOutText
                            : SettingsWindow.HotkeyCaptureCancelledText);
                });
                return;
            }

            bool replaced = target switch
            {
                HotkeyCaptureTarget.Board => _globalInput.TryReplaceBoard(hotkey, value => PersistHotkeySettings(_settings with { BoardHotkey = value })),
                HotkeyCaptureTarget.Task => _globalInput.TryReplaceTask(hotkey, value => PersistHotkeySettings(_settings with { TaskHotkey = value })),
                HotkeyCaptureTarget.EnglishDictation => _globalInput.TryReplaceEnglishDictation(
                    hotkey, value => PersistHotkeySettings(_settings with { EnglishDictationHotkey = value })),
                HotkeyCaptureTarget.Translate => _globalInput.TryReplaceTranslate(
                    hotkey, value => PersistHotkeySettings(_settings with { TranslateHotkey = value })),
                HotkeyCaptureTarget.Replay => _globalInput.TryReplaceReplay(
                    hotkey, value => PersistHotkeySettings(_settings with { ReplayHotkey = value })),
                _ => _globalInput.TryReplace(hotkey, value => PersistHotkeySettings(_settings with { Hotkey = value }))
            };
            if (replaced)
                await RunOnUiAsync(() =>
                {
                    _settingsWindow.CompleteHotkeyCapture(target, SettingsWindow.FormatHotkeyButtonText(hotkey, false));
                    // The status line shows a clash at once, so the startup notice need not report it again.
                    RememberHotkeyClashesShown(RefreshHotkeyStatuses());
                });
            else
                await RunOnUiAsync(() =>
                {
                    _settingsWindow.SetHotkey(target, GetHotkey(target));
                    _settingsWindow.SetHotkeyCaptureStatus(SettingsWindow.HotkeyCaptureUnavailableText);
                    RefreshHotkeyStatuses();
                });
        }
        finally
        {
            _hotkeyCaptureCancellation = null;
            captureCancellation.Dispose();
        }
    }

    private Hotkey GetHotkey(HotkeyCaptureTarget target) => target switch
    {
        HotkeyCaptureTarget.Board => _settings.BoardHotkey,
        HotkeyCaptureTarget.Task => _settings.TaskHotkey,
        HotkeyCaptureTarget.EnglishDictation => _settings.EnglishDictationHotkey,
        HotkeyCaptureTarget.Translate => _settings.TranslateHotkey,
        HotkeyCaptureTarget.Replay => _settings.ReplayHotkey,
        _ => _settings.Hotkey
    };

    private static readonly (HotkeyCaptureTarget Target, string Name)[] HotkeyNames =
    [
        (HotkeyCaptureTarget.Primary, "Dictation"),
        (HotkeyCaptureTarget.Replay, "Insert again"),
        (HotkeyCaptureTarget.Board, "Task board"),
        (HotkeyCaptureTarget.Task, "Task dictation"),
        (HotkeyCaptureTarget.EnglishDictation, "English dictation"),
        (HotkeyCaptureTarget.Translate, "Translate")
    ];

    private bool IsHotkeyAvailable(HotkeyCaptureTarget target) => target switch
    {
        HotkeyCaptureTarget.Board => _globalInput.BoardHotkeyAvailable,
        HotkeyCaptureTarget.Task => _globalInput.TaskHotkeyAvailable,
        HotkeyCaptureTarget.EnglishDictation => _globalInput.EnglishDictationHotkeyAvailable,
        HotkeyCaptureTarget.Translate => _globalInput.TranslateHotkeyAvailable,
        HotkeyCaptureTarget.Replay => _globalInput.ReplayHotkeyAvailable,
        _ => _globalInput.PrimaryHotkeyAvailable
    };

    /// <summary>
    /// Shows every hotkey's status in Settings, a registered Ctrl+Alt hotkey that blocks an AltGr character on an
    /// installed keyboard layout included, and returns those clashes.
    /// </summary>
    private List<(HotkeyCaptureTarget Target, string Name, Hotkey Hotkey, HotkeyCharacterClash Clash)> RefreshHotkeyStatuses()
    {
        IReadOnlyList<KeyboardLayout> layouts = KeyboardLayout.Installed();
        var clashes = new List<(HotkeyCaptureTarget, string, Hotkey, HotkeyCharacterClash)>();
        foreach ((HotkeyCaptureTarget target, string name) in HotkeyNames)
        {
            Hotkey hotkey = GetHotkey(target);
            bool available = IsHotkeyAvailable(target);
            HotkeyCharacterClash? clash = available ? KeyboardLayout.FindClash(hotkey, layouts) : null;
            _settingsWindow.SetHotkeyStatus(target, available, hotkey, clash);
            if (clash is HotkeyCharacterClash blocked)
                clashes.Add((target, name, hotkey, blocked));
        }
        return clashes;
    }

    private static string[] HotkeyClashKeys(
        IEnumerable<(HotkeyCaptureTarget Target, string Name, Hotkey Hotkey, HotkeyCharacterClash Clash)> clashes) =>
        [.. clashes.Select(clash =>
            $"{(int)clash.Hotkey.Modifiers}+{clash.Hotkey.VirtualKey}:{clash.Clash.Character}:{clash.Clash.LayoutName}")];

    private void RememberHotkeyClashesShown(
        IEnumerable<(HotkeyCaptureTarget Target, string Name, Hotkey Hotkey, HotkeyCharacterClash Clash)> clashes)
    {
        string shown = string.Join('\n', HotkeyClashKeys(clashes).Union(
            _settings.HotkeyClashesShown.Split('\n', StringSplitOptions.RemoveEmptyEntries)).Order(StringComparer.Ordinal));
        if (shown != _settings.HotkeyClashesShown)
            TrySaveSettings(_settings with { HotkeyClashesShown = shown });
    }

    /// <summary>
    /// Tells the user once, in a tray balloon, about registered hotkeys that block an AltGr character, such as a stored
    /// Ctrl + Alt + E on a German keyboard (AltGr + E types €). Returns whether the balloon shows.
    /// </summary>
    private bool ShowHotkeyClashNoticeIfNeeded(
        List<(HotkeyCaptureTarget Target, string Name, Hotkey Hotkey, HotkeyCharacterClash Clash)> clashes)
    {
        string[] shown = _settings.HotkeyClashesShown.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (HotkeyClashKeys(clashes).All(shown.Contains))
            return false;
        (string title, string body) = SettingsWindow.HotkeyClashNotice(
            [.. clashes.Select(clash => (clash.Name, clash.Hotkey, clash.Clash))]);
        if (!_tray.ShowNotice(title, body, TrayCommand.HotkeyClash, warning: true))
            return false;
        _hotkeyClashTarget = clashes[0].Target;
        RememberHotkeyClashesShown(clashes);
        return true;
    }

    private void CancelHotkeyCapture()
    {
        _hotkeyCaptureCancelRequested = true;
        _hotkeyCaptureCancellation?.Cancel();
    }

    private void HandleInput(GlobalInputAction action)
    {
        switch (action)
        {
            case GlobalInputAction.ReplayLast:
                ShowDailySummaryIfNeeded();
                _ = _session.ReplayLastAsync(new WindowTarget(GetForegroundWindow()), _shutdownCancellation.Token);
                break;
            case GlobalInputAction.PressPrimary:
                PressTrigger(DictationTrigger.PrimaryHotkey);
                break;
            case GlobalInputAction.ReleasePrimary:
                _ = _session.ReleaseAsync(DictationTrigger.PrimaryHotkey);
                break;
            case GlobalInputAction.PressTask:
                PressTrigger(DictationTrigger.TaskHotkey);
                break;
            case GlobalInputAction.ReleaseTask:
                _ = _session.ReleaseAsync(DictationTrigger.TaskHotkey);
                break;
            case GlobalInputAction.PressEnglishDictation:
                PressTrigger(DictationTrigger.EnglishDictationHotkey);
                break;
            case GlobalInputAction.ReleaseEnglishDictation:
                _ = _session.ReleaseAsync(DictationTrigger.EnglishDictationHotkey);
                break;
            case GlobalInputAction.PressTranslate:
                _ = PressTranslateAsync();
                break;
            case GlobalInputAction.CancelTranslation:
                _selection.Cancel();
                break;
            case GlobalInputAction.OpenBoard:
                _taskBoardWindow.ToggleBoard();
                break;
            case GlobalInputAction.RequestCancel:
                _ = _session.RequestCancelAsync(CancellationToken.None);
                break;
            case GlobalInputAction.ConfirmDiscard:
                _ = _session.ConfirmDiscardAsync(CancellationToken.None);
                break;
        }
    }

    private void PressTrigger(DictationTrigger trigger, WindowTarget? target = null)
    {
        // Dictation takes priority: a running selection translation or its open preview ends as a Cancel. The
        // dictation then pastes into the window the selection came from, not into the closing preview.
        // The dismissal must run for every trigger, including the tray, which passes its own target.
        WindowTarget? dismissedTarget = _selection.DismissForDictation();
        target ??= dismissedTarget;

        if (_session.State == RecordingState.Idle)
            ShowDailySummaryIfNeeded();
        _ = _session.PressAsync(trigger, target);
    }

    private void HandleTray(TrayCommand command, WindowTarget target)
    {
        switch (command)
        {
            case TrayCommand.Settings:
                // The files are the truth: a model removed or restored outside the app shows correctly.
                _settingsWindow.SetTranslationState(_translationModel.State);
                _settingsWindow.Activate();
                break;
            case TrayCommand.StartStop:
                PressTrigger(DictationTrigger.Tray, target);
                break;
            case TrayCommand.TranslateClipboard:
                _ = PressTranslateClipboardAsync(target);
                break;
            case TrayCommand.Board:
                ShowDailySummaryIfNeeded();
                _taskBoardWindow.RestoreNormalView();
                _taskBoardWindow.ActivateBoard();
                break;
            case TrayCommand.DueSummary:
                ShowDailySummaryIfNeeded();
                _taskBoardWindow.ShowDueTasks();
                break;
            case TrayCommand.Update:
                _ = RequestUpdateAsync(fromTray: true);
                break;
            case TrayCommand.HotkeyClash:
                _settingsWindow.SetTranslationState(_translationModel.State);
                _settingsWindow.ShowHotkey(_hotkeyClashTarget);
                break;
            case TrayCommand.Exit:
                Shutdown();
                break;
        }
    }

    private void ShowDailySummaryIfNeeded()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        if (!_taskBoardStore.CanSave || _taskBoardDocument.LastSummaryLocalDate == today)
            return;

        TaskDailySummary summary = TaskDailySummary.Create(_taskBoardDocument, today);
        DateOnly? previous = _taskBoardDocument.LastSummaryLocalDate;
        try
        {
            if (_tray.ShowTaskSummary(summary))
                _taskBoardWindow.TrySetSummaryDate(today);
        }
        catch (IOException)
        {
            _taskBoardDocument.LastSummaryLocalDate = previous;
        }
        catch (UnauthorizedAccessException)
        {
            _taskBoardDocument.LastSummaryLocalDate = previous;
        }
    }

    private bool TryResumeTaskDraft()
    {
        if (!_taskBoardWindow.HasTaskDraft)
            return false;
        RunOnUi(ResumeTaskDraft);
        return true;
    }

    private SpeechReadiness CurrentSpeechReadiness()
    {
        if (_changingSpeechMode)
            return SpeechReadiness.Switching;
        return _speech.State switch
        {
            SpeechRuntimeState.Ready => SpeechReadiness.Ready,
            SpeechRuntimeState.Loading => SpeechReadiness.Loading,
            SpeechRuntimeState.Missing => SpeechReadiness.Missing,
            _ => SpeechReadiness.Failed
        };
    }

    private void OnStartBlocked(SpeechReadiness readiness)
    {
        RunOnUi(() =>
        {
            switch (readiness)
            {
                case SpeechReadiness.Loading:
                    ShowOverlayStatus(OverlayStatus.Progress("Loading model"));
                    break;
                case SpeechReadiness.Switching:
                    ShowOverlayStatus(OverlayStatus.Progress("Switching processing device"));
                    break;
                case SpeechReadiness.Missing:
                    _settingsWindow.ShowModelSection();
                    break;
                default:
                    ShowOverlayStatus(OverlayStatus.Result(ModelLoadFailedOverlayText));
                    break;
            }
        });
    }

    private void OnSessionStateChanged(RecordingState state)
    {
        WindowTarget target = _session.Target;
        _timings.OnStateChanged(state);
        RunOnUi(() =>
        {
            if (state != RecordingState.Idle)
                CancelOverlayHide();

            switch (state)
            {
                case RecordingState.Idle:
                    _waveformTimer.Stop();
                    _overlay.HideOverlay();
                    _ = ApplyPendingSpeechModeAsync();
                    break;
                case RecordingState.Starting:
                    _overlay.ShowStatus(OverlayStatus.Progress("Preparing…"));
                    _overlay.ShowAt(target);
                    break;
                case RecordingState.Recording:
                    _waveformTimer.Start();
                    lock (_peaks)
                        _peaks.Clear();
                    _overlay.ShowRecording(_peakSnapshot.AsSpan(0, CopyPeaks()));
                    _overlay.ShowAt(target);
                    break;
                case RecordingState.CancelPending:
                    _overlay.ShowCancel(TimeSpan.FromSeconds(5));
                    _overlay.ShowAt(target);
                    break;
                case RecordingState.Processing:
                    _overlay.ShowStatus(OverlayStatus.Progress("Processing"));
                    _overlay.ShowAt(target);
                    break;
            }
        });
    }

    private void OnSessionNotice(DictationNotice notice)
    {
        RunOnUi(() =>
        {
            ShowOverlayStatus(DictationNoticePresentation.For(notice));
            if (notice.Kind is DictationNoticeKind.CopiedToClipboard or DictationNoticeKind.TargetMenuOpen)
                _tray?.ShowPasteFallback(menuOpen: notice.Kind == DictationNoticeKind.TargetMenuOpen);
        });
        if (notice.Kind == DictationNoticeKind.MicrophoneUnavailable)
            _ = _log.WriteAsync(TechnicalEvent.MicrophoneUnavailable);
    }

    // Dictation translation has no length limit and is never cancelled once the cancel window is over. The
    // overlay says "Loading translation…" only while the model still has to load, then "Translating…".
    private async Task<TranslationResult> TranslateDictationAsync(string transcript, CancellationToken cancellationToken)
    {
        bool loading = _translator.WouldWaitForLoadToEnglish(transcript);
        RunOnUi(() => ShowOverlayStatus(loading ? OverlayStatus.LoadingTranslation : OverlayStatus.Translating));
        Task<TranslationResult> translation = _translator.TranslateToEnglishAsync(
            transcript, TranslationUse.Dictation, cancellationToken);
        if (loading)
            _ = ShowTranslatingAfterLoadAsync(translation, cancellationToken);
        TranslationResult result = await translation.ConfigureAwait(false);
        // A failed load or translation was logged by the translator's hook with its exception type.
        TechnicalEvent? problem = result switch
        {
            TranslationResult.TimedOut => TechnicalEvent.TranslationTimedOut,
            TranslationResult.NotInstalled => TechnicalEvent.TranslationNotInstalled,
            _ => null
        };
        if (problem is TechnicalEvent technicalEvent)
            _ = _log.WriteAsync(technicalEvent, recordingState: RecordingState.Processing);
        return result;
    }

    private Task ShowTranslatingAfterLoadAsync(Task<TranslationResult> translation, CancellationToken cancellationToken) =>
        ShowTranslatingAfterLoadAsync(
            translation,
            () => _translator.IsLoadPending(TranslationDirection.GermanToEnglish),
            () => _session.State == RecordingState.Processing,
            cancellationToken);

    private async Task ShowTranslatingAfterLoadAsync(
        Task<TranslationResult> translation,
        Func<bool> loadPending,
        Func<bool> stillShowing,
        CancellationToken cancellationToken)
    {
        try
        {
            while (loadPending() && !translation.IsCompleted)
                await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!translation.IsCompleted)
            RunOnUi(() =>
            {
                if (stillShowing())
                    ShowOverlayStatus(OverlayStatus.Translating);
            });
    }

    private Task PressTranslateAsync() => RunSelectionPressAsync(_selection.PressAsync);

    // The tray menu closed without moving focus; its target only places the preview.
    private Task PressTranslateClipboardAsync(WindowTarget target)
    {
        if (!_selection.IsActive)
            _selectionTarget = target;
        return RunSelectionPressAsync(() => _selection.PressClipboardAsync(target));
    }

    private async Task RunSelectionPressAsync(Func<Task> press)
    {
        try
        {
            await press().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            _ = _log.WriteAsync(TechnicalEvent.TranslationFailed, exception: exception);
            OnTranslationNotice(new TranslationNotice(TranslationNoticeKind.TranslationFailed));
        }
    }

    // The overlay follows the phases: nothing while the selection is read, the progress status while it
    // translates (set by the translation itself), nothing while the preview is open, and gone at the end unless a
    // dictation took the overlay over. The notice of a failed translation is shown after Idle.
    private void OnSelectionPhaseChanged(SelectionTranslationPhase phase) => RunOnUi(() =>
    {
        switch (phase)
        {
            case SelectionTranslationPhase.Preview:
            case SelectionTranslationPhase.Idle:
                CancelOverlayHide();
                if (!_session.IsActive)
                    _overlay.HideOverlay();
                // A preview left busy by a swap that was cancelled or ended must not stay on screen.
                if (phase == SelectionTranslationPhase.Idle)
                    _preview?.Dismiss();
                break;
        }
    });

    private void OnTranslationNotice(TranslationNotice notice) =>
        RunOnUi(() => ShowOverlayStatus(DictationNoticePresentation.For(notice)));

    // "Loading translation…" shows only while the model still has to load, then "Translating…". A selection over
    // the limit comes back at once and never flashes a status.
    private async Task<TranslationResult> TranslateSelectionAsync(string text, CancellationToken cancellationToken)
    {
        bool loading = _translator.WouldWaitForLoad(text);
        TranslationDirection? direction = _translator.DetectLanguage(text).ToDirection();
        Task<TranslationResult> translation = _translator.TranslateAsync(text, cancellationToken: cancellationToken);
        if (!translation.IsCompleted)
        {
            RunOnUi(() => ShowOverlayStatus(loading ? OverlayStatus.LoadingTranslation : OverlayStatus.Translating));
            if (loading && direction is { } loadingDirection)
                _ = ShowTranslatingAfterLoadAsync(
                    translation,
                    () => _translator.IsLoadPending(loadingDirection),
                    () => _selection.IsTranslating,
                    cancellationToken);
        }

        return await translation.ConfigureAwait(true);
    }

    // A swap translates the original selection in the other direction. The preview itself shows "Translating…",
    // so the overlay stays hidden.
    private Task<TranslationResult> TranslateSwappedAsync(
        string text, TranslationDirection direction, CancellationToken cancellationToken) =>
        _translator.TranslateAsync(text, direction, cancellationToken);

    private Task<PreviewChoice> ShowTranslationPreviewAsync(TranslationPreview preview, CancellationToken cancellationToken)
    {
        _preview ??= new TranslationPreviewWindow();
        return _preview.ShowAsync(preview, _selectionTarget, cancellationToken);
    }

    private void OnTranslationFailureObserved(Exception exception) =>
        _ = _log.WriteAsync(TechnicalEvent.TranslationFailed, exception: exception);

    private async Task HandleTaskTranscriptAsync(string transcript, CancellationToken cancellationToken)
        => await HandleTaskTranscriptRawAsync(transcript, transcript, cancellationToken).ConfigureAwait(false);

    private async Task HandleTaskTranscriptRawAsync(string rawTranscript, string transcript, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_taskBoardWindow.HasTaskDraft)
        {
            await RunOnUiAsync(() => ShowOverlayStatus(OverlayStatus.Result("Finish or discard the existing task draft first")));
            return;
        }
        TaskDraftParseResult parsed;
        try
        {
            parsed = TaskDraftParser.Parse(
                transcript,
                _taskBoardWindow.GetTags(),
                DateOnly.FromDateTime(DateTime.Now)) with { OriginalTranscript = rawTranscript };
        }
        catch (Exception exception) when (exception is RegexMatchTimeoutException or ArgumentException)
        {
            System.Diagnostics.Trace.WriteLine($"WinWispr task parser failed: {exception.GetType().Name}");
            parsed = new TaskDraftParseResult(rawTranscript, rawTranscript, "New task", null, null, [], [], []);
        }
        await RunOnUiAsync(() => ShowTaskCapture(parsed)).ConfigureAwait(false);
    }

    private void ResumeTaskDraft()
    {
        if (_taskBoardWindow.GetTaskDraft() is not TaskCaptureDraft draft)
            return;
        ShowTaskCapture(new TaskDraftParseResult(
            draft.OriginalTranscript,
            draft.Description,
            draft.SuggestedTitle,
            draft.SuggestedDueDate,
            draft.UnresolvedDatePhrase,
            draft.SelectedTagIds,
            draft.SuggestedTagIds,
            draft.ExplicitTagNames));
    }

    private void ShowTaskCapture(TaskDraftParseResult parsed)
    {
        TaskCaptureDraft draft = _taskBoardWindow.BeginTaskCapture(parsed);
        if (_taskCaptureWindow is { } existing && existing.DraftId == draft.Id)
        {
            existing.UpdateTags(_taskBoardWindow.GetTags());
            existing.ShowCapture();
            return;
        }
        if (_taskCaptureWindow is { } stale)
            stale.Dispose();

        TaskDraftParseResult current = new(
            draft.OriginalTranscript,
            draft.Description,
            draft.SuggestedTitle,
            draft.SuggestedDueDate,
            draft.UnresolvedDatePhrase,
            draft.SelectedTagIds,
            draft.SuggestedTagIds,
            draft.ExplicitTagNames);
        _taskCaptureWindow = new TaskCaptureWindow(
            draft.Id,
            current,
            _taskBoardWindow.GetTags(),
            _dispatcher,
            (draftId, parsedDraft, title, description, dueDate, tags) =>
            {
                BoardTask? task = _taskBoardWindow.CommitTaskCapture(draftId, parsedDraft, title, description, dueDate, tags);
                if (task is null)
                    return false;
                _taskBoardWindow.ShowTaskSaved(task);
                return true;
            },
            (draftId, title, description, dueDate, tags) =>
                _taskBoardWindow.UpdateTaskCaptureDraft(draftId, title, description, dueDate, tags),
            draftId => _taskBoardWindow.DiscardTaskDraft(draftId));

        _taskCaptureWindow.ShowCapture();
    }

    private void OnSpeechStateChanged(SpeechRuntimeState state, SpeechBackend? backend)
    {
        RunOnUi(() =>
        {
            UpdateSpeechStatus();
            if (_changingSpeechMode)
                return;
            bool startPending = _session.HasPendingStart;
            _ = _session.SetSpeechReadinessAsync(CurrentSpeechReadiness());
            _settingsWindow.SetModelState(MapSpeechRuntimeStateToModelUiState(state));
            if (state == SpeechRuntimeState.Loading)
                ShowOverlayStatus(OverlayStatus.Progress("Loading model"));
            else if (state == SpeechRuntimeState.Ready && !startPending && _session.State == RecordingState.Idle)
                ShowOverlayStatus(OverlayStatus.Result("Ready"));
            else if (state == SpeechRuntimeState.Failed)
                ShowOverlayStatus(OverlayStatus.Result(_speech.RequiresRestart
                    ? "Restart WinWispr"
                    : ModelLoadFailedOverlayText));
        });
    }

    private void OnAudioPeakChanged(float peak)
    {
        lock (_peaks)
        {
            if (_peaks.Count >= WaveformHistoryPeakCount)
                _peaks.Dequeue();
            _peaks.Enqueue(peak);
        }
    }

    private int CopyPeaks()
    {
        lock (_peaks)
        {
            int count = 0;
            foreach (float peak in _peaks)
                _peakSnapshot[count++] = peak;
            return count;
        }
    }

    private void ShowOverlayStatus(OverlayStatus status)
    {
        CancelOverlayHide();
        _overlay.ShowStatus(status);
        _overlay.ShowAt(new WindowTarget(GetForegroundWindow()));
        if (status.Duration is TimeSpan duration)
        {
            CancellationTokenSource cancellation = new();
            _overlayHideCancellation = cancellation;
            _ = HideOverlayAfterDelayAsync(cancellation, duration);
        }
    }

    private void CancelOverlayHide()
    {
        CancellationTokenSource? cancellation = _overlayHideCancellation;
        _overlayHideCancellation = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private async Task HideOverlayAfterDelayAsync(CancellationTokenSource cancellation, TimeSpan duration)
    {
        CancellationToken token = cancellation.Token;
        try
        {
            await Task.Delay(duration, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        RunOnUi(() =>
        {
            if (!ReferenceEquals(_overlayHideCancellation, cancellation))
                return;

            _overlayHideCancellation = null;
            try
            {
                if (!token.IsCancellationRequested && _session.State == RecordingState.Idle)
                    _overlay.HideOverlay();
            }
            finally
            {
                cancellation.Dispose();
            }
        });
    }

    private SettingsSaveResult SaveSettings(AppSettings settings)
    {
        SettingsSaveResult result = AppSettingsStore.Current.Save(settings);
        if (result.Status == SettingsSaveStatus.Saved)
            ApplySettings(settings);
        return result;
    }

    private bool TrySaveSettings(AppSettings settings) => SaveSettings(settings).Status == SettingsSaveStatus.Saved;

    private bool SaveSettingsOrWarn(AppSettings settings)
    {
        SettingsSaveResult result = SaveSettings(settings);
        if (result.Status != SettingsSaveStatus.Saved)
            _settingsWindow.SetSettingsWarning(result.Message);
        return result.Status == SettingsSaveStatus.Saved;
    }

    private void SaveBoardSettings(AppSettings settings)
    {
        if (!TrySaveSettings(settings))
            _taskBoardWindow.ShowWindowPositionSaveFailed();
    }

    // HotkeyRegistration rolls the native registration back when persisting throws.
    private void PersistHotkeySettings(AppSettings settings)
    {
        SettingsSaveResult result = SaveSettings(settings);
        if (result.Status != SettingsSaveStatus.Saved)
            throw new IOException(result.Message);
    }

    private DictionarySaveResult SaveDictionary(string text)
    {
        DictionarySaveResult result = PersonalDictionaryStore.Current.Replace(text);
        if (result.Status == DictionarySaveStatus.Saved)
            _session.UpdateDictionary(PersonalDictionaryStore.Current.Dictionary);
        return result;
    }

    private static string? LoadWarnings()
    {
        string[] warnings = new[] { AppSettingsStore.Current.LoadWarning, PersonalDictionaryStore.Current.LoadWarning }
            .OfType<string>().ToArray();
        return warnings.Length == 0 ? null : string.Join(" ", warnings);
    }

    internal void ApplySettings(AppSettings settings)
    {
        bool speechModeChanged = settings.SpeechMode != _settings.SpeechMode;
        _settings = settings;
        UpdateSessionSettings();
        _taskBoardWindow?.SetTaskButtonMode(_settings.TaskButtonMode);
        if (speechModeChanged)
        {
            _pendingSpeechMode = true;
            UpdateSpeechStatus();
            _ = ApplyPendingSpeechModeAsync();
        }
    }

    private void UpdateSpeechStatus() => _settingsWindow.SetSpeechStatus(
        _speech.StatusText,
        _changingSpeechMode || _speech.State == SpeechRuntimeState.Loading,
        _pendingSpeechMode);

    private async Task ApplyPendingSpeechModeAsync()
    {
        if (!_pendingSpeechMode || _changingSpeechMode || _session.State != RecordingState.Idle
            || _speech.State == SpeechRuntimeState.Loading || _shutdownCancellation.IsCancellationRequested)
            return;

        _pendingSpeechMode = false;
        _changingSpeechMode = true;
        _ = _session.SetSpeechReadinessAsync(SpeechReadiness.Switching);
        UpdateSpeechStatus();
        ShowOverlayStatus(OverlayStatus.Progress("Switching processing device"));
        try
        {
            await _speech.ReloadAsync(_settings.SpeechMode, _shutdownCancellation.Token);
        }
        catch (Exception exception)
        {
            await _log.WriteAsync(TechnicalEvent.ModelLoadFailed, exception: exception);
        }
        finally
        {
            _changingSpeechMode = false;
            _ = _session.SetSpeechReadinessAsync(CurrentSpeechReadiness());
            if (!_shutdownCancellation.IsCancellationRequested)
            {
                UpdateSpeechStatus();
                _settingsWindow.SetModelState(MapSpeechRuntimeStateToModelUiState(_speech.State));
                ShowOverlayStatus(OverlayStatus.Result(_speech.State switch
                {
                    SpeechRuntimeState.Ready => "Ready",
                    SpeechRuntimeState.Missing => "Model unavailable",
                    _ => ModelLoadFailedOverlayText
                }));
            }
        }
    }

    private void UpdateSessionSettings() => _session.UpdateSettings(_settings);

    private bool TryAcquireSingleInstance()
    {
        string name = AppDataPaths.IsTaskBoardE2e
            ? $@"Local\WinWispr-E2E-{AppDataPaths.E2eInstanceId}"
            : @"Local\WinWispr";
        _singleInstance = new Mutex(true, name, out bool createdNew);
        if (createdNew)
            return true;

        _singleInstance.Dispose();
        _singleInstance = null;
        return false;
    }

    private void StartE2eExitListener()
    {
        _e2eExitEvent = EventWaitHandle.OpenExisting(AppDataPaths.E2eExitEventName);
        _e2eExitRegistration = ThreadPool.RegisterWaitForSingleObject(
            _e2eExitEvent,
            (_, timedOut) =>
            {
                if (!timedOut)
                    _dispatcher.TryEnqueue(() => Shutdown());
            },
            state: null,
            millisecondsTimeOutInterval: Timeout.Infinite,
            executeOnlyOnce: true);
    }

    private Task RunOnUiAsync(Action action) => _dispatcher.InvokeAsync(action);

    private void RunOnUi(Action action)
    {
        if (_dispatcher.HasThreadAccess)
            action();
        else
            _dispatcher.TryEnqueue(() => action());
    }

    /// <summary>
    /// Closes everything and exits. <paramref name="beforeExit"/> runs last, after the single-instance lock is gone.
    /// </summary>
    private async void Shutdown(Action? beforeExit = null)
    {
        if (_shutdownCancellation.IsCancellationRequested)
            return;
        if (!_taskBoardWindow.TryPrepareForShutdown())
        {
            _taskBoardWindow.ActivateBoard();
            ShowOverlayStatus(OverlayStatus.Result(
                "Task changes could not be saved. Retry or keep WinWispr open.", detail: null, duration: null));
            return;
        }
        _shutdownCancellation.Cancel();
        CancelHotkeyCapture();
        _downloadCancellation?.Cancel();
        CancelOverlayHide();
        _waveformTimer.Stop();
        _session.Dispose();
        _selection.Dispose();
        bool audioDiscarded = await CompleteWithinAsync(
            _session.ReleaseAsync(),
            TimeSpan.FromSeconds(5));
        _globalInput.Dispose();
        _tray.Dispose();
        _overlay.Close();
        _preview?.CloseForShutdown();
        _taskCaptureWindow?.Dispose();
        _taskBoardWindow.Close();
        _settingsWindow.Close();
        if (audioDiscarded)
            await CompleteWithinAsync(Task.Run(_audio.Dispose), TimeSpan.FromSeconds(5));
        if (_tts is not null)
            await CompleteWithinAsync(_tts.UnloadAsync(), TimeSpan.FromSeconds(6));
        await CompleteWithinAsync(Task.Run(_translator.Dispose), TimeSpan.FromSeconds(6));
        await CompleteWithinAsync(Task.Run(_speech.Dispose), TimeSpan.FromSeconds(6));
        _e2eExitRegistration?.Unregister(null);
        _e2eExitRegistration = null;
        _e2eExitEvent?.Dispose();
        _e2eExitEvent = null;
        _singleInstance?.Dispose();
        _singleInstance = null;
        beforeExit?.Invoke();
        Environment.Exit(0);
    }

    private static async Task<bool> CompleteWithinAsync(Task operation, TimeSpan timeout)
    {
        try
        {
            await operation.WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    internal static ModelUiState MapSpeechRuntimeStateToModelUiState(SpeechRuntimeState state) => state switch
    {
        SpeechRuntimeState.Loading => ModelUiState.Loading,
        SpeechRuntimeState.Ready => ModelUiState.Ready,
        SpeechRuntimeState.Missing => ModelUiState.Unavailable,
        SpeechRuntimeState.Failed => ModelUiState.InstallationFailed,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
    };
}
