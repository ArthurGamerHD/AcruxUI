using System;
using System.Threading;
using Sandbox.ModAPI;

namespace Acrux.Render3D
{
    /// <summary>
    /// Runs paint work on every hardware thread, not only the game's fixed worker pool (8 threads, shared with
    /// the simulation). The calling thread and helper threads (the game's background workers: one dedicated,
    /// pooled thread per task) take indices from an atomic counter until none are left, so a thread that
    /// finishes early takes more instead of idling, and nothing ever locks.
    /// </summary>
    internal static class PaintWorkers
    {
        // The caller included.
        public static int ThreadCount
        {
            get
            {
                int configured = Render3DSettings.Current == null ? 0 : Render3DSettings.Current.PaintThreads;
                if (configured > 0)
                    return configured;
                // All hardware threads but two: the game's own main and render threads keep a core each.
                return Math.Max(1, Environment.ProcessorCount - 2);
            }
        }

        public static void For(int from, int to, Action<int> body)
        {
            int count = to - from;
            if (count <= 0)
                return;
            int helpers = Math.Min(ThreadCount - 1, count - 1);
            var parallel = MyAPIGateway.Parallel;
            if (helpers <= 0 || parallel == null)
            {
                for (int i = from; i < to; i++)
                    body(i);
                return;
            }

            var job = new Job(from, count, body);
            for (int h = 0; h < helpers; h++)
                parallel.StartBackground(job.Run);
            job.Run();
            // Every index is taken; wait for the ones still running on helpers (at most one each).
            while (job.Done < count)
            {
            }
        }

        sealed class Job
        {
            readonly int _from;
            readonly int _count;
            readonly Action<int> _body;
            int _next;
            volatile int _done;
            public readonly Action Run;

            public Job(int from, int count, Action<int> body)
            {
                _from = from;
                _count = count;
                _body = body;
                Run = Work;
            }

            public int Done
            {
                get { return _done; }
            }

            void Work()
            {
                while (true)
                {
                    int index = Interlocked.Increment(ref _next) - 1;
                    // A helper that starts after the work ran out leaves at once.
                    if (index >= _count)
                        return;
                    try
                    {
                        _body(_from + index);
                    }
                    finally
                    {
                        Interlocked.Increment(ref _done);
                    }
                }
            }
        }
    }
}
