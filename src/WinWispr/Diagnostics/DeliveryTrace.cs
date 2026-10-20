namespace WinWispr.Diagnostics;

/// <summary>
/// One window that held the clipboard open, by its process image name. Offsets are milliseconds after the dictation
/// went on the clipboard, only as precise as the checks that saw it: about 10 to 16 ms before Ctrl+V and about 1 ms
/// after it.
/// </summary>
internal sealed record ClipboardHold(string Process, int AtMs, int ForMs);

/// <summary>
/// What putting one dictation on the clipboard and pasting it observed, for its DeliveryCompleted entry: counts,
/// offsets and process image names, never text, window titles or paths. <see cref="DictationTimings"/> opens a trace
/// around the delivery it measures and the text output fills <see cref="Current"/> when one is open, so no delivery
/// signature carries diagnostics and a delivery that nothing measures (a selection translation's) records nothing.
/// </summary>
internal sealed class DeliveryTrace
{
    private static readonly AsyncLocal<DeliveryTrace?> Ambient = new();

    internal static DeliveryTrace? Current => Ambient.Value;

    /// <summary>How many times WinWispr opened, or tried to open, the clipboard to put the dictation on it.</summary>
    internal int? ClipboardSetAttempts { get; set; }

    /// <summary>The image name of the target window's process, such as <c>notepad.exe</c>.</summary>
    internal string? TargetProcess { get; set; }

    /// <summary>Milliseconds from the clipboard set to the Ctrl+V; null when no Ctrl+V was sent.</summary>
    internal int? PasteAtMs { get; set; }

    /// <summary>Another program changed the clipboard after WinWispr set it and before Ctrl+V.</summary>
    internal bool ClipboardChangedBeforePaste { get; set; }

    /// <summary>
    /// The windows that held the clipboard open from the set until about a second after Ctrl+V; it completes once
    /// that second is over. Null when the delivery never set the clipboard.
    /// </summary>
    internal Task<IReadOnlyList<ClipboardHold>>? ClipboardHolds { get; set; }

    /// <summary>Opens a trace for the rest of the calling async flow.</summary>
    internal static DeliveryTrace Begin()
    {
        var trace = new DeliveryTrace();
        Ambient.Value = trace;
        return trace;
    }

    /// <summary>The trace for the log entry; null when the delivery recorded nothing (no text, or no traced output).</summary>
    internal DeliveryDiagnostics? ToDiagnostics(IReadOnlyList<ClipboardHold>? holds) =>
        ClipboardSetAttempts is null && TargetProcess is null
            ? null
            : new(ClipboardSetAttempts, TargetProcess, PasteAtMs, ClipboardChangedBeforePaste, holds);
}

/// <summary>
/// Turns repeated checks of which window holds the clipboard open into <see cref="ClipboardHold"/> runs. The checks
/// come from the delivery and then from the watch after Ctrl+V, one after the other, never at once.
/// </summary>
internal sealed class ClipboardHoldRecorder(TimeProvider clock, long origin, Func<nint, string> processOf)
{
    /// <summary>Holds beyond this many are not recorded, which bounds the log entry.</summary>
    internal const int MaxHolds = 10;

    private readonly List<ClipboardHold> _holds = [];
    private readonly Dictionary<nint, string> _processes = [];
    private nint _window;
    private long _since;

    /// <summary>Notes the window that holds the clipboard open at <paramref name="timestamp"/>, 0 for none.</summary>
    internal void Observe(long timestamp, nint openWindow)
    {
        if (openWindow == _window)
            return;
        Close(timestamp);
        _window = openWindow;
        _since = timestamp;
        // Named while the window is still there: the program may close it, or exit, before the hold ends.
        if (openWindow != 0 && _holds.Count < MaxHolds && !_processes.ContainsKey(openWindow))
            _processes[openWindow] = processOf(openWindow);
    }

    /// <summary>Milliseconds from the clipboard set to <paramref name="timestamp"/>.</summary>
    internal int OffsetMs(long timestamp) =>
        (int)Math.Round(clock.GetElapsedTime(origin, timestamp).TotalMilliseconds);

    /// <summary>Ends a hold still in progress at <paramref name="timestamp"/> and returns every hold seen.</summary>
    internal IReadOnlyList<ClipboardHold> Finish(long timestamp)
    {
        Close(timestamp);
        _window = 0;
        return _holds.ToArray();
    }

    private void Close(long timestamp)
    {
        if (_window == 0 || _holds.Count >= MaxHolds)
            return;
        int at = OffsetMs(_since);
        _holds.Add(new ClipboardHold(_processes[_window], at, OffsetMs(timestamp) - at));
    }
}
