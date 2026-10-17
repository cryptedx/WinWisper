using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Audio;
using WinWispr.Speech;

namespace WinWispr.Tests.Speech;

[TestClass]
public sealed class SpeechRuntimeTests
{
    [TestMethod]
    public async Task DirectMl_failure_falls_back_to_one_resident_cpu_recognizer()
    {
        var attempts = new List<SpeechBackend>();
        int recognitions = 0;
        using var runtime = CreateRuntime(
            resolveModel: _ => "model",
            hasNvidia: () => true,
            createRecognizer: backend =>
            {
                attempts.Add(backend);
                if (backend == SpeechBackend.DirectMl)
                    throw new InvalidOperationException("gpu unavailable");
                return new RecognizerHandle(backend, new DisposableSpy(), _ => $"text-{++recognitions}");
            });

        await runtime.LoadInstalledAsync(CancellationToken.None);
        string first = await runtime.RecognizeAsync([0.1f], CancellationToken.None);
        string second = await runtime.RecognizeAsync([0.2f], CancellationToken.None);

        CollectionAssert.AreEqual(new[] { SpeechBackend.DirectMl, SpeechBackend.Cpu }, attempts);
        Assert.AreEqual(SpeechRuntimeState.Ready, runtime.State);
        Assert.AreEqual(SpeechBackend.Cpu, runtime.Backend);
        Assert.AreEqual("text-1", first);
        Assert.AreEqual("text-2", second);
    }

    [TestMethod]
    public async Task Missing_model_never_scans_hardware_or_invokes_native_factory()
    {
        int hardwareScans = 0;
        int calls = 0;
        using var runtime = CreateRuntime(
            resolveModel: _ => null,
            hasNvidia: () =>
            {
                hardwareScans++;
                return true;
            },
            createRecognizer: backend =>
            {
                calls++;
                throw new AssertFailedException(backend.ToString());
            });

        await runtime.LoadInstalledAsync(CancellationToken.None);

        Assert.AreEqual(SpeechRuntimeState.Missing, runtime.State);
        Assert.AreEqual(0, hardwareScans);
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task Without_nvidia_only_cpu_is_created()
    {
        var attempts = new List<SpeechBackend>();
        using var runtime = CreateRuntime(
            resolveModel: _ => "model",
            hasNvidia: () => false,
            createRecognizer: backend =>
            {
                attempts.Add(backend);
                return new RecognizerHandle(backend, new DisposableSpy(), _ => "text");
            });

        await runtime.LoadInstalledAsync(CancellationToken.None);

        CollectionAssert.AreEqual(new[] { SpeechBackend.Cpu }, attempts);
        Assert.AreEqual(SpeechRuntimeState.Ready, runtime.State);
    }

    [TestMethod]
    public async Task Native_factory_does_not_run_on_the_captured_ui_context()
    {
        var uiContext = new InlineSynchronizationContext();
        SynchronizationContext? previous = SynchronizationContext.Current;
        SynchronizationContext? factoryContext = null;
        SynchronizationContext.SetSynchronizationContext(uiContext);
        try
        {
            using var runtime = CreateRuntime(
                resolveModel: _ => "model",
                hasNvidia: () => false,
                createRecognizer: backend =>
                {
                    factoryContext = SynchronizationContext.Current;
                    return new RecognizerHandle(backend, new DisposableSpy(), _ => "text");
                });

            await runtime.LoadInstalledAsync(CancellationToken.None);

            Assert.AreNotSame(uiContext, factoryContext);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [TestMethod]
    public async Task Concurrent_loads_create_one_recognizer_and_ready_reload_is_a_noop()
    {
        int calls = 0;
        using var runtime = CreateRuntime(
            resolveModel: _ => "model",
            hasNvidia: () => false,
            createRecognizer: backend =>
            {
                Interlocked.Increment(ref calls);
                return new RecognizerHandle(backend, new DisposableSpy(), _ => "text");
            });

        await Task.WhenAll(
            runtime.LoadInstalledAsync(CancellationToken.None),
            runtime.LoadInstalledAsync(CancellationToken.None));
        await runtime.LoadInstalledAsync(CancellationToken.None);

        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task Failed_load_can_be_retried()
    {
        int calls = 0;
        using var runtime = CreateRuntime(
            resolveModel: _ => "model",
            hasNvidia: () => false,
            createRecognizer: backend =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                    throw new InvalidOperationException("first load failed");
                return new RecognizerHandle(backend, new DisposableSpy(), _ => "text");
            });

        await runtime.LoadInstalledAsync(CancellationToken.None);
        Assert.AreEqual(SpeechRuntimeState.Failed, runtime.State);
        await runtime.LoadInstalledAsync(CancellationToken.None);

        Assert.AreEqual(SpeechRuntimeState.Ready, runtime.State);
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public async Task Cancelled_load_reports_failed_and_a_later_load_can_retry()
    {
        using var cancelled = new CancellationTokenSource();
        int successfulRecognizers = 0;
        var attempts = new List<SpeechBackend>();
        using var runtime = CreateRuntime(
            resolveModel: token =>
            {
                if (!cancelled.IsCancellationRequested)
                {
                    cancelled.Cancel();
                    token.ThrowIfCancellationRequested();
                }

                return "model";
            },
            hasNvidia: () => false,
            createRecognizer: backend =>
            {
                attempts.Add(backend);
                successfulRecognizers++;
                return new RecognizerHandle(backend, new DisposableSpy(), _ => "text");
            });

        await runtime.LoadInstalledAsync(cancelled.Token);
        Assert.AreEqual(SpeechRuntimeState.Failed, runtime.State);

        await runtime.LoadInstalledAsync(CancellationToken.None);

        Assert.AreEqual(SpeechRuntimeState.Ready, runtime.State);
        Assert.AreEqual(SpeechBackend.Cpu, runtime.Backend);
        CollectionAssert.AreEqual(new[] { SpeechBackend.Cpu }, attempts);
        Assert.AreEqual(1, successfulRecognizers);
    }

    [TestMethod]
    public async Task Recognition_calls_are_serialized()
    {
        int active = 0;
        int maximum = 0;
        using var runtime = CreateRuntime(
            resolveModel: _ => "model",
            hasNvidia: () => false,
            createRecognizer: backend => new RecognizerHandle(
                backend,
                new DisposableSpy(),
                _ =>
                {
                    int current = Interlocked.Increment(ref active);
                    InterlockedMax(ref maximum, current);
                    Thread.Sleep(50);
                    Interlocked.Decrement(ref active);
                    return "text";
                }));

        await runtime.LoadInstalledAsync(CancellationToken.None);
        await Task.WhenAll(
            runtime.RecognizeAsync([0.1f], CancellationToken.None),
            runtime.RecognizeAsync([0.2f], CancellationToken.None));

        Assert.AreEqual(1, maximum);
    }

    [TestMethod]
    public async Task Empty_native_text_is_returned_for_no_speech_mapping()
    {
        using var runtime = CreateRuntime(
            resolveModel: _ => "model",
            hasNvidia: () => false,
            createRecognizer: backend => new RecognizerHandle(backend, new DisposableSpy(), _ => string.Empty));

        await runtime.LoadInstalledAsync(CancellationToken.None);

        Assert.AreEqual(string.Empty, await runtime.RecognizeAsync([0.1f], CancellationToken.None));
    }

    [TestMethod]
    public async Task Dispose_waits_for_in_flight_recognition_and_disposes_once()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lifetime = new DisposableSpy();
        using var runtime = CreateRuntime(
            resolveModel: _ => "model",
            hasNvidia: () => false,
            createRecognizer: backend => new RecognizerHandle(
                backend,
                lifetime,
                _ =>
                {
                    started.SetResult();
                    release.Task.GetAwaiter().GetResult();
                    return "text";
                }));

        await runtime.LoadInstalledAsync(CancellationToken.None);
        Task<string> recognition = runtime.RecognizeAsync([0.1f], CancellationToken.None);
        await started.Task;
        Task dispose = Task.Run(runtime.Dispose);
        await Task.Delay(50);
        Assert.IsFalse(dispose.IsCompleted);

        release.SetResult();
        Assert.AreEqual("text", await recognition);
        await dispose;
        Assert.AreEqual(1, lifetime.DisposeCount);
    }

    [TestMethod]
    public async Task Recognition_timeout_marks_runtime_failed_without_releasing_native_call_early()
    {
        var runtimeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtimeRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runtimeObserved = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var runtime = CreateRuntime(
            resolveModel: _ => "model",
            hasNvidia: () => false,
            createRecognizer: backend => new RecognizerHandle(
                backend,
                new DisposableSpy(),
                captured =>
                {
                    runtimeStarted.SetResult();
                    runtimeRelease.Task.GetAwaiter().GetResult();
                    runtimeObserved.SetResult(captured.SampleCount);
                    return "text";
                }),
            recognitionTimeout: TimeSpan.FromMilliseconds(25));

        await runtime.LoadInstalledAsync(CancellationToken.None);
        using CapturedAudio input = CapturedAudio.FromArray([0.1f]);
        Task<string> recognition = runtime.RecognizeAsync(input, CancellationToken.None);
        await runtimeStarted.Task;

        try
        {
            await recognition;
            Assert.Fail("Recognition should time out while the native call is blocked.");
        }
        catch (SpeechRecognitionTimeoutException)
        {
        }
        Assert.AreEqual(SpeechRuntimeState.Failed, runtime.State);
        input.Dispose();
        try
        {
            await runtime.ReloadAsync(SpeechMode.Cpu, CancellationToken.None);
            Assert.Fail("A poisoned runtime must require an application restart.");
        }
        catch (InvalidOperationException)
        {
        }

        runtimeRelease.SetResult();
        Assert.AreEqual(1, await runtimeObserved.Task);
    }

    [TestMethod]
    public async Task Recognition_before_ready_is_rejected()
    {
        using var runtime = CreateRuntime(
            resolveModel: _ => null,
            hasNvidia: () => false,
            createRecognizer: backend => throw new AssertFailedException());

        await runtime.LoadInstalledAsync(CancellationToken.None);

        try
        {
            await runtime.RecognizeAsync([0.1f], CancellationToken.None);
            Assert.Fail("Recognition should not run before the runtime is ready.");
        }
        catch (InvalidOperationException)
        {
        }
    }

    [TestMethod]
    public void Native_runtime_loads_its_dependencies_before_sherpa()
    {
        var loaded = new List<string>();
        nint nextHandle = 0;

        nint sherpaHandle = SpeechRuntime.LoadNativeRuntime(
            Path.Combine("root", "runtime"),
            path =>
            {
                loaded.Add(Path.GetFileName(path));
                return ++nextHandle;
            });

        CollectionAssert.AreEqual(
            new[] { "DirectML.dll", "onnxruntime.dll", "sherpa-onnx-c-api.dll" },
            loaded);
        Assert.AreEqual((nint)3, sherpaHandle);
    }

    private static SpeechRuntime CreateRuntime(
        Func<CancellationToken, string?> resolveModel,
        Func<bool> hasNvidia,
        Func<SpeechBackend, RecognizerHandle> createRecognizer,
        TimeSpan? recognitionTimeout = null)
        => new(resolveModel,
            () => hasNvidia() ? [new SpeechAdapter(1, SpeechAdapter.NvidiaVendor, "NVIDIA test")] : [],
            (_, adapter) => createRecognizer(adapter is null ? SpeechBackend.Cpu : SpeechBackend.DirectMl),
            recognitionTimeout: recognitionTimeout);

    private static void InterlockedMax(ref int target, int value)
    {
        while (true)
        {
            int current = Volatile.Read(ref target);
            if (current >= value || Interlocked.CompareExchange(ref target, value, current) == current)
                return;
        }
    }

    private sealed class DisposableSpy : IDisposable
    {
        public int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;
    }

    private sealed class InlineSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => callback(state);
    }
}
