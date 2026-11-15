using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;
using ZenStatesDebugTool;
using ZenStatesDebugTool.Profiles;

namespace ProfileCore.Tests
{
    public class PerformanceTests
    {
        private static CoreVidReading Row(int slot, long timestamp, decimal vid = 1.10m, decimal clock = 5000, double effective = 4950, string issue = null, int? ccd = null)
            => new CoreVidReading(slot, ccd ?? slot / 8, "Synthetic", timestamp, 1, vid, clock, effective, issue);
        [Fact]
        public void SinglePassEligibilityMatchesTheFrozenContractForShuffledAndInvalidWindows()
        {
            var random = new Random(1719); long now = Stopwatch.GetTimestamp();
            for (int run = 0; run < 160; run++) {
                int variant = run % 16;
                var offsets = Enumerable.Range(0, 16).ToDictionary(c => c, c => variant == 9 && c == 0 ? -40 : variant == 10 && c == 0 ? 1 : 0);
                var rows = new List<CoreVidReading>();
                for (int frame = 0; frame < 31; frame++) for (int core = 0; core < 16; core++) {
                    if (variant == 5 && core == 1) continue;
                    long stamp = now - (long)((30 - frame) * .1 * Stopwatch.Frequency);
                    if (variant == 6) stamp -= Stopwatch.Frequency;
                    decimal vid = (core % 8 == 0 ? 1.15m : 1.10m) + random.Next(3) * .00625m;
                    decimal mhz = variant == 7 && core == 0 && frame == 0 ? 4500 : 5000;
                    rows.Add(Row(core, stamp, vid, mhz, variant == 4 && frame == 0 ? 20 : 4950,
                        variant == 2 && core == 0 && frame == 30 || variant == 3 && core == 0 && frame == 0 ? "Unavailable" : null,
                        variant == 8 && core == 0 && frame == 0 ? 1 : (int?)null));
                }
                if (variant == 11) rows.Add(Row(127, now));
                if (variant == 12) rows.Add(Row(127, now + Stopwatch.Frequency));
                if (variant == 13) rows.Add(new CoreVidReading(0, 0, "Synthetic", now - Stopwatch.Frequency, null, null, null, null, null));
                if (variant == 14) rows.Add(Row(0, now, issue: "Last equal-timestamp input wins"));
                if (variant == 15) rows = rows.Where(r => r.Timestamp >= now - Stopwatch.Frequency).ToList();
                rows = rows.OrderBy(_ => random.Next()).ToList();
                var blocked = new HashSet<int>(run % 2 == 0 ? new[] { 0 } : new int[0]);
                var expected = ReferenceEligibility.Analyze(rows, offsets, -40, now, blocked);
                var actual = CoEligibility.Analyze(rows, offsets, -40, now, blocked);
                Assert.Equal(expected.StopReason, actual.StopReason); Assert.Equal(expected.Proposal?.Slot, actual.Proposal?.Slot);
                Assert.Equal(expected.Proposal?.Requested, actual.Proposal?.Requested);
                for (int i = 0; i < expected.Cores.Count; i++) {
                    var a = actual.Cores[i]; var e = expected.Cores[i];
                    Assert.Equal(e.Slot, a.Slot); Assert.Equal(e.Ccd, a.Ccd); Assert.Equal(e.Co, a.Co); Assert.Equal(e.Samples, a.Samples);
                    Assert.Equal(e.Vid, a.Vid); Assert.Equal(e.Mhz, a.Mhz); Assert.Equal(e.PeerMedian, a.PeerMedian);
                    Assert.Equal(e.Reason, a.Reason); Assert.Equal(e.Comparable, a.Comparable);
                }
            }
        }
        [Fact]
        public void CachedWindowsAreImmutableAndCannotMakeStaleEvidenceEligible()
        {
            long now = Stopwatch.GetTimestamp(); var window = new CoreVidWindow();
            window.Add(Enumerable.Range(0, 31).SelectMany(f => Enumerable.Range(0, 3).Select(c => Row(c, now - (long)((30 - f) * .1 * Stopwatch.Frequency), c == 0 ? 1.15m : 1.1m))), now);
            var first = window.Recent; Assert.Same(first, window.Recent);
            Assert.Throws<NotSupportedException>(() => ((IList<CoreVidReading>)first)[0] = Row(99, now));
            var offsets = new Dictionary<int, int> { [0] = 0, [1] = 0, [2] = 0 };
            Assert.NotNull(CoEligibility.Analyze(first, offsets, -40, now).Proposal);
            Assert.Null(CoEligibility.Analyze(window.Recent, offsets, -40, now + Stopwatch.Frequency).Proposal);
            window.Add(new CoreVidReading[0], now + Stopwatch.Frequency * 6); Assert.Empty(window.Recent); Assert.Equal(93, first.Count);
            window.Add(new[] { Row(0, now + Stopwatch.Frequency * 6) }, now + Stopwatch.Frequency * 6); Assert.Single(window.Recent);
            window.Clear(); Assert.Empty(window.Recent);
        }
        [Fact]
        public void LatestFrameCannotBeMutatedThroughItsSourceOrPublishedSnapshot()
        {
            var rows = new[] { Row(0, Stopwatch.GetTimestamp()) }; int reads = 0;
            using (var monitor = new NativeVidMonitor(ct => {
                if (Interlocked.Increment(ref reads) > 1) { ct.WaitHandle.WaitOne(); ct.ThrowIfCancellationRequested(); }
                return rows;
            }, new NativeCoSession())) {
                monitor.Start(20); Assert.True(SpinWait.SpinUntil(() => monitor.Latest.Count == 1, 3000));
                var snapshot = monitor.Latest; rows[0] = Row(99, Stopwatch.GetTimestamp());
                Assert.Equal(0, snapshot[0].Slot); Assert.Same(snapshot, monitor.Latest);
                Assert.Throws<NotSupportedException>(() => ((IList<CoreVidReading>)snapshot)[0] = rows[0]);
            }
        }
        [Fact]
        public async Task DisplayProbeNeverWaitsForSlowIoAndDoesNotPublishAfterClose()
        {
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim()) {
                var probe = new BackgroundStatusProbe(() => { entered.Set(); release.Wait(); return true; }, false);
                var pending = probe.Refresh(); Assert.True(entered.Wait(3000)); Assert.False(probe.Value);
                Assert.Same(pending, probe.Refresh()); probe.Dispose(); release.Set(); await pending; Assert.False(probe.Value);
            }
        }
        [Fact]
        public async Task DisplayProbeThrottlesAndTreatsReadErrorsConservatively()
        {
            long now = 0; int reads = 0;
            using (var probe = new BackgroundStatusProbe(() => { reads++; throw new IOException(); }, false, 1000, () => now)) {
                await probe.Refresh(); Assert.True(probe.Value); await probe.Refresh(); Assert.Equal(1, reads);
                now += Stopwatch.Frequency; await probe.Refresh(); Assert.Equal(2, reads);
                probe.Invalidate(); await probe.Refresh(); Assert.Equal(3, reads);
            }
        }
        [Fact]
        public void CooperativeScanReleasesTheGateBetweenBoundedBatchesAndStopsOnContention()
        {
            var sync = new object(); var reads = new List<int>(); int batches = 0, yields = 0;
            bool complete = CooperativeScan.ReadBatches(130, 64, i => { Assert.True(Monitor.IsEntered(sync)); reads.Add(i); }, batch => {
                if (++batches == 3) return false;
                lock (sync) batch(); return true;
            }, CancellationToken.None, () => { Assert.False(Monitor.IsEntered(sync)); yields++; });
            Assert.False(complete); Assert.Equal(Enumerable.Range(0, 128), reads); Assert.Equal(2, yields);
        }
        [Fact]
        public void CooperativeScanCancelsBeforeTheNextNativeRead()
        {
            using (var closing = new CancellationTokenSource()) {
                int reads = 0;
                Assert.ThrowsAny<OperationCanceledException>(() => CooperativeScan.ReadBatches(100, 64, i => { reads++; if (i == 5) closing.Cancel(); }, action => { action(); return true; }, closing.Token));
                Assert.Equal(6, reads);
            }
        }
        [Fact]
        public void StreamedExportPreservesSchemaAndOversizeFailurePreservesTheOriginalFile()
        {
            string dir = Path.Combine(Path.GetTempPath(), "sdt-export-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "report.json");
            try {
                var document = new { SchemaVersion = 2, Unicode = "电压与温度", Nested = new[] { 1, 2, 3 } };
                AtomicJsonExport.Write(path, document, 4096);
                Assert.True(JToken.DeepEquals(JToken.Parse(JsonConvert.SerializeObject(document)), JToken.Parse(File.ReadAllText(path))));
                int exactBytes = System.Text.Encoding.UTF8.GetByteCount(JsonConvert.SerializeObject(document, Formatting.Indented));
                AtomicJsonExport.Write(path, document, exactBytes); Assert.Equal(exactBytes, new FileInfo(path).Length);
                string original = File.ReadAllText(path);
                var error = Record.Exception(() => AtomicJsonExport.Write(path, new { Payload = new string('电', 10000) }, 100));
                Assert.NotNull(error); Assert.Contains("UTF-8 byte limit", error.ToString()); Assert.Equal(original, File.ReadAllText(path)); Assert.Single(Directory.GetFiles(dir));
            } finally { if (File.Exists(path)) File.Delete(path); Directory.Delete(dir); }
        }
        [Fact]
        public void ExportSnapshotsRemainDetachedWhileTheJournalAndRunContinue()
        {
            string dir = Path.Combine(Path.GetTempPath(), "sdt-snapshot-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "journal.json");
            try {
                var context = new NativeCoContext("Synthetic", "Synthetic", new Profile(), new Dictionary<int, int> { [0] = 0 }, 1);
                var journal = new CoTrialJournal(path); journal.Start(context); journal.Intent("Synthetic", context.ObservedCo, context.ObservedCo);
                var journalSnapshot = journal.ExportSnapshot(); string initial = JsonConvert.SerializeObject(journalSnapshot);
                journal.Phase("Changed later"); Assert.Equal(initial, JsonConvert.SerializeObject(journalSnapshot));
                Assert.False(JToken.DeepEquals(JToken.Parse(initial), JToken.Parse(journal.Export())));
                var session = new NativeCoSession(); session.Begin("Baseline", "Synthetic", context, DateTimeOffset.UtcNow);
                session.Add(new[] { Row(0, Stopwatch.GetTimestamp()) }); var snapshot = session.ExportSnapshot(); initial = JsonConvert.SerializeObject(snapshot);
                session.NoteChange("Later change"); session.Add(new[] { Row(0, Stopwatch.GetTimestamp(), 1.2m) }); Assert.True(session.ActiveHasChanges); Assert.Equal(1, session.TrialCount);
                Assert.Equal(initial, JsonConvert.SerializeObject(snapshot)); session.Interrupt(); session.Reset(); Assert.Equal(0, session.TrialCount);
            } finally { if (File.Exists(path)) File.Delete(path); Directory.Delete(dir); }
        }
    }
    [CollectionDefinition("Performance UI", DisableParallelization = true)] public sealed class PerformanceUiCollection { }
    [Collection("Performance UI")]
    public class LiveGridTests
    {
        [Fact]
        public async Task ChangedCellUpdatesPreserveRowSelectionAndRejectInvalidFrames()
        {
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() => {
                try {
                    using (var form = new Form { Opacity = 0 })
                    using (var grid = new DataGridView { Dock = DockStyle.Fill, AllowUserToAddRows = false, ReadOnly = true, RowHeadersVisible = false }) {
                        grid.Columns.Add("Core", "Core"); grid.Columns.Add("Value", "Value"); form.Controls.Add(grid); form.Show();
                        var display = new LiveGrid(grid); var rows = Enumerable.Range(0, 32).Select(i => new LiveGridRow(i, i, "Value " + i)).ToArray();
                        Assert.Equal(64, display.Update(rows)); grid.CurrentCell = grid.Rows[10].Cells[1]; grid.FirstDisplayedScrollingRowIndex = 8;
                        var selected = grid.CurrentRow; int first = grid.FirstDisplayedScrollingRowIndex;
                        Assert.Equal(0, display.Update(rows)); Assert.Same(selected, grid.CurrentRow); Assert.Equal(first, grid.FirstDisplayedScrollingRowIndex);
                        rows[10] = new LiveGridRow(10, 10, "Updated"); Assert.Equal(1, display.Update(rows)); Assert.Same(selected, grid.CurrentRow); Assert.Equal(first, grid.FirstDisplayedScrollingRowIndex);
                        Assert.Throws<ArgumentException>(() => display.Update(new[] { rows[0], rows[0] })); Assert.Equal(32, grid.Rows.Count);
                        display.Update(rows.Skip(1)); Assert.Equal(10, grid.CurrentRow.Tag);
                    }
                    done.SetResult(true);
                } catch (Exception ex) { done.SetException(ex); }
            }) { IsBackground = true }; thread.SetApartmentState(ApartmentState.STA); thread.Start();
            Assert.Same(done.Task, await Task.WhenAny(done.Task, Task.Delay(15000))); await done.Task;
        }
    }
}
