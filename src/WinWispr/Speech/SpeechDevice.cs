using System.Runtime.InteropServices;

namespace WinWispr.Speech;

internal enum SpeechMode { Automatic, Cpu, Nvidia, Intel }

internal sealed record SpeechAdapter(int Index, uint VendorId, string Name)
{
    internal const uint NvidiaVendor = 0x10DE;
    internal const uint IntelVendor = 0x8086;

    // Resolve the DXGI index afresh; display enumeration and persisted indices
    // do not reliably identify the same GPU after a driver update or reboot.
    internal static SpeechAdapter? Select(SpeechMode mode, IReadOnlyList<SpeechAdapter> adapters) =>
        mode == SpeechMode.Cpu ? null : adapters.FirstOrDefault(adapter =>
            adapter.VendorId == (mode == SpeechMode.Intel ? IntelVendor : NvidiaVendor));
}

internal static class SpeechDevices
{
    internal static unsafe IReadOnlyList<SpeechAdapter> Enumerate()
    {
        var adapters = new List<SpeechAdapter>();
        Guid factoryId = new("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
        Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref factoryId, out nint factory));
        try
        {
            var enumerate = (delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)(*(nint**)factory)[12];
            for (uint index = 0; ; index++)
            {
                nint adapter = 0;
                int result = enumerate(factory, index, &adapter);
                if (result == unchecked((int)0x887A0002)) // DXGI_ERROR_NOT_FOUND
                    break;
                Marshal.ThrowExceptionForHR(result);
                try
                {
                    AdapterDescription description = default;
                    var getDescription = (delegate* unmanaged[Stdcall]<nint, AdapterDescription*, int>)(*(nint**)adapter)[10];
                    Marshal.ThrowExceptionForHR(getDescription(adapter, &description));
                    if ((description.Flags & 2) != 0) // Software adapter
                        continue;
                    Guid deviceId = new("189819f1-1db6-4b57-be54-1821339b85f7"); // ID3D12Device
                    // A null output tests support without creating a resident device.
                    if (D3D12CreateDevice(adapter, 0xb000, ref deviceId, 0) < 0)
                        continue;
                    adapters.Add(new SpeechAdapter((int)index, description.VendorId,
                        new string(description.Description, 0, 128).TrimEnd('\0')));
                }
                finally { Marshal.Release(adapter); }
            }
        }
        finally { Marshal.Release(factory); }
        return adapters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct AdapterDescription
    {
        internal fixed char Description[128];
        internal uint VendorId, DeviceId, SubsystemId, Revision;
        internal nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        internal uint LuidLow;
        internal int LuidHigh;
        internal uint Flags;
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid interfaceId, out nint factory);

    [DllImport("d3d12.dll", ExactSpelling = true)]
    private static extern int D3D12CreateDevice(nint adapter, uint minimumFeatureLevel, ref Guid interfaceId, nint device);
}
