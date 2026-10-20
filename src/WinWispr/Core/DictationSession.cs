using WinWispr.Audio;
using WinWispr.Speech;
using WinWispr.Translation;

namespace WinWispr.Core;

internal enum RecordingState
{
    Idle,
    Starting,
    Recording,
    CancelPending,
    Processing
}

internal enum StopReason
{
    User,
    Limit
}

internal enum DictationDestination
{
    Text,
    Task
}

internal readonly record struct WindowTarget(nint Hwnd);

internal enum DeliveryResult
{
    PasteSent,
    CopiedToClipboard,
    NoSpeech,

    /// <summary>The target's menu holds the keyboard, so no paste was sent; the text is on the clipboard.</summary>
    TargetMenuOpen
}

/// <summary>What a dictation notice reports. The visible text lives in DictationNoticePresentation.</summary>
internal enum DictationNoticeKind
{
    MicrophoneUnavailable,
    RecordingLimitReached,
    TranscriptionTimedOut,
    TranscriptionFailed,
    NoSpeech,
    CopiedToClipboard,

    /// <summary>The target window was in menu mode; the text is on the clipboard and nothing was pasted.</summary>
    TargetMenuOpen,
    ClipboardUnavailable,
    NothingToReplay,
    TaskReady,
    TaskCaptureFailed,
    TaskCaptureUnavailable,
    TeamsMicrophone,
    MicrophoneFallback,

    /// <summary>The English dictation hotkey was pressed while the translation model is not installed. Nothing records.</summary>
    TranslationNotInstalled,

    /// <summary>The transcript could not be translated; the German transcript is on the clipboard and nothing was pasted.</summary>
    TranslationFailedTranscriptCopied
}

/// <summary>An outcome or problem reported to the user. Only TeamsMicrophone carries a Detail.</summary>
internal readonly record struct DictationNotice(DictationNoticeKind Kind, string? Detail = null);

/// <summary>
/// The meeting microphone could not be muted, or its state could not be confirmed. Capture must not
/// start. The message is shown to the user as the notice detail.
/// </summary>
internal sealed class MeetingMuteException(string message) : Exception(message);

internal enum DictationTrigger
{
    PrimaryHotkey,
    TaskHotkey,
    TaskButton,
    Tray,

    /// <summary>Starts a dictation translation: the transcript is translated into English before delivery.</summary>
    EnglishDictationHotkey
}

internal enum SpeechReadiness
{
    Ready,
    Loading,
    Switching,
    Missing,
    Failed
}

internal sealed record DictationEffects(
    Func<string?, CancellationToken, Task<DictationNotice?>> StartCaptureAsync,
    Func<CancellationToken, Task> PauseCaptureAsync,
    Func<CancellationToken, Task> ResumeCaptureAsync,
    Func<CancellationToken, Task<CapturedAudio>> StopCaptureAsync,
    Func<Task> DiscardCaptureAsync,
    Func<CapturedAudio, CancellationToken, Task<string>> RecognizeAsync,
    Func<WindowTarget, string, CancellationToken, Task<DeliveryResult>> DeliverAsync)
{
    internal Func<string, CancellationToken, Task>? TaskCapturedAsync { get; init; }
    internal Func<string, string, CancellationToken, Task>? TaskCapturedRawAsync { get; init; }
    internal Func<bool>? TryResumeTaskDraft { get; init; }
    internal Func<WindowTarget>? CurrentForegroundWindow { get; init; }

    /// <summary>
    /// Mutes the user's meeting microphone before capture starts, so the meeting does not hear the
    /// dictation. Throws <see cref="MeetingMuteException"/> when the mute cannot be confirmed.
    /// </summary>
    internal Func<CancellationToken, Task>? MuteMeetingAsync { get; init; }

    /// <summary>
    /// Gives back what <see cref="MuteMeetingAsync"/> muted. Safe to call at any time: it does nothing
    /// when no mute is owned. Returns a warning when the restore could not be confirmed.
    /// </summary>
    internal Func<Task<DictationNotice?>>? RestoreMeetingAsync { get; init; }

    /// <summary>
    /// Whether the translation model is installed. Asked when the English dictation hotkey is pressed; without
    /// this effect (or when it says no) the press reports <see cref="DictationNoticeKind.TranslationNotInstalled"/>
    /// and nothing records.
    /// </summary>
    internal Func<bool>? IsTranslationInstalled { get; init; }

    /// <summary>
    /// Starts loading the German to English model in the background. Called once when an English dictation
    /// press is accepted, so that the load overlaps the recording. It must return at once and never throw.
    /// </summary>
    internal Action? PreloadTranslation { get; init; }

    /// <summary>
    /// Translates a transcript into English (text that is already English comes back unchanged). Dictation has
    /// no length limit. Any result other than a translation, or an exception, is a failed translation.
    /// </summary>
    internal Func<string, CancellationToken, Task<TranslationResult>>? TranslateToEnglishAsync { get; init; }

    /// <summary>Puts a transcript on the clipboard without pasting it. Throws when the clipboard is unusable.</summary>
    internal Func<string, CancellationToken, Task>? CopyTranscriptAsync { get; init; }
}

/// <summary>
/// Owns one dictation session from the accepting trigger press to Idle: the owner trigger,
/// its recording mode and destination, the target window, and a start deferred while the
/// speech model loads. Callers report trigger presses and releases; the session decides
/// what they mean. It also orders the meeting mute around the capture: muted before the capture
/// starts, restored as soon as the capture stops or is abandoned.
/// </summary>
internal sealed class DictationSession : IDisposable
{
    private static readonly TimeSpan CancelWindow = TimeSpan.FromSeconds(5);
    private AppSettings _settings;
    private PersonalDictionary _dictionary = PersonalDictionary.Empty;
    private readonly DictationEffects _effects;
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly object _stateGate = new();
    private CancellationTokenSource? _cancelWindowCancellation;
    private RecordingState _state = RecordingState.Idle;
    private DateTimeOffset? _cancelDeadline;
    private WindowTarget _target;
    private bool _hasTarget;
    private DictationDestination _destination = DictationDestination.Text;
    private SpeechReadiness _readiness;
    // Ownership is claimed and released synchronously under _stateGate, before any await,
    // so a release that is processed before its start reaches the command gate still wins.
    private DictationTrigger? _owner;
    private RecordingMode _ownerMode;
    private bool _held;
    private bool _pendingStart;
    private bool _disposed;
    private DictationNotice? _startNotice;
    private string? _lastDictation;

    internal DictationSession(AppSettings settings, DictationEffects effects, SpeechReadiness readiness = SpeechReadiness.Ready)
    {
        _settings = settings;
        _effects = effects;
        _readiness = readiness;
    }

    internal RecordingState State
    {
        get
        {
            lock (_stateGate)
                return _state;
        }
    }

    /// <summary>
    /// True from an accepted trigger press to the return to Idle, including a pending start while the speech model
    /// loads (when <see cref="State"/> is still Idle) and processing after the owner has released. Other
    /// features, such as selection translation, stay out of the way while it is true.
    /// </summary>
    internal bool IsActive
    {
        get
        {
            lock (_stateGate)
                return _owner is not null || _state != RecordingState.Idle;
        }
    }

    internal void UpdateSettings(AppSettings settings) => _settings = settings;

    internal void UpdateDictionary(PersonalDictionary dictionary) => _dictionary = dictionary;

    internal DateTimeOffset? CancelDeadline
    {
        get
        {
            lock (_stateGate)
                return _cancelDeadline;
        }
    }

    internal WindowTarget Target
    {
        get
        {
            lock (_stateGate)
                return _target;
        }
    }

    internal bool HasPendingStart
    {
        get
        {
            lock (_stateGate)
                return _pendingStart;
        }
    }

    internal event Action<RecordingState>? StateChanged;
    internal event Action<DictationNotice>? Notice;
    internal event Action<SpeechReadiness>? StartBlocked;

    internal async Task PressAsync(
        DictationTrigger trigger,
        WindowTarget? target = null,
        CancellationToken cancellationToken = default)
    {
        bool stop;
        SpeechReadiness readiness;
        lock (_stateGate)
        {
            if (_disposed)
                return;
            if (_owner is DictationTrigger owner)
            {
                // Only the owner (in Toggle) and the tray may end an owned session.
                if (trigger != DictationTrigger.Tray && (trigger != owner || _ownerMode != RecordingMode.Toggle))
                    return;
                stop = true;
            }
            else if (_state != RecordingState.Idle)
                return;
            else
                stop = false;
            readiness = _readiness;
        }

        if (stop)
        {
            await StopAsync(StopReason.User, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (trigger == DictationTrigger.EnglishDictationHotkey && !(_effects.IsTranslationInstalled?.Invoke() ?? false))
        {
            // A dictation that could not be translated is never recorded.
            PublishNotice(new DictationNotice(DictationNoticeKind.TranslationNotInstalled));
            return;
        }

        DictationDestination destination = DestinationFor(trigger);
        if (destination == DictationDestination.Task && (_effects.TryResumeTaskDraft?.Invoke() ?? false))
            return;
        if (readiness is not (SpeechReadiness.Ready or SpeechReadiness.Loading))
        {
            StartBlocked?.Invoke(readiness);
            return;
        }

        WindowTarget resolvedTarget = target ?? _effects.CurrentForegroundWindow?.Invoke() ?? default;
        lock (_stateGate)
        {
            if (_disposed || _owner is not null || _state != RecordingState.Idle)
                return;
            _owner = trigger;
            _ownerMode = ModeFor(trigger);
            _held = true;
            _destination = destination;
            _target = resolvedTarget;
            _hasTarget = true;
            _pendingStart = readiness == SpeechReadiness.Loading;
        }

        if (trigger == DictationTrigger.EnglishDictationHotkey)
            PreloadTranslationQuietly();

        if (readiness == SpeechReadiness.Loading)
        {
            StartBlocked?.Invoke(SpeechReadiness.Loading);
            return;
        }
        await StartOwnedAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task ReleaseAsync(DictationTrigger trigger, CancellationToken cancellationToken = default)
    {
        lock (_stateGate)
        {
            if (_disposed || _owner != trigger || _ownerMode != RecordingMode.Hold || !_held)
                return;
            _held = false;
            if (_pendingStart)
            {
                ClearOwnershipLocked();
                return;
            }
        }
        await StopAsync(StopReason.User, cancellationToken).ConfigureAwait(false);
    }

    internal Task SetSpeechReadinessAsync(SpeechReadiness readiness)
    {
        bool start = false;
        lock (_stateGate)
        {
            _readiness = readiness;
            if (_pendingStart && readiness == SpeechReadiness.Ready)
            {
                _pendingStart = false;
                start = true;
            }
            else if (_pendingStart && readiness != SpeechReadiness.Loading)
                ClearOwnershipLocked();
        }
        return start ? StartOwnedAsync(CancellationToken.None) : Task.CompletedTask;
    }

    private async Task StartOwnedAsync(CancellationToken cancellationToken)
    {
        using var startCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _disposeCancellation.Token);
        DictationNotice? notice = null;
        try
        {
            await _commandGate.WaitAsync(startCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            lock (_stateGate)
            {
                if (_state == RecordingState.Idle && !_pendingStart)
                    ClearOwnershipLocked();
            }
            return;
        }
        try
        {
            lock (_stateGate)
            {
                if (_disposed || _state != RecordingState.Idle || _owner is null || _pendingStart)
                    return;
                if (_ownerMode == RecordingMode.Hold && !_held)
                {
                    // The trigger was released before the start reached the gate.
                    ClearOwnershipLocked();
                    return;
                }
            }
            SetState(RecordingState.Starting);
            try
            {
                if (_effects.MuteMeetingAsync is { } muteMeeting)
                    await muteMeeting(startCancellation.Token).ConfigureAwait(false);
                startCancellation.Token.ThrowIfCancellationRequested();
                _startNotice = await _effects.StartCaptureAsync(_settings.MicrophoneId, startCancellation.Token).ConfigureAwait(false);
                startCancellation.Token.ThrowIfCancellationRequested();
                SetState(RecordingState.Recording);
            }
            catch (Exception error)
            {
                _startNotice = null;
                await AbandonCaptureQuietlyAsync().ConfigureAwait(false);
                SetState(RecordingState.Idle);
                if (!startCancellation.IsCancellationRequested)
                    notice = error is MeetingMuteException
                        ? new DictationNotice(DictationNoticeKind.TeamsMicrophone, error.Message)
                        : new DictationNotice(DictationNoticeKind.MicrophoneUnavailable);
            }
        }
        finally
        {
            _commandGate.Release();
        }
        if (notice is DictationNotice published)
            PublishNotice(published);
    }

    internal async Task RequestCancelAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? oldCancellation = null;
        CancellationTokenSource? newCancellation = null;
        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed || State != RecordingState.Recording)
                return;

            oldCancellation = _cancelWindowCancellation;
            newCancellation = new CancellationTokenSource();
            _cancelWindowCancellation = newCancellation;
            lock (_stateGate)
                _cancelDeadline = DateTimeOffset.UtcNow.Add(CancelWindow);
            SetState(RecordingState.CancelPending);
        }
        finally
        {
            _commandGate.Release();
            oldCancellation?.Cancel();
        }

        try
        {
            await _effects.PauseCaptureAsync(cancellationToken).ConfigureAwait(false);
            if (State == RecordingState.CancelPending)
                _ = ResumeAfterTimeoutAsync(newCancellation!);
            else
                newCancellation!.Dispose();
        }
        catch
        {
            newCancellation!.Cancel();
            await AbandonCaptureQuietlyAsync().ConfigureAwait(false);
            await ReturnToIdleAsync(new DictationNotice(DictationNoticeKind.MicrophoneUnavailable)).ConfigureAwait(false);
            newCancellation!.Dispose();
        }
    }

    internal async Task ConfirmDiscardAsync(CancellationToken cancellationToken)
    {
        DictationNotice? startNotice = null;
        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed || State != RecordingState.CancelPending)
                return;
            TakeCancelWindowCancellation()?.Cancel();
            await AbandonCaptureQuietlyAsync().ConfigureAwait(false);
            SetState(RecordingState.Idle);
            startNotice = TakeStartNotice();
        }
        finally
        {
            _commandGate.Release();
        }

        if (startNotice is DictationNotice published)
            PublishNotice(published);
    }

    internal async Task CancelTimeoutAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? cancellation = null;
        bool shouldResume;
        bool shouldStop;
        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            shouldStop = !_disposed && State == RecordingState.CancelPending && IsHoldReleased();
            shouldResume = !_disposed && State == RecordingState.CancelPending && !shouldStop;
            if (shouldStop || shouldResume)
            {
                cancellation = TakeCancelWindowCancellation();
                SetState(RecordingState.Recording);
            }
        }
        finally
        {
            _commandGate.Release();
            cancellation?.Cancel();
        }

        if (shouldStop)
        {
            await StopAsync(StopReason.User, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (!shouldResume)
            return;

        try
        {
            await _effects.ResumeCaptureAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await AbandonCaptureQuietlyAsync().ConfigureAwait(false);
            await ReturnToIdleAsync(new DictationNotice(DictationNoticeKind.MicrophoneUnavailable)).ConfigureAwait(false);
        }
    }

    internal async Task StopAsync(StopReason reason, CancellationToken cancellationToken = default)
    {
        lock (_stateGate)
        {
            if (_pendingStart)
            {
                ClearOwnershipLocked();
                return;
            }
        }

        using CancellationTokenSource terminalCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposeCancellation.Token);
        CancellationToken terminalToken = terminalCancellation.Token;
        WindowTarget target;
        DictationDestination destination;
        bool translate;
        await _commandGate.WaitAsync(terminalToken).ConfigureAwait(false);
        try
        {
            if (_disposed || State != RecordingState.Recording || !_hasTarget)
                return;
            target = _target;
            // The destination is owned by the accepted start. A late stop event
            // must not be able to reroute task text into normal output.
            destination = _destination;
            translate = _owner == DictationTrigger.EnglishDictationHotkey;
            SetState(RecordingState.Processing);
        }
        finally
        {
            _commandGate.Release();
        }

        if (reason == StopReason.Limit)
            PublishNotice(new DictationNotice(DictationNoticeKind.RecordingLimitReached));

        DictationNotice? notice = null;
        try
        {
            CapturedAudio samples;
            try
            {
                samples = await _effects.StopCaptureAsync(terminalToken).ConfigureAwait(false);
            }
            catch
            {
                await AbandonCaptureQuietlyAsync().ConfigureAwait(false);
                notice = new DictationNotice(DictationNoticeKind.MicrophoneUnavailable);
                return;
            }

            // The meeting is free again before the model starts working.
            await RestoreMeetingQuietlyAsync().ConfigureAwait(false);

            using (samples)
            {
                string rawText;
                string text;
                try
                {
                    rawText = await _effects.RecognizeAsync(samples, terminalToken).ConfigureAwait(false);
                    text = DictationTextProcessor.Process(rawText, _settings, _dictionary);
                }
                catch (SpeechRecognitionTimeoutException)
                {
                    notice = new DictationNotice(DictationNoticeKind.TranscriptionTimedOut);
                    return;
                }
                catch
                {
                    notice = new DictationNotice(DictationNoticeKind.TranscriptionFailed);
                    return;
                }

                if (text.Length == 0)
                {
                    notice = new DictationNotice(DictationNoticeKind.NoSpeech);
                    return;
                }

                try
                {
                    terminalToken.ThrowIfCancellationRequested();
                    if (destination == DictationDestination.Task)
                    {
                        if (_effects.TaskCapturedAsync is null && _effects.TaskCapturedRawAsync is null)
                        {
                            notice = new DictationNotice(DictationNoticeKind.TaskCaptureUnavailable);
                            return;
                        }
                        notice = await CaptureTaskAsync(rawText, text, terminalToken).ConfigureAwait(false);
                    }
                    else
                    {
                        string delivered = text;
                        if (translate)
                        {
                            TranslationStep step = await TranslateTranscriptAsync(text, terminalToken).ConfigureAwait(false);
                            if (step.Abandoned)
                                return;
                            if (step.Text is null)
                            {
                                notice = step.Notice;
                                return;
                            }
                            delivered = step.Text;
                        }
                        lock (_stateGate)
                        {
                            if (_disposed)
                                return;
                            _lastDictation = delivered;
                        }
                        notice = await DeliverTextAsync(target, delivered, terminalToken).ConfigureAwait(false);
                    }
                }
                catch
                {
                    notice = new DictationNotice(DictationNoticeKind.ClipboardUnavailable);
                }
            }
        }
        finally
        {
            await ReturnToIdleAsync(notice).ConfigureAwait(false);
        }
    }

    internal async Task ReplayLastAsync(WindowTarget target, CancellationToken cancellationToken)
    {
        string? text;
        await _commandGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_stateGate)
            {
                if (_disposed || _state != RecordingState.Idle || _owner is not null)
                    return;
                text = _lastDictation;
            }
            if (text is null)
            {
                PublishNotice(new DictationNotice(DictationNoticeKind.NothingToReplay));
                return;
            }
            SetState(RecordingState.Processing);
        }
        finally
        {
            _commandGate.Release();
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _disposeCancellation.Token);
        DictationNotice? notice = null;
        try
        {
            notice = await DeliverTextAsync(target, text, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
        }
        catch
        {
            notice = new DictationNotice(DictationNoticeKind.ClipboardUnavailable);
        }
        finally
        {
            await ReturnToIdleAsync(notice).ConfigureAwait(false);
        }
    }

    /// <summary>What the translation step leaves for delivery: the English text, or the notice that ends the session.</summary>
    private readonly record struct TranslationStep(string? Text, DictationNotice? Notice, bool Abandoned = false);

    /// <summary>
    /// Translates the transcript after the dictionary has been applied. Once recognition is done nothing cancels
    /// it but shutdown, so what the user said is never lost: when the translation does not produce text, the
    /// German transcript goes to the clipboard instead and nothing is pasted.
    /// </summary>
    private async Task<TranslationStep> TranslateTranscriptAsync(string transcript, CancellationToken cancellationToken)
    {
        try
        {
            if (_effects.TranslateToEnglishAsync is { } translate
                && await translate(transcript, cancellationToken).ConfigureAwait(false)
                    is TranslationResult.Translated { Text: var english }
                && !string.IsNullOrWhiteSpace(english))
                return new TranslationStep(english, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new TranslationStep(null, null, Abandoned: true);
        }
        catch
        {
            // A throwing translator is a failed translation like any other.
        }

        try
        {
            if (_effects.CopyTranscriptAsync is not { } copy)
                throw new InvalidOperationException("No clipboard effect.");
            await copy(transcript, cancellationToken).ConfigureAwait(false);
            return new TranslationStep(null, new DictationNotice(DictationNoticeKind.TranslationFailedTranscriptCopied));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new TranslationStep(null, null, Abandoned: true);
        }
        catch
        {
            return new TranslationStep(null, new DictationNotice(DictationNoticeKind.ClipboardUnavailable));
        }
    }

    private void PreloadTranslationQuietly()
    {
        try
        {
            _effects.PreloadTranslation?.Invoke();
        }
        catch
        {
            // A preload is only an optimization: the translation itself loads on demand.
        }
    }

    /// <summary>A sent paste is the normal outcome and stays silent; Windows cannot confirm it anyway.</summary>
    private async Task<DictationNotice?> DeliverTextAsync(WindowTarget target, string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DeliveryResult result = await _effects.DeliverAsync(target, text, cancellationToken).ConfigureAwait(false);
        return result switch
        {
            DeliveryResult.PasteSent => null,
            DeliveryResult.CopiedToClipboard => new DictationNotice(DictationNoticeKind.CopiedToClipboard),
            DeliveryResult.TargetMenuOpen => new DictationNotice(DictationNoticeKind.TargetMenuOpen),
            _ => new DictationNotice(DictationNoticeKind.NoSpeech)
        };
    }

    private async Task<DictationNotice> CaptureTaskAsync(string rawText, string text, CancellationToken cancellationToken)
    {
        try
        {
            if (_effects.TaskCapturedRawAsync is not null)
                await _effects.TaskCapturedRawAsync(rawText, text, cancellationToken).ConfigureAwait(false);
            else
                await _effects.TaskCapturedAsync!(text, cancellationToken).ConfigureAwait(false);
            return new DictationNotice(DictationNoticeKind.TaskReady);
        }
        catch
        {
            return new DictationNotice(DictationNoticeKind.TaskCaptureFailed);
        }
    }

    /// <summary>
    /// Shutdown: discards a live capture and gives the meeting back. Safe in any state and at any
    /// time, including after <see cref="Dispose"/>.
    /// </summary>
    internal Task ReleaseAsync() => AbandonCaptureQuietlyAsync();

    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        lock (_stateGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            _lastDictation = null;
            _state = RecordingState.Idle;
            _cancelDeadline = null;
            cancellation = _cancelWindowCancellation;
            _cancelWindowCancellation = null;
            ClearOwnershipLocked();
        }

        cancellation?.Cancel();
        _disposeCancellation.Cancel();
    }

    private async Task ResumeAfterTimeoutAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(CancelWindow, TimeProvider.System, cancellation.Token).ConfigureAwait(false);
            await CancelTimeoutAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private async Task ReturnToIdleAsync(DictationNotice? notice = null)
    {
        CancellationTokenSource? cancellation = null;
        await _commandGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            cancellation = TakeCancelWindowCancellation();
            SetState(RecordingState.Idle);
            // An outcome of the session replaces what its start reported.
            DictationNotice? startNotice = TakeStartNotice();
            notice ??= startNotice;
        }
        finally
        {
            _commandGate.Release();
            cancellation?.Cancel();
        }

        if (notice is DictationNotice published)
            PublishNotice(published);
    }

    // Every path that ends a capture without delivering it: discard the audio, then give the meeting
    // back. Both steps are quiet and safe to repeat.
    private async Task AbandonCaptureQuietlyAsync()
    {
        try
        {
            await _effects.DiscardCaptureAsync().ConfigureAwait(false);
        }
        catch
        {
        }

        await RestoreMeetingQuietlyAsync().ConfigureAwait(false);
    }

    private async Task RestoreMeetingQuietlyAsync()
    {
        if (_effects.RestoreMeetingAsync is not { } restoreMeeting)
            return;
        try
        {
            if (await restoreMeeting().ConfigureAwait(false) is DictationNotice warning)
                PublishNotice(warning);
        }
        catch
        {
        }
    }

    private CancellationTokenSource? TakeCancelWindowCancellation()
    {
        CancellationTokenSource? cancellation = _cancelWindowCancellation;
        _cancelWindowCancellation = null;
        lock (_stateGate)
            _cancelDeadline = null;
        return cancellation;
    }

    private RecordingMode ModeFor(DictationTrigger trigger) => trigger switch
    {
        DictationTrigger.PrimaryHotkey => _settings.Mode,
        DictationTrigger.TaskHotkey => _settings.TaskHotkeyMode,
        DictationTrigger.TaskButton => _settings.TaskButtonMode,
        DictationTrigger.EnglishDictationHotkey => _settings.EnglishDictationMode,
        _ => RecordingMode.Toggle
    };

    private static DictationDestination DestinationFor(DictationTrigger trigger) =>
        trigger is DictationTrigger.TaskHotkey or DictationTrigger.TaskButton
            ? DictationDestination.Task
            : DictationDestination.Text;

    private bool IsHoldReleased()
    {
        lock (_stateGate)
            return _owner is not null && _ownerMode == RecordingMode.Hold && !_held;
    }

    private void ClearOwnershipLocked()
    {
        _owner = null;
        _held = false;
        _pendingStart = false;
        _hasTarget = false;
        _target = default;
        _destination = DictationDestination.Text;
    }

    private void SetState(RecordingState state)
    {
        Action<RecordingState>? stateChanged;
        lock (_stateGate)
        {
            _state = state;
            if (state == RecordingState.Idle)
                ClearOwnershipLocked();
            stateChanged = StateChanged;
        }

        stateChanged?.Invoke(state);
    }

    private void PublishNotice(DictationNotice notice) => Notice?.Invoke(notice);

    // What the capture start reported, kept until the session ends. Read and written under the command gate.
    private DictationNotice? TakeStartNotice()
    {
        DictationNotice? notice = _startNotice;
        _startNotice = null;
        return notice;
    }
}
