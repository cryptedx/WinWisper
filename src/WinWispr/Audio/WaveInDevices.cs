using System.Runtime.InteropServices;

namespace WinWispr.Audio;

/// <summary>
/// A WinMM capture device. <see cref="EndpointId"/> is the audio endpoint behind it, as the driver reports it
/// (for example <c>{0.0.1.00000000}.{guid}</c>); null when the driver cannot say. The product name is cut to 31
/// characters by WinMM.
/// </summary>
internal sealed record WaveInDevice(uint Index, string ProductName, string? EndpointId);

/// <summary>Lists the WinMM capture devices with their endpoint IDs.</summary>
internal static class WaveInDevices
{
    private const uint MmSysErrNoError = 0;
    private const uint DrvQueryFunctionInstanceId = 0x0811;
    private const uint DrvQueryFunctionInstanceIdSize = 0x0812;

    internal static IReadOnlyList<WaveInDevice> List()
    {
        uint count = WaveInGetNumDevs();
        uint capsSize = (uint)Marshal.SizeOf<WaveInCaps>();
        var devices = new List<WaveInDevice>((int)count);
        for (uint index = 0; index < count; index++)
        {
            if (WaveInGetDevCaps(index, out WaveInCaps caps, capsSize) != MmSysErrNoError)
                continue;
            devices.Add(new WaveInDevice(index, caps.ProductName.TrimEnd('\0'), QueryEndpointId(index)));
        }

        return devices;
    }

    private static string? QueryEndpointId(uint index)
    {
        if (WaveInMessage((nint)index, DrvQueryFunctionInstanceIdSize, out uint byteCount, 0) != MmSysErrNoError
            || byteCount < sizeof(char) * 2 || byteCount > 4096)
            return null;

        nint buffer = Marshal.AllocHGlobal((int)byteCount);
        try
        {
            if (WaveInMessage((nint)index, DrvQueryFunctionInstanceId, buffer, (nint)byteCount) != MmSysErrNoError)
                return null;
            string? id = Marshal.PtrToStringUni(buffer, (int)byteCount / sizeof(char))?.TrimEnd('\0');
            return string.IsNullOrWhiteSpace(id) ? null : id;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("winmm.dll", EntryPoint = "waveInGetNumDevs", ExactSpelling = true)]
    private static extern uint WaveInGetNumDevs();

    [DllImport("winmm.dll", EntryPoint = "waveInGetDevCapsW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint WaveInGetDevCaps(uint deviceId, out WaveInCaps caps, uint capsSize);

    [DllImport("winmm.dll", EntryPoint = "waveInMessage", ExactSpelling = true)]
    private static extern uint WaveInMessage(nint device, uint message, out uint size, nint unused);

    [DllImport("winmm.dll", EntryPoint = "waveInMessage", ExactSpelling = true)]
    private static extern uint WaveInMessage(nint device, uint message, nint buffer, nint size);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WaveInCaps
    {
        internal ushort ManufacturerId;
        internal ushort ProductId;
        internal uint DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        internal string ProductName;
        internal uint Formats;
        internal ushort Channels;
        internal ushort Reserved;
    }
}
