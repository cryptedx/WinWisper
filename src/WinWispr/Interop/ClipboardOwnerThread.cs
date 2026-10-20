using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace WinWispr.Interop;

/// <summary>
/// The thread whose message-only window owns what WinWispr puts on the clipboard. Windows sends the clipboard owner
/// messages (WM_DESTROYCLIPBOARD when another program empties the clipboard), so this thread only pumps messages and
/// runs the short clipboard work posted to it; it never waits between attempts. It starts with the first work.
/// </summary>
internal sealed class ClipboardOwnerThread
{
    private const uint WmAppWork = 0x8000 + 0x43;
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(5);
    private readonly ConcurrentQueue<Action> _work = new();
    private readonly object _startGate = new();
    private nint _window;

    /// <summary>Runs <paramref name="work"/> on the owner thread with the owner window's handle.</summary>
    internal Task<T> InvokeAsync<T>(Func<nint, T> work)
    {
        nint window = EnsureStarted();
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Enqueue(() =>
        {
            // Work whose post failed was already reported as failed; a later post must not run it after all.
            if (completion.Task.IsCompleted)
                return;
            try
            {
                completion.TrySetResult(work(window));
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        if (!PostMessage(window, WmAppWork, 0, 0))
            completion.TrySetException(new InvalidOperationException("The clipboard thread is unavailable."));
        return completion.Task;
    }

    private nint EnsureStarted()
    {
        lock (_startGate)
        {
            if (_window != 0)
                return _window;

            var ready = new TaskCompletionSource<nint>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() => Run(ready)) { IsBackground = true, Name = "WinWispr.Clipboard" };
            thread.Start();
            return _window = ready.Task.WaitAsync(StartTimeout).GetAwaiter().GetResult();
        }
    }

    private void Run(TaskCompletionSource<nint> ready)
    {
        NativeMessageWindow window;
        try
        {
            window = new NativeMessageWindow(OnMessage);
        }
        catch (Exception exception)
        {
            ready.TrySetException(exception);
            return;
        }

        ready.TrySetResult(window.Handle);
        while (GetMessage(out NativeMessage message, 0, 0, 0) > 0)
            DispatchMessage(ref message);
        GC.KeepAlive(window);
    }

    private nint OnMessage(uint message, nint wParam, nint lParam)
    {
        if (message == WmAppWork)
        {
            while (_work.TryDequeue(out Action? work))
                work();
        }
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMessage
    {
        internal nint Window;
        internal uint Message;
        internal nint WParam;
        internal nint LParam;
        internal uint Time;
        internal int PointX;
        internal int PointY;
    }

    [DllImport("user32.dll", EntryPoint = "GetMessageW")]
    private static extern int GetMessage(out NativeMessage message, nint window, uint minimum, uint maximum);

    [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
    private static extern nint DispatchMessage(ref NativeMessage message);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
}
