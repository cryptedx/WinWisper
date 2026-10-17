namespace WinWispr.Translation;

/// <summary>One loaded translation direction. It translates a batch of segments at a time and is not thread-safe.</summary>
internal interface ITranslationEngine : IDisposable
{
    /// <summary>The most source tokens one segment may have. The caller cuts longer text before it gets here.</summary>
    int MaxSegmentTokens { get; }

    /// <summary>The number of source tokens of <paramref name="text"/>, without the end-of-sequence token.</summary>
    int CountSourceTokens(string text);

    /// <summary>
    /// Translates every segment (one sentence or chunk each) and returns the translations in the same order. Throws
    /// <see cref="TranslationEngineException"/> when a segment cannot be translated and
    /// <see cref="OperationCanceledException"/> when <paramref name="cancellationToken"/> fires; the work stops
    /// within one decoder step.
    /// </summary>
    IReadOnlyList<string> TranslateBatch(IReadOnlyList<string> segments, CancellationToken cancellationToken);
}

/// <summary>The engine could not translate a sentence. The message never contains the sentence.</summary>
internal sealed class TranslationEngineException : Exception
{
    internal TranslationEngineException(string message)
        : base(message)
    {
    }
}

/// <summary>How an engine spreads a batch over the model: segments per decoder pass and parallel passes.</summary>
internal sealed record EngineTuning(int MaxBatchSize, int Parallelism)
{
    /// <summary>
    /// The setting chosen from the throughput measurement (docs/testing/windows-11-acceptance.md): one segment per
    /// decoder pass, because padded batches change dynamically quantized outputs, and one segment per core in
    /// parallel, up to six, which keeps every output identical to translating the sentences one by one.
    /// </summary>
    internal static EngineTuning Default { get; } = new(MaxBatchSize: 1, Parallelism: Math.Clamp(Environment.ProcessorCount, 1, 6));
}

/// <summary>
/// One direction of Opus-MT: the model's tokenizer plus the encoder-decoder generation loop. Decoding is greedy in
/// both directions (no beam search: the user's three-second requirement rules it out), stops at the end-of-sequence
/// token and is capped, so a runaway output ends in a failure instead of an endless loop. Segments are decoded in
/// batches, longest first, through one decoder pass per batch.
/// </summary>
internal sealed class OpusMtEngine : ITranslationEngine
{
    /// <summary>The most source tokens one segment may have (QUALIFICATION.md section 3).</summary>
    internal const int MaxSourceTokens = 128;

    private const int MaxOutputCap = 512;
    private const double OutputTokensPerSourceToken = 2.0;

    private readonly MarianTokenizer _tokenizer;
    private readonly MarianModel _model;
    private readonly EngineTuning _tuning;

    private OpusMtEngine(MarianTokenizer tokenizer, MarianModel model, EngineTuning tuning)
    {
        _tokenizer = tokenizer;
        _model = model;
        _tuning = tuning;
    }

    public int MaxSegmentTokens => MaxSourceTokens;

    /// <summary>The files of one direction, relative to its folder, that must exist before a load is attempted.</summary>
    internal static readonly string[] RequiredFiles =
    [
        Path.Combine("onnx", "encoder_model_quantized.onnx"),
        Path.Combine("onnx", "decoder_model_merged_quantized.onnx"),
        "source.spm",
        "target.spm",
        "vocab.json"
    ];

    internal static string DirectionFolder(TranslationDirection direction) => direction switch
    {
        TranslationDirection.GermanToEnglish => "opus-mt-de-en",
        TranslationDirection.EnglishToGerman => "opus-mt-en-de",
        _ => throw new ArgumentOutOfRangeException(nameof(direction))
    };

    /// <summary>
    /// Loads one direction from <paramref name="modelDirectory"/>, the folder that holds both direction folders.
    /// Returns null when the files are missing or empty (not installed or incomplete) without touching the native
    /// runtime. Any other problem (a damaged file, a missing native runtime) throws.
    /// </summary>
    internal static ITranslationEngine? TryLoad(string modelDirectory, TranslationDirection direction) =>
        TryLoad(modelDirectory, direction, EngineTuning.Default);

    internal static ITranslationEngine? TryLoad(string modelDirectory, TranslationDirection direction, EngineTuning tuning)
    {
        string directionDirectory = Path.Combine(modelDirectory, DirectionFolder(direction));
        foreach (string relative in RequiredFiles)
        {
            var file = new FileInfo(Path.Combine(directionDirectory, relative));
            if (!file.Exists || file.Length == 0)
                return null;
        }

        var tokenizer = new MarianTokenizer(directionDirectory);
        OnnxRuntimeBinding.EnsureBound();
        var model = new MarianModel(
            Path.Combine(directionDirectory, RequiredFiles[0]),
            Path.Combine(directionDirectory, RequiredFiles[1]));
        return new OpusMtEngine(tokenizer, model, tuning);
    }

    internal static int MaxOutputTokens(int sourceTokens) =>
        Math.Min(MaxOutputCap, (int)Math.Ceiling(OutputTokensPerSourceToken * sourceTokens) + 8);

    public int CountSourceTokens(string text) => _tokenizer.Encode(text).Length - 1;

    public IReadOnlyList<string> TranslateBatch(IReadOnlyList<string> segments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var encoded = new int[segments.Count][];
        var results = new string[segments.Count];
        var work = new List<int>(segments.Count);
        for (int i = 0; i < segments.Count; i++)
        {
            encoded[i] = _tokenizer.Encode(segments[i]);
            int sourceTokens = encoded[i].Length - 1;
            if (sourceTokens == 0)
                results[i] = string.Empty;
            else if (sourceTokens > MaxSourceTokens)
                throw new TranslationEngineException(
                    $"A segment has {sourceTokens} tokens; one translation takes at most {MaxSourceTokens}.");
            else
                work.Add(i);
        }

        // Longest first, so that a batch holds sequences of similar length and padding stays small.
        work.Sort((a, b) => encoded[b].Length.CompareTo(encoded[a].Length));
        List<int[]> batches = [.. work.Chunk(Math.Max(1, _tuning.MaxBatchSize))];
        void Run(int[] indexes)
        {
            IReadOnlyList<string> translated = DecodeBatch(indexes.Select(i => encoded[i]).ToArray(), cancellationToken);
            for (int k = 0; k < indexes.Length; k++)
                results[indexes[k]] = translated[k];
        }

        if (_tuning.Parallelism <= 1 || batches.Count <= 1)
        {
            foreach (int[] batch in batches)
                Run(batch);
        }
        else
        {
            try
            {
                Parallel.ForEach(
                    batches,
                    new ParallelOptions { MaxDegreeOfParallelism = _tuning.Parallelism, CancellationToken = cancellationToken },
                    Run);
            }
            catch (AggregateException aggregate)
            {
                throw aggregate.Flatten().InnerExceptions[0];
            }
        }

        return results;
    }

    /// <summary>Greedy decoding of one batch through one decoder pass per step.</summary>
    private IReadOnlyList<string> DecodeBatch(int[][] batch, CancellationToken cancellationToken)
    {
        int rows = batch.Length;
        int[] limits = batch.Select(ids => MaxOutputTokens(ids.Length - 1)).ToArray();
        using EncoderOutput encoder = _model.Encode(batch);
        using var cache = new MarianModel.Cache(_model, rows);
        var generated = new List<int>[rows];
        for (int row = 0; row < rows; row++)
            generated[row] = [];
        var finished = new bool[rows];
        var last = new long[rows];
        Array.Fill(last, MarianModel.StartId);
        int remaining = rows;
        for (int step = 0; remaining > 0; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int[] next = cache.NextTokens(last, encoder);
            for (int row = 0; row < rows; row++)
            {
                if (finished[row])
                    continue;
                if (next[row] == MarianTokenizer.EosId)
                {
                    finished[row] = true;
                    remaining--;
                    last[row] = MarianModel.StartId;
                    continue;
                }

                generated[row].Add(next[row]);
                last[row] = next[row];
                if (generated[row].Count >= limits[row])
                    throw new TranslationEngineException(
                        $"The translation did not end within {limits[row]} tokens.");
            }
        }

        return generated.Select(_tokenizer.Decode).ToArray();
    }

    public void Dispose() => _model.Dispose();
}
