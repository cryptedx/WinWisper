using WinWispr.Diagnostics;

namespace WinWispr.Audio;

internal sealed class CapturedAudio : IDisposable
{
    private float[][] _chunks;

    internal CapturedAudio(float[][] chunks, int sampleCount)
    {
        _chunks = chunks;
        SampleCount = sampleCount;
    }

    internal int SampleCount { get; private set; }

    internal IReadOnlyList<float[]> Chunks => _chunks;

    internal float[] ToArray()
    {
        if (SampleCount == 0)
            return [];
        if (_chunks.Length == 1 && _chunks[0].Length == SampleCount)
            return _chunks[0];

        var samples = new float[SampleCount];
        int offset = 0;
        foreach (float[] chunk in _chunks)
        {
            chunk.AsSpan().CopyTo(samples.AsSpan(offset));
            offset += chunk.Length;
        }

        return samples;
    }

    internal static CapturedAudio FromArray(float[] samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        return new CapturedAudio([samples], samples.Length);
    }

    public void Dispose()
    {
        _chunks = [];
        SampleCount = 0;
    }
}

internal sealed class SessionAudioBuffer
{
    private const int ChunkSize = 64 * 1024;
    private readonly object _gate = new();
    private readonly int _maxSamples;
    private readonly List<float[]> _chunks = [];
    private int _count;
    private int _chunkOffset;
    private bool _paused;
    private bool _completed;
    private bool _limitRaised;

    internal SessionAudioBuffer(int maxSamples)
    {
        if (maxSamples <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxSamples));
        _maxSamples = maxSamples;
    }

    internal int Count
    {
        get
        {
            lock (_gate)
                return _count;
        }
    }

    internal event Action? LimitReached;
    internal event Action<float>? PeakChanged;

    internal void Append(ReadOnlySpan<float> samples)
    {
        Action? limitReached = null;
        Action<float>? peakChanged = null;
        float peak = 0;

        lock (_gate)
        {
            if (_completed)
                throw new InvalidOperationException("The audio session is complete.");
            if (_paused || samples.Length == 0)
                return;

            int remaining = _maxSamples - _count;
            int count = Math.Min(remaining, samples.Length);
            if (count > 0)
            {
                ReadOnlySpan<float> accepted = samples[..count];
                int copied = 0;
                while (copied < count)
                {
                    if (_chunks.Count == 0 || _chunkOffset == _chunks[^1].Length)
                    {
                        _chunks.Add(new float[Math.Min(ChunkSize, _maxSamples - _count)]);
                        _chunkOffset = 0;
                    }

                    float[] chunk = _chunks[^1];
                    int copyCount = Math.Min(count - copied, chunk.Length - _chunkOffset);
                    accepted.Slice(copied, copyCount).CopyTo(chunk.AsSpan(_chunkOffset, copyCount));
                    copied += copyCount;
                    _count += copyCount;
                    _chunkOffset += copyCount;
                }

                for (int index = 0; index < accepted.Length; index++)
                {
                    float value = MathF.Abs(accepted[index]);
                    if (!float.IsNaN(value) && value > peak)
                        peak = Math.Min(value, 1f);
                }

                peakChanged = PeakChanged;
            }

            if (count < samples.Length && !_limitRaised)
            {
                _limitRaised = true;
                limitReached = LimitReached;
            }
        }

        if (peakChanged is not null && peak > 0)
            peakChanged(peak);
        limitReached?.Invoke();
    }

    internal void Pause()
    {
        lock (_gate)
        {
            ThrowIfCompleted();
            _paused = true;
        }
    }

    internal void Resume()
    {
        lock (_gate)
        {
            ThrowIfCompleted();
            _paused = false;
        }
    }

    internal CapturedAudio Complete()
    {
        lock (_gate)
        {
            ThrowIfCompleted();
            _completed = true;
            var chunks = new float[_chunks.Count][];
            for (int index = 0; index < _chunks.Count; index++)
            {
                int length = index == _chunks.Count - 1 ? _chunkOffset : _chunks[index].Length;
                chunks[index] = length == _chunks[index].Length
                    ? _chunks[index]
                    : _chunks[index][..length];
            }

            return new CapturedAudio(chunks, _count);
        }
    }

    internal void Discard()
    {
        lock (_gate)
        {
            ThrowIfCompleted();
            _completed = true;
            _chunks.Clear();
            _count = 0;
            _chunkOffset = 0;
        }
    }

    private void ThrowIfCompleted()
    {
        if (_completed)
            throw new InvalidOperationException("The audio session is complete.");
    }
}

/// <summary>
/// One recording at a time: owns the session buffer, serializes the lifecycle, and turns the signals of
/// the buffer and the device into the Peak, Limit and Failed events. The device behind it is the
/// <see cref="ICaptureDevice"/> seam. Stop and Discard hand the device's blocking work to the thread pool,
/// so callers never wait on a native driver.
/// </summary>
internal sealed class AudioCapture : IDisposable
{
    internal const int MaxSamples = 9_600_000;

    private readonly int _maxSamples;
    private readonly Func<SessionAudioBuffer, ICaptureDevice> _openDevice;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _gate = new();
    private ICaptureDevice? _device;
    private SessionAudioBuffer? _buffer;
    private bool _disposed;

    internal AudioCapture(Func<SessionAudioBuffer, ICaptureDevice> openDevice, int maxSamples = MaxSamples)
    {
        ArgumentNullException.ThrowIfNull(openDevice);
        if (maxSamples <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxSamples));
        _openDevice = openDevice;
        _maxSamples = maxSamples;
    }

    /// <summary>A capture on the microphone through WinMM.</summary>
    internal static AudioCapture CreateNative(
        MicrophoneCatalog? microphones = null, int maxSamples = MaxSamples, Action<TechnicalEvent, double>? timing = null)
    {
        MicrophoneCatalog catalog = microphones ?? new MicrophoneCatalog();
        return new(buffer => new WinMmCaptureDevice(buffer, catalog.ChooseForCaptureAsync, timing: timing), maxSamples);
    }

    internal event Action<float>? PeakChanged;
    internal event Action? LimitReached;
    internal event Action? Failed;

    internal async Task StartAsync(string? microphoneId, CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SessionAudioBuffer buffer;
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_device is not null)
                    throw new InvalidOperationException("Audio capture is already running.");
                buffer = _buffer = new SessionAudioBuffer(_maxSamples);
                buffer.PeakChanged += OnPeakChanged;
                buffer.LimitReached += OnLimitReached;
            }

            ICaptureDevice? starting = null;
            try
            {
                ICaptureDevice device = _openDevice(buffer);
                starting = device;
                device.Failed += () => OnDeviceFailed(device);
                await device.StartAsync(microphoneId, cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    ObjectDisposedException.ThrowIf(_disposed, this);
                    _device = device;
                    starting = null;
                }
            }
            catch
            {
                starting?.Dispose();
                lock (_gate)
                {
                    _device = null;
                    _buffer?.Discard();
                    _buffer = null;
                }
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private void OnDeviceFailed(ICaptureDevice device)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_device, device))
                return;
        }

        Failed?.Invoke();
    }

    internal async Task PauseAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            (SessionAudioBuffer buffer, ICaptureDevice device) = RunningCapture();
            device.Pause();
            buffer.Pause();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    internal async Task ResumeAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            (SessionAudioBuffer buffer, ICaptureDevice device) = RunningCapture();
            buffer.Resume();
            device.Resume();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    internal async Task<CapturedAudio> StopAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ICaptureDevice? device;
            lock (_gate)
                device = _device;

            CapturedAudio samples = device is null
                ? new CapturedAudio([], 0)
                : await Task.Run(device.Stop).ConfigureAwait(false);
            ClearCapture(device);
            return samples;
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    internal async Task DiscardAsync()
    {
        await _lifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            ICaptureDevice? device;
            lock (_gate)
                device = _device;

            if (device is not null)
                await Task.Run(device.Discard).ConfigureAwait(false);
            ClearCapture(device);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public void Dispose()
    {
        _lifecycle.Wait();
        try
        {
            ICaptureDevice? device;
            SessionAudioBuffer? buffer;
            lock (_gate)
            {
                if (_disposed)
                    return;
                _disposed = true;
                device = _device;
                buffer = _buffer;
                _device = null;
                _buffer = null;
            }

            device?.Discard();
            if (device is null)
                buffer?.Discard();
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private (SessionAudioBuffer Buffer, ICaptureDevice Device) RunningCapture()
    {
        lock (_gate)
        {
            SessionAudioBuffer buffer = _buffer ?? throw new InvalidOperationException("Audio buffer is not running.");
            ICaptureDevice device = _device ?? throw new InvalidOperationException("Audio buffer is not running.");
            return (buffer, device);
        }
    }

    // Forget the finished capture, unless a newer one replaced it in the meantime.
    private void ClearCapture(ICaptureDevice? finished)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_device, finished))
            {
                _device = null;
                _buffer = null;
            }
        }
    }

    private void OnPeakChanged(float peak) => PeakChanged?.Invoke(peak);

    private void OnLimitReached() => LimitReached?.Invoke();
}
