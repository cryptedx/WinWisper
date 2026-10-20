using WinWispr.Diagnostics;
using WinWispr.Translation;

namespace WinWispr.Core;

/// <summary>What a translation notice reports. The visible text lives in DictationNoticePresentation.</summary>
internal enum TranslationNoticeKind
{
    /// <summary>The translate hotkey was pressed with nothing selected (or only blanks).</summary>
    NoTextSelected,

    /// <summary>The tray's Translate clipboard was chosen while the clipboard is empty or holds no text.</summary>
    ClipboardHasNoText,

    /// <summary>The selection is longer than the limit that translates within three seconds.</summary>
    SelectionTooLong,

    /// <summary>The translation model is not installed (or incomplete). Nothing was read or translated.</summary>
    TranslationNotInstalled,

    /// <summary>The selection could not be read or translated, or the translation timed out.</summary>
    TranslationFailed,

    /// <summary>The translation could not be put on the clipboard.</summary>
    ClipboardUnavailable,

    /// <summary>
    /// The translation is on the clipboard but was not pasted: the original window is gone or could not be
    /// reactivated, or the paste could not be sent.
    /// </summary>
    CopiedToClipboard
}

/// <summary>
/// An outcome or problem that a selection translation reports to the user. <paramref name="Limit"/> is the
/// selection limit and is set only for <see cref="TranslationNoticeKind.SelectionTooLong"/>.
/// </summary>
internal readonly record struct TranslationNotice(TranslationNoticeKind Kind, int? Limit = null);

/// <summary>Where a selection translation stands. The app shows the overlay and the preview from it.</summary>
internal enum SelectionTranslationPhase
{
    Idle,

    /// <summary>The selection is being copied from the other application.</summary>
    Reading,

    /// <summary>The translation runs (or waits for its model to load). Esc cancels it.</summary>
    Translating,

    /// <summary>The translation preview is open and awaits the user's choice.</summary>
    Preview
}

/// <summary>What the user chose in the translation preview.</summary>
internal enum PreviewChoice
{
    /// <summary>
    /// Close the preview, reactivate the window the selection came from and paste the translation in place of the
    /// selection (Enter or the Replace button).
    /// </summary>
    Replace,

    /// <summary>
    /// Translate the original selection again in the opposite direction (the swap button). The preview stays open
    /// but busy and is shown again with the result.
    /// </summary>
    Swap,

    /// <summary>Put the translation on the clipboard and close the preview.</summary>
    Copy,

    /// <summary>Close the preview and change nothing (Esc, the Cancel button, or a click elsewhere).</summary>
    Cancel
}

/// <summary>What the translation preview shows: the translation and the direction that produced it.</summary>
/// <param name="Text">The translation, plain text with the selection's line breaks.</param>
/// <param name="Direction">The direction used, or null when no translation ran (the text came back as is).</param>
/// <param name="CanReplace">
/// False for a translation of the clipboard: there is no selection to replace, so the preview offers only Copy and
/// Cancel (no Replace, no swap, and Enter does nothing).
/// </param>
internal sealed record TranslationPreview(string Text, TranslationDirection? Direction, bool CanReplace = true);

/// <summary>What a translation starts from.</summary>
internal enum TranslationSource
{
    /// <summary>The text selected in another application, read with Ctrl+C (the translate hotkey).</summary>
    Selection,

    /// <summary>The text on the clipboard, read without changing it (the tray's Translate clipboard).</summary>
    Clipboard
}

/// <summary>
/// Everything a selection translation does to the outside world. The app wires the real selection reader,
/// translator, preview window and clipboard; tests drive the coordinator with fakes.
/// <para>
/// <c>CaptureTarget</c> names the window the selection comes from, once, when a press is accepted.
/// <c>TranslateInDirectionAsync</c> translates the original selection in an explicit direction (swap).
/// <c>ReactivateAsync</c> brings the target to the foreground and says whether it is there (false: the window is
/// gone or refused). <c>DeliverAsync</c> puts the text on the clipboard, waits for the keys to be released and pastes
/// it into the target, with the dictation delivery's result semantics.
/// </para>
/// <para>
/// <c>ShowPreviewAsync</c> is called again with the new content after a swap while the preview stays open. It
/// completes with <see cref="PreviewChoice.Swap"/> without closing the preview, which then shows "Translating…" and
/// disables its actions until the next call; every other choice closes it.
/// </para>
/// </summary>
internal sealed record SelectionTranslationEffects(
    Func<CancellationToken, Task<string?>> ReadSelectionAsync,
    Func<string, CancellationToken, Task<TranslationResult>> TranslateAsync,
    Func<TranslationPreview, CancellationToken, Task<PreviewChoice>> ShowPreviewAsync,
    Func<string, CancellationToken, Task> CopyAsync,
    Action<TranslationNotice> ShowNotice,
    Func<bool> IsDictationActive,
    Func<bool> IsTranslationInstalled,
    Func<WindowTarget> CaptureTarget,
    Func<string, TranslationDirection, CancellationToken, Task<TranslationResult>> TranslateInDirectionAsync,
    Func<WindowTarget, CancellationToken, Task<bool>> ReactivateAsync,
    Func<WindowTarget, string, CancellationToken, Task<DeliveryResult>> DeliverAsync)
{
    /// <summary>
    /// Called with the selection as soon as it is read: asks the translator for its language and starts loading
    /// the direction it needs in the background, so that the load overlaps the rest of the work. It must return at
    /// once and never throw.
    /// </summary>
    internal Action<string>? PrepareTranslation { get; init; }

    /// <summary>
    /// Reads the text on the clipboard without modifying the clipboard, or null when it holds none. Used only by
    /// <see cref="SelectionTranslation.PressClipboardAsync"/>.
    /// </summary>
    internal required Func<CancellationToken, Task<string?>> ReadClipboardTextAsync { get; init; }

    /// <summary>
    /// Writes a failure to the technical log by kind, with the exception (its type only is logged) and never any
    /// text. Not called for a failed translation, which the translator already logged.
    /// </summary>
    internal Action<TechnicalEvent, Exception?>? LogFailure { get; init; }

    /// <summary>Called on every change of the phase, in order, from the thread that drives the coordinator.</summary>
    internal Action<SelectionTranslationPhase>? PhaseChanged { get; init; }
}

/// <summary>
/// Owns one selection translation from the translate hotkey press to its end, the way
/// <see cref="DictationSession"/> owns a dictation: it reads the selection, translates it, shows the preview and
/// carries out the user's choice. It is not a dictation session and never starts or changes one; a press is
/// ignored silently while a dictation session is active and while another selection translation is running or its
/// preview is open. The personal dictionary does not apply to selected text.
/// </summary>
internal sealed class SelectionTranslation : IDisposable
{
    private readonly SelectionTranslationEffects _effects;
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _run;
    private WindowTarget _target;
    private SelectionTranslationPhase _phase = SelectionTranslationPhase.Idle;
    private bool _disposed;

    internal SelectionTranslation(SelectionTranslationEffects effects)
    {
        _effects = effects;
    }

    /// <summary>True from the accepted press until the translation ends, including while the preview is open.</summary>
    internal bool IsActive
    {
        get
        {
            lock (_gate)
                return _phase != SelectionTranslationPhase.Idle;
        }
    }

    /// <summary>
    /// True while the selection is read or translated: Esc cancels it then. While the preview is open it has the
    /// keyboard focus and handles Esc itself, so this is false.
    /// </summary>
    internal bool IsTranslating
    {
        get
        {
            lock (_gate)
                return _phase is SelectionTranslationPhase.Reading or SelectionTranslationPhase.Translating;
        }
    }

    /// <summary>
    /// Handles one press of the translate hotkey and completes when the selection translation has ended. Ignored
    /// silently while a dictation session is active and while another selection translation is active.
    /// </summary>
    internal Task PressAsync() => PressCoreAsync(TranslationSource.Selection, default);

    /// <summary>
    /// Handles the tray's Translate clipboard: the same translation as a selection, but the text comes from the
    /// clipboard (read, never changed, no Ctrl+C, no focus change) and the preview offers only Copy and Cancel.
    /// <paramref name="target"/> only places the preview. The same guards apply as for <see cref="PressAsync"/>.
    /// </summary>
    internal Task PressClipboardAsync(WindowTarget target) => PressCoreAsync(TranslationSource.Clipboard, target);

    private async Task PressCoreAsync(TranslationSource source, WindowTarget clipboardTarget)
    {
        // The effects run outside the lock: the keyboard hook asks IsTranslating on its own thread and must not wait
        // for a file check. Presses come from one thread, so nothing can claim the translation in between.
        lock (_gate)
        {
            if (_disposed || _phase != SelectionTranslationPhase.Idle)
                return;
        }

        if (_effects.IsDictationActive())
            return;
        bool installed = _effects.IsTranslationInstalled();
        CancellationTokenSource? run = null;
        lock (_gate)
        {
            if (_disposed || _phase != SelectionTranslationPhase.Idle)
                return;
            // Without a model nothing can translate, so the clipboard is not touched either.
            if (installed)
            {
                run = CancellationTokenSource.CreateLinkedTokenSource(_disposeCancellation.Token);
                _run = run;
                _phase = SelectionTranslationPhase.Reading;
            }
        }

        if (run is null)
        {
            _effects.ShowNotice(new TranslationNotice(TranslationNoticeKind.TranslationNotInstalled));
            return;
        }

        WindowTarget target = source == TranslationSource.Selection ? _effects.CaptureTarget() : clipboardTarget;
        lock (_gate)
            _target = target;
        PublishPhase(SelectionTranslationPhase.Reading);
        TranslationNotice? notice;
        try
        {
            notice = await RunAsync(source, target, run.Token).ConfigureAwait(true);
        }
        finally
        {
            lock (_gate)
            {
                _run = null;
                _phase = SelectionTranslationPhase.Idle;
            }

            run.Dispose();
            PublishPhase(SelectionTranslationPhase.Idle);
        }

        // The notice comes after the return to Idle, so that ending the overlay cannot hide it.
        if (notice is { } outcome && !_disposeCancellation.IsCancellationRequested)
            _effects.ShowNotice(outcome);
    }

    /// <summary>
    /// Cancels the active selection translation: stops the translation, or closes the open preview as Cancel,
    /// without a result or a notice. Returns whether a translation was active.
    /// </summary>
    internal bool Cancel()
    {
        CancellationTokenSource? run;
        lock (_gate)
            run = _run;
        if (run is null)
            return false;
        try
        {
            run.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Called when a dictation trigger is pressed: dictation takes priority. Cancels a running translation, or closes
    /// an open preview as Cancel (the clipboard stays as it is), and returns the window the translation came from so
    /// the dictation can paste there instead of into the closing preview. Returns null when nothing was active, or
    /// when that window is unknown.
    /// </summary>
    internal WindowTarget? DismissForDictation()
    {
        WindowTarget target;
        lock (_gate)
            target = _target;
        if (!Cancel())
            return null;
        return target.Hwnd == 0 ? null : target;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        _disposeCancellation.Cancel();
    }

    private async Task<TranslationNotice?> RunAsync(
        TranslationSource source, WindowTarget target, CancellationToken token)
    {
        string? selection;
        try
        {
            selection = source == TranslationSource.Selection
                ? await _effects.ReadSelectionAsync(token).ConfigureAwait(true)
                : await _effects.ReadClipboardTextAsync(token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception)
        {
            _effects.LogFailure?.Invoke(TechnicalEvent.TranslationFailed, exception);
            return new TranslationNotice(TranslationNoticeKind.TranslationFailed);
        }

        if (token.IsCancellationRequested)
            return null;
        if (string.IsNullOrWhiteSpace(selection))
            return new TranslationNotice(source == TranslationSource.Selection
                ? TranslationNoticeKind.NoTextSelected
                : TranslationNoticeKind.ClipboardHasNoText);

        _effects.PrepareTranslation?.Invoke(selection);
        SetPhase(SelectionTranslationPhase.Translating);
        TranslationResult result;
        try
        {
            result = await _effects.TranslateAsync(selection, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception)
        {
            _effects.LogFailure?.Invoke(TechnicalEvent.TranslationFailed, exception);
            return new TranslationNotice(TranslationNoticeKind.TranslationFailed);
        }

        if (token.IsCancellationRequested)
            return null;
        if (result is TranslationResult.Translated translated)
            return await ShowPreviewAsync(source, selection, translated, target, token).ConfigureAwait(true);
        return NoticeFor(result);
    }

    /// <summary>Maps a result that is not a translation to its notice and logs the kinds that are failures.</summary>
    private TranslationNotice NoticeFor(TranslationResult result)
    {
        switch (result)
        {
            case TranslationResult.TooLong tooLong:
                return new TranslationNotice(TranslationNoticeKind.SelectionTooLong, tooLong.Limit);
            case TranslationResult.NotInstalled:
                _effects.LogFailure?.Invoke(TechnicalEvent.TranslationNotInstalled, null);
                return new TranslationNotice(TranslationNoticeKind.TranslationNotInstalled);
            case TranslationResult.TimedOut:
                _effects.LogFailure?.Invoke(TechnicalEvent.TranslationTimedOut, null);
                return new TranslationNotice(TranslationNoticeKind.TranslationFailed);
            default:
                // A failed load or translation was logged by the translator with its exception type.
                return new TranslationNotice(TranslationNoticeKind.TranslationFailed);
        }
    }

    private async Task<TranslationNotice?> ShowPreviewAsync(
        TranslationSource source,
        string selection,
        TranslationResult.Translated first,
        WindowTarget target,
        CancellationToken token)
    {
        // The original text stays here for swaps and is never shown. Each direction's first result is kept, so that
        // swapping twice returns to the first result at once.
        var results = new Dictionary<TranslationDirection, TranslationResult.Translated>();
        TranslationResult.Translated current = first;
        if (first.Direction is { } firstDirection)
            results[firstDirection] = first;

        while (true)
        {
            SetPhase(SelectionTranslationPhase.Preview);
            PreviewChoice choice;
            try
            {
                choice = await _effects.ShowPreviewAsync(
                    new TranslationPreview(current.Text, current.Direction, source == TranslationSource.Selection), token).ConfigureAwait(true);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception exception)
            {
                _effects.LogFailure?.Invoke(TechnicalEvent.TranslationFailed, exception);
                return new TranslationNotice(TranslationNoticeKind.TranslationFailed);
            }

            if (token.IsCancellationRequested)
                return null;
            switch (choice)
            {
                case PreviewChoice.Copy:
                    return await CopyAsync(current.Text, token).ConfigureAwait(true);
                case PreviewChoice.Replace or PreviewChoice.Swap when source == TranslationSource.Clipboard:
                    continue; // the preview does not offer them: there is no selection to replace or swap
                case PreviewChoice.Replace:
                    return await ReplaceAsync(target, current.Text, token).ConfigureAwait(true);
                case PreviewChoice.Swap:
                    if (current.Direction is not { } direction)
                        continue; // no translation ran, so there is no direction to turn around
                    TranslationDirection opposite = Opposite(direction);
                    if (results.TryGetValue(opposite, out TranslationResult.Translated? kept))
                    {
                        current = kept;
                        continue;
                    }

                    (TranslationResult.Translated? swapped, TranslationNotice? failure, bool cancelled) =
                        await SwapAsync(selection, opposite, token).ConfigureAwait(true);
                    if (cancelled)
                        return null;
                    if (swapped is not null)
                    {
                        current = swapped;
                        results[swapped.Direction ?? opposite] = swapped;
                    }
                    else
                    {
                        // The previous result stays on screen: the preview is shown again after the notice.
                        SetPhase(SelectionTranslationPhase.Preview);
                        _effects.ShowNotice(failure!.Value);
                    }

                    continue;
                default:
                    return null;
            }
        }
    }

    private async Task<(TranslationResult.Translated? Result, TranslationNotice? Failure, bool Cancelled)> SwapAsync(
        string selection,
        TranslationDirection direction,
        CancellationToken token)
    {
        SetPhase(SelectionTranslationPhase.Translating);
        TranslationResult result;
        try
        {
            result = await _effects.TranslateInDirectionAsync(selection, direction, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return (null, null, true);
        }
        catch (Exception exception)
        {
            _effects.LogFailure?.Invoke(TechnicalEvent.TranslationFailed, exception);
            return (null, new TranslationNotice(TranslationNoticeKind.TranslationFailed), false);
        }

        if (token.IsCancellationRequested)
            return (null, null, true);
        return result is TranslationResult.Translated translated
            ? (translated, null, false)
            : (null, NoticeFor(result), false);
    }

    private async Task<TranslationNotice?> CopyAsync(string text, CancellationToken token)
    {
        try
        {
            await _effects.CopyAsync(text, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _effects.LogFailure?.Invoke(TechnicalEvent.ClipboardUnavailable, exception);
            return new TranslationNotice(TranslationNoticeKind.ClipboardUnavailable);
        }

        return null;
    }

    /// <summary>
    /// Pastes the translation over the selection in the window it came from. A selection that vanished in the
    /// meantime is not detected: the paste then lands at the caret and Ctrl+Z in the application undoes it. When the
    /// window cannot be reactivated, Replace becomes Copy and says so.
    /// </summary>
    private async Task<TranslationNotice?> ReplaceAsync(WindowTarget target, string text, CancellationToken token)
    {
        bool reactivated;
        try
        {
            reactivated = await _effects.ReactivateAsync(target, token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception)
        {
            reactivated = false;
        }

        if (token.IsCancellationRequested)
            return null;
        if (!reactivated)
            return await CopyAsync(text, token).ConfigureAwait(true) ?? CopiedNotice;

        try
        {
            DeliveryResult delivery = await _effects.DeliverAsync(target, text, token).ConfigureAwait(true);
            return delivery is DeliveryResult.CopiedToClipboard or DeliveryResult.TargetMenuOpen ? CopiedNotice : null;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception exception)
        {
            _effects.LogFailure?.Invoke(TechnicalEvent.ClipboardUnavailable, exception);
            return new TranslationNotice(TranslationNoticeKind.ClipboardUnavailable);
        }
    }

    private static TranslationNotice CopiedNotice => new(TranslationNoticeKind.CopiedToClipboard);

    private static TranslationDirection Opposite(TranslationDirection direction) =>
        direction == TranslationDirection.GermanToEnglish
            ? TranslationDirection.EnglishToGerman
            : TranslationDirection.GermanToEnglish;

    private void SetPhase(SelectionTranslationPhase phase)
    {
        lock (_gate)
        {
            if (_phase == phase)
                return;
            _phase = phase;
        }

        PublishPhase(phase);
    }

    private void PublishPhase(SelectionTranslationPhase phase) => _effects.PhaseChanged?.Invoke(phase);
}
