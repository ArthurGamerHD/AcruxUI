using System;
using Acrux.Rendering;
using Sandbox.ModAPI;

namespace Acrux.Render3D
{
    /// <summary>
    /// A renderer's frames in flight. Up to two are painted at once: the next one is captured on the caller's
    /// thread and starts while the previous still paints, so a frame may take up to two ticks without dropping
    /// the rate. Each frame in flight owns a slot (its buffers and pooled scene objects), so the caller never
    /// touches what a painting frame reads. Completions run in request order on the caller's thread.
    /// </summary>
    internal sealed class SceneFramePipeline<TSlot> where TSlot : class
    {
        public const int FRAMES_IN_FLIGHT = 2;

        readonly Func<TSlot> _createSlot;
        readonly TSlot[] _slots;
        // Oldest first, until their completions have run on the caller's thread.
        readonly Job[] _jobs = new Job[FRAMES_IN_FLIGHT];
        int _jobCount;
        bool _disposed;

        sealed class Job
        {
            public TSlot Slot;
            public Action<Exception> Completed;
            public Exception Failure;
            // Set last by the paint thread (volatile: its writes above are visible once this reads true).
            public volatile bool Finished;
        }

        // Activator is not whitelisted, so slots come from a factory instead of a new() constraint.
        public SceneFramePipeline(Func<TSlot> createSlot)
        {
            _createSlot = createSlot;
            _slots = new TSlot[FRAMES_IN_FLIGHT];
            for (int i = 0; i < _slots.Length; i++)
                _slots[i] = createSlot();
        }

        // Caller's thread, before the frame's own completion.
        public Action<Exception> Failed { get; set; }

        public bool IsFull
        {
            get { return _jobCount >= FRAMES_IN_FLIGHT; }
        }

        public void Render(PixelCanvas canvas, Func<TSlot, SceneFrame> prepare)
        {
            prepare(FreeSlot()).Paint(canvas, (first, end, paint) =>
            {
                for (int row = first; row < end; row++)
                    paint(row);
            });
        }

        public bool Request(PixelCanvas canvas, Func<TSlot, SceneFrame> prepare, Action<Exception> completed)
        {
            var parallel = MyAPIGateway.Parallel;
            if (_disposed || IsFull || parallel == null)
                return false;

            // Every entity and camera value is captured here, on the caller (simulation/UI) thread.
            TSlot slot = FreeSlot();
            SceneFrame frame = prepare(slot);
            var job = new Job { Slot = slot, Completed = completed };
            _jobs[_jobCount++] = job;
            try
            {
                // A background thread, not the game's worker pool: the simulation's parallel steps share that
                // pool, and a frame painting there for a few milliseconds held one of its workers each time.
                parallel.StartBackground(() =>
                {
                    try
                    {
                        frame.Paint(canvas, PaintWorkers.For);
                    }
                    catch (Exception exception)
                    {
                        job.Failure = exception;
                    }
                    job.Finished = true;
                }, () => CompleteFinished());
            }
            catch
            {
                _jobCount--;
                _jobs[_jobCount] = null;
                throw;
            }
            return true;
        }

        TSlot FreeSlot()
        {
            for (int s = 0; s < _slots.Length; s++)
            {
                bool used = false;
                for (int j = 0; j < _jobCount; j++)
                    used |= ReferenceEquals(_jobs[j].Slot, _slots[s]);
                if (!used)
                    return _slots[s];
            }
            // Only a synchronous Render while every slot is painting gets here: it must not share their buffers.
            return _createSlot();
        }

        // Caller's thread, outside any draw (a completion may ask the host to render). The game runs the paint
        // tasks' callbacks a tick late; a host calls this each tick to deliver finished frames at once.
        public bool CompleteFinished()
        {
            bool delivered = false;
            // A frame finished out of order waits for the older one, so frames are shown in order.
            while (_jobCount > 0 && _jobs[0].Finished)
            {
                Job job = _jobs[0];
                for (int j = 1; j < _jobCount; j++)
                    _jobs[j - 1] = _jobs[j];
                _jobs[--_jobCount] = null;
                if (!_disposed && job.Failure != null && Failed != null)
                    Failed(job.Failure);
                job.Completed(job.Failure);
                delivered = true;
            }
            return delivered;
        }

        public void Dispose()
        {
            _disposed = true;
        }
    }
}
