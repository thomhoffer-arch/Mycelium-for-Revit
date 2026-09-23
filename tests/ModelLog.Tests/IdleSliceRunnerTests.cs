using Loam.Revit.Connector.ModelLog;
using System.Collections.Generic;
using System.Threading;
using Xunit;

namespace ModelLog.Tests
{
    public class IdleSliceRunnerTests
    {
        private static IEnumerator<bool> CountTo(int n, List<int> seen)
        {
            for (var i = 0; i < n; i++)
            {
                seen.Add(i);
                yield return i < n - 1;
            }
        }

        private static IEnumerator<bool> Slow(int steps, int msPerStep)
        {
            for (var i = 0; i < steps; i++)
            {
                Thread.Sleep(msPerStep);
                yield return i < steps - 1;
            }
        }

        [Fact]
        public void SmallJob_FinishesInOneSlice_FiresCallback()
        {
            var seen = new List<int>();
            string? finishedName = null;
            int finishedSteps = 0;

            var runner = new IdleSliceRunner((name, ms, steps) => { finishedName = name; finishedSteps = steps; });
            runner.Enqueue("job-1", CountTo(5, seen));
            runner.RunSlice();

            Assert.Equal(new List<int> { 0, 1, 2, 3, 4 }, seen);
            Assert.Equal("job-1", finishedName);
            Assert.Equal(5, finishedSteps);
            Assert.False(runner.HasWork);
        }

        [Fact]
        public void LongJob_SpreadsAcrossMultipleSlices()
        {
            var finished = false;
            var runner = new IdleSliceRunner((_, __, ___) => finished = true);
            // Each step sleeps 20ms; the 50ms budget allows ~2 steps per slice, so a 6-step
            // job needs multiple RunSlice() calls, never completing in the first.
            runner.Enqueue("slow-job", Slow(6, 20));

            runner.RunSlice();
            Assert.False(finished);
            Assert.True(runner.HasWork);

            var guard = 0;
            while (runner.HasWork && guard++ < 20) runner.RunSlice();

            Assert.True(finished);
        }

        [Fact]
        public void OverBudgetSlice_InvokesCallback()
        {
            string? overBudgetName = null;
            var runner = new IdleSliceRunner((_, __, ___) => { }, (name, ms) => overBudgetName = name);
            // A single step that itself takes longer than the whole slice budget.
            runner.Enqueue("hog", Slow(1, 80));
            runner.RunSlice();

            Assert.Equal("hog", overBudgetName);
        }

        [Fact]
        public void MultipleJobs_RunFifo()
        {
            var order = new List<string>();
            var runner = new IdleSliceRunner((name, __, ___) => order.Add(name));
            runner.Enqueue("first", CountTo(1, new List<int>()));
            runner.Enqueue("second", CountTo(1, new List<int>()));

            runner.RunSlice();
            runner.RunSlice();

            Assert.Equal(new List<string> { "first", "second" }, order);
        }
    }
}
