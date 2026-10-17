using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinRT;
using SystemCompositor = Windows.UI.Composition.Compositor;
using SystemCompositionBrush = Windows.UI.Composition.CompositionBrush;
using SystemEffectSourceParameter = Windows.UI.Composition.CompositionEffectSourceParameter;
using SystemRoundedRectangleGeometry = Windows.UI.Composition.CompositionRoundedRectangleGeometry;
using SystemShapeVisual = Windows.UI.Composition.ShapeVisual;
using SystemVisualSurface = Windows.UI.Composition.CompositionVisualSurface;

namespace WinWispr.Views;

/// <summary>
/// Blurred desktop backdrop clipped to an antialiased capsule. Outside the capsule the
/// window stays fully transparent, so the XAML content defines the visible shape.
/// Without blur the whole backdrop is transparent and the XAML tint carries the capsule.
/// </summary>
internal sealed partial class CapsuleBackdrop : SystemBackdrop
{
    private static Windows.System.DispatcherQueueController? s_systemDispatcherQueueController;

    private SystemCompositor? _compositor;
    private SystemShapeVisual? _maskVisual;
    private SystemRoundedRectangleGeometry? _maskGeometry;
    private SystemVisualSurface? _maskSurface;
    private ICompositionSupportsSystemBackdrop? _target;
    private SystemCompositionBrush? _blurBrush;
    private SystemCompositionBrush? _clearBrush;
    private bool _blur = true;
    private Vector2 _windowSize = new(1, 1);
    private Vector2 _capsuleOffset;
    private Vector2 _capsuleSize = new(1, 1);

    /// <summary>Sets the window client size and the capsule bounds, both in physical pixels.</summary>
    internal void SetGeometry(Vector2 windowSize, Vector2 capsuleOffset, Vector2 capsuleSize)
    {
        _windowSize = Vector2.Max(windowSize, Vector2.One);
        _capsuleOffset = capsuleOffset;
        _capsuleSize = Vector2.Max(capsuleSize, Vector2.One);
        ApplyGeometry();
    }

    /// <summary>Switches between the blurred capsule and a fully transparent backdrop.</summary>
    internal void SetBlur(bool blur)
    {
        _blur = blur;
        ApplyBrush();
    }

    protected override void OnTargetConnected(ICompositionSupportsSystemBackdrop connectedTarget, XamlRoot xamlRoot)
    {
        base.OnTargetConnected(connectedTarget, xamlRoot);
        EnsureSystemDispatcherQueue();
        _target = connectedTarget;
        _compositor = new SystemCompositor();

        _maskGeometry = _compositor.CreateRoundedRectangleGeometry();
        var maskShape = _compositor.CreateSpriteShape(_maskGeometry);
        maskShape.FillBrush = _compositor.CreateColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));
        _maskVisual = _compositor.CreateShapeVisual();
        _maskVisual.Shapes.Add(maskShape);
        _maskSurface = _compositor.CreateVisualSurface();
        _maskSurface.SourceVisual = _maskVisual;
        var maskBrush = _compositor.CreateSurfaceBrush(_maskSurface);
        maskBrush.Stretch = Windows.UI.Composition.CompositionStretch.Fill;

        // Backdrop brushes are only valid as effect inputs, so clip with an alpha-mask effect.
        var effect = new AlphaMaskEffect(
            new SystemEffectSourceParameter("backdrop"),
            new SystemEffectSourceParameter("mask"));
        var brush = _compositor.CreateEffectFactory(effect).CreateBrush();
        brush.SetSourceParameter("backdrop", _compositor.CreateHostBackdropBrush());
        brush.SetSourceParameter("mask", maskBrush);
        _blurBrush = brush;
        _clearBrush = _compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        ApplyGeometry();
        ApplyBrush();
    }

    protected override void OnTargetDisconnected(ICompositionSupportsSystemBackdrop disconnectedTarget)
    {
        base.OnTargetDisconnected(disconnectedTarget);
        disconnectedTarget.SystemBackdrop = null;
        _target = null;
        _blurBrush?.Dispose();
        _clearBrush?.Dispose();
        _maskSurface?.Dispose();
        _maskVisual?.Dispose();
        _compositor?.Dispose();
        _blurBrush = null;
        _clearBrush = null;
        _maskSurface = null;
        _maskVisual = null;
        _maskGeometry = null;
        _compositor = null;
    }

    private void ApplyBrush()
    {
        if (_target is not null)
            _target.SystemBackdrop = _blur ? _blurBrush : _clearBrush;
    }

    private void ApplyGeometry()
    {
        if (_maskVisual is null || _maskSurface is null || _maskGeometry is null)
            return;

        _maskVisual.Size = _windowSize;
        _maskSurface.SourceSize = _windowSize;
        // Keep the blur one pixel inside the XAML edge so no blurred fringe shows outside the stroke.
        Vector2 inset = new(1, 1);
        _maskGeometry.Offset = _capsuleOffset + inset;
        _maskGeometry.Size = Vector2.Max(_capsuleSize - inset * 2, Vector2.One);
        float radius = _maskGeometry.Size.Y / 2;
        _maskGeometry.CornerRadius = new Vector2(radius, radius);
    }

    private static void EnsureSystemDispatcherQueue()
    {
        if (Windows.System.DispatcherQueue.GetForCurrentThread() is not null || s_systemDispatcherQueueController is not null)
            return;

        var options = new DispatcherQueueOptions
        {
            Size = Marshal.SizeOf<DispatcherQueueOptions>(),
            ThreadType = 2, // DQTYPE_THREAD_CURRENT
            ApartmentType = 2 // DQTAT_COM_STA
        };
        Marshal.ThrowExceptionForHR(CreateDispatcherQueueController(options, out nint controller));
        s_systemDispatcherQueueController = MarshalInspectable<Windows.System.DispatcherQueueController>.FromAbi(controller);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DispatcherQueueOptions
    {
        public int Size;
        public int ThreadType;
        public int ApartmentType;
    }

    [DllImport("CoreMessaging.dll")]
    private static extern int CreateDispatcherQueueController(DispatcherQueueOptions options, out nint controller);
}
