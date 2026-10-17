namespace WinWispr.Translation;

/// <summary>Creates the engine of one direction from the model directory, or null when it is not installed.</summary>
internal delegate ITranslationEngine? TranslationEngineFactory(string modelDirectory, TranslationDirection direction);

/// <summary>
/// Translates text between German and English, entirely on the machine, in the direction the caller names or the
/// one the detected language implies.
/// Nothing is loaded until a direction is needed: the first translation of a direction (or an explicit
/// <see cref="Preload"/>) loads it, later translations reuse it until the module is disposed at exit. Callers can
/// ask whether a load is still pending, so that "Loading translation..." shows only when the wait is real.
/// </summary>
/// <remarks>
/// A missing, incomplete or unreadable model never throws: the result says not installed or failed, and the next
/// request tries the load again. Only the caller's own cancellation throws <see cref="OperationCanceledException"/>.
/// Inference is serialized, so the module is safe to call from several threads. Tokenizers, the runtime and the
/// generation loop stay behind this interface.
/// </remarks>
internal sealed class Translator : IDisposable
{
    private readonly Func<string?> _resolveModelDirectory;
    private readonly TranslationEngineFactory _createEngine;
    private readonly Action<Exception>? _failureObserved;
    private readonly TranslationLimits _limits;
    private readonly object _gate = new();
    private readonly Dictionary<TranslationDirection, Task<LoadOutcome>> _loads = [];
    private readonly SemaphoreSlim _inference = new(1, 1);
    private bool _isDisposed;

    /// <param name="resolveModelDirectory">
    /// Returns the folder that holds both direction folders, or null when the translation model is not installed.
    /// It runs on a background thread when a load starts.
    /// </param>
    /// <param name="failureObserved">
    /// Optional hook for the technical log. It receives the exception of a failed load or translation, never the
    /// text. It must not throw.
    /// </param>
    internal Translator(
        Func<string?> resolveModelDirectory,
        Action<Exception>? failureObserved = null,
        TranslationLimits? limits = null)
        : this(resolveModelDirectory, OpusMtEngine.TryLoad, failureObserved, limits)
    {
    }

    internal Translator(
        Func<string?> resolveModelDirectory,
        TranslationEngineFactory createEngine,
        Action<Exception>? failureObserved = null,
        TranslationLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(resolveModelDirectory);
        ArgumentNullException.ThrowIfNull(createEngine);
        _resolveModelDirectory = resolveModelDirectory;
        _createEngine = createEngine;
        _failureObserved = failureObserved;
        _limits = limits ?? TranslationLimits.Default;
    }

    /// <summary>True while a load of <paramref name="direction"/> is running.</summary>
    internal bool IsLoadPending(TranslationDirection direction)
    {
        lock (_gate)
            return _loads.TryGetValue(direction, out Task<LoadOutcome>? load) && !load.IsCompleted;
    }

    /// <summary>True once <paramref name="direction"/> is loaded and resident.</summary>
    internal bool IsLoaded(TranslationDirection direction)
    {
        lock (_gate)
            return _loads.TryGetValue(direction, out Task<LoadOutcome>? load) && IsResident(load);
    }

    /// <summary>
    /// Starts loading <paramref name="direction"/> in the background and returns at once. Does nothing when the
    /// direction is loaded or already loading. A translation requested meanwhile waits for this load.
    /// </summary>
    internal void Preload(TranslationDirection direction)
    {
        if (!Enum.IsDefined(direction))
            throw new ArgumentOutOfRangeException(nameof(direction));
        try
        {
            _ = StartLoad(direction);
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>
    /// True when translating <paramref name="text"/> into English would have to wait for the German to English
    /// model: the text is German or ambiguous and that direction is not loaded yet (a load that is already running
    /// counts). Text that needs no model (empty, no letters, already English) never waits. Loads nothing.
    /// </summary>
    internal bool WouldWaitForLoadToEnglish(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrWhiteSpace(text))
            return false;
        DetectedLanguage language = LanguageDetector.Detect(text);
        return language is DetectedLanguage.German or DetectedLanguage.Ambiguous
            && !IsLoaded(TranslationDirection.GermanToEnglish);
    }

    /// <summary>
    /// True when translating the selection <paramref name="text"/> in the direction its detected language implies
    /// would have to wait for a model: that direction is not loaded yet (a load that is already running counts).
    /// Text that needs no model (empty, no letters) never waits. Loads nothing.
    /// </summary>
    internal bool WouldWaitForLoad(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrWhiteSpace(text))
            return false;
        return LanguageDetector.Detect(text).ToDirection() is { } direction && !IsLoaded(direction);
    }

    /// <summary>
    /// Asks for the language of a selection just read and starts loading the direction it needs in the
    /// background, so that the load overlaps the rest of the work. Does nothing for text without letters and for a
    /// selection over the length limit, which is not translated. Returns at once and never throws.
    /// </summary>
    internal void PreloadForSelection(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrWhiteSpace(text) || text.Length > _limits.SelectionMaxCharacters)
            return;
        if (LanguageDetector.Detect(text).ToDirection() is { } direction)
            Preload(direction);
    }

    /// <summary>The language the detector finds in <paramref name="text"/>. Loads nothing and never throws.</summary>
    internal DetectedLanguage DetectLanguage(string text) => LanguageDetector.Detect(text);

    /// <summary>
    /// Translates a selection (or any text held to the selection limit). Without a direction the detected language
    /// decides and the text goes into the other language.
    /// </summary>
    internal Task<TranslationResult> TranslateAsync(
        string text,
        TranslationDirection? direction = null,
        CancellationToken cancellationToken = default) =>
        TranslateCoreAsync(text, TranslationUse.Selection, direction, toEnglish: false, cancellationToken);

    /// <summary>
    /// Translates <paramref name="text"/> for <paramref name="use"/>: a selection is refused above the length
    /// limit and has the short safety deadline, a dictation has neither a limit nor that deadline. An explicit
    /// <paramref name="direction"/> overrides detection.
    /// </summary>
    internal Task<TranslationResult> TranslateAsync(
        string text,
        TranslationUse use,
        TranslationDirection? direction = null,
        CancellationToken cancellationToken = default) =>
        TranslateCoreAsync(text, use, direction, toEnglish: false, cancellationToken);

    /// <summary>
    /// Translates <paramref name="text"/> into English whatever its source language: text that is already English
    /// (or has no letters) comes back unchanged and says so, German and ambiguous text is translated from German.
    /// </summary>
    internal Task<TranslationResult> TranslateToEnglishAsync(
        string text,
        TranslationUse use,
        CancellationToken cancellationToken = default) =>
        TranslateCoreAsync(text, use, explicitDirection: null, toEnglish: true, cancellationToken);

    private async Task<TranslationResult> TranslateCoreAsync(
        string text,
        TranslationUse use,
        TranslationDirection? explicitDirection,
        bool toEnglish,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!Enum.IsDefined(use))
            throw new ArgumentOutOfRangeException(nameof(use));
        if (explicitDirection is { } named && !Enum.IsDefined(named))
            throw new ArgumentOutOfRangeException(nameof(explicitDirection));
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text))
            return new TranslationResult.Translated(string.Empty, explicitDirection);
        // The limit is checked before anything else, so that an oversized selection costs nothing.
        if (use == TranslationUse.Selection && text.Length > _limits.SelectionMaxCharacters)
            return new TranslationResult.TooLong(_limits.SelectionMaxCharacters);

        TranslationDirection direction;
        bool ambiguous = false;
        if (explicitDirection is { } given)
        {
            if (!text.Any(char.IsLetter))
                return new TranslationResult.Translated(text, given, Unchanged: true);
            direction = given;
        }
        else
        {
            DetectedLanguage detected = LanguageDetector.Detect(text);
            if (detected == DetectedLanguage.NoLetters || (toEnglish && detected == DetectedLanguage.English))
                return new TranslationResult.Translated(text, null, Unchanged: true);
            direction = toEnglish ? TranslationDirection.GermanToEnglish : detected.ToDirection()!.Value;
            ambiguous = detected == DetectedLanguage.Ambiguous;
        }

        LoadOutcome load;
        try
        {
            load = await StartLoad(direction).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return new TranslationResult.Failed("The translator was shut down.");
        }

        if (load.Engine is not { } engine)
            return load.Problem!;

        await _inference.WaitAsync(cancellationToken).ConfigureAwait(false);
        TimeSpan deadline = TimeSpan.Zero;
        using var running = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            lock (_gate)
            {
                if (_isDisposed)
                    return new TranslationResult.Failed("The translator was shut down.");
            }

            string translated = await Task.Run(
                () =>
                {
                    List<TextSegmenter.Part> parts = TextSegmenter.Split(
                        text, engine.CountSourceTokens, engine.MaxSegmentTokens);
                    deadline = use == TranslationUse.Selection
                        ? _limits.SelectionDeadline
                        : _limits.DictationDeadline(parts.Sum(part => part.Tokens));
                    // The deadline starts here: the load is done and it is this request's turn.
                    running.CancelAfter(deadline);
                    return Translate(engine, parts, running.Token);
                },
                CancellationToken.None).ConfigureAwait(false);
            bool unchanged = ambiguous && SameWords(text, translated);
            return new TranslationResult.Translated(translated, direction, unchanged);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (running.IsCancellationRequested)
        {
            return new TranslationResult.TimedOut(deadline);
        }
        catch (Exception exception)
        {
            Observe(exception);
            return new TranslationResult.Failed(Describe("The translation failed", exception));
        }
        finally
        {
            _inference.Release();
        }
    }

    /// <summary>Translates the segments in one batch and puts the layout back around the answers.</summary>
    private static string Translate(
        ITranslationEngine engine, List<TextSegmenter.Part> parts, CancellationToken cancellationToken)
    {
        string[] segments = [.. parts.Where(part => part.IsSegment).Select(part => part.Text)];
        if (segments.Length == 0)
            return string.Concat(parts.Select(part => part.Text));

        IReadOnlyList<string> translations = engine.TranslateBatch(segments, cancellationToken);
        if (translations.Count != segments.Length)
            throw new TranslationEngineException("The engine returned a different number of segments.");

        var result = new System.Text.StringBuilder();
        int next = 0;
        foreach (TextSegmenter.Part part in parts)
            result.Append(part.IsSegment ? translations[next++] : part.Text);
        return result.ToString();
    }

    /// <summary>True when both texts have the same letters and digits, ignoring case, spaces and punctuation.</summary>
    private static bool SameWords(string input, string output)
    {
        static string Normalize(string text) =>
            new([.. text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);

        return Normalize(input) == Normalize(output);
    }

    private Task<LoadOutcome> StartLoad(TranslationDirection direction)
    {
        lock (_gate)
        {
            if (_isDisposed)
                throw new ObjectDisposedException(nameof(Translator));
            if (_loads.TryGetValue(direction, out Task<LoadOutcome>? existing)
                && !(existing.IsCompleted && !IsResident(existing)))
                return existing;

            // The task runs on the thread pool so that the caller never blocks on file checks or on the load.
            Task<LoadOutcome> load = Task.Run(() => Load(direction));
            _loads[direction] = load;
            _ = load.ContinueWith(
                finished => Forget(direction, finished),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return load;
        }
    }

    /// <summary>A load that found nothing or failed is forgotten, so that the next request retries it.</summary>
    private void Forget(TranslationDirection direction, Task<LoadOutcome> finished)
    {
        if (IsResident(finished))
            return;
        lock (_gate)
        {
            if (_loads.TryGetValue(direction, out Task<LoadOutcome>? current) && ReferenceEquals(current, finished))
                _loads.Remove(direction);
        }
    }

    private static bool IsResident(Task<LoadOutcome> load) =>
        load.IsCompletedSuccessfully && load.Result.Engine is not null;

    private LoadOutcome Load(TranslationDirection direction)
    {
        try
        {
            string? modelDirectory = _resolveModelDirectory();
            if (string.IsNullOrWhiteSpace(modelDirectory))
                return LoadOutcome.NotInstalled;

            ITranslationEngine? engine = _createEngine(modelDirectory, direction);
            if (engine is null)
                return LoadOutcome.NotInstalled;

            lock (_gate)
            {
                if (_isDisposed)
                {
                    engine.Dispose();
                    return LoadOutcome.Failure("The translator was shut down.");
                }
            }

            return LoadOutcome.Loaded(engine);
        }
        catch (Exception exception)
        {
            Observe(exception);
            return LoadOutcome.Failure(Describe("The translation model could not be loaded", exception));
        }
    }

    private void Observe(Exception exception)
    {
        try
        {
            _failureObserved?.Invoke(exception);
        }
        catch
        {
            // A logging hook must never turn a result into a crash.
        }
    }

    /// <summary>Names the exception type only: its message may quote paths but never the text.</summary>
    private static string Describe(string what, Exception exception) => $"{what} ({exception.GetType().Name}).";

    public void Dispose()
    {
        List<Task<LoadOutcome>> loads;
        lock (_gate)
        {
            if (_isDisposed)
                return;
            _isDisposed = true;
            loads = [.. _loads.Values];
            _loads.Clear();
        }

        // Wait for a running translation before the native sessions go away. A request that queued behind it
        // finds the translator disposed and returns without touching an engine.
        _inference.Wait();
        foreach (Task<LoadOutcome> load in loads)
        {
            try
            {
                load.Wait();
                load.Result.Engine?.Dispose();
            }
            catch (AggregateException)
            {
            }
        }

        _inference.Release();
    }

    /// <summary>A loaded engine, or the result that every request gets while the load has a problem.</summary>
    private sealed record LoadOutcome(ITranslationEngine? Engine, TranslationResult? Problem)
    {
        internal static readonly LoadOutcome NotInstalled = new(null, new TranslationResult.NotInstalled());

        internal static LoadOutcome Loaded(ITranslationEngine engine) => new(engine, null);

        internal static LoadOutcome Failure(string reason) => new(null, new TranslationResult.Failed(reason));
    }
}
