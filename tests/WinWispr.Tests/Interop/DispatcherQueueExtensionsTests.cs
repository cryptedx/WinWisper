using Microsoft.UI.Dispatching;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using WinWispr.Interop;

namespace WinWispr.Tests.Interop;

[TestClass]
public sealed class DispatcherQueueExtensionsTests
{
    [TestMethod]
    public async Task Action_runs_on_the_dispatcher_thread()
    {
        DispatcherQueueController controller = DispatcherQueueController.CreateOnDedicatedThread();
        try
        {
            bool onDispatcher = false;
            await controller.DispatcherQueue.InvokeAsync(() => onDispatcher = controller.DispatcherQueue.HasThreadAccess);
            Assert.IsTrue(onDispatcher);
        }
        finally { await controller.ShutdownQueueAsync(); }
    }

    [TestMethod]
    public async Task Failure_on_the_dispatcher_reaches_the_caller()
    {
        DispatcherQueueController controller = DispatcherQueueController.CreateOnDedicatedThread();
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => controller.DispatcherQueue.InvokeAsync(() => throw new InvalidOperationException("clipboard")));
        }
        finally { await controller.ShutdownQueueAsync(); }
    }

    [TestMethod]
    public async Task Cancelled_work_does_not_run()
    {
        DispatcherQueueController controller = DispatcherQueueController.CreateOnDedicatedThread();
        try
        {
            bool ran = false;
            await Assert.ThrowsAsync<TaskCanceledException>(
                () => controller.DispatcherQueue.InvokeAsync(() => ran = true, new CancellationToken(canceled: true)));
            Assert.IsFalse(ran);
        }
        finally { await controller.ShutdownQueueAsync(); }
    }

    [TestMethod]
    public async Task Shut_down_dispatcher_fails_instead_of_hanging()
    {
        DispatcherQueueController controller = DispatcherQueueController.CreateOnDedicatedThread();
        DispatcherQueue dispatcher = controller.DispatcherQueue;
        await controller.ShutdownQueueAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.InvokeAsync(() => { }));
    }
}
