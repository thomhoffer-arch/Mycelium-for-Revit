using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Loam.Revit.Connector.ModelLog
{
    /// <summary>
    /// Runs long-running work (a full snapshot, a reconcile) as a sequence of small slices
    /// during Revit's own <c>Idling</c> event, never inside a document-event callback — the
    /// handoff's "never block the user" rule: "all capture work runs in small idle-time
    /// slices". Deliberately Revit-free (it knows nothing about <c>Document</c>/<c>Element</c>)
    /// so it's covered by the same plain unit tests as the rest of src/ModelLog/; the add-in's
    /// Revit-touching code (src/ModelLogCapture/) supplies the work as plain enumerators.
    ///
    /// A "job" is any <see cref="IEnumerator{Boolean}"/> whose <c>MoveNext()</c> performs ONE
    /// unit of work (e.g. one element) and returns true while there's more to do, false when
    /// the job has finished. Jobs run FIFO; only one job's slice runs per idle tick, and a job
    /// that isn't finished when the slice budget runs out stays at the front of the queue and
    /// resumes on the next tick — so a snapshot or reconcile of a large model spreads over many
    /// idle ticks without ever occupying one for longer than the budget.
    /// </summary>
    public sealed class IdleSliceRunner
    {
        public static readonly TimeSpan SliceBudget = TimeSpan.FromMilliseconds(50);

        private readonly Queue<Job> _jobs = new();
        private readonly Action<string, double, int> _onJobFinished;
        private readonly Action<string, double>? _onSliceOverBudget;

        /// <param name="onJobFinished">Called once, when a job's last slice returns false —
        /// name, total milliseconds spent across all its slices, total steps run. The handoff's
        /// own verification checklist: "measure it: log the total time and slice count per
        /// snapshot and reconcile."</param>
        /// <param name="onSliceOverBudget">Called after any single slice that ran longer than
        /// <see cref="SliceBudget"/> before the work enumerator itself yielded control back —
        /// "a slice that runs longer than the budget is a bug" (the same checklist). Not called
        /// for a slice that simply used its full budget and still has more work queued; that's
        /// normal — this fires only when ONE MoveNext() call itself overran.</param>
        public IdleSliceRunner(
            Action<string, double, int> onJobFinished,
            Action<string, double>? onSliceOverBudget = null)
        {
            _onJobFinished = onJobFinished;
            _onSliceOverBudget = onSliceOverBudget;
        }

        public bool HasWork => _jobs.Count > 0;
        public int QueueDepth => _jobs.Count;

        public void Enqueue(string name, IEnumerator<bool> work) => _jobs.Enqueue(new Job(name, work));

        /// <summary>Call from the host's Idling handler. Runs slices from the front of the
        /// queue until the budget is spent or the queue empties.</summary>
        public void RunSlice()
        {
            if (_jobs.Count == 0) return;
            var sw = Stopwatch.StartNew();
            var job = _jobs.Peek();

            var steps = 0;
            var more = true;
            while (more && sw.Elapsed < SliceBudget)
            {
                var stepStart = sw.Elapsed;
                more = job.Work.MoveNext();
                steps++;

                var stepElapsed = (sw.Elapsed - stepStart).TotalMilliseconds;
                if (stepElapsed > SliceBudget.TotalMilliseconds)
                    _onSliceOverBudget?.Invoke(job.Name, stepElapsed);
            }

            job.TotalSteps += steps;
            job.TotalMs += sw.Elapsed.TotalMilliseconds;

            if (!more)
            {
                _jobs.Dequeue();
                _onJobFinished(job.Name, job.TotalMs, job.TotalSteps);
            }
        }

        private sealed class Job
        {
            public string Name { get; }
            public IEnumerator<bool> Work { get; }
            public double TotalMs;
            public int TotalSteps;

            public Job(string name, IEnumerator<bool> work)
            {
                Name = name;
                Work = work;
            }
        }
    }
}
