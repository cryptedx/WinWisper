namespace WinWispr.Views;

/// <summary>Physical-pixel rectangle; right and bottom are exclusive.</summary>
internal readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    internal int Width => Right - Left;
    internal int Height => Bottom - Top;
}

/// <summary>Where the overlay window and its capsule go on a monitor, in physical pixels.</summary>
internal readonly record struct OverlayPlacement(PixelRect Window, PixelRect Capsule);

internal static class OverlayGeometry
{
    internal const double CapsuleWidthDip = 224;
    internal const double CapsuleHeightDip = 48;
    // Transparent room around the capsule for its shadow.
    internal const double ShadowMarginDip = 12;
    // Gap between the capsule and the bottom of the work area.
    internal const double BottomGapDip = 24;

    /// <summary>Centres the capsule above the bottom of the work area at the monitor's DPI.</summary>
    internal static OverlayPlacement Place(PixelRect workArea, uint dpi)
    {
        if (dpi == 0)
            dpi = 96;
        int capsuleWidth = DipToPixels(CapsuleWidthDip, dpi);
        int capsuleHeight = DipToPixels(CapsuleHeightDip, dpi);
        int shadowMargin = DipToPixels(ShadowMarginDip, dpi);
        int bottomGap = DipToPixels(BottomGapDip, dpi);

        int capsuleLeft = workArea.Left + Math.Max(0, (workArea.Width - capsuleWidth) / 2);
        int capsuleTop = workArea.Bottom - bottomGap - capsuleHeight;
        var capsule = new PixelRect(capsuleLeft, capsuleTop, capsuleLeft + capsuleWidth, capsuleTop + capsuleHeight);
        var window = new PixelRect(
            capsule.Left - shadowMargin,
            capsule.Top - shadowMargin,
            capsule.Right + shadowMargin,
            capsule.Bottom + shadowMargin);
        return new OverlayPlacement(window, capsule);
    }

    /// <summary>True when the pixel at (x, y) lies on the capsule: a rectangle with semicircular ends.</summary>
    internal static bool HitsCapsule(PixelRect capsule, int x, int y)
    {
        if (capsule.Width <= 0 || capsule.Height <= 0)
            return false;

        // Test the pixel centre against the exact outline.
        double px = x + 0.5;
        double py = y + 0.5;
        double radius = Math.Min(capsule.Width, capsule.Height) / 2.0;
        double centreY = capsule.Top + capsule.Height / 2.0;
        double nearestX = Math.Clamp(px, capsule.Left + radius, capsule.Right - radius);
        double dx = px - nearestX;
        double dy = py - centreY;
        return dx * dx + dy * dy <= radius * radius;
    }

    /// <summary>The blurred backdrop needs Windows transparency effects, no energy saver and a local session.</summary>
    internal static bool UsesAcrylic(bool transparencyEffectsEnabled, bool energySaverOn, bool remoteSession) =>
        transparencyEffectsEnabled && !energySaverOn && !remoteSession;

    internal static int DipToPixels(double dips, uint dpi) => (int)Math.Round(dips * dpi / 96d);
}
