namespace WinWispr.Interop;

/// <summary>The clipboard operations the selection reader needs. The app implements them over the Windows clipboard.</summary>
internal interface ISelectionClipboard
{
    /// <summary>The clipboard's change counter. It rises on every write, whatever the text.</summary>
    uint SequenceNumber { get; }

    /// <summary>Captures what the clipboard holds now (null when it is empty) so that it can be put back.</summary>
    Task<object?> CaptureAsync(CancellationToken cancellationToken);

    /// <summary>Puts a captured clipboard back; null empties the clipboard. It must not need the caller's token.</summary>
    Task RestoreAsync(object? snapshot, CancellationToken cancellationToken);

    /// <summary>The text on the clipboard now, or null when it holds none.</summary>
    Task<string?> ReadTextAsync(CancellationToken cancellationToken);
}

/// <summary>How long the selection reader waits at each step.</summary>
internal sealed record SelectionReaderTiming(
    TimeSpan ModifierPoll,
    int ModifierPollLimit,
    TimeSpan CopyPoll,
    TimeSpan CopyTimeout,
    TimeSpan Settle)
{
    /// <summary>Two seconds for the user to let go of the hotkey, one second for the application to copy.</summary>
    internal static SelectionReaderTiming Default { get; } = new(
        ModifierPoll: TimeSpan.FromMilliseconds(20),
        ModifierPollLimit: 100,
        CopyPoll: TimeSpan.FromMilliseconds(10),
        CopyTimeout: TimeSpan.FromMilliseconds(1000),
        Settle: TimeSpan.FromMilliseconds(25));
}

/// <summary>
/// Reads the text the user has selected in another application by sending Ctrl+C and reading the clipboard. It
/// waits until the user has released the modifier keys of the hotkey (it never releases the user's keys itself, as
/// the paste path does not either), notices the copy by the clipboard's change counter instead of comparing text, so
/// a selection equal to the current clipboard text still counts, and puts the previous clipboard back on every
/// path. Restoring is safe here, unlike after a paste, because no paste is pending.
/// </summary>
internal sealed class SelectionReader
{
    private const int MaxSettleRounds = 8;
    private readonly ISelectionClipboard _clipboard;
    private readonly Func<bool> _modifiersPressed;
    private readonly Func<int> _sendCopy;
    private readonly SelectionReaderTiming _timing;

    internal SelectionReader(
        ISelectionClipboard clipboard,
        Func<bool> modifiersPressed,
        Func<int> sendCopy,
        SelectionReaderTiming? timing = null)
    {
        _clipboard = clipboard;
        _modifiersPressed = modifiersPressed;
        _sendCopy = sendCopy;
        _timing = timing ?? SelectionReaderTiming.Default;
    }

    /// <summary>
    /// Returns the selected text, or null when nothing was selected (the application did not copy) or what it
    /// copied holds no text. Throws <see cref="TimeoutException"/> when the user keeps a modifier key held,
    /// <see cref="InvalidOperationException"/> when Ctrl+C could not be sent, and the clipboard's own failure. The
    /// previous clipboard is restored whenever the copy changed it, whatever the outcome.
    /// </summary>
    internal async Task<string?> ReadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Let the triggering chord go before sending Ctrl+C; Alt still held would turn it into another chord.
        for (int attempt = 0; _modifiersPressed(); attempt++)
        {
            if (attempt >= _timing.ModifierPollLimit)
                throw new TimeoutException("The hotkey's modifier keys were not released.");
            await Task.Delay(_timing.ModifierPoll, cancellationToken).ConfigureAwait(false);
        }

        // Never overwrite a clipboard that cannot be put back.
        object? previous = await _clipboard.CaptureAsync(cancellationToken).ConfigureAwait(false);
        uint before = _clipboard.SequenceNumber;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_sendCopy() != 4)
                throw new InvalidOperationException("Ctrl+C could not be sent to the foreground window.");

            if (!await WaitForChangeAsync(before, cancellationToken).ConfigureAwait(false))
                return null;
            string? text = await _clipboard.ReadTextAsync(cancellationToken).ConfigureAwait(false);
            return string.IsNullOrEmpty(text) ? null : text;
        }
        finally
        {
            if (_clipboard.SequenceNumber != before)
                await RestoreQuietlyAsync(previous).ConfigureAwait(false);
        }
    }

    // True when the counter changed and then held still, so that an application that writes the clipboard in
    // several steps is read after the last one.
    private async Task<bool> WaitForChangeAsync(uint before, CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + _timing.CopyTimeout;
        while (_clipboard.SequenceNumber == before)
        {
            if (DateTime.UtcNow >= deadline)
                return false;
            await Task.Delay(_timing.CopyPoll, cancellationToken).ConfigureAwait(false);
        }

        for (int round = 0; round < MaxSettleRounds; round++)
        {
            uint seen = _clipboard.SequenceNumber;
            await Task.Delay(_timing.Settle, cancellationToken).ConfigureAwait(false);
            if (_clipboard.SequenceNumber == seen)
                break;
        }

        return true;
    }

    private async Task RestoreQuietlyAsync(object? previous)
    {
        try
        {
            // The restore must also happen when the caller cancelled, so it does not use the caller's token.
            await _clipboard.RestoreAsync(previous, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A clipboard that cannot be restored never costs the user the selection that was read.
        }
    }
}
