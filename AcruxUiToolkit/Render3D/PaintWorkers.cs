using System;
using System.Threading;
using Sandbox.ModAPI;

namespace Acrux.Render3D
{
    /// <summary>
    /// Runs paint work on the game's background threads (one dedicated, pooled thread per task), never on its
    /// fixed worker pool, which the simulation's parallel steps (physics, entity updates) share. The calling
    /// thread and its helpers take indices from an atomic counter until none are left, so a thread that finishes
    /// early takes more instead of idling.
    /// </summary>
    /// <remarks>
    /// Helpers come from one process-wide budget (<see cref="ThreadCount"/> minus the caller): a nested call, or
    /// a second frame painting at the same time, only gets the helpers still free and otherwise runs inline, so
    /// the game never creates more background threads than the budget. The caller waits for helpers still
    /// running its last indices on a monitor, not by spinning, so it never burns a core it does not use.
    /// </remarks>
    internal static class PaintWorkers
    {
        // Helpers running across every For in the process.
        static int _activeHelpers;

        // The caller included.
        public static int ThreadCount
        {
            get
            {
                int configured = Render3DSettings.Current == null ? 0 : Render3DSettings.Current.PaintThreads;
                if (configured > 0)
                    return configured;
                // Half the hardware threads: the game's main, render and worker threads keep the rest.
                return Math.Max(1, Environment.ProcessorCount / 2);
            }
        }

        public static void For(int from, int to, Action<int> body)
        {
            int count = to - from;
            if (count <= 0)
                return;
            var parallel = MyAPIGateway.Parallel;
            int helpers = parallel == null ? 0 : Reserve(Math.Min(ThreadCount - 1, count - 1));
            if (helpers <= 0)
            {
                for (int i = from; i < to; i++)
                    body(i);
                return;
            }

            var job = new Job(from, count, body, helpers);
            int started = 0;
            try
            {
                for (; started < helpers; started++)
                    parallel.StartBackground(job.RunHelper);
            }
            finally
            {
                // Budget reserved for helpers that never started goes back at once.
                if (started < helpers)
                    job.Leave(helpers - started);
            }
            job.Work();
            job.WaitForHelpers();
            if (job.Failure != null)
                throw new Exception("Paint worker failed.", job.Failure);
        }

        static int Reserve(int wanted)
        {
            if (wanted <= 0)
                return 0;
            int budget = ThreadCount - 1;
            while (true)
            {
                // A stale read only makes the exchange below fail and retry.
                int active = _activeHelpers;
                int granted = Math.Min(wanted, budget - active);
                if (granted <= 0)
                    return 0;
                if (Interlocked.CompareExchange(ref _activeHelpers, active + granted, active) == active)
                    return granted;
            }
        }

        sealed class Job
        {
            readonly int _from;
            readonly int _count;
            readonly Action<int> _body;
            readonly object _gate = new object();
            int _next;
            // Helpers that have not left yet (started or not); guarded by _gate.
            int _helpers;
            public readonly Action RunHelper;
            public Exception Failure;

            public Job(int from, int count, Action<int> body, int helpers)
            {
                _from = from;
                _count = count;
                _body = body;
                _helpers = helpers;
                RunHelper = Helper;
            }

            public void Work()
            {
                while (true)
                {
                    int index = Interlocked.Increment(ref _next) - 1;
                    // A thread that starts after the work ran out leaves at once.
                    if (index >= _count)
                        return;
                    try
                    {
                        _body(_from + index);
                    }
                    catch (Exception exception)
                    {
                        // Caught here: an exception escaping a background task may be fatal to the game.
                        Failure = exception;
                        // The remaining indices are skipped.
                        Interlocked.Exchange(ref _next, _count);
                    }
                }
            }

            void Helper()
            {
                try
                {
                    Work();
                }
                finally
                {
                    Leave(1);
                }
            }

            public void Leave(int count)
            {
                Interlocked.Add(ref _activeHelpers, -count);
                lock (_gate)
                {
                    _helpers -= count;
                    if (_helpers == 0)
                        Monitor.PulseAll(_gate);
                }
            }

            // The monitor's release/acquire also publishes the helpers' pixel and Failure writes to the caller.
            public void WaitForHelpers()
            {
                lock (_gate)
                {
                    while (_helpers > 0)
                        Monitor.Wait(_gate);
                }
            }
        }
    }
}
