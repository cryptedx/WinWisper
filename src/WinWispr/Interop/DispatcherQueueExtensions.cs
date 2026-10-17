using Microsoft.UI.Dispatching;

namespace WinWispr.Interop;

internal static class DispatcherQueueExtensions
{
    /// <summary>
    /// Runs <paramref name="action"/> on the dispatcher thread, inline when already on it, and completes
    /// with its outcome. Fails when the dispatcher no longer accepts work.
    /// </summary>
    internal static Task InvokeAsync(
        this DispatcherQueue dispatcher,
        Action action,
        CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Run()
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                action();
                completion.TrySetResult();
            }
            catch (OperationCanceledException exception) { completion.TrySetCanceled(exception.CancellationToken); }
            catch (Exception exception) { completion.TrySetException(exception); }
        }
        if (dispatcher.HasThreadAccess)
            Run();
        else if (!dispatcher.TryEnqueue(Run))
            completion.TrySetException(new InvalidOperationException("The UI dispatcher is unavailable."));
        return completion.Task;
    }
}
