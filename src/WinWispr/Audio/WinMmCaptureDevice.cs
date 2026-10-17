using System.Diagnostics;
using System.Runtime.InteropServices;
using WinWispr.Diagnostics;

namespace WinWispr.Audio;

internal sealed class WinMmCaptureDevice : ICaptureDevice
{
    private const uint CallbackEvent = 0x00050000;
    private const uint WhdrDone = 0x00000001;
    private const uint MmSysErrNoError = 0;
    private const int BufferCount = 4;
    private const int BufferSize = 512;
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly object _cleanupGate = new();
    private readonly TimeSpan _cleanupTimeout;
    private readonly Action<nint> _stopAndReset;
    private readonly Action<TechnicalEvent, double>? _timing;
    private readonly Func<string?, CancellationToken, Task<MicrophoneChoice>> _choose;
    private Task<bool>? _cleanupTask;
    private readonly SessionAudioBuffer _buffer;
    private readonly List<WaveBuffer> _buffers = [];
    private EventWaitHandle? _dataReady;
    private Thread? _worker;
    private nint _handle;
    private bool _running;
    private bool _stopping;
    private int _cleanupTimedOut;

    internal WinMmCaptureDevice(SessionAudioBuffer buffer,
        Func<string?, CancellationToken, Task<MicrophoneChoice>>? choose = null,
        TimeSpan? cleanupTimeout = null, Action<nint>? stopAndReset = null,
        Action<TechnicalEvent, double>? timing = null)
    {
        _buffer = buffer;
        _choose = choose ?? new MicrophoneCatalog().ChooseForCaptureAsync;
        _timing = timing;
        _cleanupTimeout = cleanupTimeout ?? CleanupTimeout;
        if (_cleanupTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(cleanupTimeout));
        _stopAndReset = stopAndReset ?? StopAndReset;
    }

    public event Action? Failed;

    public async Task StartAsync(string? microphoneId, CancellationToken cancellationToken)
    {
        MicrophoneChoice choice;
        long resolutionStarted = Stopwatch.GetTimestamp();
        try
        {
            choice = await _choose(microphoneId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try { _timing?.Invoke(TechnicalEvent.MicrophoneDeviceResolution, Stopwatch.GetElapsedTime(resolutionStarted).TotalMilliseconds); }
            catch { /* Diagnostics must not affect capture startup. */ }
        }

        // The session falls back to the Windows default before it starts a capture; a saved microphone that is
        // missing here was unplugged in between.
        if (choice.SavedMissing)
            throw new InvalidOperationException("The selected microphone is unavailable.");
        StartNative(choice.WaveInIndex, cancellationToken);
    }

    private void StartNative(uint deviceId, CancellationToken cancellationToken)
    {
        long deviceStarted = Stopwatch.GetTimestamp();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var format = new WaveFormatEx
            {
                FormatTag = 1,
                Channels = 1,
                SamplesPerSecond = CaptureFormat.SampleRate,
                BitsPerSample = 16,
                BlockAlign = 2,
                AverageBytesPerSecond = CaptureFormat.SampleRate * 2,
                ExtraSize = 0
            };

            try
            {
                lock (_gate)
                {
                    _dataReady = new EventWaitHandle(false, EventResetMode.AutoReset);
                    ThrowIfFailed(
                        WaveInOpen(
                            out _handle,
                            deviceId,
                            ref format,
                            _dataReady.SafeWaitHandle.DangerousGetHandle(),
                            nint.Zero,
                            CallbackEvent),
                        "waveInOpen");

                    for (int index = 0; index < BufferCount; index++)
                    {
                        var buffer = new WaveBuffer(BufferSize);
                        _buffers.Add(buffer);
                        ThrowIfFailed(
                            WaveInPrepareHeader(_handle, buffer.HeaderPointer, (uint)Marshal.SizeOf<WaveHeader>()),
                            "waveInPrepareHeader");
                        buffer.Prepared = true;
                        ThrowIfFailed(WaveInAddBuffer(_handle, buffer.HeaderPointer, (uint)Marshal.SizeOf<WaveHeader>()), "waveInAddBuffer");
                    }

                    _stopping = false;
                    _running = true;
                    ThrowIfFailed(WaveInStart(_handle), "waveInStart");
                    var worker = new Thread(ProcessBuffers)
                    {
                        IsBackground = true,
                        Name = "WinWispr.AudioCapture"
                    };
                    worker.Start();
                    _worker = worker;
                }
            }
            catch
            {
                CleanupNative();
                throw;
            }
        }
        finally
        {
            try { _timing?.Invoke(TechnicalEvent.AudioDeviceStart, Stopwatch.GetElapsedTime(deviceStarted).TotalMilliseconds); }
            catch { /* Diagnostics must not affect capture startup. */ }
        }
    }

    public void Pause()
    {
        nint handle;
        lock (_gate)
        {
            handle = _handle;
            _running = false;
        }

        if (handle != nint.Zero)
            ThrowIfFailed(WaveInStop(handle), "waveInStop");
    }

    public void Resume()
    {
        nint handle;
        lock (_gate)
        {
            handle = _handle;
            if (handle == nint.Zero || _stopping || _running)
                return;
            _running = true;
        }

        try
        {
            ThrowIfFailed(WaveInStart(handle), "waveInStart");
        }
        catch
        {
            lock (_gate)
                _running = false;
            throw;
        }
    }

    public CapturedAudio Stop()
    {
        if (!CleanupNative())
            throw new InvalidOperationException("waveInClose failed during audio cleanup.");

        return _buffer.Complete();
    }

    public void Discard()
    {
        bool cleaned = CleanupNative();
        if (!cleaned && Volatile.Read(ref _cleanupTimedOut) == 0)
            throw new InvalidOperationException("waveInClose failed during audio cleanup.");

        _buffer.Discard();
    }

    public void Dispose() => CleanupNative();

    private void ProcessBuffers()
    {
        int nextBuffer = 0;
        try
        {
            while (true)
            {
                _dataReady!.WaitOne();
                lock (_gate)
                {
                    bool stopping = _stopping;

                    for (int processed = 0; processed < _buffers.Count; processed++)
                    {
                        WaveBuffer buffer = _buffers[nextBuffer];
                        WaveHeader header = Marshal.PtrToStructure<WaveHeader>(buffer.HeaderPointer);
                        if ((header.Flags & WhdrDone) == 0)
                        {
                            if (stopping)
                            {
                                nextBuffer = (nextBuffer + 1) % _buffers.Count;
                                continue;
                            }
                            break;
                        }

                        int byteCount = (int)Math.Min(header.BytesRecorded, (uint)buffer.Data.Length) & ~1;
                        if ((_running || stopping) && byteCount > 0)
                            AppendPcm16(buffer.Data.AsSpan(0, byteCount));

                        if (stopping)
                        {
                            nextBuffer = (nextBuffer + 1) % _buffers.Count;
                            continue;
                        }

                        ThrowIfFailed(WaveInAddBuffer(_handle, buffer.HeaderPointer, (uint)Marshal.SizeOf<WaveHeader>()), "waveInAddBuffer");
                        nextBuffer = (nextBuffer + 1) % _buffers.Count;
                    }

                    if (stopping)
                        return;
                }
            }
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"WinWispr audio worker failed: {exception}");
            lock (_gate)
            {
                _running = false;
                _stopping = true;
            }
            Failed?.Invoke();
        }
    }

    private void AppendPcm16(ReadOnlySpan<byte> pcm)
    {
        Span<float> samples = stackalloc float[pcm.Length / sizeof(short)];
        Pcm16.ToFloat(pcm, samples);
        _buffer.Append(samples);
    }

    private bool CleanupNative()
    {
        if (Volatile.Read(ref _cleanupTimedOut) != 0)
            return false;

        Task<bool> cleanup;
        // This gate only publishes the single cleanup task. Neither the audio
        // worker nor native calls use it, so waiting for _gate is also timed.
        lock (_cleanupGate)
            cleanup = _cleanupTask ??= Task.Run(CleanupNativeCore);

        if (cleanup.Wait(_cleanupTimeout))
            return cleanup.GetAwaiter().GetResult();

        Interlocked.Exchange(ref _cleanupTimedOut, 1);
        Trace.WriteLine("WinMM audio cleanup timed out; native resources remain owned by the cleanup task.");
        return false;
    }

    private bool CleanupNativeCore()
    {
        try
        {
            nint handle;
            lock (_gate)
            {
                _running = false;
                _stopping = true;
                handle = _handle;
                // Keep the worker outside the gate until reset has returned
                // every pending header, including the final partial buffer.
                _stopAndReset(handle);
            }

            _dataReady?.Set();
            _worker?.Join();
            _worker = null;
            return FinalizeNativeCleanup(handle);
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"WinMM audio cleanup failed: {exception}");
            return false;
        }
    }

    private void StopAndReset(nint handle)
    {
        if (handle == nint.Zero)
            return;

        try
        {
            uint stopResult = WaveInStop(handle);
            if (stopResult != MmSysErrNoError)
                Trace.WriteLine($"waveInStop failed with MMRESULT {stopResult}.");

            uint resetResult = WaveInReset(handle);
            if (resetResult != MmSysErrNoError)
                Trace.WriteLine($"waveInReset failed with MMRESULT {resetResult}.");
        }
        catch (Exception exception)
        {
            Trace.WriteLine($"WinMM audio stop/reset failed: {exception}");
        }
    }

    private bool FinalizeNativeCleanup(nint handle)
    {
        if (handle == nint.Zero)
        {
            FreeBuffers();
            _dataReady?.Dispose();
            _dataReady = null;
            return true;
        }

        foreach (WaveBuffer buffer in _buffers)
        {
            if (!buffer.Prepared)
                continue;

            uint unprepareResult = WaveInUnprepareHeader(
                _handle,
                buffer.HeaderPointer,
                (uint)Marshal.SizeOf<WaveHeader>());
            if (unprepareResult != MmSysErrNoError)
                Trace.WriteLine($"waveInUnprepareHeader failed with MMRESULT {unprepareResult}.");
        }

        uint closeResult = MmSysErrNoError;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            closeResult = WaveInClose(_handle);
            if (closeResult == MmSysErrNoError)
                break;
            WaveInReset(_handle);
        }

        if (closeResult != MmSysErrNoError)
        {
            Trace.WriteLine($"waveInClose failed with MMRESULT {closeResult}.");
            return false;
        }

        lock (_gate)
            _handle = nint.Zero;
        FreeBuffers();
        _dataReady?.Dispose();
        _dataReady = null;
        return true;
    }

    private void FreeBuffers()
    {
        foreach (WaveBuffer buffer in _buffers)
            buffer.Dispose();
        _buffers.Clear();
    }

    private static void ThrowIfFailed(uint result, string operation)
    {
        if (result != MmSysErrNoError)
            throw new InvalidOperationException($"{operation} failed with MMRESULT {result}.");
    }

    [DllImport("winmm.dll", EntryPoint = "waveInOpen", ExactSpelling = true)]
    private static extern uint WaveInOpen(
        out nint handle,
        uint deviceId,
        ref WaveFormatEx format,
        nint callback,
        nint instance,
        uint flags);

    [DllImport("winmm.dll", EntryPoint = "waveInPrepareHeader", ExactSpelling = true)]
    private static extern uint WaveInPrepareHeader(nint handle, nint header, uint headerSize);

    [DllImport("winmm.dll", EntryPoint = "waveInUnprepareHeader", ExactSpelling = true)]
    private static extern uint WaveInUnprepareHeader(nint handle, nint header, uint headerSize);

    [DllImport("winmm.dll", EntryPoint = "waveInAddBuffer", ExactSpelling = true)]
    private static extern uint WaveInAddBuffer(nint handle, nint header, uint headerSize);

    [DllImport("winmm.dll", EntryPoint = "waveInStart", ExactSpelling = true)]
    private static extern uint WaveInStart(nint handle);

    [DllImport("winmm.dll", EntryPoint = "waveInStop", ExactSpelling = true)]
    private static extern uint WaveInStop(nint handle);

    [DllImport("winmm.dll", EntryPoint = "waveInReset", ExactSpelling = true)]
    private static extern uint WaveInReset(nint handle);

    [DllImport("winmm.dll", EntryPoint = "waveInClose", ExactSpelling = true)]
    private static extern uint WaveInClose(nint handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveFormatEx
    {
        internal ushort FormatTag;
        internal ushort Channels;
        internal uint SamplesPerSecond;
        internal uint AverageBytesPerSecond;
        internal ushort BlockAlign;
        internal ushort BitsPerSample;
        internal ushort ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        internal nint Data;
        internal uint BufferLength;
        internal uint BytesRecorded;
        internal nint User;
        internal uint Flags;
        internal uint Loops;
        internal nint Next;
        internal nint Reserved;
    }

    private sealed class WaveBuffer : IDisposable
    {
        private readonly GCHandle _dataHandle;

        internal WaveBuffer(int size)
        {
            Data = new byte[size];
            _dataHandle = GCHandle.Alloc(Data, GCHandleType.Pinned);
            HeaderPointer = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
            Marshal.StructureToPtr(
                new WaveHeader
                {
                    Data = _dataHandle.AddrOfPinnedObject(),
                    BufferLength = (uint)Data.Length
                },
                HeaderPointer,
                false);
        }

        internal byte[] Data { get; }
        internal nint HeaderPointer { get; }
        internal bool Prepared { get; set; }

        public void Dispose()
        {
            Marshal.FreeHGlobal(HeaderPointer);
            if (_dataHandle.IsAllocated)
                _dataHandle.Free();
        }
    }
}
