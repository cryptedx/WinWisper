namespace WinWispr.Audio;

/// <summary>The format every capture device delivers: 16 kHz, mono, normalized float samples.</summary>
internal static class CaptureFormat
{
    internal const int SampleRate = 16_000;
}

/// <summary>
/// The seam under <see cref="AudioCapture"/>: a source that feeds one <see cref="SessionAudioBuffer"/>
/// between <see cref="StartAsync"/> and <see cref="Stop"/> or <see cref="Discard"/>. The buffer is handed
/// to the device when it is created. Adapters: <see cref="WinMmCaptureDevice"/> (the microphone) and
/// <see cref="ReplayCaptureDevice"/> (a recorded clip, for the E2E profile).
/// </summary>
internal interface ICaptureDevice : IDisposable
{
    /// <summary>Raised when the device stops delivering samples on its own.</summary>
    event Action? Failed;

    Task StartAsync(string? microphoneId, CancellationToken cancellationToken);

    void Pause();

    void Resume();

    /// <summary>Stops the device, delivers what it still holds, and returns the completed audio. May block.</summary>
    CapturedAudio Stop();

    /// <summary>Stops the device and drops the audio. May block.</summary>
    void Discard();
}
