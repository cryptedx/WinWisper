using System.Runtime.InteropServices;
using WinWispr.Core;

namespace WinWispr.Interop;

/// <summary>
/// Brings the window a selection came from back to the foreground, so that Replace can paste into it. It reports
/// whether the window is in front afterwards; a window that no longer exists, or that Windows keeps in the
/// background, reports false and the caller falls back to copying.
/// </summary>
internal sealed class WindowActivator
{
    private const int PollInterval = 20;
    private const int MaxPolls = 25;
    private const int SwRestore = 9;

    private readonly Func<nint, bool> _exists;
    private readonly Func<nint, bool> _isMinimized;
    private readonly Action<nint> _restore;
    private readonly Action<nint> _bringToFront;
    private readonly Func<nint> _foreground;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    internal WindowActivator()
        : this(IsWindow, IsIconic, handle => ShowWindow(handle, SwRestore), BringToFront, GetForegroundWindow, Task.Delay)
    {
    }

    internal WindowActivator(
        Func<nint, bool> exists,
        Func<nint, bool> isMinimized,
        Action<nint> restore,
        Action<nint> bringToFront,
        Func<nint> foreground,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        _exists = exists;
        _isMinimized = isMinimized;
        _restore = restore;
        _bringToFront = bringToFront;
        _foreground = foreground;
        _delay = delay;
    }

    internal async Task<bool> ActivateAsync(WindowTarget target, CancellationToken cancellationToken)
    {
        nint handle = target.Hwnd;
        if (handle == 0 || !_exists(handle))
            return false;
        if (_foreground() == handle)
            return true;

        if (_isMinimized(handle))
            _restore(handle);
        _bringToFront(handle);
        // Activation is asynchronous: closing the preview and the focus change settle within a few frames.
        for (int poll = 0; poll < MaxPolls; poll++)
        {
            if (!_exists(handle))
                return false;
            if (_foreground() == handle)
                return true;
            await _delay(TimeSpan.FromMilliseconds(PollInterval), cancellationToken).ConfigureAwait(false);
            if (poll == MaxPolls / 2)
                _bringToFront(handle);
        }

        return _exists(handle) && _foreground() == handle;
    }

    // The last input event went to another process, so a plain SetForegroundWindow may be refused. Sharing the
    // foreground thread's input queue for the call lifts the restriction.
    private static void BringToFront(nint handle)
    {
        nint foreground = GetForegroundWindow();
        uint foregroundThread = foreground == 0 ? 0 : GetWindowThreadProcessId(foreground, out _);
        uint currentThread = GetCurrentThreadId();
        bool attached = foregroundThread != 0 && foregroundThread != currentThread
            && AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            SetForegroundWindow(handle);
        }
        finally
        {
            if (attached)
                AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint handle);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint handle);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint handle, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint handle);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint attachThread, uint attachToThread, bool attach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
