using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Compositor = Microsoft.UI.Composition.Compositor;
using Visual = Microsoft.UI.Composition.Visual;
using Windows.System.Power;
using Windows.UI.ViewManagement;
using WinWispr.Core;
using WinRT.Interop;

namespace WinWispr.Views;

internal sealed partial class OverlayWindow : Window
{
    private const double OverlayWidthDip = OverlayGeometry.CapsuleWidthDip;
    private const double OverlayHeightDip = OverlayGeometry.CapsuleHeightDip;
    // Surface tint over the blurred backdrop, and the stronger tint when blur is unavailable.
    private const byte AcrylicTintAlpha = 0xCC;
    private const byte FallbackTintAlpha = 0xD9;
    private const int WaveformBarCount = 17;
    private const double WaveformBarMinHeight = 4;
    private const double WaveformBarMaxHeight = 26;
    private const int WaveformRefreshIntervalMilliseconds = 16;
    private const double CancelRingCenter = 20;
    private const double CancelRingRadius = 18;
    private const int GwlStyle = -16;
    private const int GwlExStyle = -20;
    private const uint WsCaption = 0x00C00000;
    private const uint WsThickFrame = 0x00040000;
    private const uint WsMinimizeBox = 0x00020000;
    private const uint WsMaximizeBox = 0x00010000;
    private const uint WsExToolWindow = 0x00000080;
    private const uint WsExNoActivate = 0x08000000;
    private const uint WsExLayered = 0x00080000;
    private const uint WsExTransparent = 0x00000020;
    private const uint LwaAlpha = 0x00000002;
    private const int SmRemoteSession = 0x1000;
    private const uint DwmwaBorderColor = 34;
    private const uint DwmwaCloak = 13;
    private const uint DwmwaWindowCornerPreference = 33;
    private const uint DwmwcpDoNotRound = 1;
    private const uint WmDpiChanged = 0x02E0;
    private const int MdtEffectiveDpi = 0;
    private const int HitTestIntervalMilliseconds = 30;
    private const uint DwmColorNone = 0xFFFFFFFE;
    private const uint WmMouseActivate = 0x0021;
    private const nint MaNoActivate = 3;
    private const nint HwndTopmost = -1;
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpFrameChanged = 0x0020;

    private readonly DispatcherQueueTimer _cancelTimer;
    private readonly DispatcherQueueTimer _hitTestTimer;
    private readonly UISettings _uiSettings = new();
    private readonly List<Border> _waveformBars = [];
    private readonly CapsuleBackdrop _backdrop = new();
    private readonly double[] _waveformLevels = new double[WaveformBarCount];
    private readonly WindowProc _windowProc;
    private DateTimeOffset _lastWaveformRefresh;
    private DateTimeOffset? _cancelDeadline;
    private TimeSpan _cancelDuration;
    private Action? _transcribeRequested;
    private Action? _deleteRequested;
    private Action? _discardRequested;
    private OverlayView _view = OverlayView.Recording;
    private bool _pointerOver;
    private bool _awaitingFirstFrame;
    private int _preparedFrames;
    private nint _windowHandle;
    private nint _originalWindowProc;
    private nint _targetHandle;
    private PixelRect _capsule;
    private bool _clickThrough;

    internal OverlayWindow()
    {
        InitializeComponent();
        SystemBackdrop = _backdrop;
        ShadowHost.Loaded += (_, _) => AttachShadow();
        InitializeWaveformBars();
        _windowProc = WindowProcedure;
        _cancelTimer = DispatcherQueue.GetForCurrentThread()?.CreateTimer()
            ?? throw new InvalidOperationException("WinWispr overlay must be created on the UI dispatcher.");
        _cancelTimer.Interval = TimeSpan.FromMilliseconds(50);
        _cancelTimer.Tick += CancelTimer_Tick;
        _hitTestTimer = this.DispatcherQueue.CreateTimer();
        _hitTestTimer.Interval = TimeSpan.FromMilliseconds(HitTestIntervalMilliseconds);
        _hitTestTimer.Tick += (_, _) => UpdateClickThrough();
        Closed += (_, _) => CancelPendingReveal();

        if (AppWindow.Presenter is OverlappedPresenter presenter)
            presenter.SetBorderAndTitleBar(false, false);
    }

    /// <summary>Forces the blurred (true) or tinted (false) material; null follows Windows. For visual checks.</summary>
    internal bool? AcrylicOverride { get; set; }

    internal void ShowAt(WindowTarget target)
    {
        if (_windowHandle == 0)
        {
            _windowHandle = WindowNative.GetWindowHandle(this);
            ApplyOverlayPresentation();
            ApplyNoActivateStyle();
        }

        if (!AppWindow.IsVisible)
        {
            // Keep the native surface off screen until XAML has drawn its content.
            SetCloaked(true);
            _awaitingFirstFrame = true;
            _preparedFrames = 0;
            CompositionTarget.Rendering += OnPreparingFrame;
        }
        PlaceOnTargetMonitor(target.Hwnd);
        ApplyMaterial();
        UpdateClickThrough();
        AppWindow.Show(activateWindow: false);
        Surface.UpdateLayout();
        _hitTestTimer.Start();
    }

    internal void HideOverlay()
    {
        _cancelTimer.Stop();
        _hitTestTimer.Stop();
        CancelPendingReveal();
        StatusSpinner.IsActive = false;
        AppWindow.Hide();
    }

    private void OnPreparingFrame(object? sender, object args)
    {
        if (!Surface.IsLoaded || Surface.ActualWidth <= 0 || Surface.ActualHeight <= 0)
            return;
        // Reveal after two layout-ready rendering passes. Rendered does not fire for every pass.
        if (++_preparedFrames < 3)
            return;

        CancelPendingReveal();
        SetCloaked(false);
    }

    private void CancelPendingReveal()
    {
        if (!_awaitingFirstFrame)
            return;
        CompositionTarget.Rendering -= OnPreparingFrame;
        _awaitingFirstFrame = false;
    }

    private void SetCloaked(bool cloaked)
    {
        uint value = cloaked ? 1u : 0u;
        Marshal.ThrowExceptionForHR(DwmSetWindowAttribute(
            _windowHandle, DwmwaCloak, ref value, sizeof(uint)));
    }

    internal void SetActions(Action transcribeRequested, Action deleteRequested, Action discardRequested)
    {
        _transcribeRequested = transcribeRequested;
        _deleteRequested = deleteRequested;
        _discardRequested = discardRequested;
    }

    internal void ShowRecording(ReadOnlySpan<float> peaks)
    {
        _cancelTimer.Stop();
        _cancelDeadline = null;
        _view = OverlayView.Recording;
        Surface.Width = OverlayWidthDip;
        Array.Clear(_waveformLevels, 0, _waveformLevels.Length);
        SetWaveform(peaks);
        UpdatePanelVisibility();
    }

    internal void ShowCancel(TimeSpan remaining)
    {
        _view = OverlayView.Cancel;
        _cancelDuration = remaining > TimeSpan.Zero ? remaining : TimeSpan.FromSeconds(5);
        _cancelDeadline = DateTimeOffset.UtcNow + _cancelDuration;
        Surface.Width = OverlayWidthDip;
        SetCancelProgress(1);
        UpdatePanelVisibility();
        _cancelTimer.Start();
    }

    internal void ShowStatus(OverlayStatus status)
    {
        _cancelTimer.Stop();
        _cancelDeadline = null;
        _view = OverlayView.Status;
        Surface.Width = OverlayWidthDip;
        bool hasDetail = status.Detail is not null;
        StatusText.Text = status.Text;
        StatusText.FontSize = hasDetail ? 12 : 14;
        StatusDetail.Text = status.Detail ?? string.Empty;
        StatusDetail.Visibility = hasDetail ? Visibility.Visible : Visibility.Collapsed;
        StatusSpinner.Visibility = status.ShowsProgress ? Visibility.Visible : Visibility.Collapsed;
        UpdatePanelVisibility();
    }

    internal void SetWaveform(ReadOnlySpan<float> peaks)
    {
        if (_view != OverlayView.Recording)
            return;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (now - _lastWaveformRefresh < TimeSpan.FromMilliseconds(WaveformRefreshIntervalMilliseconds))
            return;
        _lastWaveformRefresh = now;

        for (int index = 0; index < WaveformBarCount; index++)
        {
            double targetLevel = 0;
            if (peaks.Length > 0)
            {
                int bucketStart = index * peaks.Length / WaveformBarCount;
                int bucketEnd = Math.Max(bucketStart + 1, (index + 1) * peaks.Length / WaveformBarCount);
                for (int peakIndex = bucketStart; peakIndex < bucketEnd && peakIndex < peaks.Length; peakIndex++)
                    targetLevel = Math.Max(targetLevel, NormalizePeak(peaks[peakIndex]));
            }
            double currentLevel = _waveformLevels[index];
            double smoothing = targetLevel >= currentLevel ? 0.35 : 0.15;
            double level = currentLevel + (targetLevel - currentLevel) * smoothing;
            _waveformLevels[index] = level;
            Border bar = _waveformBars[index];
            // Resize instead of scaling so the round caps keep their shape; at rest each bar is a dot.
            bar.Height = WaveformBarMinHeight + level * (WaveformBarMaxHeight - WaveformBarMinHeight);
            bar.Opacity = 0.35 + level * 0.65;
        }
    }

    internal static double NormalizePeak(float peak)
    {
        if (!float.IsFinite(peak) || peak <= 0)
            return 0;

        double decibels = 20 * Math.Log10(Math.Min(peak, 1f));
        return Math.Clamp((decibels + 50) / 42, 0, 1);
    }

    private void InitializeWaveformBars()
    {
        var brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x6D, 0x5D, 0xFC));
        for (int index = 0; index < WaveformBarCount; index++)
        {
            var bar = new Border
            {
                Width = WaveformBarMinHeight,
                Height = WaveformBarMinHeight,
                VerticalAlignment = VerticalAlignment.Center,
                Background = brush,
                CornerRadius = new CornerRadius(WaveformBarMinHeight / 2),
                Opacity = 0.35
            };
            _waveformBars.Add(bar);
            RecordingBars.Children.Add(bar);
        }
    }

    private void Surface_PointerEntered(object sender, PointerRoutedEventArgs args)
    {
        if (_view != OverlayView.Recording)
            return;

        _pointerOver = true;
        UpdatePanelVisibility();
    }

    private void Surface_PointerExited(object sender, PointerRoutedEventArgs args)
    {
        _pointerOver = false;
        if (_view == OverlayView.Recording)
            UpdatePanelVisibility();
    }

    private void TranscribeButton_Click(object sender, RoutedEventArgs args) => _transcribeRequested?.Invoke();

    private void StopButton_Click(object sender, RoutedEventArgs args) => _deleteRequested?.Invoke();

    private void CancelButton_Click(object sender, RoutedEventArgs args) => _discardRequested?.Invoke();

    private void CancelTimer_Tick(DispatcherQueueTimer sender, object args)
    {
        if (_view != OverlayView.Cancel || _cancelDeadline is not DateTimeOffset deadline)
        {
            _cancelTimer.Stop();
            return;
        }

        TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
        double progress = remaining.TotalSeconds / _cancelDuration.TotalSeconds;
        SetCancelProgress(Math.Clamp(progress, 0, 1));
        if (remaining <= TimeSpan.Zero)
        {
            _cancelDeadline = null;
            _cancelTimer.Stop();
        }
    }

    private void UpdatePanelVisibility()
    {
        bool recording = _view == OverlayView.Recording;
        RecordingPanel.Visibility = recording && !_pointerOver
            ? Visibility.Visible
            : Visibility.Collapsed;
        HoverPanel.Visibility = recording && _pointerOver
            ? Visibility.Visible
            : Visibility.Collapsed;
        CancelPanel.Visibility = _view == OverlayView.Cancel
            ? Visibility.Visible
            : Visibility.Collapsed;
        bool status = _view == OverlayView.Status;
        StatusPanel.Visibility = status
            ? Visibility.Visible
            : Visibility.Collapsed;
        StatusSpinner.IsActive = status && StatusSpinner.Visibility == Visibility.Visible;
        // The overlay has no title bar; the title names its state for screen readers and UI Automation.
        Title = _view switch
        {
            OverlayView.Recording => "WinWispr: Recording",
            OverlayView.Cancel => "WinWispr: Delete recording?",
            OverlayView.Status => $"WinWispr: {StatusText.Text}",
            _ => "WinWispr"
        };
    }

    private void SetCancelProgress(double progress)
    {
        progress = Math.Clamp(progress, 0, 1);
        if (progress <= 0)
        {
            CancelProgressArc.Data = null;
            return;
        }

        var segments = new PathSegmentCollection();
        Windows.Foundation.Point start = PolarPoint(-90);
        if (progress >= 0.9999)
        {
            segments.Add(new ArcSegment
            {
                Point = PolarPoint(90),
                Size = new Windows.Foundation.Size(CancelRingRadius, CancelRingRadius),
                SweepDirection = SweepDirection.Clockwise
            });
            segments.Add(new ArcSegment
            {
                Point = start,
                Size = new Windows.Foundation.Size(CancelRingRadius, CancelRingRadius),
                SweepDirection = SweepDirection.Clockwise
            });
        }
        else
        {
            double sweep = progress * 360;
            segments.Add(new ArcSegment
            {
                Point = PolarPoint(-90 + sweep),
                Size = new Windows.Foundation.Size(CancelRingRadius, CancelRingRadius),
                IsLargeArc = sweep > 180,
                SweepDirection = SweepDirection.Clockwise
            });
        }

        var figure = new PathFigure
        {
            StartPoint = start,
            Segments = segments
        };
        CancelProgressArc.Data = new PathGeometry
        {
            Figures = new PathFigureCollection { figure }
        };
    }

    private static Windows.Foundation.Point PolarPoint(double angleDegrees)
    {
        double radians = angleDegrees * Math.PI / 180;
        return new Windows.Foundation.Point(
            CancelRingCenter + CancelRingRadius * Math.Cos(radians),
            CancelRingCenter + CancelRingRadius * Math.Sin(radians));
    }

    private void ApplyOverlayPresentation()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        nint style = GetWindowLongPtr(_windowHandle, GwlStyle);
        style &= ~(nint)(WsCaption | WsThickFrame | WsMinimizeBox | WsMaximizeBox);
        SetWindowLongPtr(_windowHandle, GwlStyle, style);
        uint borderColor = DwmColorNone;
        DwmSetWindowAttribute(_windowHandle, DwmwaBorderColor, ref borderColor, sizeof(uint));
        // The XAML capsule draws the shape; DWM must not round or clip the transparent window.
        uint cornerPreference = DwmwcpDoNotRound;
        DwmSetWindowAttribute(_windowHandle, DwmwaWindowCornerPreference, ref cornerPreference, sizeof(uint));
        // Let DWM compose the client area with per-pixel alpha so the shadow margin stays see-through.
        var sheet = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Marshal.ThrowExceptionForHR(DwmExtendFrameIntoClientArea(_windowHandle, ref sheet));
        SetWindowPos(
            _windowHandle,
            nint.Zero,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpFrameChanged);
    }

    private void ApplyNoActivateStyle()
    {
        nint style = GetWindowLongPtr(_windowHandle, GwlExStyle);
        SetWindowLongPtr(
            _windowHandle,
            GwlExStyle,
            style | (nint)(WsExToolWindow | WsExNoActivate | WsExLayered));
        // Layered only so WS_EX_TRANSPARENT can pass clicks to other applications; content stays opaque.
        SetLayeredWindowAttributes(_windowHandle, 0, 255, LwaAlpha);
        _originalWindowProc = SetWindowLongPtr(
            _windowHandle,
            -4,
            Marshal.GetFunctionPointerForDelegate(_windowProc));
    }

    private void PlaceOnTargetMonitor(nint targetHandle)
    {
        _targetHandle = targetHandle;
        nint monitor = targetHandle == 0
            ? MonitorFromWindow(_windowHandle, MonitorDefaultToNearest)
            : MonitorFromWindow(targetHandle, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == 0 || !GetMonitorInfo(monitor, ref info))
            return;

        // Size for the target monitor, not the monitor the hidden overlay was last on.
        uint dpi = GetDpiForMonitor(monitor, MdtEffectiveDpi, out uint dpiX, out _) == 0 ? dpiX : 96;
        var workArea = new PixelRect(info.Work.left, info.Work.top, info.Work.right, info.Work.bottom);
        OverlayPlacement placement = OverlayGeometry.Place(workArea, dpi);
        _capsule = placement.Capsule;
        PixelRect window = placement.Window;

        _backdrop.SetGeometry(
            new Vector2(window.Width, window.Height),
            new Vector2(_capsule.Left - window.Left, _capsule.Top - window.Top),
            new Vector2(_capsule.Width, _capsule.Height));
        SetWindowPos(
            _windowHandle,
            HwndTopmost,
            window.Left,
            window.Top,
            window.Width,
            window.Height,
            SwpNoActivate);
    }

    private void ApplyMaterial()
    {
        bool acrylic = AcrylicOverride ?? OverlayGeometry.UsesAcrylic(
            _uiSettings.AdvancedEffectsEnabled,
            PowerManager.EnergySaverStatus == EnergySaverStatus.On,
            GetSystemMetrics(SmRemoteSession) != 0);
        _backdrop.SetBlur(acrylic);
        byte alpha = acrylic ? AcrylicTintAlpha : FallbackTintAlpha;
        if (Surface.Background is not SolidColorBrush { Color.A: var current } || current != alpha)
            Surface.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(alpha, 0x2C, 0x2C, 0x2C));
    }

    // Clicks outside the capsule (the shadow margin) go to whatever is underneath.
    private void UpdateClickThrough()
    {
        if (_windowHandle == 0 || !GetCursorPos(out Point cursor))
            return;

        bool inside = OverlayGeometry.HitsCapsule(_capsule, cursor.X, cursor.Y);
        if (_clickThrough == !inside)
            return;

        _clickThrough = !inside;
        nint style = GetWindowLongPtr(_windowHandle, GwlExStyle);
        style = _clickThrough ? style | (nint)WsExTransparent : style & ~(nint)WsExTransparent;
        SetWindowLongPtr(_windowHandle, GwlExStyle, style);
        if (_clickThrough && _pointerOver)
        {
            _pointerOver = false;
            if (_view == OverlayView.Recording)
                UpdatePanelVisibility();
        }
    }

    private void AttachShadow()
    {
        Visual hostVisual = ElementCompositionPreview.GetElementVisual(ShadowHost);
        Compositor compositor = hostVisual.Compositor;
        var capsule = compositor.CreateRoundedRectangleGeometry();
        capsule.Size = new Vector2((float)OverlayWidthDip, (float)OverlayHeightDip);
        capsule.CornerRadius = new Vector2((float)OverlayHeightDip / 2);
        var shape = compositor.CreateShapeVisual();
        shape.Size = capsule.Size;
        var fill = compositor.CreateSpriteShape(capsule);
        fill.FillBrush = compositor.CreateColorBrush(Microsoft.UI.Colors.Black);
        shape.Shapes.Add(fill);
        var maskSurface = compositor.CreateVisualSurface();
        maskSurface.SourceVisual = shape;
        maskSurface.SourceSize = capsule.Size;

        var shadow = compositor.CreateDropShadow();
        shadow.BlurRadius = 10;
        shadow.Offset = new Vector3(0, 3, 0);
        shadow.Color = Microsoft.UI.Colors.Black;
        shadow.Opacity = 0.45f;
        shadow.Mask = compositor.CreateSurfaceBrush(maskSurface);
        var caster = compositor.CreateSpriteVisual();
        caster.Size = capsule.Size;
        caster.Shadow = shadow;
        ElementCompositionPreview.SetElementChildVisual(ShadowHost, caster);
    }

    private nint WindowProcedure(nint handle, uint message, nint wParam, nint lParam)
    {
        if (message == WmMouseActivate)
            return MaNoActivate;

        nint result = CallWindowProc(_originalWindowProc, handle, message, wParam, lParam);
        // WinUI rescales the content; resize the window and the backdrop mask to match.
        if (message == WmDpiChanged)
            DispatcherQueue.TryEnqueue(() => PlaceOnTargetMonitor(_targetHandle));
        return result;
    }

    private enum OverlayView
    {
        Recording,
        Cancel,
        Status
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProc(nint handle, uint message, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Margins
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
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

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmSetWindowAttribute(
        nint handle,
        uint attribute,
        ref uint value,
        uint valueSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(nint handle, ref Margins margins);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint handle, int index, nint value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint MonitorFromWindow(nint handle, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(
        nint handle,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    private static extern nint CallWindowProc(
        nint previousProcedure,
        nint handle,
        uint message,
        nint wParam,
        nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(nint handle, uint colorKey, byte alpha, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);
}
