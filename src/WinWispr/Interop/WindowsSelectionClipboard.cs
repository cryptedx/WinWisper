using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;

namespace WinWispr.Interop;

/// <summary>
/// The Windows clipboard behind <see cref="SelectionReader"/>. It captures the standard formats that can be put back
/// (text, HTML, RTF, a bitmap, files and a web link). Application-private formats cannot be copied and are lost
/// when the application's copy replaced them; a clipboard that held only such formats is left as the copy made it.
/// Everything that touches the clipboard runs on the UI dispatcher.
/// </summary>
internal sealed class WindowsSelectionClipboard(DispatcherQueue dispatcher) : ISelectionClipboard
{
    private const int ClipboardAttempts = 4;
    private static readonly TimeSpan ClipboardRetryDelay = TimeSpan.FromMilliseconds(25);

    public uint SequenceNumber => GetClipboardSequenceNumber();

    public Task<object?> CaptureAsync(CancellationToken cancellationToken) =>
        OnDispatcherAsync<object?>(CaptureCoreAsync, cancellationToken);

    public Task RestoreAsync(object? snapshot, CancellationToken cancellationToken) =>
        OnDispatcherAsync<object?>(
            () =>
            {
                switch (snapshot)
                {
                    case null:
                        Clipboard.Clear();
                        break;
                    case ClipboardSnapshot { Package: { } package }:
                        if (!Clipboard.SetContentWithOptions(package, WindowsTextOutput.CreateClipboardOptions()))
                            throw new InvalidOperationException("Could not restore the clipboard content.");
                        Clipboard.Flush();
                        break;
                }

                return Task.FromResult<object?>(null);
            },
            cancellationToken);

    public Task<string?> ReadTextAsync(CancellationToken cancellationToken) =>
        OnDispatcherAsync(
            async () =>
            {
                for (int attempt = 1; ; attempt++)
                {
                    try
                    {
                        DataPackageView view = Clipboard.GetContent();
                        return view.Contains(StandardDataFormats.Text) ? await view.GetTextAsync() : null;
                    }
                    catch (COMException) when (attempt < ClipboardAttempts)
                    {
                        // Another process holds the clipboard open for a moment.
                        await Task.Delay(ClipboardRetryDelay);
                    }
                }
            },
            cancellationToken);

    private static async Task<object?> CaptureCoreAsync()
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await CaptureOnceAsync();
            }
            catch (COMException) when (attempt < ClipboardAttempts)
            {
                await Task.Delay(ClipboardRetryDelay);
            }
        }
    }

    private static async Task<object?> CaptureOnceAsync()
    {
        DataPackageView view = Clipboard.GetContent();
        if (view.AvailableFormats.Count == 0)
            return null;

        var package = new DataPackage();
        bool restorable = false;
        if (view.Contains(StandardDataFormats.Text))
        {
            package.SetText(await view.GetTextAsync());
            restorable = true;
        }

        if (view.Contains(StandardDataFormats.Html))
        {
            package.SetHtmlFormat(await view.GetHtmlFormatAsync());
            restorable = true;
        }

        if (view.Contains(StandardDataFormats.Rtf))
        {
            package.SetRtf(await view.GetRtfAsync());
            restorable = true;
        }

        if (view.Contains(StandardDataFormats.Bitmap))
        {
            // The view stops serving once the clipboard changes, so the pixels are copied now.
            RandomAccessStreamReference reference = await view.GetBitmapAsync();
            using IRandomAccessStreamWithContentType source = await reference.OpenReadAsync();
            var copy = new InMemoryRandomAccessStream();
            await RandomAccessStream.CopyAsync(source, copy);
            copy.Seek(0);
            package.SetBitmap(RandomAccessStreamReference.CreateFromStream(copy));
            restorable = true;
        }

        if (view.Contains(StandardDataFormats.StorageItems))
        {
            package.SetStorageItems(await view.GetStorageItemsAsync());
            restorable = true;
        }

        if (view.Contains(StandardDataFormats.WebLink))
        {
            package.SetWebLink(await view.GetWebLinkAsync());
            restorable = true;
        }

        return new ClipboardSnapshot(restorable ? package : null);
    }

    private Task<T> OnDispatcherAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task RunAsync()
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                completion.TrySetResult(await work());
            }
            catch (OperationCanceledException exception)
            {
                completion.TrySetCanceled(exception.CancellationToken);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        }

        if (!dispatcher.TryEnqueue(() => _ = RunAsync()))
            completion.TrySetException(new InvalidOperationException("The UI dispatcher is unavailable."));
        return completion.Task;
    }

    // Package is null when the clipboard held content but none of it can be put back.
    private sealed record ClipboardSnapshot(DataPackage? Package);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
