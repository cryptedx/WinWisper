using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using WinRT.Interop;
using WinWispr.Core;

namespace WinWispr.Views;

/// <summary>
/// The translation preview: a thin panel at the overlay's position with the direction, the read-only scrollable
/// translation, Replace, Copy, Cancel and swap direction. It shows what the coordinator gives it and reports the user's choice; it holds no
/// decisions. It takes keyboard focus, and losing focus (a click elsewhere) acts as Cancel.
/// </summary>
internal sealed partial class TranslationPreviewWindow : Window
{
    private const double WidthDip = 520;
    private const double HeightDip = 300;
    private const double BottomMarginDip = 24;
    private const int GwlExStyle = -20;
    private const nint WsExToolWindow = 0x00000080;
    private const uint DwmwaWindowCornerPreference = 33;
    private const uint DwmwcpRound = 2;
    private const nint HwndTopmost = -1;
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpShowWindow = 0x0040;

    private TaskCompletionSource<PreviewChoice>? _choice;
    private CancellationTokenRegistration _cancellation;
    private nint _windowHandle;
    private bool _armed;
    private bool _busy;
    private bool _visible;
    private bool _closingForShutdown;
    private bool _canReplace = true;

    internal TranslationPreviewWindow()
    {
        InitializeComponent();
        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.SetBorderAndTitleBar(false, false);
        Activated += OnActivated;
        AppWindow.Closing += (_, args) =>
        {
            // The preview is reused: closing it hides it and counts as Cancel, except at shutdown.
            if (!_closingForShutdown)
                args.Cancel = true;
            Complete(PreviewChoice.Cancel);
            if (!_closingForShutdown)
                HideNow();
        };
    }

    /// <summary>
    /// Shows the preview at the overlay's position on the monitor of <paramref name="target"/> and completes with the
    /// user's choice. Cancelling the token closes the preview as Cancel. After a Swap the preview stays open and
    /// busy, with its actions disabled and "Translating…" shown; the next call shows the result in the same place.
    /// </summary>
    internal Task<PreviewChoice> ShowAsync(TranslationPreview preview, WindowTarget target, CancellationToken cancellationToken)
    {
        // Only one preview is open at a time; a stale one closes as Cancel. A busy one (a swap ran) is reused.
        bool reuse = _busy && _visible;
        if (!reuse)
            Complete(PreviewChoice.Cancel);
        _cancellation.Dispose();
        _busy = false;
        SetActionsEnabled(true);
        var choice = new TaskCompletionSource<PreviewChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        _choice = choice;
        if (!reuse)
            _armed = false;
        DirectionText.Text = TranslationPreviewPresentation.DirectionLabel(preview.Direction);
        DirectionText.Visibility = DirectionText.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        TranslationText.Text = preview.Text;
        _canReplace = preview.CanReplace;
        ReplaceButton.Visibility = preview.CanReplace ? Visibility.Visible : Visibility.Collapsed;
        SwapButton.Visibility = preview.CanReplace ? Visibility.Visible : Visibility.Collapsed;
        TextScroller.ChangeView(null, 0, null, disableAnimation: true);
        Title = TranslationPreviewPresentation.WindowTitle(preview.Direction);
        _cancellation = cancellationToken.Register(() => DispatcherQueue.TryEnqueue(() =>
        {
            Complete(PreviewChoice.Cancel);
            HideNow();
        }));

        if (_windowHandle == 0)
        {
            _windowHandle = WindowNative.GetWindowHandle(this);
            ApplyPresentation();
        }

        if (!reuse)
            Place(target.Hwnd);
        _visible = true;
        Activate();
        TakeForeground();
        TextScroller.Focus(FocusState.Programmatic);
        return choice.Task;
    }

    /// <summary>Closes the preview as Cancel when it is open, also while it waits busy for a swap.</summary>
    internal void Dismiss()
    {
        Complete(PreviewChoice.Cancel);
        HideNow();
    }

    /// <summary>Closes the window for good when the app exits.</summary>
    internal void CloseForShutdown()
    {
        _closingForShutdown = true;
        Close();
    }

    // The application the user copied from received the last input event (the synthetic Ctrl+C), so Windows may
    // refuse a plain SetForegroundWindow. Sharing that thread's input queue for the call lifts the restriction.
    private void TakeForeground()
    {
        nint foreground = GetForegroundWindow();
        uint foregroundThread = foreground == 0 ? 0 : GetWindowThreadProcessId(foreground, out _);
        uint currentThread = GetCurrentThreadId();
        bool attached = foregroundThread != 0 && foregroundThread != currentThread
            && AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            SetForegroundWindow(_windowHandle);
        }
        finally
        {
            if (attached)
                AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    private void Complete(PreviewChoice choice)
    {
        TaskCompletionSource<PreviewChoice>? pending = _choice;
        if (pending is null)
            return;
        _choice = null;
        if (choice == PreviewChoice.Swap)
        {
            // The window stays and the cancellation registration stays with it: Esc during the swap, a dictation
            // press or the end of the translation still close it through the token.
            _busy = true;
            SetActionsEnabled(false);
            TranslationText.Text = "Translating\u2026";
            pending.TrySetResult(choice);
            return;
        }

        _cancellation.Dispose();
        _cancellation = default;
        HideNow();
        pending.TrySetResult(choice);
    }

    private void HideNow()
    {
        _busy = false;
        _visible = false;
        AppWindow.Hide();
    }

    private void SetActionsEnabled(bool enabled)
    {
        ReplaceButton.IsEnabled = enabled;
        CopyButton.IsEnabled = enabled;
        SwapButton.IsEnabled = enabled;
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            // A click elsewhere acts as Cancel, but only after the preview was active once: showing a window
            // can send a deactivation before it ever had the focus.
            // While a swap runs the preview ignores a click elsewhere; it returns to the front with the result.
            if (_armed && !_busy)
                Complete(PreviewChoice.Cancel);
        }
        else
        {
            _armed = true;
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs args) => Complete(PreviewChoice.Cancel);

    private void CopyButton_Click(object sender, RoutedEventArgs args) => Complete(PreviewChoice.Copy);

    private void ReplaceButton_Click(object sender, RoutedEventArgs args) => Complete(PreviewChoice.Replace);

    private void SwapButton_Click(object sender, RoutedEventArgs args) => Complete(PreviewChoice.Swap);

    private void ReplaceAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (_canReplace && ReplaceButton.IsEnabled)
            Complete(PreviewChoice.Replace);
    }

    private void CancelAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Complete(PreviewChoice.Cancel);
    }

    private void CopyAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Complete(PreviewChoice.Copy);
    }

    private void ApplyPresentation()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        // A tool window stays out of the taskbar and Alt+Tab, like the overlay.
        nint style = GetWindowLongPtr(_windowHandle, GwlExStyle);
        SetWindowLongPtr(_windowHandle, GwlExStyle, style | WsExToolWindow);
        uint corners = DwmwcpRound;
        DwmSetWindowAttribute(_windowHandle, DwmwaWindowCornerPreference, ref corners, sizeof(uint));
    }

    // The overlay sits centered at the bottom of the work area; the preview's bottom edge sits where the overlay's does.
    private void Place(nint targetHandle)
    {
        nint monitor = targetHandle == 0
            ? MonitorFromWindow(_windowHandle, MonitorDefaultToNearest)
            : MonitorFromWindow(targetHandle, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info))
            return;

        uint dpi = GetDpiForWindow(_windowHandle);
        if (dpi == 0)
            dpi = 96;
        int workWidth = info.Work.right - info.Work.left;
        int workHeight = info.Work.bottom - info.Work.top;
        int margin = DipToPixels(BottomMarginDip, dpi);
        int width = Math.Min(DipToPixels(WidthDip, dpi), workWidth);
        int height = Math.Min(DipToPixels(HeightDip, dpi), Math.Max(1, workHeight - margin));
        int x = info.Work.left + Math.Max(0, (workWidth - width) / 2);
        int y = info.Work.bottom - height - margin;
        SetWindowPos(_windowHandle, HwndTopmost, x, y, width, height, SwpShowWindow);
    }

    private static int DipToPixels(double dips, uint dpi) => (int)Math.Round(dips * dpi / 96d);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint handle, int index, nint value);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint handle, uint attribute, ref uint value, uint valueSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint MonitorFromWindow(nint handle, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint handle, nint insertAfter, int x, int y, int width, int height, uint flags);

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

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(nint handle);
}
