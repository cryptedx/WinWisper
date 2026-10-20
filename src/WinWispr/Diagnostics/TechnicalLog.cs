using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinWispr.Core;
using WinWispr.Speech;

namespace WinWispr.Diagnostics;

internal enum TechnicalEvent
{
    StartupStarted,
    StartupReady,
    StartupFailed,

    /// <summary>An exception reached the application unhandled and ends the process. Logged with the exception type only.</summary>
    UnhandledException,
    ModelDownloadFailed,
    ModelIntegrityFailed,
    ModelLoadFailed,
    MicrophoneUnavailable,
    RecognitionFailed,
    RecognitionTimedOut,
    ClipboardUnavailable,
    CapturePreparation,
    TeamsDiscovery,
    TeamsMuteConfirmation,
    HotkeyDispatch,
    AudioDeviceStart,
    MicrophoneDeviceResolution,
    CaptureStopStarted,
    CaptureStopCompleted,
    RecognitionStarted,
    RecognitionCompleted,
    DeliveryStarted,
    DeliveryCompleted,
    MicrophoneFallback,

    /// <summary>A translation or the load of its model failed. Logged with the exception type only, never text.</summary>
    TranslationFailed,
    TranslationTimedOut,

    /// <summary>A translation found no complete translation model.</summary>
    TranslationNotInstalled,

    /// <summary>The check for a newer release failed. Logged with the exception type only.</summary>
    UpdateCheckFailed,

    /// <summary>A Setup download failed or the Setup failed its size, SHA-256 or signature check.</summary>
    UpdateDownloadFailed,

    /// <summary>A verified Setup was handed to the update helper and WinWispr is exiting for it.</summary>
    UpdateStarted,

    /// <summary>The update helper could not be started.</summary>
    UpdateStartFailed
}

internal sealed record TechnicalLogEntry(
    DateTimeOffset Timestamp,
    TechnicalEvent Event,
    RecordingState? RecordingState,
    SpeechRuntimeState? ModelState,
    string? ExceptionType,
    int? HResult,
    double? DurationMilliseconds = null,
    DeliveryResult? DeliveryResult = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DeliveryDiagnostics? Delivery = null);

/// <summary>
/// What a dictation delivery observed on the clipboard (see <see cref="DeliveryTrace"/>), written with its
/// DeliveryCompleted entry: counts, millisecond offsets and process image names, never text, window titles or paths.
/// </summary>
internal sealed record DeliveryDiagnostics(
    int? ClipboardSetAttempts,
    string? TargetProcess,
    int? PasteAtMs,
    bool ClipboardChangedBeforePaste,
    IReadOnlyList<ClipboardHold>? ClipboardHolds);

internal sealed class TechnicalLog
{
    internal const long MaxBytes = 1_048_576;
    private static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };
    private readonly string _rootPath;
    private readonly TimeProvider _timeProvider;
    private readonly object _writeQueueGate = new();
    private Task _writeTail = Task.CompletedTask;

    internal TechnicalLog(string rootPath, TimeProvider timeProvider)
    {
        _rootPath = rootPath;
        _timeProvider = timeProvider;
    }

    internal Task WriteAsync(
        TechnicalEvent technicalEvent,
        RecordingState? recordingState = null,
        SpeechRuntimeState? modelState = null,
        Exception? exception = null,
        int? hresult = null,
        CancellationToken cancellationToken = default,
        double? durationMilliseconds = null,
        DateTimeOffset? occurredAtUtc = null,
        DeliveryResult? deliveryResult = null,
        DeliveryDiagnostics? delivery = null)
    {
        DateTimeOffset timestamp = occurredAtUtc ?? _timeProvider.GetUtcNow();
        lock (_writeQueueGate)
        {
            Task previous = _writeTail;
            _writeTail = Task.Run(async () =>
            {
                try
                {
                    await previous.ConfigureAwait(false);
                    await WriteEntryAsync(
                        timestamp,
                        technicalEvent,
                        recordingState,
                        modelState,
                        exception,
                        hresult,
                        cancellationToken,
                        durationMilliseconds,
                        deliveryResult,
                        delivery).ConfigureAwait(false);
                }
                catch (Exception failure)
                {
                    Debug.WriteLine($"WinWispr technical log unavailable: {failure.GetType().Name}");
                }
            });
            return _writeTail;
        }
    }

    /// <summary>Completes when every entry queued so far is written or dropped.</summary>
    internal Task FlushAsync()
    {
        lock (_writeQueueGate)
            return _writeTail;
    }

    private async Task WriteEntryAsync(
        DateTimeOffset timestamp,
        TechnicalEvent technicalEvent,
        RecordingState? recordingState,
        SpeechRuntimeState? modelState,
        Exception? exception,
        int? hresult,
        CancellationToken cancellationToken,
        double? durationMilliseconds,
        DeliveryResult? deliveryResult,
        DeliveryDiagnostics? delivery)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var entry = new TechnicalLogEntry(
            timestamp,
            technicalEvent,
            recordingState,
            modelState,
            exception?.GetType().Name,
            hresult ?? exception?.HResult,
            durationMilliseconds,
            deliveryResult,
            delivery);
        byte[] payload = Utf8.GetBytes(JsonSerializer.Serialize(entry, JsonOptions) + "\n");
        if (payload.LongLength > MaxBytes)
            return;

        Directory.CreateDirectory(_rootPath);
        string currentPath = Path.Combine(
            _rootPath,
            timestamp.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl");
        if (!PrepareCapacity(timestamp, payload.LongLength))
            return;

        await using var stream = new FileStream(
            currentPath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    private bool PrepareCapacity(DateTimeOffset now, long nextEntryBytes)
    {
        var files = Directory.GetFiles(_rootPath, "*.jsonl")
            .Select(path => new FileInfo(path))
            .Where(file => now.UtcDateTime - file.LastWriteTimeUtc > Retention)
            .ToList();

        foreach (FileInfo file in files)
            file.Delete();

        List<FileInfo> retained = Directory.GetFiles(_rootPath, "*.jsonl")
            .Select(path => new FileInfo(path))
            .OrderBy(file => file.LastWriteTimeUtc)
            .ToList();
        long total = retained.Sum(file => file.Length);
        while (total + nextEntryBytes > MaxBytes && retained.Count > 0)
        {
            FileInfo oldest = retained[0];
            retained.RemoveAt(0);
            total -= oldest.Length;
            oldest.Delete();
        }

        return total + nextEntryBytes <= MaxBytes;
    }
}
