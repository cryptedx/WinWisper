using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using Windows.Graphics.Effects;
using WinRT;

namespace WinWispr.Views;

/// <summary>
/// Direct2D alpha-mask effect description for composition effect brushes: keeps
/// <see cref="Source"/> where <see cref="AlphaMask"/> is opaque.
/// </summary>
/// <remarks>
/// Composition reads effect descriptions through the classic COM interface
/// IGraphicsEffectD2D1Interop, which CsWinRT does not project. The interface is
/// handed out on query from a separate source-generated COM wrapper.
/// </remarks>
internal sealed partial class AlphaMaskEffect(IGraphicsEffectSource source, IGraphicsEffectSource alphaMask)
    : IGraphicsEffect, ICustomQueryInterface
{
    private static readonly Guid AlphaMaskEffectId = new("c80ecff0-3fd5-4f05-8328-c5d1724b4f0a");
    private static readonly StrategyBasedComWrappers ComWrappers = new();

    public string Name { get; set; } = string.Empty;

    CustomQueryInterfaceResult ICustomQueryInterface.GetInterface(ref Guid iid, out nint ppv)
    {
        ppv = 0;
        if (iid != typeof(IGraphicsEffectD2D1Interop).GUID)
            return CustomQueryInterfaceResult.NotHandled;

        nint unknown = ComWrappers.GetOrCreateComInterfaceForObject(
            new D2D1Interop(this), CreateComInterfaceFlags.None);
        try
        {
            return Marshal.QueryInterface(unknown, in iid, out ppv) == 0
                ? CustomQueryInterfaceResult.Handled
                : CustomQueryInterfaceResult.Failed;
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    [GeneratedComClass]
    private sealed partial class D2D1Interop(AlphaMaskEffect effect) : IGraphicsEffectD2D1Interop
    {
        private const int InvalidArgument = unchecked((int)0x80070057);

        public int GetEffectId(out Guid id)
        {
            id = AlphaMaskEffectId;
            return 0;
        }

        public int GetNamedPropertyMapping(string name, out uint index, out int mapping)
        {
            index = 0;
            mapping = 0;
            return InvalidArgument;
        }

        public int GetPropertyCount(out uint count)
        {
            count = 0;
            return 0;
        }

        public int GetProperty(uint index, out nint value)
        {
            value = 0;
            return InvalidArgument;
        }

        public int GetSource(uint index, out nint source)
        {
            IGraphicsEffectSource? selected = index switch
            {
                0 => effect._source,
                1 => effect._alphaMask,
                _ => null
            };
            source = selected is null ? 0 : MarshalInterface<IGraphicsEffectSource>.FromManaged(selected);
            return selected is null ? InvalidArgument : 0;
        }

        public int GetSourceCount(out uint count)
        {
            count = 2;
            return 0;
        }
    }

    private readonly IGraphicsEffectSource _source = source;
    private readonly IGraphicsEffectSource _alphaMask = alphaMask;
}

[GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
[Guid("2FC57384-A068-44D7-A331-30982FCF7177")]
internal partial interface IGraphicsEffectD2D1Interop
{
    [PreserveSig]
    int GetEffectId(out Guid id);

    [PreserveSig]
    int GetNamedPropertyMapping(string name, out uint index, out int mapping);

    [PreserveSig]
    int GetPropertyCount(out uint count);

    [PreserveSig]
    int GetProperty(uint index, out nint value);

    [PreserveSig]
    int GetSource(uint index, out nint source);

    [PreserveSig]
    int GetSourceCount(out uint count);
}
