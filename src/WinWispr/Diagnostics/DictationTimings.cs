using WinWispr.Audio;
using WinWispr.Core;
using WinWispr.Speech;

namespace WinWispr.Diagnostics;

/// <summary>
/// Writes the dictation's timing and failure entries to the technical log. <see cref="Wrap"/> measures
/// the session's stop, recognition and delivery effects; <see cref="OnStateChanged"/> measures capture
/// preparation; adapters that time their own inner steps report them through <see cref="Record"/>.
/// </summary>
internal sealed class DictationTimings
{
    private readonly TechnicalLog _log;
    private readonly TimeProvider _clock;
    private long _preparationStarted;

    internal DictationTimings(TechnicalLog log, TimeProvider clock)
    {
        _log = log;
        _clock = clock;
    }

    /// <summary>Returns <paramref name="inner"/> with stop, recognition and delivery measured.</summary>
    internal DictationEffects Wrap(DictationEffects inner) => inner with
    {
        StopCaptureAsync = cancellationToken => StopCaptureAsync(inner.StopCaptureAsync, cancellationToken),
        RecognizeAsync = (samples, cancellationToken) => RecognizeAsync(inner.RecognizeAsync, samples, cancellationToken),
        DeliverAsync = (target, text, cancellationToken) => DeliverAsync(inner.DeliverAsync, target, text, cancellationToken)
    };

    internal void Record(TechnicalEvent phase, double milliseconds) =>
        _ = _log.WriteAsync(phase, durationMilliseconds: milliseconds, occurredAtUtc: _clock.GetUtcNow());

    // Preparation is the whole way from Starting (meeting mute plus device start) to a live capture
    // (Recording) or a failed one (Idle).
    internal void OnStateChanged(RecordingState state)
    {
        if (state == RecordingState.Starting)
        {
            Interlocked.Exchange(ref _preparationStarted, _clock.GetTimestamp());
            return;
        }

        if (state is not (RecordingState.Recording or RecordingState.Idle))
            return;
        long started = Interlocked.Exchange(ref _preparationStarted, 0);
        if (started != 0)
            Record(TechnicalEvent.CapturePreparation, Elapsed(started));
    }

    private async Task<CapturedAudio> StopCaptureAsync(
        Func<CancellationToken, Task<CapturedAudio>> inner,
        CancellationToken cancellationToken)
    {
        Mark(TechnicalEvent.CaptureStopStarted);
        long started = _clock.GetTimestamp();
        try { return await inner(cancellationToken).ConfigureAwait(false); }
        finally { Record(TechnicalEvent.CaptureStopCompleted, Elapsed(started)); }
    }

    private async Task<string> RecognizeAsync(
        Func<CapturedAudio, CancellationToken, Task<string>> inner,
        CapturedAudio samples,
        CancellationToken cancellationToken)
    {
        Mark(TechnicalEvent.RecognitionStarted);
        long started = _clock.GetTimestamp();
        try { return await inner(samples, cancellationToken).ConfigureAwait(false); }
        catch (SpeechRecognitionTimeoutException exception)
        {
            _ = _log.WriteAsync(
                TechnicalEvent.RecognitionTimedOut,
                recordingState: RecordingState.Processing,
                exception: exception);
            throw;
        }
        catch (Exception exception)
        {
            _ = _log.WriteAsync(
                TechnicalEvent.RecognitionFailed,
                recordingState: RecordingState.Processing,
                exception: exception);
            throw;
        }
        finally { Record(TechnicalEvent.RecognitionCompleted, Elapsed(started)); }
    }

    private async Task<DeliveryResult> DeliverAsync(
        Func<WindowTarget, string, CancellationToken, Task<DeliveryResult>> inner,
        WindowTarget target,
        string text,
        CancellationToken cancellationToken)
    {
        Mark(TechnicalEvent.DeliveryStarted);
        long started = _clock.GetTimestamp();
        DeliveryTrace trace = DeliveryTrace.Begin();
        DeliveryResult? result = null;
        try
        {
            result = await inner(target, text, cancellationToken).ConfigureAwait(false);
            return result.Value;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _ = _log.WriteAsync(
                TechnicalEvent.ClipboardUnavailable,
                recordingState: RecordingState.Processing,
                exception: exception);
            throw;
        }
        finally
        {
            _ = WriteDeliveryCompletedAsync(trace, result, Elapsed(started), _clock.GetUtcNow());
        }
    }

    // After a Ctrl+V the trace watches the clipboard for about a second more, so the entry is written once that is
    // over, still stamped with the moment the delivery completed.
    private async Task WriteDeliveryCompletedAsync(
        DeliveryTrace trace,
        DeliveryResult? result,
        double milliseconds,
        DateTimeOffset completedAt)
    {
        IReadOnlyList<ClipboardHold>? holds = null;
        if (trace.ClipboardHolds is { } watch)
        {
            try
            {
                holds = await watch.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // The entry is still written, without the holds.
            }
        }

        _ = _log.WriteAsync(
            TechnicalEvent.DeliveryCompleted,
            durationMilliseconds: milliseconds,
            occurredAtUtc: completedAt,
            deliveryResult: result,
            delivery: trace.ToDiagnostics(holds));
    }

    private void Mark(TechnicalEvent phase) =>
        _ = _log.WriteAsync(phase, recordingState: RecordingState.Processing, occurredAtUtc: _clock.GetUtcNow());

    private double Elapsed(long started) => _clock.GetElapsedTime(started).TotalMilliseconds;
}
