using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Core;
using WinWispr.Interop;

namespace WinWispr.Tests.Interop;

[TestClass]
public sealed class WindowActivatorTests
{
    [TestMethod]
    public async Task A_window_that_no_longer_exists_cannot_be_activated()
    {
        var world = new World { Exists = false };

        Assert.IsFalse(await world.Activator.ActivateAsync(new WindowTarget(10), CancellationToken.None));
        Assert.AreEqual(0, world.BringToFrontCalls);
    }

    [TestMethod]
    public async Task No_window_cannot_be_activated()
    {
        var world = new World();

        Assert.IsFalse(await world.Activator.ActivateAsync(default, CancellationToken.None));
    }

    [TestMethod]
    public async Task A_window_already_in_front_is_reported_without_touching_it()
    {
        var world = new World { Foreground = 10 };

        Assert.IsTrue(await world.Activator.ActivateAsync(new WindowTarget(10), CancellationToken.None));
        Assert.AreEqual(0, world.BringToFrontCalls);
    }

    [TestMethod]
    public async Task A_background_window_is_brought_to_the_front_and_reported_once_it_is_there()
    {
        var world = new World { ForegroundAfterCalls = 1 };

        Assert.IsTrue(await world.Activator.ActivateAsync(new WindowTarget(10), CancellationToken.None));
        Assert.AreEqual(0, world.Restores);
    }

    [TestMethod]
    public async Task A_minimized_window_is_restored_first()
    {
        var world = new World { Minimized = true, ForegroundAfterCalls = 1 };

        Assert.IsTrue(await world.Activator.ActivateAsync(new WindowTarget(10), CancellationToken.None));
        Assert.AreEqual(1, world.Restores);
    }

    [TestMethod]
    public async Task A_window_that_stays_in_the_background_is_reported_as_not_activated()
    {
        var world = new World { ForegroundAfterCalls = int.MaxValue };

        Assert.IsFalse(await world.Activator.ActivateAsync(new WindowTarget(10), CancellationToken.None));
    }

    [TestMethod]
    public async Task A_window_that_closes_while_waiting_is_reported_as_not_activated()
    {
        var world = new World { ForegroundAfterCalls = int.MaxValue, CloseOnDelay = true };

        Assert.IsFalse(await world.Activator.ActivateAsync(new WindowTarget(10), CancellationToken.None));
    }

    private sealed class World
    {
        internal bool Exists { get; set; } = true;
        internal bool Minimized { get; set; }
        internal nint Foreground { get; set; } = 1;
        internal int ForegroundAfterCalls { get; set; }
        internal bool CloseOnDelay { get; set; }
        internal int BringToFrontCalls { get; private set; }
        internal int Restores { get; private set; }

        internal WindowActivator Activator => new(
            _ => Exists,
            _ => Minimized,
            _ => Restores++,
            handle =>
            {
                BringToFrontCalls++;
                if (BringToFrontCalls >= ForegroundAfterCalls)
                    Foreground = handle;
            },
            () => Foreground,
            (_, _) =>
            {
                if (CloseOnDelay)
                    Exists = false;
                return Task.CompletedTask;
            });
    }
}
