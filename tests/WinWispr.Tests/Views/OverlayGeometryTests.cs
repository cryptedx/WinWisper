using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Views;

namespace WinWispr.Tests.Views;

[TestClass]
public sealed class OverlayGeometryTests
{
    [TestMethod]
    [DataRow(96u, 224, 48, 12, 24)]
    [DataRow(144u, 336, 72, 18, 36)]
    [DataRow(192u, 448, 96, 24, 48)]
    public void Placement_scales_the_capsule_and_its_shadow_margin_with_the_monitor_dpi(
        uint dpi, int capsuleWidth, int capsuleHeight, int shadowMargin, int bottomGap)
    {
        var workArea = new PixelRect(0, 0, 1920, 1040);

        OverlayPlacement placement = OverlayGeometry.Place(workArea, dpi);

        Assert.AreEqual(capsuleWidth, placement.Capsule.Width);
        Assert.AreEqual(capsuleHeight, placement.Capsule.Height);
        Assert.AreEqual((1920 - capsuleWidth) / 2, placement.Capsule.Left);
        Assert.AreEqual(1040 - bottomGap, placement.Capsule.Bottom);
        Assert.AreEqual(
            new PixelRect(
                placement.Capsule.Left - shadowMargin,
                placement.Capsule.Top - shadowMargin,
                placement.Capsule.Right + shadowMargin,
                placement.Capsule.Bottom + shadowMargin),
            placement.Window);
    }

    [TestMethod]
    public void Placement_follows_a_monitor_with_a_negative_origin()
    {
        var workArea = new PixelRect(-2560, -200, -640, 880);

        OverlayPlacement placement = OverlayGeometry.Place(workArea, 96);

        Assert.AreEqual(-2560 + (1920 - 224) / 2, placement.Capsule.Left);
        Assert.AreEqual(880 - 24, placement.Capsule.Bottom);
    }

    [TestMethod]
    public void Placement_treats_an_unknown_dpi_as_96()
    {
        var workArea = new PixelRect(0, 0, 1920, 1040);

        Assert.AreEqual(OverlayGeometry.Place(workArea, 96), OverlayGeometry.Place(workArea, 0));
    }

    [TestMethod]
    [DataRow(150, 124, true)]  // centre
    [DataRow(101, 124, true)]  // inside the rectangular middle, near the left end cap
    [DataRow(78, 124, true)]   // left end cap, on the centre line
    [DataRow(100, 100, true)]  // top edge where the straight part starts
    [DataRow(80, 103, false)]  // corner of the bounding box, outside the end cap
    [DataRow(321, 145, false)] // corner of the bounding box at the right end
    [DataRow(77, 124, false)]  // left of the capsule
    [DataRow(150, 148, false)] // below the capsule (bottom is exclusive)
    public void Hit_test_follows_the_semicircular_ends(int x, int y, bool expected)
    {
        var capsule = new PixelRect(78, 100, 322, 148);

        Assert.AreEqual(expected, OverlayGeometry.HitsCapsule(capsule, x, y));
    }

    [TestMethod]
    public void An_empty_capsule_is_never_hit()
    {
        Assert.IsFalse(OverlayGeometry.HitsCapsule(default, 0, 0));
    }

    [TestMethod]
    [DataRow(true, false, false, true)]
    [DataRow(false, false, false, false)]
    [DataRow(true, true, false, false)]
    [DataRow(true, false, true, false)]
    public void Acrylic_needs_transparency_effects_no_energy_saver_and_a_local_session(
        bool transparencyEffects, bool energySaver, bool remoteSession, bool expected)
    {
        Assert.AreEqual(expected, OverlayGeometry.UsesAcrylic(transparencyEffects, energySaver, remoteSession));
    }
}
