using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Audio;

namespace WinWispr.Tests.Audio;

[TestClass]
public sealed class WinMmCleanupTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task Cleanup_times_out_even_when_the_worker_owns_the_gate()
    {
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        using var capture = new WinMmCaptureDevice(new SessionAudioBuffer(8),
            cleanupTimeout: TimeSpan.FromMilliseconds(100));
        object gate = Read<object>(capture, "_gate");
        var worker = new Thread(() =>
        {
            lock (gate)
            {
                entered.Set();
                release.Wait();
            }
        }) { IsBackground = true };
        Write(capture, "_worker", worker);
        worker.Start();
        try
        {
            Assert.IsTrue(entered.Wait(Deadline));
            await Task.Run(capture.Discard).WaitAsync(Deadline);
            Assert.IsTrue(worker.IsAlive, "Discard must return before the blocked worker is released.");
            Assert.IsFalse(Read<Task<bool>>(capture, "_cleanupTask").IsCompleted);
        }
        finally
        {
            release.Set();
            Assert.IsTrue(worker.Join(Deadline));
            await Read<Task<bool>>(capture, "_cleanupTask").WaitAsync(Deadline);
        }
    }

    [TestMethod]
    public async Task Cleanup_times_out_during_worker_join_without_starting_another_cleanup()
    {
        using var release = new ManualResetEventSlim();
        int resets = 0;
        using var capture = new WinMmCaptureDevice(new SessionAudioBuffer(8),
            cleanupTimeout: TimeSpan.FromMilliseconds(100),
            stopAndReset: _ => Interlocked.Increment(ref resets));
        var worker = new Thread(() => release.Wait()) { IsBackground = true };
        Write(capture, "_worker", worker);
        worker.Start();
        try
        {
            await Task.Run(capture.Discard).WaitAsync(Deadline);
            await Task.Run(capture.Dispose).WaitAsync(Deadline);
            Assert.IsTrue(worker.IsAlive);
            Assert.IsFalse(Read<Task<bool>>(capture, "_cleanupTask").IsCompleted);
        }
        finally
        {
            release.Set();
            Assert.IsTrue(worker.Join(Deadline));
            await Read<Task<bool>>(capture, "_cleanupTask").WaitAsync(Deadline);
        }
        Assert.AreEqual(1, resets);
    }

    [TestMethod]
    public async Task Cleanup_times_out_while_native_reset_is_blocked()
    {
        using var release = new ManualResetEventSlim();
        using var entered = new ManualResetEventSlim();
        using var capture = new WinMmCaptureDevice(new SessionAudioBuffer(8),
            cleanupTimeout: TimeSpan.FromMilliseconds(100), stopAndReset: _ =>
            {
                entered.Set();
                release.Wait();
            });
        try
        {
            await Task.Run(capture.Discard).WaitAsync(Deadline);
            Assert.IsTrue(entered.Wait(Deadline));
            Assert.IsFalse(Read<Task<bool>>(capture, "_cleanupTask").IsCompleted);
        }
        finally
        {
            release.Set();
            await Read<Task<bool>>(capture, "_cleanupTask").WaitAsync(Deadline);
        }
    }

    [TestMethod]
    public async Task Worker_drains_the_final_partial_buffer_only_after_reset_returns()
    {
        using var releaseReset = new ManualResetEventSlim();
        using var resetEntered = new ManualResetEventSlim();
        var buffer = new SessionAudioBuffer(8);
        nint headerPointer = 0;
        Type headerType = typeof(WinMmCaptureDevice).GetNestedType("WaveHeader", BindingFlags.NonPublic)!;
        using var capture = new WinMmCaptureDevice(buffer, cleanupTimeout: Deadline,
            stopAndReset: _ =>
            {
                resetEntered.Set();
                releaseReset.Wait();
                object header = Marshal.PtrToStructure(headerPointer, headerType)!;
                headerType.GetField("BytesRecorded", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(header, 2u);
                headerType.GetField("Flags", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(header, 1u);
                Marshal.StructureToPtr(header, headerPointer, false);
            });

        Type bufferType = typeof(WinMmCaptureDevice).GetNestedType("WaveBuffer", BindingFlags.NonPublic)!;
        object nativeBuffer = Activator.CreateInstance(bufferType,
            BindingFlags.Instance | BindingFlags.NonPublic, null, [2], null)!;
        byte[] data = (byte[])bufferType.GetProperty("Data", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(nativeBuffer)!;
        data[0] = 0xFF;
        data[1] = 0x7F;
        headerPointer = (nint)bufferType.GetProperty("HeaderPointer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(nativeBuffer)!;
        Read<IList>(capture, "_buffers").Add(nativeBuffer);
        var ready = new EventWaitHandle(true, EventResetMode.AutoReset);
        Write(capture, "_dataReady", ready);
        MethodInfo process = typeof(WinMmCaptureDevice).GetMethod("ProcessBuffers", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var worker = new Thread(() => process.Invoke(capture, null)) { IsBackground = true };
        Task<CapturedAudio> stop = Task.Run(capture.Stop);
        try
        {
            Assert.IsTrue(resetEntered.Wait(Deadline));
            Write(capture, "_worker", worker);
            worker.Start();
            Assert.IsTrue(SpinWait.SpinUntil(() => !worker.IsAlive ||
                (worker.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, Deadline));
            Assert.IsTrue(worker.IsAlive, "The worker must not exit while reset is still returning the final header.");
        }
        finally
        {
            releaseReset.Set();
        }
        using CapturedAudio samples = await stop.WaitAsync(Deadline);
        Assert.AreEqual(1, samples.SampleCount);
        CollectionAssert.AreEqual(new[] { 1f }, samples.ToArray());
        Assert.IsFalse(worker.IsAlive);
    }

    private static T Read<T>(object instance, string name) =>
        (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

    private static void Write(object instance, string name, object value) =>
        instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(instance, value);
}
