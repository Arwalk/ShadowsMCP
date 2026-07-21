using System;
using System.Collections.Concurrent;
using System.Threading;
using ShadowsMcp.Core.Mcp;

namespace ShadowsMcp.Core.Util
{
    /// <summary>
    /// Marshals tool work from HTTP worker threads onto the thread that calls Pump()
    /// (in the game: Unity's main thread, via a MonoBehaviour's Update).
    /// Game state must only ever be touched on the main thread.
    /// </summary>
    public sealed class MainThreadDispatcher
    {
        private sealed class Job
        {
            public Func<ToolResult> Work;
            public readonly ManualResetEventSlim Done = new ManualResetEventSlim(false);
            public ToolResult Result;
            public volatile bool Cancelled;
        }

        private readonly ConcurrentQueue<Job> _queue = new ConcurrentQueue<Job>();
        private readonly int _mainThreadId;

        /// <summary>Construct on the main thread (e.g. inside a ModKernel hook).</summary>
        public MainThreadDispatcher()
        {
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        public bool OnMainThread
        {
            get { return Thread.CurrentThread.ManagedThreadId == _mainThreadId; }
        }

        /// <summary>
        /// Run work on the main thread and wait for the result. On timeout, returns an error
        /// result and flags the job so a late pump discards it instead of running stale work.
        /// </summary>
        public ToolResult Run(Func<ToolResult> work, int timeoutMs)
        {
            if (OnMainThread)
            {
                try { return work(); }
                catch (Exception ex)
                {
                    Log.Error("main-thread tool call threw", ex);
                    return ToolResult.Error("tool failed: " + Log.Describe(ex));
                }
            }

            var job = new Job { Work = work };
            _queue.Enqueue(job);
            if (!job.Done.Wait(timeoutMs))
            {
                job.Cancelled = true;
                return ToolResult.Error(
                    "the game did not process the request within " + (timeoutMs / 1000) +
                    "s - it may be frozen, minimized with background execution disabled, or stuck on a modal dialog");
            }
            return job.Result;
        }

        /// <summary>Called every frame on the main thread. Drains all pending jobs.</summary>
        public void Pump()
        {
            Job job;
            while (_queue.TryDequeue(out job))
            {
                if (job.Cancelled) continue;
                try
                {
                    job.Result = job.Work();
                }
                catch (Exception ex)
                {
                    Log.Error("dispatched job threw", ex);
                    job.Result = ToolResult.Error("tool failed: " + Log.Describe(ex));
                }
                job.Done.Set();
            }
        }
    }
}
