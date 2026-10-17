using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace WinWispr.Translation;

/// <summary>
/// The encoder result of a batch of sentences padded to one length: hidden states and the attention mask
/// (1 for a token, 0 for padding).
/// </summary>
internal sealed class EncoderOutput : IDisposable
{
    private readonly IDisposableReadOnlyCollection<OrtValue> _outputs;

    internal EncoderOutput(IDisposableReadOnlyCollection<OrtValue> outputs, OrtValue mask)
    {
        _outputs = outputs;
        Mask = mask;
    }

    internal OrtValue Hidden => _outputs[0]; // [B, S, H]
    internal OrtValue Mask { get; }          // [B, S] int64

    public void Dispose()
    {
        Mask.Dispose();
        _outputs.Dispose();
    }
}

/// <summary>
/// The encoder and the merged decoder graphs of one Opus-MT direction (int8 ONNX) on the shipped ONNX Runtime.
/// Session options follow the qualification: intra-op threads by the Parakeet rule, one inter-op thread, no
/// spinning and the shared environment arena with exact-size allocation, plus the QDQ switch below. Decoding takes a
/// batch of segments at a time; the sessions are safe for concurrent runs.
/// </summary>
internal sealed class MarianModel : IDisposable
{
    internal const int StartId = 58100;
    internal const int VocabularySize = 58101;

    private static readonly object AllocatorGate = new();
    private static bool _environmentAllocatorRegistered;

    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoder;
    private readonly RunOptions _run = new();
    private readonly string[] _pastNames;     // past_key_values.N.{decoder|encoder}.{key|value}
    private readonly string[] _presentNames;  // the matching present.* outputs
    private readonly bool[] _isEncoderKeyValue;
    private readonly int _heads;
    private readonly int _headDimension;

    internal MarianModel(string encoderPath, string decoderPath)
    {
        using SessionOptions options = CreateSessionOptions();

        InferenceSession? encoder = null;
        InferenceSession? decoder = null;
        try
        {
            encoder = new InferenceSession(encoderPath, options);
            decoder = new InferenceSession(decoderPath, options);
            _pastNames = decoder.InputMetadata.Keys
                .Where(name => name.StartsWith("past_key_values.", StringComparison.Ordinal)).ToArray();
            if (_pastNames.Length == 0)
                throw new InvalidDataException("The decoder graph has no key/value inputs.");
            _presentNames = _pastNames.Select(name => "present." + name["past_key_values.".Length..]).ToArray();
            _isEncoderKeyValue = _pastNames.Select(name => name.Contains(".encoder.", StringComparison.Ordinal)).ToArray();
            foreach (string present in _presentNames)
                if (!decoder.OutputMetadata.ContainsKey(present))
                    throw new InvalidDataException("The decoder graph has no output " + present + ".");
            int[] dimensions = decoder.InputMetadata[_pastNames[0]].Dimensions;
            _heads = dimensions[1];
            _headDimension = dimensions[3];
            _ = encoder.OutputMetadata["last_hidden_state"]; // fail early when the encoder is not a Marian encoder
        }
        catch
        {
            encoder?.Dispose();
            decoder?.Dispose();
            _run.Dispose();
            throw;
        }

        _encoder = encoder;
        _decoder = decoder;
    }

    /// <summary>The session options of every translation graph (see the class remarks).</summary>
    internal static SessionOptions CreateSessionOptions()
    {
        var options = new SessionOptions
        {
            IntraOpNumThreads = IntraOpThreads(),
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };
        try
        {
            options.AddSessionConfigEntry("session.intra_op.allow_spinning", "0");
            // The decoder stores the shared embedding as int8 and dequantizes it to a 119 MB float matrix for the
            // output projection. With the QDQ transformations on, that dequantization runs again in every decoder
            // step of every run (the main cost of a step, and 119 MB of extra memory per concurrent run). Switching
            // them off lets constant folding do it once at load. The outputs stay byte-identical (measured).
            options.AddSessionConfigEntry("session.disable_quant_qdq", "1");
            RegisterEnvironmentAllocator();
            options.AddSessionConfigEntry("session.use_env_allocators", "1");
            return options;
        }
        catch
        {
            options.Dispose();
            throw;
        }
    }

    /// <summary>The Parakeet rule: half the cores, between one and four.</summary>
    private static int IntraOpThreads() => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    private static void RegisterEnvironmentAllocator()
    {
        lock (AllocatorGate)
        {
            if (_environmentAllocatorRegistered)
                return;
            // kSameAsRequested (1): the arena grows by exactly what a run asks for instead of by powers of two.
            using var config = new OrtArenaCfg(0u, 1, -1, -1);
            using var info = new OrtMemoryInfo(
                OrtMemoryInfo.allocatorCPU, OrtAllocatorType.ArenaAllocator, 0, OrtMemType.Default);
            OrtEnv.Instance().CreateAndRegisterAllocator(info, config);
            _environmentAllocatorRegistered = true;
        }
    }

    /// <summary>Encodes a batch of sequences (each ends with the end-of-sequence id), padded to the longest.</summary>
    internal EncoderOutput Encode(IReadOnlyList<int[]> batch)
    {
        int rows = batch.Count;
        int length = batch.Max(ids => ids.Length);
        var idData = new long[rows * length];
        var maskData = new long[rows * length];
        Array.Fill(idData, MarianTokenizer.PadId);
        for (int row = 0; row < rows; row++)
        {
            int[] ids = batch[row];
            for (int i = 0; i < ids.Length; i++)
            {
                idData[row * length + i] = ids[i];
                maskData[row * length + i] = 1;
            }
        }

        using var idValue = OrtValue.CreateTensorValueFromMemory(idData, [rows, length]);
        using var maskValue = OrtValue.CreateTensorValueFromMemory(maskData, [rows, length]);
        IDisposableReadOnlyCollection<OrtValue> outputs = _encoder.Run(
            _run, ["input_ids", "attention_mask"], [idValue, maskValue], ["last_hidden_state"]);
        OrtValue mask = OrtValue.CreateAllocatedTensorValue(
            OrtAllocator.DefaultInstance, TensorElementType.Int64, [rows, length]);
        maskData.AsSpan().CopyTo(mask.GetTensorMutableDataAsSpan<long>());
        return new EncoderOutput(outputs, mask);
    }

    /// <summary>The key/value cache of the merged decoder for a batch of sentences.</summary>
    internal sealed class Cache : IDisposable
    {
        private readonly MarianModel _model;
        private readonly OrtValue[] _past;
        private readonly int _rows;
        private IDisposableReadOnlyCollection<OrtValue>? _first;
        private IDisposableReadOnlyCollection<OrtValue>? _current;
        private bool _started;

        internal Cache(MarianModel model, int rows)
        {
            _model = model;
            _rows = rows;
            _past = new OrtValue[model._pastNames.Length];
        }

        /// <summary>
        /// Runs one decoder step for the last token of every row and returns, per row, the index of the most likely
        /// next token. The start token is never chosen (the model's bad-words rule).
        /// </summary>
        internal int[] NextTokens(long[] lastTokens, EncoderOutput encoder)
        {
            var names = new List<string>(4 + _past.Length)
            {
                "input_ids", "encoder_attention_mask", "encoder_hidden_states", "use_cache_branch"
            };
            using var idValue = OrtValue.CreateTensorValueFromMemory(lastTokens, [_rows, 1L]);
            using var cacheFlag = OrtValue.CreateTensorValueFromMemory(new[] { _started }, [1L]);
            var values = new List<OrtValue>(4 + _past.Length) { idValue, encoder.Mask, encoder.Hidden, cacheFlag };
            OrtValue[]? emptyPast = null;
            try
            {
                if (!_started)
                {
                    emptyPast = new OrtValue[_past.Length];
                    for (int i = 0; i < _past.Length; i++)
                        emptyPast[i] = OrtValue.CreateAllocatedTensorValue(
                            OrtAllocator.DefaultInstance, TensorElementType.Float,
                            [_rows, _model._heads, 0L, _model._headDimension]);
                }

                for (int i = 0; i < _past.Length; i++)
                {
                    names.Add(_model._pastNames[i]);
                    values.Add(_started ? _past[i] : emptyPast![i]);
                }

                var outputNames = new List<string>(1 + _past.Length) { "logits" };
                outputNames.AddRange(_model._presentNames);
                IDisposableReadOnlyCollection<OrtValue> result = _model._decoder.Run(
                    _model._run, names, values, outputNames);

                if (!_started)
                {
                    _first = result;
                    for (int i = 0; i < _past.Length; i++)
                        _past[i] = result[1 + i];
                    _started = true;
                }
                else
                {
                    for (int i = 0; i < _past.Length; i++)
                        if (!_model._isEncoderKeyValue[i])
                            _past[i] = result[1 + i];
                    _current?.Dispose();
                    _current = result;
                    // The encoder key/values stay the ones of the first step.
                    for (int i = 0; i < _past.Length; i++)
                        if (_model._isEncoderKeyValue[i])
                            _past[i] = _first![1 + i];
                }

                ReadOnlySpan<float> logits = result[0].GetTensorDataAsSpan<float>();
                int vocabulary = logits.Length / _rows;
                var next = new int[_rows];
                for (int row = 0; row < _rows; row++)
                    next[row] = Argmax(logits.Slice(row * vocabulary, vocabulary));
                return next;
            }
            finally
            {
                if (emptyPast is not null)
                    foreach (OrtValue empty in emptyPast)
                        empty?.Dispose();
            }
        }

        private static int Argmax(ReadOnlySpan<float> logits)
        {
            int best = 0;
            float bestValue = float.NegativeInfinity;
            for (int v = 0; v < logits.Length; v++)
            {
                if (v == StartId)
                    continue;
                if (logits[v] > bestValue)
                {
                    bestValue = logits[v];
                    best = v;
                }
            }

            return best;
        }

        public void Dispose()
        {
            _current?.Dispose();
            _first?.Dispose();
        }
    }

    public void Dispose()
    {
        _encoder.Dispose();
        _decoder.Dispose();
        _run.Dispose();
    }
}
