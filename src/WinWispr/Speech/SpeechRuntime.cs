using System.Runtime.InteropServices;
using SherpaOnnx;
using WinWispr.Audio;

namespace WinWispr.Speech;

public enum SpeechBackend
{
    Cpu,
    DirectMl
}

internal enum SpeechRuntimeState
{
    Missing,
    Loading,
    Ready,
    Failed
}

internal sealed class SpeechRecognitionTimeoutException : TimeoutException
{
    internal SpeechRecognitionTimeoutException(TimeSpan timeout)
        : base($"Speech recognition did not complete within {timeout.TotalSeconds:0} seconds.")
    {
    }
}

internal sealed record RecognizerHandle(
    SpeechBackend Backend,
    IDisposable Lifetime,
    Func<CapturedAudio, string> Recognize);

internal sealed class SpeechRuntime : IDisposable
{
    private const int SampleRate = 16_000;
    private readonly Func<CancellationToken, string?> _resolveModel;
    private readonly Func<IReadOnlyList<SpeechAdapter>> _getAdapters;
    private readonly Func<string, SpeechAdapter?, RecognizerHandle> _createRecognizer;
    private readonly object _stateGate = new();
    private readonly SemaphoreSlim _recognitionGate = new(1, 1);
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly TimeSpan? _recognitionTimeoutOverride;
    private static readonly object ResolverGate = new();
    private static bool _resolverInstalled;
    private static nint _nativeRuntimeHandle;
    private Task? _loadTask;
    private RecognizerHandle? _recognizer;
    private SpeechRuntimeState _state = SpeechRuntimeState.Missing;
    private SpeechBackend? _backend;
    private bool _disposed;
    private bool _recognizerPoisoned;

    private SpeechMode _mode;
    private string? _adapterName;
    private string? _fallbackReason;

    internal SpeechRuntime(
        Func<CancellationToken, string?> resolveModel,
        Func<IReadOnlyList<SpeechAdapter>> getAdapters,
        Func<string, SpeechAdapter?, RecognizerHandle> createRecognizer,
        SpeechMode mode = SpeechMode.Automatic,
        TimeSpan? recognitionTimeout = null)
    {
        _resolveModel = resolveModel ?? throw new ArgumentNullException(nameof(resolveModel));
        _getAdapters = getAdapters ?? throw new ArgumentNullException(nameof(getAdapters));
        _createRecognizer = createRecognizer ?? throw new ArgumentNullException(nameof(createRecognizer));
        _mode = mode;
        _recognitionTimeoutOverride = recognitionTimeout;
        if (_recognitionTimeoutOverride is TimeSpan timeout && timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(recognitionTimeout));
    }

    internal string StatusText
    {
        get
        {
            lock (_stateGate)
            {
                return _state switch
                {
                    SpeechRuntimeState.Loading => "Loading processing device...",
                    SpeechRuntimeState.Missing => "Not active — install the speech model first.",
                    SpeechRuntimeState.Failed => _recognizerPoisoned
                        ? "Processing unavailable. Restart WinWispr."
                        : "Processing unavailable. Select a device to retry.",
                    _ => (_backend == SpeechBackend.DirectMl ? $"Active: DirectML · {_adapterName}" : "Active: CPU")
                        + (_fallbackReason is null ? string.Empty : $". {_fallbackReason}")
                };
            }
        }
    }

    internal SpeechRuntimeState State
    {
        get
        {
            lock (_stateGate)
                return _state;
        }
    }

    internal SpeechBackend? Backend
    {
        get
        {
            lock (_stateGate)
                return _backend;
        }
    }

    internal bool RequiresRestart
    {
        get
        {
            lock (_stateGate)
                return _recognizerPoisoned;
        }
    }

    internal event Action<SpeechRuntimeState, SpeechBackend?>? StateChanged;

    internal static SpeechRuntime CreateNative(Func<CancellationToken, string?> resolveModel,
        SpeechMode mode = SpeechMode.Automatic) =>
        new(resolveModel, SpeechDevices.Enumerate,
            (model, adapter) => CreateNativeRecognizer(model, adapter, enableNativeDebug: false), mode);

    internal static SpeechRuntime LoadForAcceptance(string modelDirectory, SpeechBackend backend)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelDirectory);
        string fullPath = Path.GetFullPath(modelDirectory);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException(fullPath);

        var runtime = new SpeechRuntime(
            _ => fullPath,
            () => SpeechDevices.Enumerate(),
            (model, adapter) => CreateNativeRecognizer(model, adapter, enableNativeDebug: true),
            backend == SpeechBackend.Cpu ? SpeechMode.Cpu : SpeechMode.Automatic);
        runtime.LoadInstalledAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (runtime.State != SpeechRuntimeState.Ready || runtime.Backend != backend)
        {
            runtime.Dispose();
            throw new InvalidOperationException($"The {backend} acceptance runtime did not become ready.");
        }
        return runtime;
    }

    internal Task LoadInstalledAsync(CancellationToken cancellationToken) =>
        ScheduleLoad(null, cancellationToken);

    internal Task ReloadAsync(SpeechMode mode, CancellationToken cancellationToken) =>
        ScheduleLoad(mode, cancellationToken);

    private Task ScheduleLoad(SpeechMode? mode, CancellationToken cancellationToken)
    {
        if (mode is SpeechMode value && !Enum.IsDefined(value))
            throw new ArgumentOutOfRangeException(nameof(mode));
        lock (_stateGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_loadTask is not null && !_loadTask.IsCompleted && _state == SpeechRuntimeState.Loading)
            {
                if (mode is not null)
                    throw new InvalidOperationException("Wait for the current model load before switching devices.");
                return _loadTask;
            }
            if (_recognizerPoisoned)
                throw new InvalidOperationException("The speech runtime requires an application restart after a recognition timeout.");
            if (mode is null && _state == SpeechRuntimeState.Ready)
                return Task.CompletedTask;
            _mode = mode ?? _mode;
            SetState(SpeechRuntimeState.Loading, null);
            return _loadTask = Task.Run(async () =>
            {
                await _recognitionGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    RecognizerHandle? previous;
                    lock (_stateGate)
                    {
                        previous = _recognizer;
                        _recognizer = null;
                        _adapterName = null;
                        _fallbackReason = null;
                    }
                    previous?.Lifetime.Dispose();
                    await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
                }
                finally { _recognitionGate.Release(); }
            });
        }
    }

    internal async Task<string> RecognizeAsync(CapturedAudio samples, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(samples);
        using CancellationTokenSource linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposeCancellation.Token);
        await _recognitionGate.WaitAsync(linkedCancellation.Token).ConfigureAwait(false);
        bool releaseGate = true;
        try
        {
            RecognizerHandle recognizer;
            lock (_stateGate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_state != SpeechRuntimeState.Ready || _recognizer is null)
                    throw new InvalidOperationException("Speech runtime is not ready.");
                recognizer = _recognizer;
            }

            CapturedAudio recognitionSamples = CapturedAudio.FromArray(samples.ToArray());
            Task<string> recognition = Task.Run(() => recognizer.Recognize(recognitionSamples));
            TimeSpan recognitionTimeout = GetRecognitionTimeout(samples);
            try
            {
                string text = await recognition.WaitAsync(recognitionTimeout, cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return text;
            }
            catch (TimeoutException) when (!recognition.IsCompleted)
            {
                releaseGate = false;
                MarkRecognitionUnavailable();
                _ = ReleaseRecognitionGateWhenCompleteAsync(recognition, recognitionSamples);
                throw new SpeechRecognitionTimeoutException(recognitionTimeout);
            }
            catch (OperationCanceledException) when (!recognition.IsCompleted)
            {
                releaseGate = false;
                MarkRecognitionUnavailable();
                _ = ReleaseRecognitionGateWhenCompleteAsync(recognition, recognitionSamples);
                throw;
            }
            finally
            {
                if (releaseGate)
                    recognitionSamples.Dispose();
            }
        }
        finally
        {
            if (releaseGate)
                _recognitionGate.Release();
        }
    }

    internal async Task<string> RecognizeAsync(float[] samples, CancellationToken cancellationToken)
    {
        using CapturedAudio captured = CapturedAudio.FromArray(samples);
        return await RecognizeAsync(captured, cancellationToken).ConfigureAwait(false);
    }

    internal string Recognize(float[] samples) =>
        RecognizeAsync(samples, CancellationToken.None).GetAwaiter().GetResult();

    public void Dispose()
    {
        Task? loadTask;
        lock (_stateGate)
        {
            if (_disposed)
                return;
            _disposed = true;
            loadTask = _loadTask;
        }

        _disposeCancellation.Cancel();
        try
        {
            loadTask?.GetAwaiter().GetResult();
        }
        catch
        {
        }

        // A native recognition call cannot be forcefully interrupted safely. If
        // it has already been declared unavailable, let process shutdown reclaim
        // it instead of blocking the UI forever on the semaphore.
        bool recognitionUnavailable;
        lock (_stateGate)
            recognitionUnavailable = _recognizerPoisoned;
        if (recognitionUnavailable)
        {
            _disposeCancellation.Dispose();
            return;
        }

        if (!_recognitionGate.Wait(TimeSpan.FromSeconds(5)))
        {
            _disposeCancellation.Dispose();
            return;
        }
        RecognizerHandle? recognizer;
        lock (_stateGate)
        {
            recognizer = _recognizer;
            _recognizer = null;
            _backend = null;
        }

        recognizer?.Lifetime.Dispose();
        _recognitionGate.Dispose();
        _disposeCancellation.Dispose();
    }

    private void MarkRecognitionUnavailable()
    {
        lock (_stateGate)
            _recognizerPoisoned = true;
        SetState(SpeechRuntimeState.Failed, null);
    }

    private async Task ReleaseRecognitionGateWhenCompleteAsync(Task recognition, CapturedAudio recognitionSamples)
    {
        try
        {
            await recognition.ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            recognitionSamples.Dispose();
            _recognitionGate.Release();
        }
    }

    private TimeSpan GetRecognitionTimeout(CapturedAudio samples)
    {
        if (_recognitionTimeoutOverride is TimeSpan overrideValue)
            return overrideValue;

        double durationSeconds = samples.SampleCount / (double)SampleRate;
        double seconds = Math.Clamp(30d + durationSeconds * 3d, 60d, 300d);
        return TimeSpan.FromSeconds(seconds);
    }

    internal static nint LoadNativeRuntime(string runtimeDirectory, Func<string, nint> loadLibrary)
    {
        loadLibrary(Path.Combine(runtimeDirectory, "DirectML.dll"));
        loadLibrary(Path.Combine(runtimeDirectory, "onnxruntime.dll"));
        return loadLibrary(Path.Combine(runtimeDirectory, "sherpa-onnx-c-api.dll"));
    }

    private Task LoadCoreAsync(CancellationToken cancellationToken)
    {
        RecognizerHandle? selected = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? modelDirectory = _resolveModel(cancellationToken);
            if (string.IsNullOrWhiteSpace(modelDirectory))
            {
                SetState(SpeechRuntimeState.Missing, null);
                return Task.CompletedTask;
            }

            SpeechAdapter? adapter = null;
            string? fallbackReason = null;
            if (_mode != SpeechMode.Cpu)
            {
                try
                {
                    adapter = SpeechAdapter.Select(_mode, _getAdapters());
                    if (adapter is null && _mode != SpeechMode.Automatic)
                        fallbackReason = "Selected GPU unavailable. Using CPU; check the driver or select another device.";
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    fallbackReason = "Could not list graphics devices. Using CPU; check the graphics driver.";
                }
            }
            if (adapter is not null)
            {
                try
                {
                    selected = _createRecognizer(modelDirectory, adapter);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    fallbackReason = "GPU could not load this model. Using CPU; update the driver or select another device.";
                }
            }
            cancellationToken.ThrowIfCancellationRequested();
            selected ??= _createRecognizer(modelDirectory, null);
            cancellationToken.ThrowIfCancellationRequested();
            lock (_stateGate)
            {
                if (_disposed)
                {
                    selected.Lifetime.Dispose();
                    selected = null;
                    return Task.CompletedTask;
                }

                _recognizer = selected;
                _backend = selected.Backend;
                _adapterName = selected.Backend == SpeechBackend.DirectMl ? adapter?.Name : null;
                _fallbackReason = fallbackReason;
                selected = null;
            }

            SetState(SpeechRuntimeState.Ready, Backend);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            selected?.Lifetime.Dispose();
            SetState(SpeechRuntimeState.Failed, null);
        }
        catch
        {
            selected?.Lifetime.Dispose();
            SetState(SpeechRuntimeState.Failed, null);
        }
        return Task.CompletedTask;
    }

    private void SetState(SpeechRuntimeState state, SpeechBackend? backend)
    {
        Action<SpeechRuntimeState, SpeechBackend?>? stateChanged;
        lock (_stateGate)
        {
            if (_disposed)
                return;
            _state = state;
            _backend = backend;
            stateChanged = StateChanged;
        }

        stateChanged?.Invoke(state, backend);
    }

    private static RecognizerHandle CreateNativeRecognizer(
        string modelDirectory,
        SpeechAdapter? adapter,
        bool enableNativeDebug)
    {
        InstallNativeResolver();
        // The patched native boundary reports failures rather than hiding a CPU
        // fallback or allowing C++ exceptions to cross into managed code.
        var getStatus = Marshal.GetDelegateForFunctionPointer<GetRecognizerStatus>(
            NativeLibrary.GetExport(_nativeRuntimeHandle, "WinWisprGetLastRecognizerStatus"));
        var model = new OfflineModelConfig
        {
            Tokens = Path.Combine(modelDirectory, "tokens.txt"),
            NumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
            Debug = enableNativeDebug ? 1 : 0,
            Provider = GetProvider(adapter),
            ModelType = "nemo_transducer",
            Transducer = new OfflineTransducerModelConfig
            {
                Encoder = Path.Combine(modelDirectory, "encoder.int8.onnx"),
                Decoder = Path.Combine(modelDirectory, "decoder.int8.onnx"),
                Joiner = Path.Combine(modelDirectory, "joiner.int8.onnx")
            }
        };

        var recognizer = new OfflineRecognizer(new OfflineRecognizerConfig
        {
            FeatConfig = new FeatureConfig { SampleRate = SampleRate, FeatureDim = 80 },
            ModelConfig = model,
            DecodingMethod = "greedy_search"
        });

        if (getStatus() != 1)
        {
            recognizer.Dispose();
            throw new InvalidOperationException("The native speech model could not be loaded.");
        }

        var language = new ShortDictationLanguage(waveform =>
        {
            using OfflineStream stream = recognizer.CreateStream();
            // OfflineStream accepts one complete waveform; capture chunks are
            // an internal storage detail and must be joined before submission.
            stream.AcceptWaveform(SampleRate, waveform);
            recognizer.Decode(stream);
            return stream.Result.Text.Trim();
        });
        return new RecognizerHandle(
            adapter is null ? SpeechBackend.Cpu : SpeechBackend.DirectMl,
            recognizer,
            samples =>
            {
                ArgumentNullException.ThrowIfNull(samples);
                return language.Recognize(samples.ToArray());
            });
    }

    internal static string GetProvider(SpeechAdapter? adapter)
    {
        if (adapter is null)
            return "cpu";
        ArgumentOutOfRangeException.ThrowIfNegative(adapter.Index);
        return "directml:device_id=" + adapter.Index.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetRecognizerStatus();

    internal static nint InstallNativeResolver()
    {
        lock (ResolverGate)
        {
            if (_resolverInstalled)
                return _nativeRuntimeHandle;

            string runtimeDirectory = Path.Combine(AppContext.BaseDirectory, "Runtime", "win-x64");
            _nativeRuntimeHandle = LoadNativeRuntime(runtimeDirectory, NativeLibrary.Load);

            NativeLibrary.SetDllImportResolver(
                typeof(OfflineRecognizer).Assembly,
                static (libraryName, _, _) =>
                {
                    if (!string.Equals(libraryName, "sherpa-onnx-c-api", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(libraryName, "sherpa-onnx-c-api.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        return IntPtr.Zero;
                    }

                    return _nativeRuntimeHandle;
                });
            _resolverInstalled = true;
            return _nativeRuntimeHandle;
        }
    }
}
