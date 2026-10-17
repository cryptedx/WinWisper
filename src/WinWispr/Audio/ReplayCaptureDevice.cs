namespace WinWispr.Audio;

/// <summary>
/// Plays a recorded clip into the session buffer as a microphone would deliver it: in real time from
/// <see cref="StartAsync"/>, in the 16 ms steps of one WinMM buffer, losing what passes while paused,
/// and delivering nothing once the clip is over. The E2E profile uses it to dictate real speech
/// without a physical microphone.
/// </summary>
internal sealed class ReplayCaptureDevice : ICaptureDevice
{
    private const int StepSamples = CaptureFormat.SampleRate / 1000 * 16;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(4);

    private readonly SessionAudioBuffer _buffer;
    private readonly float[] _clip;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly ManualResetEventSlim _stopSignal = new();
    private Thread? _worker;
    private long _startTimestamp;
    private int _position;
    private bool _running;
    private bool _finished;

    internal ReplayCaptureDevice(SessionAudioBuffer buffer, float[] clip, TimeProvider? time = null)
    {
        _buffer = buffer;
        _clip = clip;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Replays a raw PCM16 clip (16 kHz, mono, little-endian).</summary>
    internal static ReplayCaptureDevice FromPcm16File(SessionAudioBuffer buffer, string path) =>
        new(buffer, Pcm16.ToFloat(File.ReadAllBytes(path)));

    /// <summary>How far the replay has advanced, in samples: played, or lost while paused.</summary>
    internal int Position
    {
        get
        {
            lock (_gate)
                return _position;
        }
    }

    // A replay never fails on its own.
    public event Action? Failed
    {
        add { }
        remove { }
    }

    public Task StartAsync(string? microphoneId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_worker is not null)
                throw new InvalidOperationException("The replay is already running.");
            _running = true;
            _startTimestamp = _time.GetTimestamp();
            _worker = new Thread(Run) { IsBackground = true, Name = "WinWispr.ReplayCapture" };
            _worker.Start();
        }

        return Task.CompletedTask;
    }

    public void Pause()
    {
        lock (_gate)
            _running = false;
    }

    public void Resume()
    {
        lock (_gate)
            _running = true;
    }

    public CapturedAudio Stop()
    {
        StopWorker(deliverDue: true);
        return _buffer.Complete();
    }

    public void Discard()
    {
        StopWorker(deliverDue: false);
        _buffer.Discard();
    }

    public void Dispose() => StopWorker(deliverDue: false);

    private void Run()
    {
        while (!_stopSignal.IsSet)
        {
            Pump();
            _stopSignal.Wait(PollInterval);
        }
    }

    // Delivers what a microphone would have captured by now. Working from the elapsed time keeps the rate
    // exact even when the timer is coarse.
    private void Pump()
    {
        lock (_gate)
        {
            if (_finished)
                return;
            long ticks = _time.GetElapsedTime(_startTimestamp).Ticks;
            int due = (int)Math.Min(_clip.Length, ticks * CaptureFormat.SampleRate / TimeSpan.TicksPerSecond);
            while (_position < due)
            {
                int count = Math.Min(StepSamples, due - _position);
                if (_running)
                    _buffer.Append(_clip.AsSpan(_position, count));
                _position += count;
            }
        }
    }

    private void StopWorker(bool deliverDue)
    {
        Thread? worker;
        lock (_gate)
        {
            worker = _worker;
            if (deliverDue && worker is not null)
                Pump();
            _finished = true;
            _worker = null;
        }

        _stopSignal.Set();
        worker?.Join();
    }
}
