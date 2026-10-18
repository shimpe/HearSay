using System.Collections.Concurrent;

namespace HearSay.Playing;

/// <summary>
/// A few threads of above-normal priority, shared by every mixer, that help the audio thread render the voices of a
/// buffer. The calling thread renders too and only ever waits for voices a helper has already started, never for a
/// helper to be scheduled: unlike <see cref="Parallel.For(int, int, Action{int})"/> called from a thread outside the
/// thread pool, which waits for a pool thread to pick up its queued work, and so for seconds while a search keeps
/// every pool thread busy.
/// </summary>
internal static class VoiceRenderCrew
{
    private static readonly ConcurrentQueue<Job> Jobs = new();
    private static readonly SemaphoreSlim Wake = new(0);
    private static readonly Lazy<int> Helpers = new(StartHelpers, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Starts the helper threads, if they are not running yet, so that the first chord does not wait for them.</summary>
    public static void EnsureStarted() => _ = Helpers.Value;

    /// <summary>Runs <paramref name="work"/> for every index below <paramref name="count"/>, in parallel, and returns when all are done.</summary>
    /// <param name="work">Work of one index; must not throw.</param>
    /// <param name="count">Number of indices.</param>
    public static void Run(Action<int> work, int count)
    {
        int helpers = count > 1 ? Math.Min(count - 1, Helpers.Value) : 0;
        if (helpers == 0)
        {
            for (int i = 0; i < count; i++)
            {
                work(i);
            }

            return;
        }

        // A new job every time: a helper that wakes up after the job is done finds nothing left to claim in it.
        var job = new Job(work, count);
        for (int i = 0; i < helpers; i++)
        {
            Jobs.Enqueue(job);
        }

        Wake.Release(helpers);
        job.Work();
        job.WaitUntilDone();
    }

    private static int StartHelpers()
    {
        int count = Math.Clamp(Environment.ProcessorCount - 1, 0, 15);
        for (int i = 0; i < count; i++)
        {
            var thread = new Thread(Help)
            {
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal,
                Name = "HearSay voice renderer",
            };
            thread.Start();
        }

        return count;
    }

    private static void Help()
    {
        while (true)
        {
            Wake.Wait();
            if (Jobs.TryDequeue(out Job? job))
            {
                job.Work();
            }
        }
    }

    private sealed class Job(Action<int> work, int count)
    {
        private int _next;
        private int _done;

        /// <summary>Claims and runs indices until none is left.</summary>
        public void Work()
        {
            int index;
            while ((index = Interlocked.Increment(ref _next) - 1) < count)
            {
                try
                {
                    work(index);
                }
                finally
                {
                    Interlocked.Increment(ref _done);
                }
            }
        }

        /// <summary>Waits for the indices other threads are running; they are already running, so the wait is short.</summary>
        public void WaitUntilDone()
        {
            var spin = default(SpinWait);
            while (Volatile.Read(ref _done) < count)
            {
                // Yield, but never Sleep(1): that can take a whole timer tick, longer than an audio buffer.
                spin.SpinOnce(sleep1Threshold: -1);
            }
        }
    }
}
