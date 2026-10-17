using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Audio;

namespace WinWispr.Tests.Audio;

[TestClass]
public sealed class ReplayCaptureDeviceTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task The_clip_is_delivered_in_real_time_and_then_the_device_falls_silent()
    {
        var clock = new ManualTimeProvider();
        var buffer = new SessionAudioBuffer(4096);
        using var device = new ReplayCaptureDevice(buffer, Clip(600), clock);
        await device.StartAsync(null, CancellationToken.None);

        clock.SetElapsed(TimeSpan.FromMilliseconds(16));
        WaitUntil(() => device.Position == 256);
        Assert.AreEqual(256, buffer.Count);

        clock.SetElapsed(TimeSpan.FromSeconds(1));
        WaitUntil(() => device.Position == 600);
        Assert.AreEqual(600, buffer.Count);

        clock.SetElapsed(TimeSpan.FromSeconds(5));
        await Task.Delay(50);
        Assert.AreEqual(600, buffer.Count, "Nothing is delivered after the clip ends.");

        using CapturedAudio audio = device.Stop();
        CollectionAssert.AreEqual(Clip(600), audio.ToArray());
    }

    [TestMethod]
    public async Task Samples_that_pass_while_paused_are_lost()
    {
        var clock = new ManualTimeProvider();
        var buffer = new SessionAudioBuffer(4096);
        float[] clip = Clip(2000);
        using var device = new ReplayCaptureDevice(buffer, clip, clock);
        await device.StartAsync(null, CancellationToken.None);
        clock.SetElapsed(TimeSpan.FromMilliseconds(16));
        WaitUntil(() => device.Position == 256);

        device.Pause();
        clock.SetElapsed(TimeSpan.FromMilliseconds(48));
        WaitUntil(() => device.Position == 768);
        device.Resume();
        clock.SetElapsed(TimeSpan.FromMilliseconds(64));
        WaitUntil(() => device.Position == 1024);
        using CapturedAudio audio = device.Stop();

        float[] expected = [.. clip[..256], .. clip[768..1024]];
        CollectionAssert.AreEqual(expected, audio.ToArray());
    }

    [TestMethod]
    public async Task Stop_delivers_what_is_already_due()
    {
        var clock = new ManualTimeProvider();
        var buffer = new SessionAudioBuffer(4096);
        using var device = new ReplayCaptureDevice(buffer, Clip(2000), clock);
        await device.StartAsync(null, CancellationToken.None);
        clock.SetElapsed(TimeSpan.FromMilliseconds(20));

        using CapturedAudio audio = device.Stop();

        Assert.AreEqual(320, audio.SampleCount);
    }

    [TestMethod]
    public async Task Discard_drops_the_clip_and_a_second_start_is_rejected()
    {
        var buffer = new SessionAudioBuffer(4096);
        using var device = new ReplayCaptureDevice(buffer, Clip(600), new ManualTimeProvider());
        await device.StartAsync(null, CancellationToken.None);

        Assert.Throws<InvalidOperationException>(() => device.StartAsync(null, CancellationToken.None));
        device.Discard();

        Assert.AreEqual(0, buffer.Count);
    }

    [TestMethod]
    public async Task A_raw_pcm16_file_becomes_the_clip()
    {
        using var directory = new TemporaryDirectory();
        string path = Path.Combine(directory.Path, "clip.pcm");
        await File.WriteAllBytesAsync(path, [0x00, 0x40, 0x00, 0xC0]);
        var buffer = new SessionAudioBuffer(16);
        using var device = ReplayCaptureDevice.FromPcm16File(buffer, path);
        await device.StartAsync(null, CancellationToken.None);
        WaitUntil(() => device.Position == 2);

        using CapturedAudio audio = device.Stop();

        float[] samples = audio.ToArray();
        Assert.HasCount(2, samples);
        Assert.AreEqual(16384 / 32767f, samples[0], 1e-6f);
        Assert.AreEqual(-16384 / 32767f, samples[1], 1e-6f);
    }

    private static float[] Clip(int length) =>
        Enumerable.Range(0, length).Select(index => index / 10_000f).ToArray();

    private static void WaitUntil(Func<bool> condition) =>
        Assert.IsTrue(SpinWait.SpinUntil(condition, Deadline), "The replay did not reach the expected position.");

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _milliseconds;

        public override long TimestampFrequency => 1000;

        public override long GetTimestamp() => Interlocked.Read(ref _milliseconds);

        public void SetElapsed(TimeSpan elapsed) =>
            Interlocked.Exchange(ref _milliseconds, (long)elapsed.TotalMilliseconds);
    }
}
