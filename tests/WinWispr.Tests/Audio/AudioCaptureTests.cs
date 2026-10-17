using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Audio;
using WinWispr.Views;

namespace WinWispr.Tests.Audio;

[TestClass]
public sealed class AudioCaptureTests
{
    [TestMethod]
    public void Waveform_capture_and_render_use_the_smooth_60_hz_cadence()
    {
        int bufferSize = ReadPrivateConstant<int>(typeof(WinMmCaptureDevice), "BufferSize");
        int refreshMilliseconds = ReadPrivateConstant<int>(
            typeof(OverlayWindow),
            "WaveformRefreshIntervalMilliseconds");
        int historyPeakCount = ReadPrivateConstant<int>(typeof(WinWispr.App), "WaveformHistoryPeakCount");

        Assert.AreEqual(512, bufferSize);
        Assert.AreEqual(16, refreshMilliseconds);
        Assert.AreEqual(32, historyPeakCount);
    }

    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    public async Task Supported_host_capture_can_start_collect_and_stop()
    {
        if (!OperatingSystem.IsWindows()
            || RuntimeInformation.OSArchitecture is not (Architecture.X64 or Architecture.Arm64))
            Assert.Inconclusive("This live microphone check requires a supported x64 or ARM64 Windows host.");

        using var capture = AudioCapture.CreateNative(maxSamples: CaptureFormat.SampleRate * 2);
        await capture.StartAsync(null, CancellationToken.None);
        await Task.Delay(1000);
        using CapturedAudio samples = await capture.StopAsync(CancellationToken.None);

        Assert.IsTrue(samples.SampleCount > 0, "The microphone returned no samples.");
    }

    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    public async Task Native_capture_can_pause_resume_stop_at_the_limit_and_start_again()
    {
        if (!OperatingSystem.IsWindows()
            || RuntimeInformation.OSArchitecture is not (Architecture.X64 or Architecture.Arm64))
            Assert.Inconclusive("This live microphone check requires a supported Windows host.");

        using var capture = AudioCapture.CreateNative(maxSamples: CaptureFormat.SampleRate);
        var stopped = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        capture.LimitReached += () => _ = Task.Run(async () =>
        {
            try
            {
                using CapturedAudio audio = await capture.StopAsync(CancellationToken.None);
                stopped.TrySetResult(audio.SampleCount);
            }
            catch (Exception exception)
            {
                stopped.TrySetException(exception);
            }
        });
        capture.Failed += () => stopped.TrySetException(new InvalidOperationException("Audio worker failed."));

        await capture.StartAsync(null, CancellationToken.None);
        for (int index = 0; index < 5; index++)
        {
            await Task.Delay(30);
            await capture.PauseAsync(CancellationToken.None);
            await Task.Delay(30);
            await capture.ResumeAsync(CancellationToken.None);
        }
        Assert.AreEqual(CaptureFormat.SampleRate, await stopped.Task.WaitAsync(TimeSpan.FromSeconds(15)));

        await capture.StartAsync(null, CancellationToken.None);
        await Task.Delay(100);
        using CapturedAudio restarted = await capture.StopAsync(CancellationToken.None);
        Assert.IsTrue(restarted.SampleCount > 0);
    }

    [TestMethod]
    public void Paused_buffer_ignores_samples_and_resumes_same_session()
    {
        var buffer = new SessionAudioBuffer(maxSamples: 8);
        buffer.Append([0.1f, 0.2f]);
        buffer.Pause();
        buffer.Append([0.8f, 0.9f]);
        buffer.Resume();
        buffer.Append([0.3f]);

        using CapturedAudio audio = buffer.Complete();
        CollectionAssert.AreEqual(new[] { 0.1f, 0.2f, 0.3f }, audio.Chunks[0]);
    }

    [TestMethod]
    public void Limit_is_raised_once_and_extra_samples_are_rejected()
    {
        var buffer = new SessionAudioBuffer(maxSamples: 4);
        int limitEvents = 0;
        buffer.LimitReached += () => limitEvents++;

        buffer.Append([0.1f, 0.2f, 0.3f]);
        buffer.Append([0.4f, 0.5f]);
        buffer.Append([0.6f]);

        Assert.AreEqual(1, limitEvents);
        using CapturedAudio audio = buffer.Complete();
        CollectionAssert.AreEqual(new[] { 0.1f, 0.2f, 0.3f, 0.4f }, audio.Chunks[0]);
    }

    [TestMethod]
    public void Discard_clears_samples_and_completes_the_session()
    {
        var buffer = new SessionAudioBuffer(maxSamples: 4);
        buffer.Append([0.1f, 0.2f]);

        buffer.Discard();

        Assert.AreEqual(0, buffer.Count);
        AssertInvalidOperation(() => buffer.Complete());
    }

    [TestMethod]
    public void Complete_is_single_use()
    {
        var buffer = new SessionAudioBuffer(maxSamples: 4);
        buffer.Append([0.1f]);

        using CapturedAudio audio = buffer.Complete();
        CollectionAssert.AreEqual(new[] { 0.1f }, audio.Chunks[0]);
        AssertInvalidOperation(() => buffer.Complete());
    }

    [TestMethod]
    public void Complete_returns_chunked_audio_with_the_exact_sample_count()
    {
        var buffer = new SessionAudioBuffer(maxSamples: 64 * 1024 + 1);
        float[] input = new float[64 * 1024 + 1];
        input[0] = 0.1f;
        input[^1] = 0.3f;
        buffer.Append(input);

        using CapturedAudio audio = buffer.Complete();

        Assert.AreEqual(input.Length, audio.SampleCount);
        Assert.AreEqual(2, audio.Chunks.Count);
        Assert.AreEqual(64 * 1024, audio.Chunks[0].Length);
        Assert.AreEqual(1, audio.Chunks[1].Length);
        Assert.AreEqual(0.1f, audio.Chunks[0][0]);
        Assert.AreEqual(0.3f, audio.Chunks[1][0]);
    }

    [TestMethod]
    public void Captured_audio_flattens_chunks_in_order_for_single_waveform_submission()
    {
        var buffer = new SessionAudioBuffer(maxSamples: 64 * 1024 + 2);
        float[] input = new float[64 * 1024 + 2];
        input[0] = 0.1f;
        input[64 * 1024] = 0.2f;
        input[^1] = 0.3f;
        buffer.Append(input);

        using CapturedAudio audio = buffer.Complete();

        CollectionAssert.AreEqual(input, audio.ToArray());
    }

    [TestMethod]
    public void Completed_buffer_rejects_append_pause_resume_and_discard()
    {
        var buffer = new SessionAudioBuffer(maxSamples: 4);
        buffer.Append([0.1f]);
        using (buffer.Complete())
        {
        }

        AssertInvalidOperation(() => buffer.Append([0.2f]));
        AssertInvalidOperation(buffer.Pause);
        AssertInvalidOperation(buffer.Resume);
        AssertInvalidOperation(buffer.Discard);
    }

    [TestMethod]
    public void Peak_is_normalized_and_contains_no_samples()
    {
        var buffer = new SessionAudioBuffer(maxSamples: 4);
        float peak = 0;
        buffer.PeakChanged += value => peak = value;

        buffer.Append([-2f, 0.25f, 0.75f]);

        Assert.AreEqual(1f, peak);
        Assert.AreEqual(3, buffer.Count);
    }

    [TestMethod]
    public void NaN_samples_never_publish_a_NaN_peak()
    {
        var buffer = new SessionAudioBuffer(maxSamples: 4);
        float peak = 0;
        buffer.PeakChanged += value => peak = value;

        buffer.Append([float.NaN, 0.5f]);

        Assert.AreEqual(0.5f, peak);
        Assert.IsFalse(float.IsNaN(peak));
    }

    [TestMethod]
    public void Pause_and_resume_are_idempotent()
    {
        var buffer = new SessionAudioBuffer(maxSamples: 4);
        buffer.Pause();
        buffer.Pause();
        buffer.Append([0.1f]);
        buffer.Resume();
        buffer.Resume();
        buffer.Append([0.2f]);

        using CapturedAudio audio = buffer.Complete();
        CollectionAssert.AreEqual(new[] { 0.2f }, audio.Chunks[0]);
    }

    private static void AssertInvalidOperation(Action action)
    {
        try
        {
            action();
            Assert.Fail("Expected InvalidOperationException.");
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static T ReadPrivateConstant<T>(Type type, string name)
    {
        FieldInfo? field = type.GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.IsNotNull(field, $"Missing private constant {type.Name}.{name}.");
        return (T)field!.GetRawConstantValue()!;
    }
}
