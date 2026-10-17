using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Audio;

namespace WinWispr.Tests.Audio;

[TestClass]
public sealed class MicrophoneCatalogTests
{
    private static readonly Microphone[] TwoDevices = [new("mic-1", "Headset"), new("mic-2", "USB microphone")];

    [TestMethod]
    [DataRow(null, 0, (int)MicrophoneLoadState.Ready, null)]
    [DataRow("", 0, (int)MicrophoneLoadState.Ready, null)]
    [DataRow("mic-2", 2, (int)MicrophoneLoadState.Ready, "mic-2")]
    [DataRow("missing", 0, (int)MicrophoneLoadState.SavedDeviceUnavailable, null)]
    public async Task Selection_keeps_the_windows_default_when_the_saved_device_is_missing(
        string? savedId, int expectedIndex, int expectedState, string? expectedEffectiveId)
    {
        MicrophoneSelection selection = await new MicrophoneCatalog(() => Task.FromResult<IReadOnlyList<Microphone>>(TwoDevices))
            .LoadAsync(savedId);

        Assert.AreEqual((MicrophoneLoadState)expectedState, selection.State);
        Assert.AreEqual(expectedIndex, selection.SelectedIndex);
        Assert.AreEqual(expectedEffectiveId, selection.EffectiveId);
        Assert.AreEqual(savedId, selection.SavedId, "The saved preference is never rewritten.");
        CollectionAssert.AreEqual(TwoDevices, selection.Devices.ToArray());
    }

    [TestMethod]
    public async Task Selection_reports_when_no_devices_exist()
    {
        MicrophoneSelection selection = await new MicrophoneCatalog(() => Task.FromResult<IReadOnlyList<Microphone>>([]))
            .LoadAsync("saved");

        Assert.AreEqual(MicrophoneLoadState.NoDevices, selection.State);
        Assert.AreEqual(0, selection.SelectedIndex);
        Assert.IsNull(selection.EffectiveId);
    }

    [TestMethod]
    public async Task A_failed_listing_is_a_state_not_an_exception()
    {
        MicrophoneSelection selection = await new MicrophoneCatalog(
            () => Task.FromException<IReadOnlyList<Microphone>>(new UnauthorizedAccessException()))
            .LoadAsync("saved");

        Assert.AreEqual(MicrophoneLoadState.EnumerationFailed, selection.State);
        Assert.AreEqual(0, selection.Devices.Count);
        Assert.IsNull(selection.EffectiveId);
    }

    [TestMethod]
    [DataRow((int)MicrophoneLoadState.Ready, "saved-mic", "saved-mic")]
    [DataRow((int)MicrophoneLoadState.Ready, null, null)]
    [DataRow((int)MicrophoneLoadState.NoDevices, "saved-mic", null)]
    [DataRow((int)MicrophoneLoadState.EnumerationFailed, "saved-mic", null)]
    [DataRow((int)MicrophoneLoadState.SavedDeviceUnavailable, "saved-mic", null)]
    public void The_windows_default_stands_in_only_while_the_saved_device_cannot_be_selected(
        int state, string? savedId, string? expectedEffectiveId)
    {
        Assert.AreEqual(expectedEffectiveId, new MicrophoneSelection(TwoDevices, (MicrophoneLoadState)state, savedId).EffectiveId);
    }

    private const string HeadsetId = @"\\?\SWD#MMDEVAPI#{0.0.1.00000000}.{aaaaaaaa-0000-0000-0000-000000000001}#{2eef81be-33fa-4800-9670-1cd474972c3f}";
    private const string UsbId = @"\\?\SWD#MMDEVAPI#{0.0.1.00000000}.{aaaaaaaa-0000-0000-0000-000000000002}#{2eef81be-33fa-4800-9670-1cd474972c3f}";

    private static MicrophoneCatalog CatalogWith(IReadOnlyList<WaveInDevice> waveIn, Dictionary<string, string>? names = null) =>
        new(
            () => Task.FromResult<IReadOnlyList<Microphone>>([]),
            () => waveIn,
            id => Task.FromResult(names is not null && names.TryGetValue(id, out string? name) ? name : null));

    [TestMethod]
    public async Task No_saved_microphone_opens_the_windows_default()
    {
        MicrophoneChoice choice = await CatalogWith([]).ChooseForCaptureAsync(null, CancellationToken.None);

        Assert.AreEqual(MicrophoneChoice.WindowsDefault, choice);
    }

    [TestMethod]
    public async Task A_saved_microphone_is_matched_by_its_endpoint_even_when_names_are_alike()
    {
        // The second device's name contains the first one's: the old name match picked whichever came first.
        MicrophoneCatalog catalog = CatalogWith(
        [
            new WaveInDevice(0, "Microphone", "{0.0.1.00000000}.{aaaaaaaa-0000-0000-0000-000000000001}"),
            new WaveInDevice(1, "Microphone (USB Audio)", "{0.0.1.00000000}.{aaaaaaaa-0000-0000-0000-000000000002}")
        ],
            new Dictionary<string, string> { [UsbId] = "Microphone" });

        Assert.AreEqual(new MicrophoneChoice(1, SavedMissing: false), await catalog.ChooseForCaptureAsync(UsbId, CancellationToken.None));
        Assert.AreEqual(new MicrophoneChoice(0, SavedMissing: false), await catalog.ChooseForCaptureAsync(HeadsetId, CancellationToken.None));
    }

    [TestMethod]
    public async Task A_saved_microphone_that_is_not_connected_falls_back_to_the_windows_default()
    {
        MicrophoneCatalog catalog = CatalogWith(
            [new WaveInDevice(0, "Headset", "{0.0.1.00000000}.{aaaaaaaa-0000-0000-0000-000000000001}")],
            new Dictionary<string, string> { [UsbId] = "Headset" });

        MicrophoneChoice choice = await catalog.ChooseForCaptureAsync(UsbId, CancellationToken.None);

        Assert.AreEqual(MicrophoneChoice.Missing, choice, "With endpoint IDs available, a similar name must not stand in for the device.");
        Assert.AreEqual(MicrophoneChoice.WaveMapper, choice.WaveInIndex);
    }

    [TestMethod]
    public async Task Without_endpoint_ids_the_name_still_finds_the_device()
    {
        MicrophoneCatalog catalog = CatalogWith(
            [new WaveInDevice(0, "Line In", null), new WaveInDevice(1, "USB Audio Device Microphone ar", null)],
            new Dictionary<string, string> { [UsbId] = "USB Audio Device Microphone array" });

        Assert.AreEqual(new MicrophoneChoice(1, SavedMissing: false), await catalog.ChooseForCaptureAsync(UsbId, CancellationToken.None));
        Assert.AreEqual(MicrophoneChoice.Missing, await catalog.ChooseForCaptureAsync(HeadsetId, CancellationToken.None),
            "A device whose name cannot be looked up is missing.");
    }

    [TestMethod]
    public async Task A_failed_name_lookup_counts_as_missing()
    {
        var catalog = new MicrophoneCatalog(
            () => Task.FromResult<IReadOnlyList<Microphone>>([]),
            () => [new WaveInDevice(0, "Headset", null)],
            _ => Task.FromException<string?>(new FileNotFoundException()));

        Assert.AreEqual(MicrophoneChoice.Missing, await catalog.ChooseForCaptureAsync(UsbId, CancellationToken.None));
    }

    [TestMethod]
    [TestCategory("WindowsAcceptance")]
    public async Task Every_capture_device_on_this_machine_resolves_to_a_wavein_device()
    {
        var catalog = new MicrophoneCatalog();
        MicrophoneSelection selection = await catalog.LoadAsync(null);
        if (selection.Devices.Count == 0)
            Assert.Inconclusive("This machine lists no capture devices.");
        IReadOnlyList<WaveInDevice> waveIn = WaveInDevices.List();
        Console.WriteLine(string.Join(Environment.NewLine, waveIn.Select(device =>
            $"waveIn {device.Index}: '{device.ProductName}' endpoint {device.EndpointId ?? "(none)"}")));

        foreach (Microphone microphone in selection.Devices)
        {
            MicrophoneChoice choice = await catalog.ChooseForCaptureAsync(microphone.Id, CancellationToken.None);
            Assert.IsFalse(choice.SavedMissing, $"{microphone.Name} ({microphone.Id}) found no waveIn device.");
            Console.WriteLine($"{microphone.Name} -> waveIn {choice.WaveInIndex}"
                + (MicrophoneCatalog.MatchEndpoint(microphone.Id, waveIn) is null ? " (by name)" : " (by endpoint)"));
        }
    }
}
