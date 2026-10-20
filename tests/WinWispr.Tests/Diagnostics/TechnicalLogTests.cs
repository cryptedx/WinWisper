using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Core;
using WinWispr.Diagnostics;
using WinWispr.Speech;

namespace WinWispr.Tests.Diagnostics;

[TestClass]
public sealed class TechnicalLogTests
{
    [TestMethod]
    public async Task Startup_failure_is_written_without_exception_message()
    {
        using var root = new TemporaryDirectory();
        var log = new TechnicalLog(root.Path, TimeProvider.System);
        await log.WriteAsync(
            TechnicalEvent.StartupFailed,
            exception: new InvalidOperationException("SECRET_TRANSCRIPT_SENTINEL"));

        string content = string.Join("\n", Directory.GetFiles(root.Path).Select(File.ReadAllText));
        StringAssert.Contains(content, "StartupFailed");
        StringAssert.Contains(content, "InvalidOperationException");
        Assert.IsFalse(content.Contains("SECRET_TRANSCRIPT_SENTINEL", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Unhandled_exception_is_written_without_exception_message()
    {
        using var root = new TemporaryDirectory();
        var log = new TechnicalLog(root.Path, TimeProvider.System);
        await log.WriteAsync(
            TechnicalEvent.UnhandledException,
            exception: new ObjectDisposedException("SECRET_TRANSCRIPT_SENTINEL"));

        using var entry = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(root.Path).Single()));
        Assert.AreEqual("UnhandledException", entry.RootElement.GetProperty("Event").GetString());
        Assert.AreEqual(nameof(ObjectDisposedException), entry.RootElement.GetProperty("ExceptionType").GetString());
        Assert.AreEqual(unchecked((int)0x80131622), entry.RootElement.GetProperty("HResult").GetInt32());
        Assert.IsFalse(entry.RootElement.GetRawText().Contains("SECRET_TRANSCRIPT_SENTINEL", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Preparation_duration_is_written_as_a_numeric_value()
    {
        using var root = new TemporaryDirectory();
        var log = new TechnicalLog(root.Path, TimeProvider.System);
        await log.WriteAsync(TechnicalEvent.CapturePreparation, durationMilliseconds: 123.5);
        using var entry = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(root.Path).Single()));
        Assert.AreEqual(123.5, entry.RootElement.GetProperty("DurationMilliseconds").GetDouble());
    }

    [TestMethod]
    public async Task Delivery_result_is_written_by_name()
    {
        using var root = new TemporaryDirectory();
        var log = new TechnicalLog(root.Path, TimeProvider.System);
        await log.WriteAsync(TechnicalEvent.DeliveryCompleted, deliveryResult: DeliveryResult.CopiedToClipboard);
        using var entry = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Directory.GetFiles(root.Path).Single()));
        Assert.AreEqual("CopiedToClipboard", entry.RootElement.GetProperty("DeliveryResult").GetString());
    }

    [TestMethod]
    public async Task Delivery_details_appear_only_on_entries_that_have_them()
    {
        using var root = new TemporaryDirectory();
        var log = new TechnicalLog(root.Path, TimeProvider.System);
        await log.WriteAsync(TechnicalEvent.DeliveryStarted);
        await log.WriteAsync(
            TechnicalEvent.DeliveryCompleted,
            deliveryResult: DeliveryResult.PasteSent,
            delivery: new DeliveryDiagnostics(1, "notepad.exe", 152, false, [new ClipboardHold("Ditto.exe", 104, 31)]));

        string[] lines = File.ReadAllLines(Directory.GetFiles(root.Path).Single());
        using var started = System.Text.Json.JsonDocument.Parse(lines[0]);
        using var completed = System.Text.Json.JsonDocument.Parse(lines[1]);
        Assert.IsFalse(started.RootElement.TryGetProperty("Delivery", out _));
        Assert.AreEqual(
            """{"ClipboardSetAttempts":1,"TargetProcess":"notepad.exe","PasteAtMs":152,"ClipboardChangedBeforePaste":false,"ClipboardHolds":[{"Process":"Ditto.exe","AtMs":104,"ForMs":31}]}""",
            completed.RootElement.GetProperty("Delivery").GetRawText());
    }

    [TestMethod]
    public async Task Entries_are_written_in_enqueue_order()
    {
        using var root = new TemporaryDirectory();
        var log = new TechnicalLog(root.Path, TimeProvider.System);

        Task first = log.WriteAsync(TechnicalEvent.CaptureStopStarted);
        Task second = log.WriteAsync(TechnicalEvent.CaptureStopCompleted);
        await Task.WhenAll(first, second);

        string[] lines = File.ReadAllLines(Directory.GetFiles(root.Path).Single());
        Assert.AreEqual(2, lines.Length);
        using var firstEntry = System.Text.Json.JsonDocument.Parse(lines[0]);
        using var secondEntry = System.Text.Json.JsonDocument.Parse(lines[1]);
        Assert.AreEqual("CaptureStopStarted", firstEntry.RootElement.GetProperty("Event").GetString());
        Assert.AreEqual("CaptureStopCompleted", secondEntry.RootElement.GetProperty("Event").GetString());
    }

    [TestMethod]
    public async Task Exception_message_is_never_written_to_the_technical_log()
    {
        using var root = new TemporaryDirectory();
        var log = new TechnicalLog(root.Path, TimeProvider.System);

        await log.WriteAsync(
            TechnicalEvent.RecognitionFailed,
            RecordingState.Processing,
            SpeechRuntimeState.Ready,
            new InvalidOperationException("SECRET_TRANSCRIPT_SENTINEL"));

        string content = string.Join("\n", Directory.GetFiles(root.Path).Select(File.ReadAllText));
        Assert.IsFalse(content.Contains("SECRET_TRANSCRIPT_SENTINEL", StringComparison.Ordinal));
        StringAssert.Contains(content, "RecognitionFailed");
        StringAssert.Contains(content, "InvalidOperationException");
    }

    [TestMethod]
    public async Task Files_older_than_seven_days_are_pruned()
    {
        using var root = new TemporaryDirectory();
        var time = new FixedTimeProvider(new DateTimeOffset(2026, 8, 16, 12, 0, 0, TimeSpan.Zero));
        string oldPath = Path.Combine(root.Path, "2026-08-01.jsonl");
        await File.WriteAllTextAsync(oldPath, "old");
        File.SetLastWriteTimeUtc(oldPath, time.GetUtcNow().UtcDateTime.AddDays(-8));
        var log = new TechnicalLog(root.Path, time);

        await log.WriteAsync(TechnicalEvent.ModelLoadFailed);

        Assert.IsFalse(File.Exists(oldPath));
        Assert.AreEqual(1, Directory.GetFiles(root.Path, "*.jsonl").Length);
    }

    [TestMethod]
    public async Task Total_log_size_including_next_entry_stays_below_one_megabyte()
    {
        using var root = new TemporaryDirectory();
        string oldest = Path.Combine(root.Path, "2026-08-01.jsonl");
        string newest = Path.Combine(root.Path, "2026-08-15.jsonl");
        await File.WriteAllBytesAsync(oldest, new byte[700_000]);
        await File.WriteAllBytesAsync(newest, new byte[400_000]);
        File.SetLastWriteTimeUtc(oldest, DateTime.UtcNow.AddDays(-2));
        File.SetLastWriteTimeUtc(newest, DateTime.UtcNow.AddDays(-1));
        var log = new TechnicalLog(root.Path, TimeProvider.System);

        await log.WriteAsync(TechnicalEvent.ClipboardUnavailable);

        long total = Directory.GetFiles(root.Path, "*.jsonl").Sum(path => new FileInfo(path).Length);
        Assert.IsTrue(total <= TechnicalLog.MaxBytes);
        Assert.IsFalse(File.Exists(oldest));
    }

    [TestMethod]
    public async Task Logging_failure_is_swallowed()
    {
        using var root = new TemporaryDirectory();
        string blockedPath = Path.Combine(root.Path, "not-a-directory");
        await File.WriteAllTextAsync(blockedPath, "file");
        var log = new TechnicalLog(blockedPath, TimeProvider.System);

        await log.WriteAsync(TechnicalEvent.MicrophoneUnavailable);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;

        public FixedTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
