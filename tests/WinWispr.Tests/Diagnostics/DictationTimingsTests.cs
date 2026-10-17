using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Audio;
using WinWispr.Core;
using WinWispr.Diagnostics;
using WinWispr.Speech;

namespace WinWispr.Tests.Diagnostics;

[TestClass]
public sealed class DictationTimingsTests
{
    [TestMethod]
    public async Task Stop_is_marked_and_its_duration_written()
    {
        using var root = new TemporaryDirectory();
        var clock = new ManualClock();
        var (timings, log) = Create(root, clock);
        DictationEffects effects = timings.Wrap(Effects(stop: _ =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(40));
            return Task.FromResult(CapturedAudio.FromArray([0.5f]));
        }));

        using CapturedAudio audio = await effects.StopCaptureAsync(CancellationToken.None);

        Entry[] entries = await ReadAsync(root, log);
        CollectionAssert.AreEqual(
            new[] { "CaptureStopStarted", "CaptureStopCompleted" },
            entries.Select(entry => entry.Event).ToArray());
        Assert.AreEqual("Processing", entries[0].RecordingState);
        Assert.AreEqual(40, entries[1].Duration);
        Assert.AreEqual(1, audio.SampleCount);
    }

    [TestMethod]
    public async Task Recognition_timeout_is_written_before_the_completed_duration()
    {
        using var root = new TemporaryDirectory();
        var clock = new ManualClock();
        var (timings, log) = Create(root, clock);
        DictationEffects effects = timings.Wrap(Effects(recognize: (_, _) =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(25));
            throw new SpeechRecognitionTimeoutException(TimeSpan.FromSeconds(1));
        }));

        using var audio = CapturedAudio.FromArray([0f]);
        await Assert.ThrowsAsync<SpeechRecognitionTimeoutException>(
            () => effects.RecognizeAsync(audio, CancellationToken.None));

        Entry[] entries = await ReadAsync(root, log);
        CollectionAssert.AreEqual(
            new[] { "RecognitionStarted", "RecognitionTimedOut", "RecognitionCompleted" },
            entries.Select(entry => entry.Event).ToArray());
        Assert.AreEqual("SpeechRecognitionTimeoutException", entries[1].ExceptionType);
        Assert.AreEqual(25, entries[2].Duration);
    }

    [TestMethod]
    public async Task Recognition_failure_is_written_by_exception_type()
    {
        using var root = new TemporaryDirectory();
        var (timings, log) = Create(root, new ManualClock());
        DictationEffects effects = timings.Wrap(Effects(
            recognize: (_, _) => throw new InvalidOperationException("SECRET_TRANSCRIPT_SENTINEL")));

        using var audio = CapturedAudio.FromArray([0f]);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => effects.RecognizeAsync(audio, CancellationToken.None));

        Entry[] entries = await ReadAsync(root, log);
        Assert.AreEqual("RecognitionFailed", entries[1].Event);
        Assert.AreEqual("InvalidOperationException", entries[1].ExceptionType);
    }

    [TestMethod]
    public async Task Delivery_writes_its_result_and_duration()
    {
        using var root = new TemporaryDirectory();
        var clock = new ManualClock();
        var (timings, log) = Create(root, clock);
        DictationEffects effects = timings.Wrap(Effects(deliver: (_, _, _) =>
        {
            clock.Advance(TimeSpan.FromMilliseconds(12));
            return Task.FromResult(DeliveryResult.PasteSent);
        }));

        DeliveryResult result = await effects.DeliverAsync(new WindowTarget(1), "text", CancellationToken.None);

        Entry[] entries = await ReadAsync(root, log);
        Assert.AreEqual(DeliveryResult.PasteSent, result);
        CollectionAssert.AreEqual(
            new[] { "DeliveryStarted", "DeliveryCompleted" },
            entries.Select(entry => entry.Event).ToArray());
        Assert.AreEqual("PasteSent", entries[1].DeliveryResult);
        Assert.AreEqual(12, entries[1].Duration);
    }

    [TestMethod]
    public async Task Delivery_failure_is_written_as_clipboard_unavailable()
    {
        using var root = new TemporaryDirectory();
        var (timings, log) = Create(root, new ManualClock());
        DictationEffects effects = timings.Wrap(Effects(
            deliver: (_, _, _) => throw new InvalidOperationException("Could not set clipboard content.")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => effects.DeliverAsync(new WindowTarget(1), "text", CancellationToken.None));

        Entry[] entries = await ReadAsync(root, log);
        CollectionAssert.AreEqual(
            new[] { "DeliveryStarted", "ClipboardUnavailable", "DeliveryCompleted" },
            entries.Select(entry => entry.Event).ToArray());
        Assert.IsNull(entries[2].DeliveryResult);
    }

    [TestMethod]
    public async Task Cancelled_delivery_is_not_written_as_a_failure()
    {
        using var root = new TemporaryDirectory();
        var (timings, log) = Create(root, new ManualClock());
        DictationEffects effects = timings.Wrap(Effects(
            deliver: (_, _, _) => throw new OperationCanceledException()));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => effects.DeliverAsync(new WindowTarget(1), "text", CancellationToken.None));

        Entry[] entries = await ReadAsync(root, log);
        CollectionAssert.AreEqual(
            new[] { "DeliveryStarted", "DeliveryCompleted" },
            entries.Select(entry => entry.Event).ToArray());
    }

    [TestMethod]
    public void Wrapping_keeps_every_other_effect()
    {
        using var root = new TemporaryDirectory();
        var (timings, _) = Create(root, new ManualClock());
        DictationEffects inner = Effects() with
        {
            TaskCapturedAsync = (_, _) => Task.CompletedTask,
            TaskCapturedRawAsync = (_, _, _) => Task.CompletedTask,
            TryResumeTaskDraft = () => true,
            CurrentForegroundWindow = () => new WindowTarget(7),
            MuteMeetingAsync = _ => Task.CompletedTask,
            RestoreMeetingAsync = () => Task.FromResult<DictationNotice?>(null)
        };

        DictationEffects wrapped = timings.Wrap(inner);

        Assert.AreSame(inner.StartCaptureAsync, wrapped.StartCaptureAsync);
        Assert.AreSame(inner.PauseCaptureAsync, wrapped.PauseCaptureAsync);
        Assert.AreSame(inner.ResumeCaptureAsync, wrapped.ResumeCaptureAsync);
        Assert.AreSame(inner.DiscardCaptureAsync, wrapped.DiscardCaptureAsync);
        Assert.AreSame(inner.TaskCapturedAsync, wrapped.TaskCapturedAsync);
        Assert.AreSame(inner.TaskCapturedRawAsync, wrapped.TaskCapturedRawAsync);
        Assert.AreSame(inner.TryResumeTaskDraft, wrapped.TryResumeTaskDraft);
        Assert.AreSame(inner.CurrentForegroundWindow, wrapped.CurrentForegroundWindow);
        Assert.AreSame(inner.MuteMeetingAsync, wrapped.MuteMeetingAsync);
        Assert.AreSame(inner.RestoreMeetingAsync, wrapped.RestoreMeetingAsync);
        Assert.AreNotSame(inner.StopCaptureAsync, wrapped.StopCaptureAsync);
        Assert.AreNotSame(inner.RecognizeAsync, wrapped.RecognizeAsync);
        Assert.AreNotSame(inner.DeliverAsync, wrapped.DeliverAsync);
    }

    [TestMethod]
    public async Task Preparation_is_measured_from_starting_to_recording()
    {
        using var root = new TemporaryDirectory();
        var clock = new ManualClock();
        var (timings, log) = Create(root, clock);

        timings.OnStateChanged(RecordingState.Starting);
        clock.Advance(TimeSpan.FromMilliseconds(180));
        timings.OnStateChanged(RecordingState.Recording);
        timings.OnStateChanged(RecordingState.Processing);
        timings.OnStateChanged(RecordingState.Idle);

        Entry[] entries = await ReadAsync(root, log);
        Assert.AreEqual(1, entries.Length);
        Assert.AreEqual("CapturePreparation", entries[0].Event);
        Assert.AreEqual(180, entries[0].Duration);
    }

    [TestMethod]
    public async Task Failed_preparation_is_measured_until_idle()
    {
        using var root = new TemporaryDirectory();
        var clock = new ManualClock();
        var (timings, log) = Create(root, clock);

        timings.OnStateChanged(RecordingState.Starting);
        clock.Advance(TimeSpan.FromMilliseconds(250));
        timings.OnStateChanged(RecordingState.Idle);
        timings.OnStateChanged(RecordingState.Idle);

        Entry[] entries = await ReadAsync(root, log);
        Assert.AreEqual(1, entries.Length);
        Assert.AreEqual(250, entries[0].Duration);
    }

    [TestMethod]
    public async Task Adapter_timings_are_recorded_as_given()
    {
        using var root = new TemporaryDirectory();
        var (timings, log) = Create(root, new ManualClock());

        timings.Record(TechnicalEvent.TeamsDiscovery, 7.5);

        Entry[] entries = await ReadAsync(root, log);
        Assert.AreEqual("TeamsDiscovery", entries.Single().Event);
        Assert.AreEqual(7.5, entries.Single().Duration);
    }

    private static (DictationTimings Timings, TechnicalLog Log) Create(TemporaryDirectory root, ManualClock clock)
    {
        var log = new TechnicalLog(root.Path, clock);
        return (new DictationTimings(log, clock), log);
    }

    private static DictationEffects Effects(
        Func<CancellationToken, Task<CapturedAudio>>? stop = null,
        Func<CapturedAudio, CancellationToken, Task<string>>? recognize = null,
        Func<WindowTarget, string, CancellationToken, Task<DeliveryResult>>? deliver = null) =>
        new(
            (_, _) => Task.FromResult<DictationNotice?>(null),
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            stop ?? (_ => Task.FromResult(CapturedAudio.FromArray([]))),
            () => Task.CompletedTask,
            recognize ?? ((_, _) => Task.FromResult(string.Empty)),
            deliver ?? ((_, _, _) => Task.FromResult(DeliveryResult.NoSpeech)));

    private static async Task<Entry[]> ReadAsync(TemporaryDirectory root, TechnicalLog log)
    {
        await log.FlushAsync();
        if (!Directory.Exists(root.Path))
            return [];
        return Directory.GetFiles(root.Path)
            .SelectMany(File.ReadAllLines)
            .Select(line =>
            {
                using var document = JsonDocument.Parse(line);
                JsonElement entry = document.RootElement;
                return new Entry(
                    entry.GetProperty("Event").GetString()!,
                    Text(entry, "RecordingState"),
                    Text(entry, "ExceptionType"),
                    Text(entry, "DeliveryResult"),
                    entry.GetProperty("DurationMilliseconds").ValueKind == JsonValueKind.Number
                        ? entry.GetProperty("DurationMilliseconds").GetDouble()
                        : null);
            })
            .ToArray();
    }

    private static string? Text(JsonElement entry, string name) =>
        entry.GetProperty(name).ValueKind == JsonValueKind.String ? entry.GetProperty(name).GetString() : null;

    private sealed record Entry(
        string Event,
        string? RecordingState,
        string? ExceptionType,
        string? DeliveryResult,
        double? Duration);

    private sealed class ManualClock : TimeProvider
    {
        private long _ticks = 1_000;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _ticks;

        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 4, 12, 0, 0, TimeSpan.Zero).AddTicks(_ticks);

        internal void Advance(TimeSpan duration) => _ticks += duration.Ticks;
    }
}
