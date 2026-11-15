using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.Windows.Forms;
using Newtonsoft.Json.Linq;
using Xunit;
using ZenStatesDebugTool;
using ZenStatesDebugTool.Profiles;

namespace ProfileCore.Tests
{
    public class GuidedCoTests
    {
        private sealed class Clock : IGuidedCoClock
        {
            public long Timestamp { get; private set; } = Stopwatch.GetTimestamp() + Stopwatch.Frequency * 10;
            public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.UtcNow;
            public Action Tick;
            public void Advance(int ms) { Timestamp += (long)(Stopwatch.Frequency * (ms / 1000.0)); UtcNow = UtcNow.AddMilliseconds(ms); }
            public async Task Delay(int ms, CancellationToken ct) { ct.ThrowIfCancellationRequested(); Advance(ms); Tick?.Invoke(); await Task.Yield(); ct.ThrowIfCancellationRequested(); }
        }
        private sealed class Safety : ICoRuntimeSafety
        {
            public string Problem;
            public bool TemperatureStable { get; set; } = true;
            public decimal? Temperature => 65;
            public string Issue(decimal stop) => Problem;
            public int Disposals;
            public void Dispose() { Disposals++; }
        }
        private sealed class Runner : IWorkloadRunner
        {
            public readonly Clock Clock;
            public int Scored, ActiveLoads, ContinuousStarts;
            public ulong LastScoredAffinity;
            public Action ScoredAction;
            public string ScoreOutcome = "Completed";
            public TaskCompletionSource<WorkloadResult> Load;
            public Func<string> Health;
            public Runner(Clock clock) { Clock = clock; }
            public Task<WorkloadResult> RunAsync(WorkloadRecipe recipe, Func<string> health, CancellationToken ct)
            {
                string problem = health(); if (problem != null) return Task.FromResult(new WorkloadResult { Outcome = "Failed", Detail = problem });
                if (recipe.Scored) { Scored++; LastScoredAffinity = recipe.AffinityMask; ScoredAction?.Invoke(); Clock.Advance(1000); return Task.FromResult(new WorkloadResult { Outcome = ScoreOutcome, Score = ScoreOutcome == "Completed" ? (decimal?)100 : null }); }
                if (recipe.Arguments.Contains("g_CinebenchMinimumTestDuration=10 ")) { Clock.Advance(10000); return Task.FromResult(new WorkloadResult { Outcome = "Load finished" }); }
                ContinuousStarts++; ActiveLoads++; Health = health; Load = new TaskCompletionSource<WorkloadResult>();
                ct.Register(() => { if (Load.TrySetResult(new WorkloadResult { Outcome = "Cancelled" })) ActiveLoads--; });
                return Load.Task;
            }
            public void ExitUnexpectedly() { if (Load.TrySetResult(new WorkloadResult { Outcome = "Failed", Detail = "Fixture load exited" })) ActiveLoads--; }
        }
        private sealed class Rig : IDisposable
        {
            public readonly Clock Clock = new Clock();
            public readonly Safety Safety = new Safety();
            public readonly Runner Runner;
            public readonly NativeCoSession Records = new NativeCoSession();
            public readonly Dictionary<int, int> Co = new Dictionary<int, int> { [0] = -20, [1] = -25, [2] = -30 };
            public readonly CoRecoveryStore Recovery;
            public readonly CoTrialJournal Journal;
            public readonly string JournalPath;
            public readonly GuidedCoServices Services;
            public readonly GuidedCoSession Session;
            public int BatchWrites, StepWrites, InFlight, MaximumInFlight, Ppt = 75;
            public bool Owner, RejectStep, MissingSource, NoVidGap, FreezeVidResponse;
            public Action BeforeStep, AfterStep;
            public Rig()
            {
                string dir = Path.Combine(Path.GetTempPath(), "SMUDebugTool-guided-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
                Recovery = new CoRecoveryStore(Path.Combine(dir, "original.json")); JournalPath = Path.Combine(dir, "trials.json"); Journal = new CoTrialJournal(JournalPath); Runner = new Runner(Clock);
                Services = new GuidedCoServices {
                    Recovery = Recovery, Journal = Journal, Runner = Runner, CreateSafety = () => Safety, Capture = () => Task.FromResult(Context()), SetHardwareOwner = value => Owner = value,
                    ApplyBatch = async (plan, ct) => {
                        ct.ThrowIfCancellationRequested(); Assert.True(Journal.Initialized); Assert.Equal(Co.OrderBy(c => c.Key), plan.Expected.OrderBy(c => c.Key));
                        BatchWrites++; foreach (var c in plan.Requested) Co[c.Key] = c.Value;
                        await Clock.Delay(10, ct); return Verified(plan.Requested);
                    },
                    ApplyStep = async (before, proposal, guard, ct) => {
                        Assert.Equal(1, Runner.ActiveLoads); Assert.Contains("Intent saved", File.ReadAllText(JournalPath));
                        BeforeStep?.Invoke(); ct.ThrowIfCancellationRequested(); var problem = guard(); if (problem != null) throw new InvalidOperationException(problem);
                        StepWrites++; InFlight++; MaximumInFlight = Math.Max(MaximumInFlight, InFlight);
                        Co[proposal.Slot] = proposal.Requested; await Clock.Delay(10, CancellationToken.None); InFlight--; AfterStep?.Invoke();
                        var result = Verified(new Dictionary<int, int> { [proposal.Slot] = proposal.Requested }); if (RejectStep) result.Rows[0].Outcome = TuningOutcome.Mismatch; return result;
                    },
                    SaveProfile = (context, name) => Task.CompletedTask
                };
                Session = new GuidedCoSession(Services, Records, Latest, Window, () => null, () => { }, CancellationToken.None, Clock);
            }
            public NativeCoContext Context() => new NativeCoContext("Synthetic CPU/BIOS/SMU", "Synthetic physical mapping", new Profile { PptWatts = Ppt }, Co, Co.Count);
            public IReadOnlyList<CoreVidReading> Latest() => MissingSource ? new CoreVidReading[0] : Co.Select(c => Row(c.Key, Clock.Timestamp)).ToArray();
            private CoreVidReading Row(int core, long timestamp) => new CoreVidReading(core, 0, "Synthetic core " + core, timestamp, 1,
                NoVidGap || core != 0 ? 1.10m : 1.15m + (FreezeVidResponse ? 0 : Co[0] * .025m), 5000, 4950, null);
            public IReadOnlyList<CoreVidReading> Window() => MissingSource ? new CoreVidReading[0] : Enumerable.Range(0, 31).SelectMany(i => Co.Keys.Select(core => Row(core, Clock.Timestamp - (long)((30 - i) * .1 * Stopwatch.Frequency)))).ToArray();
            public GuidedCoOptions Options(int bound = -5, int maxSteps = 10, int budgetSeconds = 30, int settleSeconds = 0)
                => new GuidedCoOptions(new WorkloadRecipe(ManagedWorkloadTests.FixtureExecutable, "g_CinebenchCpuXTest=true g_CinebenchMinimumTestDuration=60 g_console=true", "Synthetic frozen recipe", TimeSpan.FromSeconds(120)),
                    bound, TimeSpan.FromSeconds(budgetSeconds), 90, true, repeats: 2, maxSteps: maxSteps, warmupSeconds: 0, settleSeconds: settleSeconds, noDataSeconds: settleSeconds + 5);
            public void Dispose() => Session.Dispose();
        }
        private static TuningResult Verified(IReadOnlyDictionary<int, int> requested)
        {
            var result = new TuningResult(); foreach (var c in requested) result.Rows.Add(new TuningRow { Request = new TuningRequest { Kind = TuningKind.CurveOptimizer, Index = c.Key, Value = c.Value }, Outcome = TuningOutcome.Verified }); return result;
        }
        [Fact]
        public async Task FullFlowKeepsOneLoadAcrossTrialsExcludesMixedScoresAndValidatesFixedCandidate()
        {
            using (var rig = new Rig()) {
                await rig.Session.StartReferenceAsync(rig.Options());
                Assert.Equal(GuidedCoPhase.ReadyToBalance, rig.Session.Phase); Assert.Equal(2, rig.Session.ReferenceRuns); Assert.Equal(1, rig.BatchWrites); Assert.All(rig.Co.Values, c => Assert.Equal(0, c));
                Assert.Equal(-20, rig.Recovery.Read().OriginalCo[0]);
                await rig.Session.StartBalancingAsync();
                Assert.Equal(GuidedCoPhase.ReadyToValidate, rig.Session.Phase); Assert.Equal(2, rig.StepWrites); Assert.Equal(1, rig.Runner.ContinuousStarts); Assert.Equal(0, rig.Runner.ActiveLoads); Assert.Equal(1, rig.MaximumInFlight);
                Assert.Equal(2, rig.Records.Trials.Count); Assert.Contains("scores excluded", rig.Session.Status);
                var entries = JObject.Parse(File.ReadAllText(rig.JournalPath))["Entries"];
                Assert.Equal(3, entries.Count()); Assert.Contains("peer median", entries.Last()["Selection"].Value<string>());
                await rig.Session.ValidateAsync(); Assert.Equal(GuidedCoPhase.Results, rig.Session.Phase); Assert.Equal(2, rig.Session.CandidateRuns); Assert.Equal(4, rig.Records.Trials.Count);
                Assert.Contains("ranges overlap", rig.Session.Comparison);
                await rig.Session.KeepAsync(); Assert.Equal(GuidedCoPhase.Finished, rig.Session.Phase); Assert.False(rig.Recovery.IsPending); Assert.False(rig.Owner);
            }
        }
        [Fact]
        public async Task SettlingSamplesCannotQualifyTheNextTrial()
        {
            using (var rig = new Rig()) {
                await rig.Session.StartReferenceAsync(rig.Options(settleSeconds: 3));
                var writes = new List<long>(); rig.BeforeStep = () => writes.Add(rig.Clock.Timestamp);
                await rig.Session.StartBalancingAsync();
                Assert.Equal(GuidedCoPhase.ReadyToValidate, rig.Session.Phase); Assert.Equal(2, writes.Count);
                Assert.True((writes[1] - writes[0]) / (double)Stopwatch.Frequency >= 5, "A full two-second evidence window must follow the three-second exclusion interval.");
            }
        }
        [Fact]
        public async Task ResumeCoreCoveragePreservesItsTargetWithoutAddingComparisonScores()
        {
            using (var rig = new Rig()) {
                await rig.Session.StartReferenceAsync(rig.Options()); await rig.Session.StartBalancingAsync();
                var recipe = new WorkloadRecipe(ManagedWorkloadTests.FixtureExecutable, "Synthetic core render", "Core 0 fixture", TimeSpan.FromSeconds(120), 1);
                rig.Runner.ScoredAction = () => rig.Session.Pause();
                await rig.Session.CoreCoverageAsync(recipe); Assert.Equal(GuidedCoPhase.Paused, rig.Session.Phase);
                rig.Runner.ScoredAction = null; await rig.Session.ResumeAsync();
                Assert.Equal(GuidedCoPhase.Results, rig.Session.Phase); Assert.Equal(1UL, rig.Runner.LastScoredAffinity);
                Assert.Equal(0, rig.Session.CandidateRuns); Assert.Contains("Core 0 fixture", rig.Session.Coverage);
            }
        }
        [Fact]
        public async Task CandidateAndBalancingAreUnavailableBeforeReference()
        {
            using (var rig = new Rig()) { await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Session.ValidateAsync()); await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Session.StartBalancingAsync()); Assert.Equal(0, rig.StepWrites); Assert.Equal(0, rig.BatchWrites); }
        }
        [Fact]
        public async Task ReferenceCannotAccidentallyResetAnExistingCandidate()
        {
            using (var rig = new Rig()) {
                await rig.Session.StartReferenceAsync(rig.Options()); await rig.Session.StartBalancingAsync();
                await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Session.StartReferenceAsync(rig.Options())); Assert.Equal(-2, rig.Co[0]); Assert.Equal(1, rig.BatchWrites);
            }
        }
        [Theory]
        [InlineData("New WHEA event")]
        [InlineData("Temperature stop reached")]
        [InlineData("Temperature source stale")]
        public async Task SafetyFailureStopsTheLoadBeforeAnyFurtherTrial(string problem)
        {
            using (var rig = new Rig()) {
                await rig.Session.StartReferenceAsync(rig.Options());
                rig.Clock.Tick = () => { if (rig.Runner.ActiveLoads > 0) rig.Safety.Problem = problem; };
                await rig.Session.StartBalancingAsync(); Assert.Equal(GuidedCoPhase.Faulted, rig.Session.Phase); Assert.Equal(0, rig.StepWrites); Assert.Equal(0, rig.Runner.ActiveLoads); Assert.True(rig.Recovery.IsPending); Assert.False(rig.Owner);
            }
        }
        [Fact]
        public async Task RejectedTrialStopsAtOneWriteAndKeepsRecovery()
        {
            using (var rig = new Rig()) {
                await rig.Session.StartReferenceAsync(rig.Options()); rig.RejectStep = true; await rig.Session.StartBalancingAsync();
                Assert.Equal(GuidedCoPhase.Faulted, rig.Session.Phase); Assert.Equal(1, rig.StepWrites); Assert.True(rig.Recovery.IsPending); Assert.Equal(0, rig.Runner.ActiveLoads);
                Assert.Contains("Partial / failed", File.ReadAllText(rig.JournalPath));
            }
        }
        [Fact]
        public async Task LoadExitDuringPreflightCannotForwardTheNextSetter()
        {
            using (var rig = new Rig()) {
                await rig.Session.StartReferenceAsync(rig.Options()); rig.BeforeStep = () => rig.Runner.ExitUnexpectedly();
                await rig.Session.StartBalancingAsync(); Assert.Equal(0, rig.StepWrites); Assert.Equal(GuidedCoPhase.Faulted, rig.Session.Phase); Assert.True(rig.Recovery.IsPending);
            }
        }
        [Fact]
        public async Task PausingAfterACompletedWriteRetainsTheCandidateAndAllowsCheckedResume()
        {
            using (var rig = new Rig()) {
                await rig.Session.StartReferenceAsync(rig.Options()); rig.AfterStep = () => rig.Session.Pause();
                await rig.Session.StartBalancingAsync(); Assert.Equal(GuidedCoPhase.Paused, rig.Session.Phase); Assert.Equal(1, rig.StepWrites); Assert.Equal(0, rig.Runner.ActiveLoads);
                rig.AfterStep = null; await rig.Session.ResumeAsync(); Assert.Equal(GuidedCoPhase.ReadyToValidate, rig.Session.Phase); Assert.Equal(2, rig.StepWrites);
            }
        }
        [Fact]
        public async Task JournalFailurePreventsAnOffsetSetter()
        {
            using (var rig = new Rig()) {
                await rig.Session.StartReferenceAsync(rig.Options()); File.Move(rig.JournalPath, rig.JournalPath + ".saved"); Directory.CreateDirectory(rig.JournalPath);
                await rig.Session.StartBalancingAsync(); Assert.Equal(GuidedCoPhase.Faulted, rig.Session.Phase); Assert.Equal(0, rig.StepWrites); Assert.True(rig.Recovery.IsPending);
            }
        }
        [Theory]
        [InlineData(1)] [InlineData(2)]
        public async Task ContextChangesPreventAnotherTrial(int change)
        {
            using (var rig = new Rig()) {
                await rig.Session.StartReferenceAsync(rig.Options());
                if (change == 1) rig.Ppt = 76; else rig.Co[1] = -1;
                await rig.Session.StartBalancingAsync(); Assert.Equal(GuidedCoPhase.Faulted, rig.Session.Phase); Assert.Equal(0, rig.StepWrites);
            }
        }
        [Fact]
        public async Task BoundsAndBudgetGiveExplicitPartialOutcomesWithoutOvershoot()
        {
            using (var rig = new Rig()) {
                await rig.Session.StartReferenceAsync(rig.Options(bound: -1)); await rig.Session.StartBalancingAsync();
                Assert.Equal(1, rig.StepWrites); Assert.Equal(-1, rig.Co[0]); Assert.Contains("Bounds", rig.Session.Status);
            }
            using (var rig = new Rig()) {
                await rig.Session.StartReferenceAsync(rig.Options(budgetSeconds: 5)); rig.Safety.TemperatureStable = false;
                await rig.Session.StartBalancingAsync(); Assert.Contains("Time budget", rig.Session.Status); Assert.Equal(GuidedCoPhase.ReadyToValidate, rig.Session.Phase); Assert.Equal(0, rig.StepWrites);
            }
        }
        [Fact]
        public async Task SavingFailureNeverClearsRecoveryAndRestoreVerifiesOriginalOffsets()
        {
            using (var rig = new Rig()) {
                await rig.Session.StartReferenceAsync(rig.Options()); await rig.Session.StartBalancingAsync(); await rig.Session.ValidateAsync();
                rig.Services.SaveProfile = (c, n) => throw new IOException("Profile save failed"); await rig.Session.KeepAsync("Experiment");
                Assert.Equal(GuidedCoPhase.Faulted, rig.Session.Phase); Assert.True(rig.Recovery.IsPending);
                await rig.Session.RestoreAsync(); Assert.Equal(GuidedCoPhase.Finished, rig.Session.Phase); Assert.Equal(-20, rig.Co[0]); Assert.Equal(-25, rig.Co[1]); Assert.False(rig.Recovery.IsPending);
            }
        }
        [Fact]
        public async Task MissingSourcePreventsResetAndWithinToleranceDoesNotChaseAnotherStep()
        {
            using (var rig = new Rig()) {
                rig.MissingSource = true; await rig.Session.StartReferenceAsync(rig.Options());
                Assert.Equal(GuidedCoPhase.Faulted, rig.Session.Phase); Assert.Equal(0, rig.BatchWrites); Assert.False(rig.Recovery.IsPending);
            }
            using (var rig = new Rig()) {
                await rig.Session.StartReferenceAsync(rig.Options()); rig.NoVidGap = true; await rig.Session.StartBalancingAsync();
                Assert.Equal(GuidedCoPhase.ReadyToValidate, rig.Session.Phase); Assert.Contains("Within measured VID tolerance", rig.Session.Status); Assert.Equal(0, rig.StepWrites);
            }
        }
        [Fact]
        public async Task IneffectiveTrialsStopInsteadOfChasingTheNegativeBound()
        {
            using (var rig = new Rig()) {
                await rig.Session.StartReferenceAsync(rig.Options(bound: -40)); rig.FreezeVidResponse = true;
                await rig.Session.StartBalancingAsync(); Assert.Equal(3, rig.StepWrites); Assert.Equal(-3, rig.Co[0]); Assert.Contains("No reproducible", rig.Session.Status);
                await rig.Session.StartBalancingAsync(); Assert.Equal(3, rig.StepWrites); Assert.Contains("review", rig.Session.Evidence.Cores.First(c => c.Slot == 0).Reason);
            }
        }
        [Fact]
        public async Task RestoreDuringBalancingWaitsForTheOwnedLoadAndRestoresOnce()
        {
            using (var rig = new Rig()) {
                await rig.Session.StartReferenceAsync(rig.Options()); Task restoring = null;
                rig.AfterStep = () => { if (restoring == null) restoring = rig.Session.RestoreAsync(); };
                await rig.Session.StartBalancingAsync(); Assert.NotNull(restoring); await restoring;
                Assert.Equal(GuidedCoPhase.Finished, rig.Session.Phase); Assert.Equal(0, rig.Runner.ActiveLoads); Assert.Equal(1, rig.StepWrites); Assert.Equal(-20, rig.Co[0]); Assert.False(rig.Recovery.IsPending);
            }
        }
        [Fact]
        public async Task UnsettledReferenceNeverEnablesBalancing()
        {
            using (var rig = new Rig()) {
                rig.Safety.TemperatureStable = false; await rig.Session.StartReferenceAsync(rig.Options());
                Assert.False(rig.Session.HasReference); Assert.Equal(GuidedCoPhase.Faulted, rig.Session.Phase); Assert.Equal(0, rig.Runner.Scored); Assert.True(rig.Recovery.IsPending);
            }
        }
        private sealed class BoundaryTransport : ITuningTransport
        {
            public int Writes, Reads; public Action AfterRead;
            public decimal Co = -20;
            public bool IsSupported(TuningRequest request, out string reason) { reason = null; return true; }
            public TuningReply Read(TuningRequest request) { Reads++; var reply = new TuningReply { Status = 1, Direct = true, Value = request.Kind == TuningKind.Scalar ? 1 : Co }; AfterRead?.Invoke(); return reply; }
            public TuningReply Write(TuningRequest request) { Writes++; Co = request.Value; return new TuningReply { Status = 1 }; }
        }
        [Fact]
        public void FinalTransportGuardBlocksASetterWhenHealthChangesDuringReads()
        {
            var inner = new BoundaryTransport(); string issue = null; inner.AfterRead = () => { if (inner.Reads == 2) issue = "New WHEA incident"; };
            var guarded = new GuardedCoTransport(inner, () => issue, CancellationToken.None);
            var result = NativeCoStepTransaction.Apply(guarded, new CoStepProposal(0, -20, "Synthetic"), CancellationToken.None);
            Assert.Equal(0, inner.Writes); Assert.Equal(TuningOutcome.Error, result.Rows.Single().Outcome); Assert.Contains("WHEA", result.Rows.Single().Reason);
        }
        [Fact]
        public void BatchGuardStopsTheRemainingResetWhenAnIncidentAppears()
        {
            var inner = new BoundaryTransport();
            var guard = new GuardedCoTransport(inner, () => inner.Writes > 0 ? "New hardware incident" : null, CancellationToken.None);
            var result = CoBatchTransaction.Apply(guard, new Dictionary<int, int> { [0] = -20, [1] = -20 }, new Dictionary<int, int> { [0] = 0, [1] = 0 }, CancellationToken.None);
            Assert.Equal(1, inner.Writes); Assert.Equal(TuningOutcome.Verified, result.Rows[0].Outcome); Assert.Equal(TuningOutcome.Error, result.Rows[1].Outcome);
        }
        [Fact]
        public async Task CancellingDuringSafetyInitializationLeavesNoOffsetChangesOrLeakedObserver()
        {
            using (var rig = new Rig())
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim()) {
                rig.Services.CreateSafety = () => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); return rig.Safety; };
                var start = rig.Session.StartReferenceAsync(rig.Options()); Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                rig.Session.Pause(); release.Set(); await start;
                Assert.Equal(GuidedCoPhase.Ready, rig.Session.Phase); Assert.False(rig.Recovery.IsPending); Assert.Equal(0, rig.BatchWrites); Assert.Equal(0, rig.Runner.Scored); Assert.True(rig.Safety.Disposals > 0);
            }
        }
        [Fact]
        public void ExperimentalProfileUsesObservedCoAndNeverOverwritesAnExistingProfile()
        {
            var topology = new TopologySnapshot(new[] { new CoreTarget(0, 0, 0, 0, 0, 0) }, 1, "Synthetic", 0x1A);
            var observed = new NativeCoContext("Synthetic", topology.Signature, new Profile { CoMargins = new Dictionary<int, int> { [0] = 99 }, PptWatts = 999 }, new Dictionary<int, int> { [0] = -12 }, 1);
            var profile = GuidedCoProfiles.Create(observed, topology, "Experiment");
            Assert.Equal(-12, profile.CoMargins[0]); Assert.Null(profile.PptWatts); Assert.Null(profile.Fmax); Assert.Null(profile.CurveShaperTiers); Assert.Equal(2, profile.SchemaVersion);
            var manager = new ProfileManager(Path.Combine(Path.GetTempPath(), "SMUDebugTool-profile-" + Guid.NewGuid().ToString("N")));
            manager.SaveNew(profile); profile.CoMargins[0] = -13; Assert.Throws<IOException>(() => manager.SaveNew(profile)); Assert.Equal(-12, manager.Load("Experiment").CoMargins[0]);
        }
        [Fact]
        public async Task OpeningGuidedUiAutomaticallyPreparesWithoutHardwareWritesOrWorkloadLaunch()
        {
            string fixture = ManagedWorkloadTests.FixtureExecutable; var done = new TaskCompletionSource<bool>();
            var thread = new Thread(() => {
                try {
                    using (var rig = new Rig()) {
                        string ownExecutable = Path.Combine(Path.GetDirectoryName(rig.JournalPath), "Cinebench.exe"); File.Copy(fixture, ownExecutable);
                        int sources = 0, targets = 0, safetyStarts = 0;
                        rig.Services.InitialExecutable = ownExecutable;
                        rig.Services.CreateSafety = () => { safetyStarts++; throw new Exception("Must not observe runtime safety on opening"); };
                        rig.Services.CreateSource = () => { sources++; return Task.FromResult<Func<CancellationToken, IReadOnlyList<CoreVidReading>>>(ct => rig.Latest()); };
                        rig.Services.LoadTargets = () => { targets++; return Task.FromResult<IReadOnlyList<WorkloadTarget>>(new[] { new WorkloadTarget("All processors", 0) }); };
                        using (var form = new GuidedCoOptimizerForm(rig.Services)) {
                            form.Opacity = 0; form.ShowInTaskbar = false; form.Font = new Font(form.Font.FontFamily, form.Font.Size * 2); form.Show();
                            var primary = Descendants(form).OfType<Button>().Single(b => b.Name == "guidedPrimary"); var wait = Stopwatch.StartNew();
                            while (!primary.Enabled && wait.ElapsedMilliseconds < 8000) { Application.DoEvents(); Thread.Sleep(5); }
                            Assert.True(primary.Enabled); Assert.Equal(1, sources); Assert.Equal(1, targets); Assert.Equal(0, safetyStarts); Assert.Equal(0, rig.BatchWrites); Assert.Equal(0, rig.StepWrites); Assert.Equal(0, rig.Runner.Scored);
                            Assert.DoesNotContain(Descendants(form).OfType<Button>(), b => b.Visible && b.Text == "Run candidate");
                            form.Close();
                        }
                    }
                    done.SetResult(true);
                } catch (Exception ex) { done.SetException(ex); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA); thread.Start(); await done.Task;
        }
        private static IEnumerable<Control> Descendants(Control root)
        { foreach (Control child in root.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
        [Fact]
        public async Task PausingDoesNotResetTheTotalBalancingBudget()
        {
            using (var rig = new Rig()) {
                await rig.Session.StartReferenceAsync(rig.Options(budgetSeconds: 10)); rig.Safety.TemperatureStable = false;
                long began = rig.Clock.Timestamp;
                rig.Clock.Tick = () => { if ((rig.Clock.Timestamp - began) / (double)Stopwatch.Frequency >= 3) rig.Session.Pause(); };
                await rig.Session.StartBalancingAsync(); Assert.Equal(GuidedCoPhase.Paused, rig.Session.Phase);
                rig.Clock.Tick = null; await rig.Session.ResumeAsync();
                Assert.Contains("Time budget", rig.Session.Status); Assert.InRange((rig.Clock.Timestamp - began) / (double)Stopwatch.Frequency, 10, 11); Assert.Equal(0, rig.StepWrites);
            }
        }
        [Fact]
        public async Task IneligiblePeersGiveAnInconclusiveOutcomeAndRecoveryNeedsFreshReview()
        {
            using (var rig = new Rig()) {
                rig.Co.Remove(2); await rig.Session.StartReferenceAsync(rig.Options()); await rig.Session.StartBalancingAsync();
                Assert.Contains("Insufficient comparable", rig.Session.Status); Assert.Equal(0, rig.StepWrites);
                Assert.All(rig.Session.Evidence.Cores, c => Assert.Contains("two comparable peers", c.Reason));
                rig.Session.NoteHardwareWrite(); await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Session.KeepAsync());
                await rig.Session.ReadCurrentAsync(); Assert.True(rig.Session.RecoveryReviewed); await rig.Session.KeepAsync(); Assert.False(rig.Recovery.IsPending);
            }
        }
        [Fact]
        public async Task EarlierVersionRecoveryCanRestoreWithoutReplayingAPhaseJournal()
        {
            using (var rig = new Rig()) {
                rig.Recovery.Preserve(rig.Context()); rig.Co[0] = -99;
                using (var reopened = new GuidedCoSession(rig.Services, rig.Records, rig.Latest, rig.Window, () => null, () => { }, CancellationToken.None, rig.Clock)) {
                    Assert.Equal(GuidedCoPhase.Recovery, reopened.Phase); Assert.False(reopened.HasReference); Assert.Equal(0, rig.Runner.ContinuousStarts);
                    await reopened.RestoreAsync(); Assert.Equal(GuidedCoPhase.Finished, reopened.Phase); Assert.Equal(-20, rig.Co[0]); Assert.Equal(0, rig.StepWrites);
                }
            }
        }
        [Fact]
        public void TemperatureWindowRejectsStalenessAndThermalDrift()
        {
            var window = new CoTemperatureWindow(); long now = Stopwatch.GetTimestamp();
            Assert.NotNull(window.Issue(now, 90));
            for (int i = 0; i < 5; i++) window.Add(now + i * Stopwatch.Frequency, 65 + i / 10m);
            Assert.True(window.Stable); Assert.Null(window.Issue(now + 4 * Stopwatch.Frequency, 90)); Assert.NotNull(window.Issue(now + 7 * Stopwatch.Frequency, 90));
            window.Add(now + 5 * Stopwatch.Frequency, 92); Assert.False(window.Stable); Assert.Contains("stop", window.Issue(now + 5 * Stopwatch.Frequency, 90));
        }
        [Fact]
        public void ComparisonNeverCallsOverlappingOrIncompleteScoresAnImprovement()
        {
            Assert.Contains("pending", GuidedCoSession.Compare(new[] { 100m }, new[] { 110m }));
            Assert.Contains("no demonstrated", GuidedCoSession.Compare(new[] { 98m, 100m, 102m }, new[] { 100m, 101m, 102m }));
            Assert.Contains("below", GuidedCoSession.Compare(new[] { 100m, 102m }, new[] { 90m, 92m }));
            Assert.Contains("confirm", GuidedCoSession.Compare(new[] { 100m, 102m }, new[] { 110m, 112m }));
        }
    }
}
