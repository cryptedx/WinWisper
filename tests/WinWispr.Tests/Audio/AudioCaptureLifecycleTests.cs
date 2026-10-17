using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Audio;

namespace WinWispr.Tests.Audio;

/// <summary>The lifecycle rules of <see cref="AudioCapture"/>, driven through a controllable device.</summary>
[TestClass]
public sealed class AudioCaptureLifecycleTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task Start_opens_a_device_on_the_session_buffer_and_passes_the_microphone()
    {
        using var fixture = new CaptureFixture();

        await fixture.Capture.StartAsync("microphone-1", CancellationToken.None);

        Assert.HasCount(1, fixture.Devices);
        Assert.AreEqual("microphone-1", fixture.Devices[0].StartedWith);
    }

    [TestMethod]
    public async Task A_second_start_while_running_is_rejected_without_opening_another_device()
    {
        using var fixture = new CaptureFixture();
        await fixture.Capture.StartAsync(null, CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Capture.StartAsync(null, CancellationToken.None));

        Assert.HasCount(1, fixture.Devices);
    }

    [TestMethod]
    public async Task Pause_and_resume_reach_the_device_and_the_buffer()
    {
        using var fixture = new CaptureFixture();
        await fixture.Capture.StartAsync(null, CancellationToken.None);
        FakeCaptureDevice device = fixture.Devices[0];
        device.Deliver(0.1f);

        await fixture.Capture.PauseAsync(CancellationToken.None);
        device.Deliver(0.9f);
        await fixture.Capture.ResumeAsync(CancellationToken.None);
        device.Deliver(0.2f);
        using CapturedAudio audio = await fixture.Capture.StopAsync(CancellationToken.None);

        CollectionAssert.AreEqual(new[] { 0.1f, 0.2f }, audio.ToArray());
        Assert.AreEqual(1, device.PauseCalls);
        Assert.AreEqual(1, device.ResumeCalls);
    }

    [TestMethod]
    public async Task Stop_returns_the_completed_audio_and_the_capture_can_start_again()
    {
        using var fixture = new CaptureFixture();
        await fixture.Capture.StartAsync(null, CancellationToken.None);
        fixture.Devices[0].Deliver(0.5f, 0.25f);

        using (CapturedAudio audio = await fixture.Capture.StopAsync(CancellationToken.None))
            CollectionAssert.AreEqual(new[] { 0.5f, 0.25f }, audio.ToArray());
        await fixture.Capture.StartAsync(null, CancellationToken.None);

        Assert.AreEqual(1, fixture.Devices[0].StopCalls);
        Assert.HasCount(2, fixture.Devices);
    }

    [TestMethod]
    public async Task Discard_drops_the_audio_and_the_capture_can_start_again()
    {
        using var fixture = new CaptureFixture();
        await fixture.Capture.StartAsync(null, CancellationToken.None);
        fixture.Devices[0].Deliver(0.5f);

        await fixture.Capture.DiscardAsync();
        await fixture.Capture.StartAsync(null, CancellationToken.None);

        Assert.AreEqual(1, fixture.Devices[0].DiscardCalls);
        Assert.HasCount(2, fixture.Devices);
    }

    [TestMethod]
    public async Task The_limit_is_raised_once_and_extra_samples_are_dropped()
    {
        using var fixture = new CaptureFixture(maxSamples: 4);
        int limits = 0;
        fixture.Capture.LimitReached += () => limits++;
        await fixture.Capture.StartAsync(null, CancellationToken.None);

        fixture.Devices[0].Deliver(0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 0.6f);
        fixture.Devices[0].Deliver(0.7f);
        using CapturedAudio audio = await fixture.Capture.StopAsync(CancellationToken.None);

        Assert.AreEqual(1, limits);
        Assert.AreEqual(4, audio.SampleCount);
    }

    [TestMethod]
    public async Task Peaks_are_forwarded()
    {
        using var fixture = new CaptureFixture();
        var peaks = new List<float>();
        fixture.Capture.PeakChanged += peaks.Add;
        await fixture.Capture.StartAsync(null, CancellationToken.None);

        fixture.Devices[0].Deliver(0.25f, -0.5f);

        CollectionAssert.AreEqual(new[] { 0.5f }, peaks);
    }

    [TestMethod]
    public async Task Failure_of_the_current_device_is_raised_and_a_finished_device_is_ignored()
    {
        using var fixture = new CaptureFixture();
        int failures = 0;
        fixture.Capture.Failed += () => failures++;
        await fixture.Capture.StartAsync(null, CancellationToken.None);
        FakeCaptureDevice first = fixture.Devices[0];

        first.Fail();
        Assert.AreEqual(1, failures);
        (await fixture.Capture.StopAsync(CancellationToken.None)).Dispose();
        first.Fail();
        Assert.AreEqual(1, failures, "A device that has been stopped must not report failures.");
        await fixture.Capture.StartAsync(null, CancellationToken.None);
        fixture.Devices[1].Fail();

        Assert.AreEqual(2, failures);
    }

    [TestMethod]
    public async Task A_failed_start_disposes_the_device_and_the_capture_can_start_again()
    {
        using var fixture = new CaptureFixture { StartFailures = 1 };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Capture.StartAsync(null, CancellationToken.None));
        Assert.AreEqual(1, fixture.Devices[0].DisposeCalls);
        await fixture.Capture.StartAsync(null, CancellationToken.None);

        Assert.HasCount(2, fixture.Devices);
        Assert.AreEqual(0, fixture.Devices[1].DisposeCalls);
    }

    [TestMethod]
    public async Task Stop_and_discard_do_not_block_the_caller()
    {
        using var fixture = new CaptureFixture();
        using var stopGate = new ManualResetEventSlim();
        using var discardGate = new ManualResetEventSlim();
        await fixture.Capture.StartAsync(null, CancellationToken.None);
        fixture.Devices[0].StopGate = stopGate;

        Task<CapturedAudio> stop = fixture.Capture.StopAsync(CancellationToken.None);
        Assert.IsFalse(stop.IsCompleted, "Stop must hand the device's blocking work to the thread pool.");
        stopGate.Set();
        (await stop.WaitAsync(Deadline)).Dispose();

        await fixture.Capture.StartAsync(null, CancellationToken.None);
        fixture.Devices[1].DiscardGate = discardGate;
        Task discard = fixture.Capture.DiscardAsync();
        Assert.IsFalse(discard.IsCompleted, "Discard must hand the device's blocking work to the thread pool.");
        discardGate.Set();
        await discard.WaitAsync(Deadline);
    }

    [TestMethod]
    public async Task Stop_without_a_running_capture_returns_empty_audio()
    {
        using var fixture = new CaptureFixture();

        using CapturedAudio audio = await fixture.Capture.StopAsync(CancellationToken.None);

        Assert.AreEqual(0, audio.SampleCount);
    }

    [TestMethod]
    public async Task Pause_and_resume_without_a_running_capture_are_rejected()
    {
        using var fixture = new CaptureFixture();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Capture.PauseAsync(CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.Capture.ResumeAsync(CancellationToken.None));
    }

    [TestMethod]
    public async Task Dispose_discards_a_running_capture_and_rejects_a_new_start()
    {
        var fixture = new CaptureFixture();
        await fixture.Capture.StartAsync(null, CancellationToken.None);

        fixture.Capture.Dispose();

        Assert.AreEqual(1, fixture.Devices[0].DiscardCalls);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => fixture.Capture.StartAsync(null, CancellationToken.None));
    }

    private sealed class CaptureFixture : IDisposable
    {
        public CaptureFixture(int maxSamples = 1024)
        {
            Capture = new AudioCapture(buffer =>
            {
                var device = new FakeCaptureDevice(buffer);
                if (StartFailures > 0)
                {
                    StartFailures--;
                    device.StartException = new InvalidOperationException("Device unavailable.");
                }

                Devices.Add(device);
                return device;
            }, maxSamples);
        }

        public AudioCapture Capture { get; }
        public List<FakeCaptureDevice> Devices { get; } = [];
        public int StartFailures { get; set; }

        public void Dispose() => Capture.Dispose();
    }

    private sealed class FakeCaptureDevice(SessionAudioBuffer buffer) : ICaptureDevice
    {
        public event Action? Failed;

        public string? StartedWith { get; private set; }
        public Exception? StartException { get; set; }
        public ManualResetEventSlim? StopGate { get; set; }
        public ManualResetEventSlim? DiscardGate { get; set; }
        public int PauseCalls { get; private set; }
        public int ResumeCalls { get; private set; }
        public int StopCalls { get; private set; }
        public int DiscardCalls { get; private set; }
        public int DisposeCalls { get; private set; }

        public void Deliver(params float[] samples) => buffer.Append(samples);

        public void Fail() => Failed?.Invoke();

        public Task StartAsync(string? microphoneId, CancellationToken cancellationToken)
        {
            StartedWith = microphoneId;
            return StartException is null ? Task.CompletedTask : Task.FromException(StartException);
        }

        public void Pause() => PauseCalls++;

        public void Resume() => ResumeCalls++;

        public CapturedAudio Stop()
        {
            StopCalls++;
            StopGate?.Wait(Deadline);
            return buffer.Complete();
        }

        public void Discard()
        {
            DiscardCalls++;
            DiscardGate?.Wait(Deadline);
            buffer.Discard();
        }

        public void Dispose() => DisposeCalls++;
    }
}
