using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Speech;
using WinWispr.Core;
using System.Text.Json;

namespace WinWispr.Tests.Speech;

[TestClass]
public sealed class SpeechDeviceTests
{
    private static readonly SpeechAdapter Intel = new(0, SpeechAdapter.IntelVendor, "Intel Graphics");
    private static readonly SpeechAdapter Nvidia = new(1, SpeechAdapter.NvidiaVendor, "NVIDIA RTX A1000 6GB");

    [TestMethod]
    public void Settings_preserve_selection_and_old_files_default_to_automatic()
    {
        foreach (SpeechMode mode in Enum.GetValues<SpeechMode>())
        {
            var settings = AppSettings.Default with { SpeechMode = mode, MicrophoneId = "work microphone" };
            Assert.AreEqual(settings, AppSettingsStore.Parse(JsonSerializer.Serialize(settings)));
        }
        string legacy = JsonSerializer.Serialize(AppSettings.Default with { MicrophoneId = "work microphone" });
        legacy = legacy.Replace(",\"SpeechMode\":0", string.Empty);
        Assert.AreEqual(SpeechMode.Automatic, AppSettingsStore.Parse(legacy).SpeechMode);
        var unknown = AppSettings.Default with { SpeechMode = (SpeechMode)999, MicrophoneId = "work microphone" };
        AppSettings restored = AppSettingsStore.Parse(JsonSerializer.Serialize(unknown));
        Assert.AreEqual(SpeechMode.Automatic, restored.SpeechMode);
        Assert.AreEqual("work microphone", restored.MicrophoneId);
    }

    [TestMethod]
    public void Automatic_and_manual_selection_use_vendor_and_real_dxgi_index()
    {
        SpeechAdapter[] adapters = [Intel, Nvidia];
        Assert.AreEqual(Nvidia, SpeechAdapter.Select(SpeechMode.Automatic, adapters));
        Assert.AreEqual(Nvidia, SpeechAdapter.Select(SpeechMode.Nvidia, adapters));
        Assert.AreEqual(Intel, SpeechAdapter.Select(SpeechMode.Intel, adapters));
        Assert.IsNull(SpeechAdapter.Select(SpeechMode.Cpu, adapters));
        Assert.IsNull(SpeechAdapter.Select(SpeechMode.Automatic, [Intel]));
        Assert.AreEqual("directml:device_id=1", SpeechRuntime.GetProvider(Nvidia));
        Assert.AreEqual("directml:device_id=0", SpeechRuntime.GetProvider(Intel));
        Assert.AreEqual("cpu", SpeechRuntime.GetProvider(null));
        SpeechAdapter moved = Nvidia with { Index = 3 };
        Assert.AreEqual("directml:device_id=3", SpeechRuntime.GetProvider(SpeechAdapter.Select(SpeechMode.Nvidia, [moved, Intel])));
    }

    [TestMethod]
    public async Task Missing_or_failed_gpu_reports_cpu_and_reloading_clears_fallback()
    {
        bool fail = true;
        var attempts = new List<SpeechAdapter?>();
        using var runtime = new SpeechRuntime(_ => "model", () => [Intel, Nvidia], (_, adapter) =>
        {
            attempts.Add(adapter);
            if (adapter == Nvidia && fail)
                throw new InvalidOperationException("PRIVATE detail must not appear in UI");
            return Handle(adapter);
        }, SpeechMode.Nvidia);

        await runtime.LoadInstalledAsync(CancellationToken.None);
        CollectionAssert.AreEqual(new SpeechAdapter?[] { Nvidia, null }, attempts);
        StringAssert.Contains(runtime.StatusText, "Active: CPU");
        StringAssert.Contains(runtime.StatusText, "GPU could not load");
        Assert.IsFalse(runtime.StatusText.Contains("PRIVATE"));
        fail = false;
        await runtime.ReloadAsync(SpeechMode.Intel, CancellationToken.None);
        Assert.AreEqual("Active: DirectML · Intel Graphics", runtime.StatusText);
        await runtime.ReloadAsync(SpeechMode.Nvidia, CancellationToken.None);
        Assert.AreEqual("Active: DirectML · NVIDIA RTX A1000 6GB", runtime.StatusText);
        await runtime.ReloadAsync(SpeechMode.Cpu, CancellationToken.None);
        Assert.AreEqual("Active: CPU", runtime.StatusText);

        using var missing = new SpeechRuntime(_ => "model", () => [Intel], (_, adapter) => Handle(adapter), SpeechMode.Nvidia);
        await missing.LoadInstalledAsync(CancellationToken.None);
        Assert.AreEqual(SpeechBackend.Cpu, missing.Backend);
        StringAssert.Contains(missing.StatusText, "Selected GPU unavailable");
    }

    [TestMethod]
    public async Task Cpu_skips_enumeration_and_enumeration_failure_falls_back()
    {
        int scans = 0;
        using var runtime = new SpeechRuntime(_ => "model", () =>
        {
            scans++;
            throw new InvalidOperationException();
        }, (_, adapter) => Handle(adapter), SpeechMode.Cpu);
        await runtime.LoadInstalledAsync(CancellationToken.None);
        Assert.AreEqual(0, scans);
        await runtime.ReloadAsync(SpeechMode.Automatic, CancellationToken.None);
        Assert.AreEqual(1, scans);
        Assert.AreEqual(SpeechBackend.Cpu, runtime.Backend);
        StringAssert.Contains(runtime.StatusText, "Could not list graphics devices");
    }

    [TestMethod]
    public async Task Switch_waits_for_recognition_and_releases_old_model_before_loading_next()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int creations = 0;
        var lifetime = new Lifetime();
        using var runtime = new SpeechRuntime(_ => "model", () => [Intel, Nvidia], (_, adapter) =>
        {
            if (++creations > 1)
            {
                Assert.AreEqual(1, lifetime.Disposals);
                return Handle(adapter);
            }
            return new RecognizerHandle(SpeechBackend.Cpu, lifetime, _ =>
            {
                started.SetResult();
                release.Task.GetAwaiter().GetResult();
                return "original result";
            });
        }, SpeechMode.Cpu);
        await runtime.LoadInstalledAsync(CancellationToken.None);
        Task<string> recognition = runtime.RecognizeAsync([0.1f], CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task reload = runtime.ReloadAsync(SpeechMode.Nvidia, CancellationToken.None);
        try
        {
            Assert.AreEqual(SpeechRuntimeState.Loading, runtime.State);
            Assert.AreEqual(1, creations);
            Assert.AreEqual(0, lifetime.Disposals);
        }
        finally { release.TrySetResult(); }
        Assert.AreEqual("original result", await recognition);
        await reload;
        Assert.AreEqual(2, creations);
        Assert.AreEqual(SpeechBackend.DirectMl, runtime.Backend);
    }

    private static RecognizerHandle Handle(SpeechAdapter? adapter) =>
        new(adapter is null ? SpeechBackend.Cpu : SpeechBackend.DirectMl, new Lifetime(), _ => "text");

    private sealed class Lifetime : IDisposable
    {
        internal int Disposals;
        public void Dispose() => Disposals++;
    }
}
