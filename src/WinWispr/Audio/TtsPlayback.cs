using System.Runtime.InteropServices;
using WinWispr.Speech;

namespace WinWispr.Audio;

// One bounded PCM section at a time. Never changes system volume or microphone state.
internal sealed class TtsPlayback
{
    private readonly object _gate = new();
    private nint _device;

    internal async Task PlayAsync(TtsAudio audio, CancellationToken token)
    {
        if (audio.SampleRate is < 8000 or > 48000 || audio.Samples.Length > audio.SampleRate * 60)
            throw new InvalidDataException("Audio exceeds the playback limit.");
        short[] samples = Array.ConvertAll(audio.Samples,
            sample => (short)(Math.Clamp(float.IsFinite(sample) ? sample : 0, -1f, 1f) * short.MaxValue));
        GCHandle pinned = GCHandle.Alloc(samples, GCHandleType.Pinned);
        nint header = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
        bool prepared = false;
        nint device = 0;
        try
        {
            var format = new WaveFormat
            {
                Tag = 1, Channels = 1, SamplesPerSecond = (uint)audio.SampleRate,
                AverageBytesPerSecond = (uint)audio.SampleRate * 2, BlockAlign = 2, BitsPerSample = 16
            };
            Marshal.StructureToPtr(new WaveHeader { Data = pinned.AddrOfPinnedObject(), Bytes = (uint)samples.Length * 2 }, header, false);
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                Check(waveOutOpen(out device, uint.MaxValue, ref format, 0, 0, 0));
                _device = device;
                Check(waveOutPrepareHeader(device, header, (uint)Marshal.SizeOf<WaveHeader>()));
                prepared = true;
                Check(waveOutWrite(device, header, (uint)Marshal.SizeOf<WaveHeader>()));
            }
            while ((Marshal.PtrToStructure<WaveHeader>(header).Flags & 1) == 0)
                await Task.Delay(10, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
        finally
        {
            lock (_gate)
            {
                if (device != 0)
                {
                    // On a driver cleanup failure retain the pinned memory rather than free
                    // a buffer the driver may still reference. Process exit reclaims it.
                    Check(waveOutReset(device));
                    if (prepared) Check(waveOutUnprepareHeader(device, header, (uint)Marshal.SizeOf<WaveHeader>()));
                    Check(waveOutClose(device));
                    _device = 0;
                }
            }
            Marshal.FreeHGlobal(header);
            pinned.Free();
        }
    }

    internal void Pause() { lock (_gate) { if (_device != 0) Check(waveOutPause(_device)); } }
    internal void Resume() { lock (_gate) { if (_device != 0) Check(waveOutRestart(_device)); } }
    internal void Stop() { lock (_gate) { if (_device != 0) Check(waveOutReset(_device)); } }
    private static void Check(uint error) { if (error != 0) throw new InvalidOperationException($"Audio output failed ({error})."); }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        public ushort Tag, Channels;
        public uint SamplesPerSecond, AverageBytesPerSecond;
        public ushort BlockAlign, BitsPerSample, ExtraSize;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public nint Data;
        public uint Bytes, Recorded;
        public nuint User;
        public uint Flags, Loops;
        public nint Next;
        public nuint Reserved;
    }
    [DllImport("winmm.dll")] private static extern uint waveOutOpen(out nint handle, uint device, ref WaveFormat format, nint callback, nint instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint waveOutPrepareHeader(nint handle, nint header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutWrite(nint handle, nint header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutUnprepareHeader(nint handle, nint header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutPause(nint handle);
    [DllImport("winmm.dll")] private static extern uint waveOutRestart(nint handle);
    [DllImport("winmm.dll")] private static extern uint waveOutReset(nint handle);
    [DllImport("winmm.dll")] private static extern uint waveOutClose(nint handle);
}
