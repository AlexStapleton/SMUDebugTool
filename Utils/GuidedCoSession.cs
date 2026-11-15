using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ZenStatesDebugTool
{
    public enum GuidedCoPhase { Ready, Reference, ReadyToBalance, Balancing, Paused, ReadyToValidate, Validating, Results, Recovery, Faulted, Finished }
    public interface IGuidedCoClock
    {
        long Timestamp { get; }
        DateTimeOffset UtcNow { get; }
        Task Delay(int milliseconds, CancellationToken cancellation);
    }
    public sealed class GuidedCoClock : IGuidedCoClock
    {
        public long Timestamp => Stopwatch.GetTimestamp();
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
        public Task Delay(int milliseconds, CancellationToken cancellation) => Task.Delay(milliseconds, cancellation);
    }
    public sealed class GuidedCoServices
    {
        public CoRecoveryStore Recovery { get; set; }
        public CoTrialJournal Journal { get; set; }
        public IWorkloadRunner Runner { get; set; } = new OwnedWorkloadProcess();
        public Func<Task<NativeCoContext>> Capture { get; set; }
        public Func<CoBatchPlan, CancellationToken, Task<TuningResult>> ApplyBatch { get; set; }
        public Func<NativeCoContext, CoStepProposal, Func<string>, CancellationToken, Task<TuningResult>> ApplyStep { get; set; }
        public Func<Task<Func<CancellationToken, IReadOnlyList<CoreVidReading>>>> CreateSource { get; set; }
        public Func<ICoRuntimeSafety> CreateSafety { get; set; }
        public Action<bool> SetHardwareOwner { get; set; }
        public Func<NativeCoContext, string, Task> SaveProfile { get; set; }
        public string InitialExecutable { get; set; }
        public Func<Task<IReadOnlyList<WorkloadTarget>>> LoadTargets { get; set; }
    }
    public sealed class GuidedCoOptions
    {
        public WorkloadRecipe Recipe { get; }
        public int Bound { get; }
        public TimeSpan Budget { get; }
        public decimal StopTemperature { get; }
        public bool ZeroReference { get; }
        public int Repeats { get; }
        public int MaxSteps { get; }
        public int WarmupSeconds { get; }
        public int SettleSeconds { get; }
        public int NoDataSeconds { get; }
        public GuidedCoOptions(WorkloadRecipe recipe, int bound, TimeSpan budget, decimal stopTemperature, bool zeroReference,
            int repeats = 3, int maxSteps = 512, int warmupSeconds = 10, int settleSeconds = 3, int noDataSeconds = 45)
        {
            if (recipe == null || !recipe.Scored || recipe.AffinityMask != 0 || !recipe.Arguments.Contains("g_CinebenchCpuXTest=true")) throw new ArgumentException("Use an all-processor scored CPU recipe for the reference.");
            if (bound < -999 || bound >= 0 || budget < TimeSpan.FromSeconds(5) || budget > TimeSpan.FromMinutes(40) || stopTemperature < 40 || stopTemperature > 95 ||
                repeats < 2 || repeats > 5 || maxSteps < 1 || maxSteps > 512 || warmupSeconds < 0 || warmupSeconds > 120 || settleSeconds < 0 || settleSeconds > 30 || noDataSeconds < 5 || noDataSeconds > 120) throw new ArgumentOutOfRangeException("Invalid guided-session bounds.");
            Recipe = recipe; Bound = bound; Budget = budget; StopTemperature = stopTemperature; ZeroReference = zeroReference;
            Repeats = repeats; MaxSteps = maxSteps; WarmupSeconds = warmupSeconds; SettleSeconds = settleSeconds; NoDataSeconds = noDataSeconds;
        }
    }
    public static class GuidedCoContext
    {
        public static void Match(NativeCoContext expected, NativeCoContext actual)
        {
            CoRecoveryStore.RequireComplete(expected); CoRecoveryStore.RequireComplete(actual);
            if (expected.Hardware != actual.Hardware || expected.Topology != actual.Topology || expected.ExpectedCores != actual.ExpectedCores ||
                expected.ObservedCo.Any(c => !actual.ObservedCo.TryGetValue(c.Key, out int value) || value != c.Value)) throw new InvalidOperationException("Physical CO / hardware context changed; another trial is not permitted.");
            var a = TuningService.RequestsFromProfile(expected.EditorRequests).Where(r => r.Kind != TuningKind.CurveOptimizer).ToList();
            var b = TuningService.RequestsFromProfile(actual.EditorRequests).Where(r => r.Kind != TuningKind.CurveOptimizer).ToList();
            if (a.Count != b.Count || a.Zip(b, TuningSnapshots.Same).Any(equal => !equal)) throw new InvalidOperationException("Other tuning editor requests changed; start a new reference.");
        }
        public static void VerifyChange(NativeCoContext before, NativeCoContext after, IReadOnlyDictionary<int, int> changes)
        {
            var wanted = before.ObservedCo.ToDictionary(c => c.Key, c => changes.TryGetValue(c.Key, out int value) ? value : c.Value);
            Match(new NativeCoContext(before.Hardware, before.Topology, before.EditorRequests, wanted, before.ExpectedCores), after);
        }
    }
    // Owns phases and hardware intent, not UI rendering. All setters are single-flight.
    public sealed class GuidedCoSession : IDisposable
    {
        private readonly GuidedCoServices services;
        private readonly NativeCoSession records;
        private readonly Func<IReadOnlyList<CoreVidReading>> latest, window;
        private readonly Func<string> sourceError;
        private readonly Action clearSamples;
        private readonly IGuidedCoClock clock;
        private readonly CancellationToken lifetime;
        private CancellationTokenSource operation;
        private Task active = Task.CompletedTask;
        private ICoRuntimeSafety safety;
        private NativeCoContext expected;
        private GuidedCoOptions options;
        private WorkloadRecipe coverageRecipe;
        private GuidedCoPhase pausedFrom;
        private readonly List<decimal> referenceScores = new List<decimal>(), candidateScores = new List<decimal>();
        private long writeStarted, epoch;
        private int ownsWrite, requestedStop; // 1 pause, 2 finish balancing
        private int disposed;
        private bool referencePrepared;
        private string externalIssue;
        private double balanceSeconds;
        private IReadOnlyDictionary<int, int> referenceOffsets;
        private Tuple<int, int, decimal, decimal> pendingResponse; // sequence, core, previous gap, reported MHz
        private readonly Dictionary<int, int> unhelpfulResponses = new Dictionary<int, int>();
        public GuidedCoPhase Phase { get; private set; }
        public string Status { get; private set; }
        public bool Busy { get; private set; }
        public bool HasReference { get; private set; }
        public bool RecoveryReviewed { get; private set; }
        public bool OwnsWrite => Volatile.Read(ref ownsWrite) != 0;
        public int Steps { get; private set; }
        public CoEligibility Evidence { get; private set; }
        public int ReferenceRuns => referenceScores.Count;
        public int CandidateRuns => candidateScores.Count;
        public int Repeats => options?.Repeats ?? 3;
        public decimal? Temperature => safety?.Temperature;
        public IReadOnlyDictionary<int, int> ReferenceOffsets => referenceOffsets;
        public IReadOnlyDictionary<int, int> CurrentOffsets => expected?.ObservedCo;
        public string Comparison => Compare(referenceScores, candidateScores);
        public string Coverage { get; private set; } = "Individual core / SMT / light-load correctness coverage pending.";
        public GuidedCoSession(GuidedCoServices services, NativeCoSession records, Func<IReadOnlyList<CoreVidReading>> latest,
            Func<IReadOnlyList<CoreVidReading>> window, Func<string> sourceError, Action clearSamples, CancellationToken lifetime, IGuidedCoClock clock = null)
        {
            this.services = services; this.records = records; this.latest = latest; this.window = window; this.sourceError = sourceError; this.clearSamples = clearSamples; this.lifetime = lifetime; this.clock = clock ?? new GuidedCoClock();
            Phase = services.Recovery.IsPending ? GuidedCoPhase.Recovery : GuidedCoPhase.Ready;
            Status = Phase == GuidedCoPhase.Recovery ? "An earlier experiment is pending. Read current state, restore original offsets or explicitly keep them." : "Readiness checks do not change offsets. Choose bounds and start the reference.";
        }
        public Task StartReferenceAsync(GuidedCoOptions selected)
        {
            if (Phase != GuidedCoPhase.Ready) throw new InvalidOperationException("Finish or recover this session before starting another reference.");
            options = selected;
            return RunAsync(GuidedCoPhase.Reference, async ct => {
                var created = await Task.Run(() => services.CreateSafety());
                if (lifetime.IsCancellationRequested || Volatile.Read(ref disposed) != 0) { created.Dispose(); throw new OperationCanceledException(); }
                safety = created; ct.ThrowIfCancellationRequested(); await WaitForSafetyAsync(ct);
                await PrepareReferenceAsync(ct);
                await ReferenceAsync(ct);
            });
        }
        private async Task PrepareReferenceAsync(CancellationToken ct)
        {
            if (expected == null) expected = await services.Capture();
            CoRecoveryStore.RequireComplete(expected); ct.ThrowIfCancellationRequested();
            await Task.Run(() => services.Recovery.Preserve(expected));
            if (!services.Journal.Initialized) await Task.Run(() => services.Journal.Start(expected));
            await Task.Run(() => services.Journal.Configure(options.Recipe.Identity + "; lower bound " + options.Bound + "; budget " + options.Budget + "; software temperature stop " + options.StopTemperature + " °C; repeated tests " + options.Repeats));
            if (options.ZeroReference) await BatchAsync(expected.ObservedCo.ToDictionary(c => c.Key, c => 0), "Set zero-CO reference", ct);
            referenceOffsets = new System.Collections.ObjectModel.ReadOnlyDictionary<int, int>(expected.ObservedCo.ToDictionary(c => c.Key, c => c.Value)); referencePrepared = true;
        }
        private async Task ReferenceAsync(CancellationToken ct)
        {
            await WarmupAsync(ct);
            await CohortAsync("Baseline", referenceScores, ct);
            HasReference = true; await SetPhaseAsync(GuidedCoPhase.ReadyToBalance, "Reference recorded with fixed offsets. Start supervised balancing within the chosen bounds.");
        }
        public Task StartBalancingAsync()
        {
            if (!HasReference || Phase != GuidedCoPhase.ReadyToBalance && Phase != GuidedCoPhase.ReadyToValidate && Phase != GuidedCoPhase.Paused) throw new InvalidOperationException("A comparable completed reference is required before balancing.");
            candidateScores.Clear(); return RunAsync(GuidedCoPhase.Balancing, BalanceAsync);
        }
        public Task ValidateAsync()
        {
            if (!HasReference || Phase != GuidedCoPhase.ReadyToValidate && Phase != GuidedCoPhase.ReadyToBalance && Phase != GuidedCoPhase.Results) throw new InvalidOperationException("Measure a reference before validating a candidate.");
            candidateScores.Clear(); return RunAsync(GuidedCoPhase.Validating, async ct => { await WarmupAsync(ct); await CohortAsync("Candidate", candidateScores, ct); await SetPhaseAsync(GuidedCoPhase.Results, Comparison + " Core-specific correctness / light-load validation remains incomplete."); });
        }
        public Task ResumeAsync()
        {
            if (Phase != GuidedCoPhase.Paused) throw new InvalidOperationException("The session is not paused.");
            if (pausedFrom == GuidedCoPhase.Reference) return RunAsync(GuidedCoPhase.Reference, async ct => { if (!referencePrepared) await PrepareReferenceAsync(ct); await ReferenceAsync(ct); });
            if (pausedFrom == GuidedCoPhase.Validating && coverageRecipe != null) return RunAsync(GuidedCoPhase.Validating, ct => RunCoverageAsync(coverageRecipe, ct));
            if (pausedFrom == GuidedCoPhase.Validating) return RunAsync(GuidedCoPhase.Validating, async ct => { await WarmupAsync(ct); await CohortAsync("Candidate", candidateScores, ct); await SetPhaseAsync(GuidedCoPhase.Results, Comparison + " Broader validation remains incomplete."); });
            return StartBalancingAsync();
        }
        public Task CoreCoverageAsync(WorkloadRecipe recipe)
        {
            if (!HasReference || recipe.AffinityMask == 0 || !recipe.Scored || Phase != GuidedCoPhase.Results && Phase != GuidedCoPhase.ReadyToValidate) throw new InvalidOperationException("Complete the reference and stop balancing before adding core coverage.");
            coverageRecipe = recipe;
            return RunAsync(GuidedCoPhase.Validating, ct => RunCoverageAsync(recipe, ct));
        }
        private async Task RunCoverageAsync(WorkloadRecipe recipe, CancellationToken ct)
        {
            await CheckContextAsync(ct);
            var run = new ManagedCoRun(records, services.Runner, services.Capture, services.ApplyBatch, services.Recovery, () => clock.UtcNow);
            var result = await run.RunAsync("Candidate", false, recipe, Health, ct); ct.ThrowIfCancellationRequested();
            if (!result.Completed) throw new InvalidOperationException("Core rendering coverage failed: " + result.Detail);
            await CheckContextAsync(ct); Coverage = recipe.Identity + ": render completed. Computational correctness, light-load and transitions remain untested.";
            coverageRecipe = null;
            await SetPhaseAsync(GuidedCoPhase.Results, Comparison + " " + Coverage);
        }
        public void Pause(bool finishBalancing = false)
        {
            if (!Busy) return;
            Interlocked.Exchange(ref requestedStop, finishBalancing && Phase == GuidedCoPhase.Balancing ? 2 : 1); operation.Cancel();
        }
        public void NoteHardwareWrite()
        {
            clearSamples(); if (OwnsWrite || Phase == GuidedCoPhase.Ready || Phase == GuidedCoPhase.Finished) return;
            externalIssue = "Another app hardware-write operation was requested. Reference/candidate context must be reviewed.";
            HasReference = false; RecoveryReviewed = false; operation?.Cancel(); Phase = GuidedCoPhase.Recovery; Status = externalIssue;
        }
        public void RefreshEvidence()
        {
            if (expected != null && options != null) Evidence = CoEligibility.Analyze(window().Where(r => r.Timestamp >= epoch), expected.ObservedCo, options.Bound, clock.Timestamp,
                new HashSet<int>(unhelpfulResponses.Where(p => p.Value >= 3).Select(p => p.Key)));
        }
        private Task RunAsync(GuidedCoPhase phase, Func<CancellationToken, Task> body)
        {
            if (Busy) throw new InvalidOperationException("Another guided operation is running.");
            Busy = true; requestedStop = 0; Phase = phase; operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            return active = ExecuteAsync(phase, body, operation.Token);
        }
        private async Task ExecuteAsync(GuidedCoPhase phase, Func<CancellationToken, Task> body, CancellationToken ct)
        {
            services.SetHardwareOwner(true);
            try { await body(ct); }
            catch (OperationCanceledException) {
                if (!lifetime.IsCancellationRequested && Volatile.Read(ref disposed) == 0 && !services.Recovery.IsPending) {
                    safety?.Dispose(); safety = null; expected = null; Phase = GuidedCoPhase.Ready; Status = "Preparation cancelled; no offset changes were started.";
                } else if (!lifetime.IsCancellationRequested && Volatile.Read(ref disposed) == 0 && externalIssue == null && expected != null && requestedStop != 0) {
                    try {
                        var actual = await services.Capture(); GuidedCoContext.Match(expected, actual); expected = actual; pausedFrom = phase;
                        Phase = requestedStop == 2 && HasReference ? GuidedCoPhase.ReadyToValidate : GuidedCoPhase.Paused;
                        Status = Phase == GuidedCoPhase.ReadyToValidate ? "Balancing stopped at your request. Its score is excluded; validate fixed offsets next." : "Paused: load stopped and no further writes. Resume rechecks context and warms up again.";
                    } catch (Exception ex) { Fault(ex.Message); }
                } else { Phase = GuidedCoPhase.Recovery; Status = externalIssue ?? "Session interrupted. Recover explicitly before any further tuning."; }
            }
            catch (Exception ex) { Fault(ex.Message); }
            finally {
                if (Phase != GuidedCoPhase.Finished)
                    try { await Task.Run(() => services.Journal.Phase(Phase + ": " + Status)); } catch (Exception ex) { Fault("Journal could not be saved: " + ex.Message); }
                services.SetHardwareOwner(false); Busy = false; operation.Dispose(); operation = null;
                if (lifetime.IsCancellationRequested || Volatile.Read(ref disposed) != 0) { safety?.Dispose(); safety = null; }
            }
        }
        private void Fault(string message) { Phase = GuidedCoPhase.Faulted; RecoveryReviewed = false; Status = message + (services.Recovery.IsPending ? " Further tuning stopped; original offsets retained for recovery." : " No pending offset snapshot. Return to setup to retry."); }
        private async Task WaitForSafetyAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Status = "Checking native readings, CPU temperature and new WHEA observation before any setter…";
            long began = clock.Timestamp; string issue;
            while ((issue = Health()) != null && Seconds(began) < 5) await clock.Delay(100, ct);
            if (issue != null) throw new InvalidOperationException(issue);
        }
        private string Health()
        {
            if (lifetime.IsCancellationRequested) return "Workbench is closing.";
            string issue = externalIssue ?? sourceError() ?? safety?.Issue(options.StopTemperature); if (issue != null) return issue;
            var rows = latest();
            long beganWrite = Interlocked.Read(ref writeStarted);
            if (rows.Count == 0 && beganWrite > 0 && Seconds(beganWrite) <= 2) return null;
            if (rows.Count == 0 || expected != null && rows.Count != expected.ExpectedCores || rows.Any(r => !r.VidVolts.HasValue || Seconds(r.Timestamp) > 2)) return "Essential native per-core readings are missing or stale.";
            return null;
        }
        private double Seconds(long start) => (clock.Timestamp - start) / (double)Stopwatch.Frequency;
        private void Check(CancellationToken ct) { ct.ThrowIfCancellationRequested(); var issue = Health(); if (issue != null) throw new InvalidOperationException(issue); }
        private async Task CheckContextAsync(CancellationToken ct)
        {
            Check(ct); var actual = await services.Capture(); ct.ThrowIfCancellationRequested(); GuidedCoContext.Match(expected, actual); expected = actual;
        }
        private async Task WarmupAsync(CancellationToken ct)
        {
            await CheckContextAsync(ct);
            for (int attempt = 0; attempt < 3; attempt++) {
                Status = "Warming up the owned CPU load; warm-up scores are excluded. Thermal window " + (attempt + 1) + " / 3.";
                var result = await services.Runner.RunAsync(options.Recipe.ContinuousLoad(Math.Max(10, options.WarmupSeconds)), Health, ct);
                ct.ThrowIfCancellationRequested();
                if (result.Outcome != "Load finished") throw new InvalidOperationException("Warm-up did not finish cleanly: " + result.Detail);
                if (safety.TemperatureStable) break;
                if (attempt == 2) throw new InvalidOperationException("Temperature did not settle into a comparable window. Reference/validation measurement is incomplete.");
            }
            await CheckContextAsync(ct);
        }
        private async Task CohortAsync(string role, List<decimal> scores, CancellationToken ct)
        {
            if (records.TrialCount + (options.Repeats - scores.Count) > 100) throw new InvalidOperationException("Session history is full; export and finish this session.");
            var run = new ManagedCoRun(records, services.Runner, services.Capture, services.ApplyBatch, services.Recovery, () => clock.UtcNow);
            while (scores.Count < options.Repeats) {
                await CheckContextAsync(ct); Status = (role == "Baseline" ? "Reference" : "Candidate") + " fixed-offset run " + (scores.Count + 1) + " / " + options.Repeats;
                var result = await run.RunAsync(role, false, options.Recipe, Health, ct);
                ct.ThrowIfCancellationRequested();
                if (!result.Completed || !result.Score.HasValue) throw new InvalidOperationException("Fixed-offset test incomplete: " + result.Outcome + ". " + result.Detail);
                await CheckContextAsync(ct); scores.Add(result.Score.Value);
            }
        }
        private async Task BalanceAsync(CancellationToken ct)
        {
            if (balanceSeconds >= options.Budget.TotalSeconds) { await SetPhaseAsync(GuidedCoPhase.ReadyToValidate, "Time budget exhausted; validate the partial candidate with fixed offsets."); return; }
            await CheckContextAsync(ct); clearSamples(); epoch = clock.Timestamp; await WaitForSafetyAsync(ct);
            long began = clock.Timestamp, lastQualified = began, lastStep = began;
            using (var loadStop = CancellationTokenSource.CreateLinkedTokenSource(ct))
            using (var trialsStop = CancellationTokenSource.CreateLinkedTokenSource(ct)) {
                string observedFailure = null;
                var load = services.Runner.RunAsync(options.Recipe.ContinuousLoad((int)Math.Ceiling(options.Budget.TotalSeconds - balanceSeconds) + 120), () => {
                    var problem = Health();
                    if (problem != null) { Interlocked.CompareExchange(ref observedFailure, problem, null); trialsStop.Cancel(); }
                    return problem;
                }, loadStop.Token);
                var observeExit = load.ContinueWith(_ => { if (!loadStop.IsCancellationRequested) trialsStop.Cancel(); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                string reason = null;
                try {
                    while (true) {
                        Check(trialsStop.Token);
                        if (load.IsCompleted) { var ended = await load; throw new InvalidOperationException("Balancing load ended before the phase finished: " + ended.Outcome + ". " + ended.Detail); }
                        if (balanceSeconds + Seconds(began) >= options.Budget.TotalSeconds) { reason = "Time budget exhausted; partial candidate"; break; }
                        if (Steps >= options.MaxSteps) { reason = "Step budget reached; partial candidate"; break; }
                        RefreshEvidence();
                        if (pendingResponse != null) {
                            var response = Evidence.Cores.Single(c => c.Slot == pendingResponse.Item2);
                            if (response.Comparable && response.PeerMedian.HasValue && response.Vid.HasValue) {
                                decimal gap = response.Vid.Value - response.PeerMedian.Value;
                                bool sameClock = Math.Abs(response.Mhz.Value - pendingResponse.Item4) <= pendingResponse.Item4 * .03m;
                                bool helpful = sameClock && gap <= pendingResponse.Item3 - .00625m;
                                unhelpfulResponses.TryGetValue(response.Slot, out int count); unhelpfulResponses[response.Slot] = helpful ? 0 : count + 1;
                                int sequence = pendingResponse.Item1;
                                await Task.Run(() => services.Journal.Measurement(sequence, "VID gap " + gap + " V; reported MHz " + response.Mhz + "; same clock context " + sameClock + "; improvement evidence " + helpful + ". No stability verdict."));
                                pendingResponse = null;
                                if (unhelpfulResponses[response.Slot] >= 3) { reason = "No reproducible comparable VID-gap reduction after three trials on core " + response.Slot + "; partial candidate"; break; }
                            }
                        }
                        if (Seconds(began) < options.WarmupSeconds || !safety.TemperatureStable) Status = "Waiting for a steady thermal / clock window under load…";
                        else if (Seconds(lastStep) < options.SettleSeconds) Status = "Settling after the last CO change; transition samples excluded.";
                        else if (Evidence.Proposal != null) {
                            lastQualified = clock.Timestamp; await StepAsync(Evidence.Proposal, () => load.IsCompleted ? "Owned balancing load ended; setter blocked." : Health(), trialsStop.Token); lastStep = clock.Timestamp;
                        } else if (Evidence.StopReason != null) { reason = Evidence.StopReason; break; }
                        else {
                            Status = "Collecting comparable busy samples; inspect each core's eligibility reason.";
                            if (Seconds(lastQualified) >= options.NoDataSeconds) { reason = "Insufficient comparable data; partial / inconclusive candidate"; break; }
                        }
                        await clock.Delay(200, trialsStop.Token);
                    }
                } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
                    throw new InvalidOperationException("Balancing stopped after load/source failure: " + (observedFailure ?? (load.IsCompleted ? (await load).Detail : "Load ended unexpectedly.")));
                } finally {
                    balanceSeconds += Seconds(began);
                    loadStop.Cancel();
                    try { await load; } finally { await observeExit; }
                }
                await CheckContextAsync(ct);
                await SetPhaseAsync(GuidedCoPhase.ReadyToValidate, reason + ". Changing-offset scores excluded; validate fixed offsets next.");
            }
        }
        private async Task BatchAsync(IReadOnlyDictionary<int, int> requested, string label, CancellationToken ct)
        {
            await CheckContextAsync(ct);
            int sequence = await Task.Run(() => services.Journal.Intent(label, expected.ObservedCo, requested)); ct.ThrowIfCancellationRequested();
            var before = expected; TuningResult result;
            Interlocked.Exchange(ref writeStarted, clock.Timestamp); Interlocked.Exchange(ref ownsWrite, 1);
            try { result = await services.ApplyBatch(new CoBatchPlan(before.ObservedCo, requested, Health), ct); }
            finally { Interlocked.Exchange(ref ownsWrite, 0); clearSamples(); epoch = clock.Timestamp; }
            await Task.Run(() => services.Journal.Complete(sequence, result));
            if (result.Rows.Count != requested.Count || result.Rows.Any(r => r.Outcome != TuningOutcome.Verified)) throw new InvalidOperationException("CO batch only partially applied or failed verification.");
            var actual = await services.Capture(); GuidedCoContext.VerifyChange(before, actual, requested); expected = actual;
            await WaitForSafetyAsync(ct);
        }
        private async Task StepAsync(CoStepProposal proposal, Func<string> writeGuard, CancellationToken ct)
        {
            await CheckContextAsync(ct);
            if (proposal.Requested < options.Bound || expected.ObservedCo[proposal.Slot] != proposal.Previous) throw new InvalidOperationException("CO proposal no longer matches the configured bounds/current offsets.");
            var requested = new Dictionary<int, int> { [proposal.Slot] = proposal.Requested };
            var evidence = Evidence.Cores.Single(c => c.Slot == proposal.Slot);
            string selection = "Native timestamp " + clock.Timestamp + "; CCD " + evidence.Ccd + "; samples " + evidence.Samples + "; VID " + evidence.Vid + "; peer median " + evidence.PeerMedian + "; reported MHz " + evidence.Mhz + ". " + proposal.Reason;
            int sequence = await Task.Run(() => services.Journal.Intent("One-core negative trial", expected.ObservedCo, requested, selection)); ct.ThrowIfCancellationRequested();
            var before = expected; TuningResult result;
            Status = "Core " + proposal.Slot + ": checking CO " + proposal.Previous + " → " + proposal.Requested + " under load.";
            Interlocked.Exchange(ref writeStarted, clock.Timestamp); Interlocked.Exchange(ref ownsWrite, 1);
            try { result = await services.ApplyStep(before, proposal, writeGuard, ct); }
            finally {
                Interlocked.Exchange(ref ownsWrite, 0); clearSamples();
                // Settling is an exclusion interval, followed by a new complete evidence window.
                epoch = clock.Timestamp + (long)(options.SettleSeconds * (double)Stopwatch.Frequency);
            }
            await Task.Run(() => services.Journal.Complete(sequence, result));
            if (result.Rows.Count != 1 || result.Rows[0].Outcome != TuningOutcome.Verified) throw new InvalidOperationException("Trial failed or did not verify; no further changes.");
            var actual = await services.Capture(); GuidedCoContext.VerifyChange(before, actual, requested); expected = actual; Steps++;
            pendingResponse = Tuple.Create(sequence, proposal.Slot, evidence.Vid.Value - evidence.PeerMedian.Value, evidence.Mhz.Value);
            await WaitForSafetyAsync(ct);
        }
        private async Task SetPhaseAsync(GuidedCoPhase phase, string status) { Phase = phase; Status = status; await Task.Run(() => services.Journal.Phase(phase + ": " + status)); }
        public async Task RestoreAsync()
        {
            if (Busy) { Pause(); await active; }
            await RunAsync(GuidedCoPhase.Recovery, async ct => {
                var actual = await services.Capture(); var original = await Task.Run(() => services.Recovery.Read()); CoRecoveryStore.Match(original, actual); expected = actual;
                if (!services.Journal.Initialized) await Task.Run(() => services.Journal.Start(actual));
                // Recovery is explicit and still requires direct scalar/CO preflight/readback.
                int sequence = await Task.Run(() => services.Journal.Intent("Restore original offsets", actual.ObservedCo, original.OriginalCo)); ct.ThrowIfCancellationRequested();
                Interlocked.Exchange(ref ownsWrite, 1); TuningResult result;
                try { result = await services.ApplyBatch(new CoBatchPlan(actual.ObservedCo, original.OriginalCo), ct); }
                finally { Interlocked.Exchange(ref ownsWrite, 0); clearSamples(); }
                await Task.Run(() => services.Journal.Complete(sequence, result));
                if (result.Rows.Count != original.OriginalCo.Count || result.Rows.Any(r => r.Outcome != TuningOutcome.Verified)) throw new InvalidOperationException("Restore incomplete. Original snapshot and per-core evidence retained.");
                var restored = await services.Capture(); GuidedCoContext.VerifyChange(actual, restored, original.OriginalCo); ct.ThrowIfCancellationRequested();
                await Task.Run(() => services.Journal.Phase("Finished: original CO restored and read back"));
                await Task.Run(() => services.Recovery.Clear(restored)); expected = restored;
                Phase = GuidedCoPhase.Finished; Status = "Original offsets restored and read back. Startup profile unchanged."; safety?.Dispose(); safety = null;
            });
        }
        public Task KeepAsync(string profileName = null)
        {
            if (Busy || Phase == GuidedCoPhase.Ready || Phase == GuidedCoPhase.Finished) throw new InvalidOperationException("Finish or pause the current operation before choosing a result.");
            if ((Phase == GuidedCoPhase.Faulted || Phase == GuidedCoPhase.Recovery) && !RecoveryReviewed) throw new InvalidOperationException("Read and review current offsets before keeping an interrupted result.");
            return RunAsync(GuidedCoPhase.Recovery, async ct => {
                var actual = await services.Capture(); var original = await Task.Run(() => services.Recovery.Read()); CoRecoveryStore.Match(original, actual);
                if (!services.Journal.Initialized) await Task.Run(() => services.Journal.Start(actual));
                if (expected != null) GuidedCoContext.Match(expected, actual);
                if (profileName != null) await services.SaveProfile(actual, profileName);
                var final = await services.Capture(); GuidedCoContext.Match(actual, final); ct.ThrowIfCancellationRequested();
                await Task.Run(() => services.Journal.Phase(profileName == null ? "Finished: kept temporarily; startup unchanged" : "Finished: CO-only profile saved; startup unchanged"));
                await Task.Run(() => services.Recovery.Clear(final)); expected = final;
                Phase = GuidedCoPhase.Finished; Status = (profileName == null ? "Offsets kept temporarily. No profile saved." : "CO-only experimental profile saved; current offsets kept.") + " Startup selection unchanged; broader validation is incomplete.";
                safety?.Dispose(); safety = null;
            });
        }
        public Task ReadCurrentAsync()
        {
            if (Busy) throw new InvalidOperationException("Stop the current operation before reading recovery state.");
            return RunAsync(GuidedCoPhase.Recovery, async ct => {
                var actual = await services.Capture(); var original = await Task.Run(() => services.Recovery.Read()); CoRecoveryStore.Match(original, actual); ct.ThrowIfCancellationRequested();
                expected = actual; HasReference = false; RecoveryReviewed = true; Phase = GuidedCoPhase.Recovery;
                Status = "Current CO readback captured. Prior comparison context is invalidated; restore original or explicitly keep current values.";
            });
        }
        public static string Compare(IEnumerable<decimal> reference, IEnumerable<decimal> candidate)
        {
            var a = reference.OrderBy(x => x).ToArray(); var b = candidate.OrderBy(x => x).ToArray();
            if (a.Length < 2 || b.Length < 2) return "Repeated fixed-offset comparison pending.";
            decimal Median(decimal[] v) => v.Length % 2 == 0 ? (v[v.Length / 2 - 1] + v[v.Length / 2]) / 2 : v[v.Length / 2];
            decimal before = Median(a), after = Median(b), delta = before == 0 ? 0 : (after / before - 1) * 100;
            string conclusion = b.Min() > a.Max() ? "Candidate scores exceed the observed reference range; confirm with independent runs." : b.Max() < a.Min() ? "Candidate scores are below the observed reference range." : "Observed score ranges overlap; no demonstrated performance improvement.";
            return "Reference median " + before.ToString("0.##", CultureInfo.InvariantCulture) + " [" + a.Min() + "–" + a.Max() + "]; candidate " + after.ToString("0.##", CultureInfo.InvariantCulture) + " [" + b.Min() + "–" + b.Max() + "]; change " + delta.ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture) + "%. " + conclusion;
        }
        public void Dispose() { Interlocked.Exchange(ref disposed, 1); operation?.Cancel(); safety?.Dispose(); }
    }
    public static class GuidedCoProfiles
    {
        public static Profiles.Profile Create(NativeCoContext observed, TopologySnapshot topology, string name)
        {
            CoRecoveryStore.RequireComplete(observed);
            if (observed.Topology != topology.Signature) throw new InvalidOperationException("Profile topology differs from current readback.");
            var profile = new Profiles.Profile { Name = name, CoMargins = observed.ObservedCo.ToDictionary(c => c.Key, c => c.Value) };
            topology.Bind(profile); return profile;
        }
    }
}
