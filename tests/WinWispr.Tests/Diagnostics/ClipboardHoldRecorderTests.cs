using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Diagnostics;

namespace WinWispr.Tests.Diagnostics;

[TestClass]
public sealed class ClipboardHoldRecorderTests
{
    private static readonly long Origin = Ms(1_000);

    [TestMethod]
    public void Each_run_of_one_window_is_a_hold_with_its_offset_and_length()
    {
        var recorder = new ClipboardHoldRecorder(new TicksClock(), Origin, window => $"process-{window}.exe");

        recorder.Observe(Origin + Ms(5), 0);
        recorder.Observe(Origin + Ms(10), 7);
        recorder.Observe(Origin + Ms(20), 7);
        recorder.Observe(Origin + Ms(160), 8);
        recorder.Observe(Origin + Ms(162), 0);
        recorder.Observe(Origin + Ms(300), 9);

        CollectionAssert.AreEqual(
            new[]
            {
                new ClipboardHold("process-7.exe", 10, 150),
                new ClipboardHold("process-8.exe", 160, 2),
                new ClipboardHold("process-9.exe", 300, 5)
            },
            recorder.Finish(Origin + Ms(305)).ToArray());
    }

    [TestMethod]
    public void A_window_is_named_once_when_its_first_hold_starts()
    {
        var named = new List<nint>();
        var recorder = new ClipboardHoldRecorder(new TicksClock(), Origin, window =>
        {
            named.Add(window);
            return "monitor.exe";
        });

        for (int hold = 0; hold < 3; hold++)
        {
            recorder.Observe(Origin + Ms(100 * hold), 7);
            recorder.Observe(Origin + Ms(100 * hold + 20), 0);
        }

        CollectionAssert.AreEqual(new nint[] { 7 }, named);
        Assert.HasCount(3, recorder.Finish(Origin + Ms(400)));
    }

    [TestMethod]
    public void Holds_before_the_set_get_negative_offsets_and_the_count_is_capped()
    {
        var recorder = new ClipboardHoldRecorder(new TicksClock(), Origin, _ => "monitor.exe");

        for (int hold = 0; hold < ClipboardHoldRecorder.MaxHolds + 5; hold++)
        {
            recorder.Observe(Origin + Ms(10 * hold - 50), 7);
            recorder.Observe(Origin + Ms(10 * hold - 45), 0);
        }

        IReadOnlyList<ClipboardHold> holds = recorder.Finish(Origin + Ms(1_000));
        Assert.HasCount(ClipboardHoldRecorder.MaxHolds, holds);
        Assert.AreEqual(new ClipboardHold("monitor.exe", -50, 5), holds[0]);
    }

    [TestMethod]
    public void Nothing_seen_is_no_hold()
    {
        var recorder = new ClipboardHoldRecorder(new TicksClock(), Origin, _ => throw new AssertFailedException());

        recorder.Observe(Origin + Ms(10), 0);

        Assert.IsEmpty(recorder.Finish(Origin + Ms(20)));
    }

    [TestMethod]
    public async Task A_trace_is_seen_by_the_flow_that_opened_it_and_nowhere_else()
    {
        DeliveryTrace? outside = null;
        Task elsewhere = Task.Run(() => outside = DeliveryTrace.Current);
        await elsewhere;

        DeliveryTrace trace = DeliveryTrace.Begin();
        DeliveryTrace? inside = await Task.Run(() => DeliveryTrace.Current);

        Assert.IsNull(outside);
        Assert.AreSame(trace, inside);
    }

    [TestMethod]
    public void A_trace_with_nothing_recorded_writes_no_details()
    {
        Assert.IsNull(new DeliveryTrace().ToDiagnostics(null));
        Assert.AreEqual(
            new DeliveryDiagnostics(2, null, null, false, null),
            new DeliveryTrace { ClipboardSetAttempts = 2 }.ToDiagnostics(null));
    }

    private static long Ms(int milliseconds) => TimeSpan.FromMilliseconds(milliseconds).Ticks;

    private sealed class TicksClock : TimeProvider
    {
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }
}
