using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using Xunit;
using ZenStatesDebugTool;

namespace ProfileCore.Tests
{
    public class StartupTests
    {
        [Fact]
        public async Task OptionalDiscoveryStartsOnlyOnDemandAndCoalescesConcurrentSelections()
        {
            int reads = 0;
            using (var release = new ManualResetEventSlim()) {
                var work = new DeferredStartupWork<int>(() => { Interlocked.Increment(ref reads); release.Wait(); return 42; });
                Assert.Equal(0, reads);
                var first = work.GetAsync(CancellationToken.None); var second = work.GetAsync(CancellationToken.None);
                Assert.Same(first, second); Assert.False(first.IsCompleted);
                release.Set(); Assert.Equal(42, await first); Assert.Equal(1, reads);
                Assert.Same(first, work.GetAsync(CancellationToken.None));
            }
        }
        [Fact]
        public async Task FailedOptionalDiscoveryCanRetry()
        {
            int calls = 0;
            var work = new DeferredStartupWork<int>(() => { if (++calls == 1) throw new IOException("Provider unavailable"); return 7; });
            await Assert.ThrowsAsync<IOException>(() => work.GetAsync(CancellationToken.None));
            Assert.Equal(7, await work.GetAsync(CancellationToken.None)); Assert.Equal(2, calls);
        }
        [Fact]
        public async Task ClosingDuringDiscoveryDiscardsItsResult()
        {
            using (var started = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            using (var closing = new CancellationTokenSource()) {
                var work = new DeferredStartupWork<int>(() => { started.Set(); release.Wait(); return 7; });
                var pending = work.GetAsync(closing.Token); Assert.True(started.Wait(3000)); closing.Cancel(); release.Set();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work.GetAsync(closing.Token));
            }
        }
        [Theory]
        [InlineData(true, false)]
        [InlineData(false, true)]
        public void DriverStartupOnlyRecordsAProvablyNewInstallation(bool present, bool shouldRecord)
        {
            int probes = 0, creations = 0, records = 0;
            int result = DriverSession.CreateWithOwnership(() => { probes++; return present; }, () => { creations++; return 5; }, wasAbsent => { Assert.True(wasAbsent); records++; });
            Assert.Equal(5, result); Assert.Equal(1, probes); Assert.Equal(1, creations); Assert.Equal(shouldRecord ? 1 : 0, records);
        }
        [Fact]
        public void UnreadablePresenceNeverAdoptsAnExistingDriver()
        {
            Assert.Equal(5, DriverSession.CreateWithOwnership<int>(() => throw new UnauthorizedAccessException(), () => 5, _ => throw new Exception("Must not claim ownership")));
        }
        [Fact]
        public void FailedCpuCreationCannotRecordAnInstallation()
        {
            bool recorded = false;
            Assert.Throws<IOException>(() => DriverSession.CreateWithOwnership<int>(() => false, () => throw new IOException("Driver unavailable"), _ => recorded = true));
            Assert.False(recorded);
        }
        [Fact]
        public void OwnershipRecordFailureDoesNotLeakASuccessfullyCreatedCpu()
        {
            var cpu = new object(); Assert.Same(cpu, DriverSession.CreateWithOwnership(() => false, () => cpu, _ => throw new IOException("Registry is read-only")));
        }
        [Fact]
        public void FastOptionsKeepCoreAccessAndDisableUnusedInventoryWithoutPolling()
        {
            var fast = CoreLibraryAdapter.CreateOptions();
            Assert.True(fast.IoModule.Enabled); Assert.True(fast.Wmi.Enabled);
            Assert.False(fast.Timings.Enabled); Assert.False(fast.Aod.Enabled); Assert.False(fast.Sensors.Enabled);
            var full = CoreLibraryAdapter.CreateOptions(includeOptionalInventory: true);
            Assert.True(full.Timings.Enabled); Assert.True(full.Aod.Enabled); Assert.True(full.Sensors.Enabled);
            Assert.False(full.Timings.Autorefresh); Assert.False(full.Sensors.Autorefresh);
        }
        [Fact]
        public async Task TimingReportDistinguishesMilestonesFromOverlappingDurationsAndIsDetached()
        {
            long tick = 0; var timings = new StartupTimings(() => tick, 1000);
            timings.Mark("Program entry");
            using (timings.Measure("CPU total")) { tick = 10; using (timings.Measure("SMU")) { tick = 50; } tick = 70; }
            timings.Mark("Main window shown");
            var first = timings.Snapshot(); Assert.Equal(4, first.Count); Assert.Null(first[0].DurationMs);
            Assert.Equal(70, first.Single(s => s.Stage == "CPU total").DurationMs); Assert.Equal(40, first.Single(s => s.Stage == "SMU").DurationMs);
            first[0].Stage = "tampered"; Assert.Equal("Program entry", timings.Snapshot()[0].Stage);
            string dir = Path.Combine(Path.GetTempPath(), "sdt-startup-" + Guid.NewGuid().ToString("N")); string path = Path.Combine(dir, "timing.json");
            try {
                await timings.PersistAsync(path); timings.Mark("Tuning controls ready"); await timings.PersistAsync(path);
                var json = JObject.Parse(File.ReadAllText(path)); Assert.Equal(5, json["Stages"].Count()); Assert.Equal(1, json["SchemaVersion"].Value<int>());
                Assert.Single(Directory.GetFiles(dir));
                await timings.PersistAsync(dir); // A report failure must not fail startup.
            } finally { if (File.Exists(path)) File.Delete(path); if (Directory.Exists(dir)) Directory.Delete(dir); }
        }
    }
}
