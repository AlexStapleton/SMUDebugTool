using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ZenStatesDebugTool
{
    public sealed class ManagedCoRun
    {
        private readonly NativeCoSession session;
        private readonly IWorkloadRunner runner;
        private readonly Func<Task<NativeCoContext>> capture;
        private readonly Func<CoBatchPlan, CancellationToken, Task<TuningResult>> apply;
        private readonly CoRecoveryStore recovery;
        private int running;
        public bool AssistanceBlocked { get; private set; }
        private readonly object suggestionGate = new object();
        private CoStepProposal suggestion;
        public CoStepProposal SuggestedStep { get { lock (suggestionGate) return suggestion; } }
        public NativeCoContext SuggestionContext { get; private set; }
        public ManagedCoRun(NativeCoSession session, IWorkloadRunner runner, Func<Task<NativeCoContext>> capture,
            Func<CoBatchPlan, CancellationToken, Task<TuningResult>> apply, CoRecoveryStore recovery)
        { this.session = session; this.runner = runner; this.capture = capture; this.apply = apply; this.recovery = recovery; }
        public async Task<WorkloadResult> RunAsync(string role, bool zeroBaseline, WorkloadRecipe recipe, Func<string> health, CancellationToken cancellation, Func<NativeCoContext, long, CoStepProposal> suggest = null)
        {
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0) throw new InvalidOperationException("A managed test is already running.");
            WorkloadResult result = null; bool began = false;
            try {
                DiscardSuggestion();
                cancellation.ThrowIfCancellationRequested();
                if (session.IsRecording || session.Trials.Count >= 100 || role != "Baseline" && role != "Candidate" || zeroBaseline && role != "Baseline") throw new InvalidOperationException("End the current recording, clear a full session or choose a valid run role.");
                string issue = health?.Invoke(); if (issue != null) throw new InvalidOperationException(issue);
                var before = await capture(); CoRecoveryStore.RequireComplete(before); cancellation.ThrowIfCancellationRequested();
                if (zeroBaseline) {
                    await Task.Run(() => recovery.Preserve(before)); cancellation.ThrowIfCancellationRequested();
                    var zero = before.ObservedCo.ToDictionary(c => c.Key, c => 0);
                    var write = await apply(new CoBatchPlan(before.ObservedCo, zero), cancellation);
                    if (write.Rows.Count != zero.Count || write.Rows.Any(r => r.Outcome != TuningOutcome.Verified)) throw new InvalidOperationException("Zero-CO baseline did not fully verify. Workload was not launched; recovery retained.");
                    before = await capture(); CoRecoveryStore.RequireComplete(before);
                    if (before.ObservedCo.Any(c => c.Value != 0)) throw new InvalidOperationException("CO changed after baseline reset. Workload was not launched.");
                }
                // Hardware writes invalidate samples; wait asynchronously for a new native frame.
                var deadline = System.Diagnostics.Stopwatch.StartNew();
                while ((issue = health?.Invoke()) != null && deadline.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(100, cancellation);
                if (issue != null) throw new InvalidOperationException(issue);
                cancellation.ThrowIfCancellationRequested();
                session.Begin(role, recipe.Identity, before, DateTimeOffset.UtcNow); began = true;
                long runStart = System.Diagnostics.Stopwatch.GetTimestamp(), lastSuggestion = 0;
                CoStepProposal proposed = null;
                result = await runner.RunAsync(recipe, () => {
                    string problem = health?.Invoke();
                    if (problem == null && suggest != null) {
                        var next = suggest(before, runStart);
                        if (next != null) { proposed = next; lastSuggestion = System.Diagnostics.Stopwatch.GetTimestamp(); }
                    }
                    return problem;
                }, cancellation);
                if (cancellation.IsCancellationRequested && result.Completed) { result.Outcome = "Cancelled"; result.Score = null; result.Detail = "Stopped before run finalization."; }
                session.MarkEnd(DateTimeOffset.UtcNow);
                var after = await capture();
                string context = NativeCoSession.Context(before, after);
                if (result.Completed && (context != "CO endpoints unchanged" || session.Trials.Last().Changes.Count > 0)) { result.Outcome = "Inconclusive"; result.Score = null; result.Detail = "Run context changed or contains tuning activity: " + context; }
                session.End(after, result.Completed ? result.Score : null, result);
                AssistanceBlocked = !result.Completed;
                if (result.Completed && proposed != null && (System.Diagnostics.Stopwatch.GetTimestamp() - lastSuggestion) / (double)System.Diagnostics.Stopwatch.Frequency <= 30) {
                    SuggestionContext = after;
                    lock (suggestionGate) suggestion = proposed;
                }
                return result;
            } catch (Exception ex) {
                AssistanceBlocked = true;
                result = new WorkloadResult { Outcome = ex is OperationCanceledException ? "Cancelled" : "Failed", Detail = ex.Message, Output = result?.Output };
                if (began) session.Fail(result); return result;
            } finally { Volatile.Write(ref running, 0); }
        }
        public void DiscardSuggestion() { lock (suggestionGate) suggestion = null; SuggestionContext = null; }
        public void BlockAssistance() { AssistanceBlocked = true; DiscardSuggestion(); }
        public void Recovered() { AssistanceBlocked = false; DiscardSuggestion(); }
    }
}
