using System.Collections.Concurrent;
using Windows.Devices.Enumeration;
using Windows.Media.Devices;

namespace WinWispr.Audio;

/// <summary>A capture device as Windows lists it. The ID is what settings store.</summary>
internal sealed record Microphone(string Id, string Name);

/// <summary>Whether the saved microphone could be selected, and why not.</summary>
internal enum MicrophoneLoadState
{
    Ready,
    NoDevices,
    EnumerationFailed,
    SavedDeviceUnavailable
}

/// <summary>
/// The listed capture devices together with the saved choice. When the saved microphone cannot be selected,
/// the Windows default stands in for it without changing the saved preference.
/// </summary>
internal sealed record MicrophoneSelection(
    IReadOnlyList<Microphone> Devices,
    MicrophoneLoadState State,
    string? SavedId)
{
    /// <summary>The microphone in use: the saved one when it can be selected, otherwise the Windows default (null).</summary>
    internal string? EffectiveId => State == MicrophoneLoadState.Ready && !string.IsNullOrWhiteSpace(SavedId)
        ? SavedId
        : null;

    /// <summary>The effective microphone's position in a list that starts with "Windows default" at 0.</summary>
    internal int SelectedIndex
    {
        get
        {
            for (int index = 0; index < Devices.Count; index++)
            {
                if (string.Equals(Devices[index].Id, EffectiveId, StringComparison.Ordinal))
                    return index + 1;
            }

            return 0;
        }
    }

    /// <summary>Applies the selection rules to an enumeration result.</summary>
    internal static MicrophoneSelection For(string? savedId, IReadOnlyList<Microphone> devices)
    {
        if (devices.Count == 0)
            return new(devices, MicrophoneLoadState.NoDevices, savedId);
        if (string.IsNullOrWhiteSpace(savedId)
            || devices.Any(device => string.Equals(device.Id, savedId, StringComparison.Ordinal)))
            return new(devices, MicrophoneLoadState.Ready, savedId);
        return new(devices, MicrophoneLoadState.SavedDeviceUnavailable, savedId);
    }

    internal static MicrophoneSelection EnumerationFailed(string? savedId) =>
        new([], MicrophoneLoadState.EnumerationFailed, savedId);
}

/// <summary>
/// The WinMM device a capture opens. <see cref="SavedMissing"/> means a saved microphone was asked for but is not
/// connected, so the Windows default (<see cref="WaveMapper"/>) stands in for it.
/// </summary>
internal readonly record struct MicrophoneChoice(uint WaveInIndex, bool SavedMissing)
{
    /// <summary>WinMM's stand-in for the Windows default capture device.</summary>
    internal const uint WaveMapper = 0xFFFFFFFF;

    internal static MicrophoneChoice WindowsDefault => new(WaveMapper, SavedMissing: false);
    internal static MicrophoneChoice Missing => new(WaveMapper, SavedMissing: true);
}

/// <summary>
/// The one place that lists capture devices and decides which one is in use. Settings, the tray, and the
/// dictation session read the same <see cref="MicrophoneSelection"/>; a capture asks
/// <see cref="ChooseForCaptureAsync"/> which WinMM device to open.
/// </summary>
internal sealed class MicrophoneCatalog(
    Func<Task<IReadOnlyList<Microphone>>>? enumerate = null,
    Func<IReadOnlyList<WaveInDevice>>? waveInDevices = null,
    Func<string, Task<string?>>? lookupName = null)
{
    private readonly ConcurrentDictionary<string, string> _names = new(StringComparer.Ordinal);
    private readonly Func<Task<IReadOnlyList<Microphone>>> _enumerate = enumerate ?? EnumerateWindowsDevicesAsync;
    private readonly Func<IReadOnlyList<WaveInDevice>> _waveInDevices = waveInDevices ?? WaveInDevices.List;
    private readonly Func<string, Task<string?>> _lookupName = lookupName ?? LookUpWindowsNameAsync;

    /// <summary>Lists the devices and applies the selection rules. Never throws: a failed listing is a state.</summary>
    internal async Task<MicrophoneSelection> LoadAsync(string? savedId)
    {
        try
        {
            return MicrophoneSelection.For(savedId, await _enumerate().ConfigureAwait(false));
        }
        catch (Exception)
        {
            return MicrophoneSelection.EnumerationFailed(savedId);
        }
    }

    /// <summary>
    /// The WinMM device for a capture with this saved microphone. A device is matched exactly by its audio endpoint;
    /// only when no WinMM device reports an endpoint is the device's name compared with WinMM's product names.
    /// </summary>
    internal async Task<MicrophoneChoice> ChooseForCaptureAsync(string? savedId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(savedId))
            return MicrophoneChoice.WindowsDefault;

        IReadOnlyList<WaveInDevice> devices = _waveInDevices();
        if (MatchEndpoint(savedId, devices) is uint exact)
            return new MicrophoneChoice(exact, SavedMissing: false);

        WaveInDevice[] unidentified = devices.Where(device => device.EndpointId is null).ToArray();
        if (unidentified.Length == 0)
            return MicrophoneChoice.Missing;

        string? name = await LookUpNameAsync(savedId).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return name is not null && MatchName(name, unidentified) is uint byName
            ? new MicrophoneChoice(byName, SavedMissing: false)
            : MicrophoneChoice.Missing;
    }

    // A Windows capture device ID embeds its endpoint ID, for example
    // \\?\SWD#MMDEVAPI#{0.0.1.00000000}.{guid}#{2eef81be-...}; endpoint IDs are unique, so containment is exact.
    internal static uint? MatchEndpoint(string savedId, IReadOnlyList<WaveInDevice> devices)
    {
        foreach (WaveInDevice device in devices)
        {
            if (device.EndpointId is { } endpoint && savedId.Contains(endpoint, StringComparison.OrdinalIgnoreCase))
                return device.Index;
        }

        return null;
    }

    // WinMM cuts product names to 31 characters, so either name can contain the other.
    internal static uint? MatchName(string name, IReadOnlyList<WaveInDevice> devices)
    {
        foreach (WaveInDevice device in devices)
        {
            if (string.Equals(device.ProductName, name, StringComparison.OrdinalIgnoreCase)
                || device.ProductName.Contains(name, StringComparison.OrdinalIgnoreCase)
                || name.Contains(device.ProductName, StringComparison.OrdinalIgnoreCase))
                return device.Index;
        }

        return null;
    }

    private async Task<string?> LookUpNameAsync(string savedId)
    {
        if (_names.TryGetValue(savedId, out string? cached))
            return cached;
        string? name;
        try
        {
            name = await _lookupName(savedId).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(name))
            _names[savedId] = name;
        return name;
    }

    private static async Task<IReadOnlyList<Microphone>> EnumerateWindowsDevicesAsync()
    {
        DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(MediaDevice.GetAudioCaptureSelector());
        return devices.Select(device => new Microphone(device.Id, device.Name)).ToArray();
    }

    private static async Task<string?> LookUpWindowsNameAsync(string id) =>
        (await DeviceInformation.CreateFromIdAsync(id))?.Name;
}
