using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xunit;
using ZenStatesDebugTool;
using ZenStatesDebugTool.Profiles;

namespace ProfileCore.Tests
{
    public class NativeCoTests
    {
        private static TopologySnapshot Topology(params int[] slots) => new TopologySnapshot(slots.Select((s, i) => new CoreTarget(s, i, s / 8, 0, s % 8, (uint)(s / 8) << 28 | (uint)(s % 8) << 20)), slots.Max() + 1, "Synthetic", 0x1A);
        [Theory]
        [InlineData(0)] [InlineData(40)] [InlineData(100)] [InlineData(200)]
        public void Zen5StatusVidIsLegacyRequestEncodingNotSvi3RailCode(int code)
        {
            ulong raw = 1000UL | (ulong)code << 14;
            Assert.True(NativeVidCodec.Decode(raw, 0x1A, out decimal vid, out decimal mhz));
            Assert.Equal(1.55m - code * 0.00625m, vid); Assert.Equal(5000, mhz);
        }
        [Theory]
        [InlineData(0UL, 0x1AU)] [InlineData(ulong.MaxValue, 0x1AU)] [InlineData(1000UL, 0x17U)]
        [InlineData(1000UL | 250UL << 14, 0x1AU)] [InlineData(4095UL, 0x1AU)]
        public void InvalidStatusCannotBecomeMeasuredZero(ulong raw, uint family) => Assert.False(NativeVidCodec.Decode(raw, family, out _, out _));
        [Fact]
        public void Zen4ClockUsesDividerWhileVidRemainsStatusRequest()
        {
            Assert.True(NativeVidCodec.Decode(200UL | 8UL << 8 | 80UL << 14, 0x19, out decimal vid, out decimal mhz));
            Assert.Equal(5000, mhz); Assert.Equal(1.05m, vid);
        }
        [Fact]
        public void PhysicalMappingUsesApicIdentityNotOsEnumerationOrder()
        {
            var map = NativeCoreMap.Resolve(Topology(0, 8), new[] {
                new NativeThreadIdentity(new ProcessorTarget(1, 0), 17, 8, 2), new NativeThreadIdentity(new ProcessorTarget(0, 3), 0, 0, 2),
                new NativeThreadIdentity(new ProcessorTarget(0, 1), 16, 8, 2), new NativeThreadIdentity(new ProcessorTarget(0, 7), 1, 0, 2) });
            Assert.Equal(new[] { 0, 8 }, map.Select(m => m.Core.Slot));
            Assert.Equal(3, map[0].Processor.Processor); Assert.Equal(1, map[1].Processor.Processor);
        }
        [Theory]
        [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
        public void AmbiguousOrMissingSiblingsCannotAcquireWritableBindings(int fault)
        {
            var identities = new List<NativeThreadIdentity> {
                new NativeThreadIdentity(new ProcessorTarget(0, 0), 0, 0, 2), new NativeThreadIdentity(new ProcessorTarget(0, 1), 1, 0, 2) };
            if (fault == 0) identities.RemoveAt(1);
            if (fault == 1) identities[1] = new NativeThreadIdentity(new ProcessorTarget(0, 1), 0, 0, 2);
            if (fault == 2) identities[1] = new NativeThreadIdentity(new ProcessorTarget(0, 1), 1, 8, 2);
            if (fault == 3) identities[1] = new NativeThreadIdentity(new ProcessorTarget(0, 0), 1, 0, 2);
            Assert.Throws<InvalidOperationException>(() => NativeCoreMap.Resolve(Topology(0), identities));
        }
        private sealed class FakeAffinity : IProcessorAffinity
        {
            public int Pins, Restores; public bool ThrowRestore;
            private sealed class Scope : IDisposable { private readonly Action action; public Scope(Action action) { this.action = action; } public void Dispose() => action(); }
            public IDisposable Pin(ProcessorTarget target) { Pins++; return new Scope(() => { Restores++; if (ThrowRestore) throw new Exception("restore failed"); }); }
        }
        [Fact]
        public void ReaderRestoresAffinityAndNeverExposesFailedReadAsZero()
        {
            var pin = new FakeAffinity(); var binding = new NativeCoreBinding(Topology(0).Find(0), new ProcessorTarget(0, 0), 0);
            var reader = new NativeVidReader(new[] { binding }, pin, _ => null, 0x1A);
            var row = Assert.Single(reader.Read(CancellationToken.None));
            Assert.Equal(1, pin.Pins); Assert.Equal(1, pin.Restores); Assert.Null(row.VidVolts); Assert.Null(row.EffectiveMhz);
        }
        [Fact]
        public void AffinityRestoreFailureInvalidatesTheReadingAndPoisonsReader()
        {
            var pin = new FakeAffinity { ThrowRestore = true }; var binding = new NativeCoreBinding(Topology(0).Find(0), new ProcessorTarget(0, 0), 0);
            var reader = new NativeVidReader(new[] { binding }, pin, address => address == NativeVidCodec.StatusMsr ? (ulong?)(1000UL | 80UL << 14) : 100UL, 0x1A);
            var row = Assert.Single(reader.Read(CancellationToken.None)); Assert.Null(row.VidVolts); Assert.Contains("restore", row.Issue);
            Assert.Throws<InvalidOperationException>(() => reader.Read(CancellationToken.None));
        }
        [Fact]
        public void CancellationInsidePinRestoresAffinityBeforeStopping()
        {
            using (var stop = new CancellationTokenSource()) {
                var pin = new FakeAffinity(); var binding = new NativeCoreBinding(Topology(0).Find(0), new ProcessorTarget(0, 0), 0);
                var reader = new NativeVidReader(new[] { binding }, pin, _ => { stop.Cancel(); return 1000; }, 0x1A);
                Assert.Throws<OperationCanceledException>(() => reader.Read(stop.Token)); Assert.Equal(pin.Pins, pin.Restores);
            }
        }
        [Fact]
        public void EffectiveClockUsesElapsedCounterDeltaAndRejectsResetGapOrFailedRead()
        {
            var binding = new NativeCoreBinding(Topology(0).Find(0), new ProcessorTarget(0, 0), 0);
            long stamp = Stopwatch.Frequency; ulong counter = 1000; bool readable = true;
            var reader = new NativeVidReader(new[] { binding }, new FakeAffinity(),
                address => !readable ? null : address == NativeVidCodec.StatusMsr ? (ulong?)(1000UL | 80UL << 14) : counter, 0x1A, () => stamp);
            Assert.Null(Assert.Single(reader.Read(CancellationToken.None)).EffectiveMhz);
            stamp += Stopwatch.Frequency / 10; counter += 500000000;
            Assert.InRange(Assert.Single(reader.Read(CancellationToken.None)).EffectiveMhz.Value, 4999.99, 5000.01);
            stamp += Stopwatch.Frequency / 10; counter = 0;
            Assert.Null(Assert.Single(reader.Read(CancellationToken.None)).EffectiveMhz);
            stamp += 3 * Stopwatch.Frequency; counter += 15000000000;
            Assert.Null(Assert.Single(reader.Read(CancellationToken.None)).EffectiveMhz);
            readable = false; stamp += Stopwatch.Frequency / 10;
            Assert.Null(Assert.Single(reader.Read(CancellationToken.None)).VidVolts);
            readable = true; stamp += Stopwatch.Frequency / 10; counter += 500000000;
            Assert.Null(Assert.Single(reader.Read(CancellationToken.None)).EffectiveMhz);
        }
        [Fact]
        public void MonitorDiscardsInFlightFrameAfterClearAndCancelsWithoutOverlappingReads()
        {
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            using (var second = new ManualResetEventSlim())
            using (var exited = new ManualResetEventSlim()) {
                int calls = 0, active = 0, maximum = 0;
                var monitor = new NativeVidMonitor(token => {
                    int running = Interlocked.Increment(ref active); maximum = Math.Max(maximum, running);
                    try {
                        if (Interlocked.Increment(ref calls) == 1) {
                            entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5)));
                            return new[] { new CoreVidReading(0, 0, "core:0", Stopwatch.GetTimestamp(), 1, 1.1m, 5000, 4950, null) };
                        }
                        second.Set(); token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); return new CoreVidReading[0];
                    } finally { Interlocked.Decrement(ref active); if (token.IsCancellationRequested) exited.Set(); }
                }, new NativeCoSession());
                try {
                    monitor.Start(20); Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
                    monitor.Clear(); release.Set(); Assert.True(second.Wait(TimeSpan.FromSeconds(5)));
                    Assert.Empty(monitor.Latest); Assert.Empty(monitor.Window); Assert.Equal(1, maximum);
                } finally { release.Set(); monitor.Dispose(); Assert.True(exited.Wait(TimeSpan.FromSeconds(5))); }
            }
        }
        private static List<CoreVidReading> Stable(long now, int count = 31)
        {
            return Enumerable.Range(0, count).SelectMany(i => Enumerable.Range(0, 3).Select(core => new CoreVidReading(core, 0, "core:" + core,
                now - (long)((count - 1 - i) * 0.1 * Stopwatch.Frequency), 1, core == 0 ? 1.15m : 1.1m, 5000, 4950, null))).ToList();
        }
        private static Dictionary<int, int> Offsets() => new Dictionary<int, int> { [0] = -20, [1] = -25, [2] = -30 };
        [Fact]
        public void PlannerMakesOnlyOneBoundedStepFromFreshDirectCo()
        {
            long now = Stopwatch.GetTimestamp(); var proposal = CoStepPlanner.Suggest(Stable(now), Offsets(), -40, now);
            Assert.Equal(0, proposal.Slot); Assert.Equal(-20, proposal.Previous); Assert.Equal(-21, proposal.Requested);
        }
        [Theory]
        [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
        public void PlannerBlocksStaleUnloadedUnstableSparseFailedOrBoundedData(int fault)
        {
            long now = Stopwatch.GetTimestamp(); var rows = Stable(now); var co = Offsets(); int bound = -40;
            if (fault == 0) now += Stopwatch.Frequency;
            if (fault == 1) rows = rows.Select(r => new CoreVidReading(r.Slot, r.Ccd, r.Identity, r.Timestamp, 1, r.VidVolts, 5000, 500, null)).ToList();
            if (fault == 2) rows = rows.Select((r, i) => new CoreVidReading(r.Slot, r.Ccd, r.Identity, r.Timestamp, 1, r.VidVolts, i % 2 == 0 ? 4000 : 5000, 4950, null)).ToList();
            if (fault == 3) rows = rows.Where(r => r.Slot != 2).ToList();
            if (fault == 4) rows.Add(new CoreVidReading(0, 0, "core:0", now, null, null, null, null, "read failed"));
            if (fault == 5) bound = -20;
            if (fault == 6) co.Remove(0);
            Assert.Null(CoStepPlanner.Suggest(rows, co, bound, now));
        }
        [Fact]
        public void PlannerDoesNotCompareDifferentCcdsOrVeryDifferentFrequencies()
        {
            long now = Stopwatch.GetTimestamp();
            var rows = Stable(now).Select(r => new CoreVidReading(r.Slot, r.Slot, r.Identity, r.Timestamp, 1, r.VidVolts, r.ReportedMhz, r.EffectiveMhz, null));
            Assert.Null(CoStepPlanner.Suggest(rows, Offsets(), -40, now));
        }
        private static NativeCoContext Context(int value = -20, int expected = 1) => new NativeCoContext("Synthetic", "topology", new Profile(), new Dictionary<int, int> { [0] = value }, expected);
        [Fact]
        public void NativeSessionRecordsDetachedMetricsAndTracksMixedRuns()
        {
            var session = new NativeCoSession(); var start = DateTimeOffset.UtcNow;
            session.Begin("Baseline", "Synthetic workload", Context(), start);
            session.Add(new[] { new CoreVidReading(0, 0, "core:0", Stopwatch.GetTimestamp(), 1, 1.1m, 5000, 4950, null) });
            session.NoteChange("accepted CO write"); session.MarkEnd(start.AddSeconds(10)); session.End(Context(-21), 100);
            var trial = Assert.Single(session.Trials); Assert.Contains("Mixed", trial.Context); Assert.Equal(1.1m, Assert.Single(trial.Cores).BusyMeanVid);
            trial.Cores.Clear(); Assert.Single(Assert.Single(session.Trials).Cores);
            Assert.Contains("no external imports", session.Export());
        }
        [Fact]
        public void NewSessionCannotDiscardAnActiveRunAndClearsCompletedHistory()
        {
            var session = new NativeCoSession(); var start = DateTimeOffset.UtcNow;
            session.Begin("Baseline", "Synthetic", Context(), start);
            Assert.Throws<InvalidOperationException>(() => session.Reset());
            session.MarkEnd(start.AddSeconds(1)); session.End(Context(), null); session.Reset(); Assert.Empty(session.Trials);
        }
        [Fact]
        public void PartialCoReadsCannotCountAsUnchangedEndpoints() => Assert.Contains("incomplete", NativeCoSession.Context(Context(expected: 2), Context(expected: 2)));
        private sealed class StepTransport : ITuningTransport
        {
            public int Writes, Reads; public decimal? Co = -20, Scalar = 2; public bool Direct = true;
            public bool IsSupported(TuningRequest r, out string reason) { reason = null; return true; }
            public TuningReply Read(TuningRequest r) { Reads++; return new TuningReply { Status = 1, Direct = Direct, Value = r.Kind == TuningKind.Scalar ? Scalar : Co }; }
            public TuningReply Write(TuningRequest r) { Writes++; Co = r.Value; return new TuningReply { Status = 1 }; }
        }
        [Fact]
        public void NativeStepTransactionChecksModeAndCurrentCoThenVerifiesOneWrite()
        {
            var transport = new StepTransport();
            var result = NativeCoStepTransaction.Apply(transport, new CoStepProposal(0, -20, "Synthetic"), CancellationToken.None);
            Assert.Equal(1, transport.Writes); Assert.Equal(3, transport.Reads); Assert.Equal(TuningOutcome.Verified, Assert.Single(result.Rows).Outcome); Assert.Equal(-21, transport.Co);
        }
        [Theory]
        [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
        public void ChangedCoUnknownModeOrDerivedReadSendsNoTuningCommand(int fault)
        {
            var transport = new StepTransport();
            if (fault == 0) transport.Co = -21;
            if (fault == 1) transport.Scalar = null;
            if (fault == 2) transport.Scalar = 0;
            if (fault == 3) transport.Direct = false;
            Assert.Throws<InvalidOperationException>(() => NativeCoStepTransaction.Apply(transport, new CoStepProposal(0, -20, "Synthetic"), CancellationToken.None));
            Assert.Equal(0, transport.Writes);
        }
        [Fact]
        public void CancelledTrialSendsNeitherReadsNorWrites()
        {
            var transport = new StepTransport();
            Assert.Throws<OperationCanceledException>(() => NativeCoStepTransaction.Apply(transport, new CoStepProposal(0, -20, "Synthetic"), new CancellationToken(true)));
            Assert.Equal(0, transport.Reads); Assert.Equal(0, transport.Writes);
        }
        [Fact]
        public void ChangedOtherSettingsPreventAttributingScoreDeltaToCo()
        {
            var session = new NativeCoSession(); var start = DateTimeOffset.UtcNow;
            session.Begin("Baseline", "Cinebench 2024", Context(), start); session.MarkEnd(start.AddSeconds(10)); session.End(Context(), 100);
            var changed = new NativeCoContext("Synthetic", "topology", new Profile { PptWatts = 200 }, new Dictionary<int, int> { [0] = -21 }, 1);
            session.Begin("Candidate", "Cinebench 2024", changed, start.AddSeconds(20)); session.MarkEnd(start.AddSeconds(30)); session.End(changed, 120);
            var result = NativeCoSession.Compare(session.Trials[0], session.Trials[1]); Assert.Contains("inconclusive", result); Assert.DoesNotContain("Score delta", result);
        }
        [Fact]
        public async Task WorkbenchOpeningAndStartingMonitoringNeverInvokesTuning()
        {
            var done = new TaskCompletionSource<bool>();
            var thread = new Thread(() => {
                try {
                    int writes = 0; var session = new NativeCoSession();
                    Func<CancellationToken, IReadOnlyList<CoreVidReading>> source = _ => new CoreVidReading[0];
                    using (var form = new NativeCoWorkbenchForm(session, () => Task.FromResult(source), () => Task.FromResult(Context()), _ => { writes++; return Task.FromResult(new TuningResult()); })) {
                        form.Font = new Font(form.Font.FontFamily, form.Font.Size * 2); form.CreateControl(); form.PerformLayout();
                        form.StartMonitorAsync().GetAwaiter().GetResult(); Assert.Equal(0, writes); Assert.False(session.IsRecording);
                        form.Close(); form.Dispose();
                    }
                    done.SetResult(true);
                } catch (Exception ex) { done.SetException(ex); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA); thread.Start(); await done.Task;
        }
    }
}
