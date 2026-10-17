using WinWispr.Core;

namespace WinWispr.Tests.Core;

[TestClass]
public sealed class WindowPlacementTests
{
    [TestMethod]
    public void Centered_uses_the_work_area_instead_of_the_full_screen()
    {
        WindowPlacement placement = WindowPlacement.Centered(
            areaX: 0,
            areaY: 0,
            areaWidth: 1920,
            areaHeight: 1040,
            width: 640,
            height: 760);

        Assert.AreEqual(new WindowPlacement(640, 140, 640, 760), placement);
    }

    [TestMethod]
    public void Saved_placement_is_kept_inside_the_work_area()
    {
        WindowPlacement placement = new WindowPlacement(1800, 900, 640, 760).ConstrainTo(
            areaX: 0,
            areaY: 0,
            areaWidth: 1920,
            areaHeight: 1040);

        Assert.AreEqual(new WindowPlacement(1280, 280, 640, 760), placement);
    }

    [TestMethod]
    public void Saved_width_below_the_settings_minimum_is_raised()
    {
        WindowPlacement placement = new WindowPlacement(100, 100, 480, 760).ConstrainTo(
            areaX: 0,
            areaY: 0,
            areaWidth: 1920,
            areaHeight: 1040,
            minimumWidth: 560,
            minimumHeight: 1);

        Assert.AreEqual(new WindowPlacement(100, 100, 560, 760), placement);
    }

    [TestMethod]
    public void Saved_settings_placement_is_clamped_and_raised_to_the_minimum()
    {
        WindowPlacement placement = new WindowPlacement(1800, 900, 640, 760).ConstrainTo(
            areaX: 0,
            areaY: 0,
            areaWidth: 1920,
            areaHeight: 1040,
            minimumWidth: 560,
            minimumHeight: 1);

        Assert.AreEqual(new WindowPlacement(1280, 280, 640, 760), placement);
    }

    [TestMethod]
    public void Settings_minimum_is_reduced_when_the_work_area_is_smaller()
    {
        WindowPlacement placement = new WindowPlacement(0, 0, 480, 760).ConstrainTo(
            areaX: 0,
            areaY: 0,
            areaWidth: 480,
            areaHeight: 760,
            minimumWidth: 560,
            minimumHeight: 1);

        Assert.AreEqual(new WindowPlacement(0, 0, 480, 760), placement);
    }

    [TestMethod]
    public void Valid_default_settings_placement_is_unchanged()
    {
        WindowPlacement placement = new WindowPlacement(640, 140, 640, 760).ConstrainTo(
            areaX: 0,
            areaY: 0,
            areaWidth: 1920,
            areaHeight: 1040,
            minimumWidth: 560,
            minimumHeight: 1);

        Assert.AreEqual(new WindowPlacement(640, 140, 640, 760), placement);
    }

    [TestMethod]
    public void Centered_settings_default_is_scaled_at_150_percent_dpi()
    {
        WindowPlacement placement = WindowPlacement.Centered(
            areaX: 0,
            areaY: 0,
            areaWidth: 2560,
            areaHeight: 1440,
            width: 960,
            height: 1140);

        Assert.AreEqual(new WindowPlacement(800, 150, 960, 1140), placement);
    }

    [TestMethod]
    public void Restored_settings_minimum_is_scaled_at_150_percent_dpi()
    {
        WindowPlacement placement = new WindowPlacement(100, 100, 480, 760).ConstrainTo(
            areaX: 0,
            areaY: 0,
            areaWidth: 2560,
            areaHeight: 1440,
            minimumWidth: 840,
            minimumHeight: 1);

        Assert.AreEqual(new WindowPlacement(100, 100, 840, 760), placement);
    }
}
