using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Translation;

namespace WinWispr.Tests.Translation;

/// <summary>
/// The engine factory of the deterministic Translator tests. Its engines "translate" a segment by putting the
/// direction in front of it, count a token per word, and record every batch they receive, so that the tests can
/// observe what the Translator hands to the model (the sentences) and what it builds from the answers (the layout)
/// without a model.
/// </summary>
internal sealed class FakeTranslationEngines
{
    private readonly object _gate = new();
    private int _concurrent;
    private int _loadAttempts;

    internal ManualResetEventSlim? Gate { get; init; }
    internal ManualResetEventSlim? BlockTranslation { get; init; }
    internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource TranslationStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TimeSpan Pause { get; init; }
    internal string? RejectText { get; init; }
    internal bool Installed { get; set; } = true;
    internal int FailLoads { get; set; }

    /// <summary>The most tokens one segment may have; the Translator must cut longer text before the engine.</summary>
    internal int MaxSegmentTokens { get; init; } = 128;

    /// <summary>When true an engine returns every segment unchanged, as a model does for a name or a term.</summary>
    internal bool Echo { get; init; }

    /// <summary>Counts the tokens of a text. The default is one per word.</summary>
    internal Func<string, int> CountTokens { get; init; } =
        text => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    internal int Loads => Volatile.Read(ref _loadAttempts);
    internal int MaxConcurrentTranslations { get; private set; }
    internal int DisposedEngines { get; private set; }
    internal int StoppedByCancellation { get; private set; }
    internal List<TranslationDirection> LoadedDirections { get; } = [];

    /// <summary>Every batch that reached an engine, as direction and the segments in the order received.</summary>
    internal List<(TranslationDirection Direction, string[] Segments)> Batches { get; } = [];

    internal ITranslationEngine? Create(string modelDirectory, TranslationDirection direction)
    {
        Assert.AreEqual("models", modelDirectory);
        int attempt = Interlocked.Increment(ref _loadAttempts);
        Started.TrySetResult();
        Gate?.Wait(TimeSpan.FromSeconds(30));
        if (!Installed)
            return null;
        if (attempt <= FailLoads)
            throw new InvalidDataException("damaged model");
        lock (_gate)
            LoadedDirections.Add(direction);
        return new FakeEngine(this, direction);
    }

    internal static string Prefix(TranslationDirection direction) =>
        direction == TranslationDirection.GermanToEnglish ? "[de-en] " : "[en-de] ";

    private sealed class FakeEngine(FakeTranslationEngines owner, TranslationDirection direction) : ITranslationEngine
    {
        public int MaxSegmentTokens => owner.MaxSegmentTokens;

        public int CountSourceTokens(string text) => owner.CountTokens(text);

        public IReadOnlyList<string> TranslateBatch(IReadOnlyList<string> segments, CancellationToken cancellationToken)
        {
            int now;
            lock (owner._gate)
            {
                now = ++owner._concurrent;
                owner.MaxConcurrentTranslations = Math.Max(owner.MaxConcurrentTranslations, now);
                owner.Batches.Add((direction, [.. segments]));
            }

            try
            {
                owner.TranslationStarted.TrySetResult();
                if (owner.BlockTranslation is { } block)
                {
                    while (!block.Wait(10))
                    {
                        if (cancellationToken.IsCancellationRequested)
                        {
                            lock (owner._gate)
                                owner.StoppedByCancellation++;
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                    }
                }

                if (owner.Pause > TimeSpan.Zero)
                    Thread.Sleep(owner.Pause);
                cancellationToken.ThrowIfCancellationRequested();
                var results = new List<string>(segments.Count);
                foreach (string segment in segments)
                {
                    if (segment == owner.RejectText)
                        throw new TranslationEngineException("cannot translate");
                    results.Add(owner.Echo ? segment : Prefix(direction) + segment);
                }

                return results;
            }
            finally
            {
                lock (owner._gate)
                    owner._concurrent--;
            }
        }

        public void Dispose()
        {
            lock (owner._gate)
                owner.DisposedEngines++;
        }
    }
}
