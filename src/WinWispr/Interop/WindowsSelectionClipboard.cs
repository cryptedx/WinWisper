using System.Runtime.InteropServices;

namespace WinWispr.Interop;

/// <summary>
/// The Windows clipboard behind <see cref="SelectionReader"/>. It captures the standard formats that can be put back
/// (text, HTML, RTF, a bitmap, files and a web link) as byte copies of their Win32 formats. Application-private formats
/// cannot be copied and are lost when the application's copy replaced them; a clipboard that held only such formats is
/// left as the copy made it. Everything that touches the clipboard runs on a clipboard thread of its own and, as for a
/// dictation, retries only opening the clipboard. The restore is one change, so a clipboard monitor that opens the
/// clipboard on every change cannot refuse part of it.
/// </summary>
internal sealed class WindowsSelectionClipboard : ISelectionClipboard
{
    /// <summary>
    /// The Win32 formats a capture asks for: those WinRT's Text, Html, Rtf, Bitmap, StorageItems and WebLink use. The
    /// bitmap is kept as one DIB, from which Windows derives the other DIB and CF_BITMAP again; files as CF_HDROP and as
    /// the shell's item list, which also holds items without a file path. All of them are self-contained memory blocks.
    /// </summary>
    private static readonly Lazy<uint[]> RestorableFormats = new(() =>
    [
        NativeClipboard.UnicodeText,
        NativeClipboard.Register("HTML Format"),
        NativeClipboard.Register("Rich Text Format"),
        NativeClipboard.Dib,
        NativeClipboard.DibV5,
        NativeClipboard.FileDrop,
        NativeClipboard.Register("Shell IDList Array"),
        NativeClipboard.Register("UniformResourceLocatorW")
    ]);

    private static readonly uint[] TextFormat = [NativeClipboard.UnicodeText];

    private readonly Func<IReadOnlyCollection<uint>, Task<ClipboardContent?>> _tryRead;
    private readonly Func<IReadOnlyList<ClipboardFormatData>, Task<uint?>> _tryWrite;
    private readonly Func<Task<uint?>> _tryEmpty;
    private readonly Func<uint> _sequenceNumber;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    internal WindowsSelectionClipboard()
        : this(new ClipboardOwnerThread())
    {
    }

    /// <param name="tryRead">One read of the given formats, or null when the clipboard was open.</param>
    /// <param name="tryWrite">One set of the given formats, or null when the clipboard was open.</param>
    /// <param name="tryEmpty">One emptying of the clipboard, or null when it was open.</param>
    /// <param name="sequenceNumber">The clipboard's change counter.</param>
    /// <param name="delay">The wait between two attempts to open the clipboard.</param>
    internal WindowsSelectionClipboard(
        Func<IReadOnlyCollection<uint>, Task<ClipboardContent?>> tryRead,
        Func<IReadOnlyList<ClipboardFormatData>, Task<uint?>> tryWrite,
        Func<Task<uint?>> tryEmpty,
        Func<uint> sequenceNumber,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        _tryRead = tryRead;
        _tryWrite = tryWrite;
        _tryEmpty = tryEmpty;
        _sequenceNumber = sequenceNumber;
        _delay = delay;
    }

    private WindowsSelectionClipboard(ClipboardOwnerThread owner)
        : this(
            formats => owner.InvokeAsync(window => NativeClipboard.TryRead(window, formats)),
            formats => owner.InvokeAsync(window => NativeClipboard.TryWrite(window, formats)),
            () => owner.InvokeAsync(NativeClipboard.TryEmpty),
            () => NativeClipboard.State().Sequence,
            Task.Delay)
    {
    }

    public uint SequenceNumber => _sequenceNumber();

    public async Task<object?> CaptureAsync(CancellationToken cancellationToken)
    {
        ClipboardContent content = await ReadAsync(RestorableFormats.Value, cancellationToken).ConfigureAwait(false);
        if (content.IsEmpty)
            return null;
        List<ClipboardFormatData> formats = WithOneBitmap(content.Formats);
        return new ClipboardSnapshot(formats.Count > 0 ? formats : null);
    }

    /// <summary>
    /// Puts the captured formats back in one set, in the order the clipboard held them, together with the formats that
    /// keep them out of Windows clipboard history, off the cloud clipboard and out of monitors that honor the exclusion.
    /// </summary>
    public async Task RestoreAsync(object? snapshot, CancellationToken cancellationToken)
    {
        switch (snapshot)
        {
            case null:
                await WindowsTextOutput.WhenClipboardOpenAsync(_tryEmpty, _delay, cancellationToken).ConfigureAwait(false);
                break;
            case ClipboardSnapshot { Formats: { } formats }:
                await WindowsTextOutput.WhenClipboardOpenAsync(() => _tryWrite(formats), _delay, cancellationToken)
                    .ConfigureAwait(false);
                break;
        }
    }

    public async Task<string?> ReadTextAsync(CancellationToken cancellationToken)
    {
        ClipboardContent content = await ReadAsync(TextFormat, cancellationToken).ConfigureAwait(false);
        return content.Formats.Count == 0 ? null : Text(content.Formats[0].Content);
    }

    private async Task<ClipboardContent> ReadAsync(IReadOnlyCollection<uint> formats, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        (ClipboardContent content, _) = await WindowsTextOutput
            .WhenClipboardOpenAsync(() => _tryRead(formats), _delay, cancellationToken)
            .ConfigureAwait(false);
        return content;
    }

    // Windows derives CF_DIB and CF_DIBV5 from each other and both from CF_BITMAP. A derived CF_DIBV5 repeats the color
    // masks after its header, which a reader of that CF_DIBV5, once put back, takes for pixels. The first DIB in the
    // clipboard's order is the one the program put there, or CF_DIB when it put CF_BITMAP, so only that one is kept.
    private static List<ClipboardFormatData> WithOneBitmap(IReadOnlyList<ClipboardFormatData> formats)
    {
        var kept = new List<ClipboardFormatData>(formats.Count);
        bool bitmap = false;
        foreach (ClipboardFormatData format in formats)
        {
            if (format.Format is NativeClipboard.Dib or NativeClipboard.DibV5)
            {
                if (bitmap)
                    continue;
                bitmap = true;
            }
            kept.Add(format);
        }
        return kept;
    }

    // CF_UNICODETEXT ends at its first NUL; the memory block can be longer.
    private static string Text(byte[] content)
    {
        ReadOnlySpan<char> text = MemoryMarshal.Cast<byte, char>(content);
        int end = text.IndexOf('\0');
        return new string(end < 0 ? text : text[..end]);
    }

    // Formats is null when the clipboard held content but none of it can be put back.
    private sealed record ClipboardSnapshot(IReadOnlyList<ClipboardFormatData>? Formats);
}
