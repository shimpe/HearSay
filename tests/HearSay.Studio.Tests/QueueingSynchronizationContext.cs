using System.Collections.Concurrent;

namespace HearSay.Studio.Tests;

/// <summary>
/// A synchronization context that queues what is posted to it until the test pumps it, on the test's thread: the
/// continuations of the view model, as on the interface thread, run only when the test lets them, after what it did
/// meanwhile.
/// </summary>
internal sealed class QueueingSynchronizationContext : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];

    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

    public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException("The test context only queues.");

    public override SynchronizationContext CreateCopy() => this;

    /// <summary>
    /// Runs <paramref name="start"/> with this context installed on the current thread, then <paramref name="meanwhile"/>,
    /// then the queued continuations until the task <paramref name="start"/> returned completes; the context the thread
    /// had is put back.
    /// </summary>
    /// <param name="start">Starts the asynchronous work; its continuations are queued here.</param>
    /// <param name="meanwhile">What happens before any continuation runs.</param>
    public static void Run(Func<Task> start, Action meanwhile)
    {
        var context = new QueueingSynchronizationContext();
        SynchronizationContext? previous = Current;
        SetSynchronizationContext(context);
        try
        {
            Task task = start();
            meanwhile();
            DateTime deadline = DateTime.UtcNow.AddMinutes(1);
            while (!task.IsCompleted)
            {
                if (context._queue.TryTake(out (SendOrPostCallback Callback, object? State) item, TimeSpan.FromMilliseconds(50)))
                {
                    item.Callback(item.State);
                }
                else if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("The work did not complete within a minute.");
                }
            }

            task.GetAwaiter().GetResult();
        }
        finally
        {
            SetSynchronizationContext(previous);
        }
    }
}
