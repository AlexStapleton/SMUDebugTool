using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using Xunit;
using ZenStatesDebugTool;
using ZenStatesDebugTool.Profiles;

namespace ProfileCore.Tests
{
    public class TopologySnapshotTests
    {
        private static CoreTarget Target(int slot, int logical = -1) => new CoreTarget(slot, logical < 0 ? slot : logical, slot / 8, 0, slot % 8, CoreTopology.EncodeCoreMarginBitmask(false, slot));
        [Fact]
        public void PhysicalBindingsSurviveReorderedLogicalEnumeration()
        {
            var first = new TopologySnapshot(new[] { Target(0), Target(3, 1), Target(8, 2) }, 16, "APOB", 0x1A);
            var reordered = new TopologySnapshot(new[] { Target(8), Target(0, 2), Target(3, 1) }, 16, "APOB", 0x1A);
            var profile = new Profile { CoMargins = new Dictionary<int, int> { [3] = -25, [8] = -15 } };
            first.Bind(profile);
            Assert.Equal(first.Signature, reordered.Signature);
            Assert.Null(reordered.Validate(profile));
            Assert.Equal("ccd:0/ccx:0/core:3", profile.CoreBindings[3]);
            Assert.Equal(0x10000000U, reordered.Find(8).Mask);
        }
        [Fact]
        public void ChangedTopologyOrTamperedBindingFailsPreflight()
        {
            var first = new TopologySnapshot(new[] { Target(0), Target(8) }, 16, "APOB", 0x1A);
            var profile = new Profile { CoMargins = new Dictionary<int, int> { [8] = -25 } };
            first.Bind(profile);
            Assert.NotNull(new TopologySnapshot(new[] { Target(0) }, 16, "APOB", 0x1A).Validate(profile));
            profile.CoreBindings[8] = "ccd:0/ccx:0/core:0";
            Assert.NotNull(first.Validate(profile));
        }
        [Fact]
        public void LegacyKeysArePhysicalSlotsNotListOffsets()
        {
            var snapshot = new TopologySnapshot(new[] { Target(3), Target(8, 1) }, 16, "APOB", 0x19);
            Assert.Null(snapshot.Find(0)); Assert.Equal(0x10000000U, snapshot.Find(8).Mask);
            Assert.Null(snapshot.Validate(new Profile { CoMargins = new Dictionary<int, int> { [8] = -20, [0] = -10 } }));
            Assert.NotNull(snapshot.Validate(new Profile { CoMargins = new Dictionary<int, int> { [16] = -20 } }));
        }
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public void DuplicateAndUnrepresentableMapsAreRejected(int variant)
        {
            CoreTarget bad = variant == 0 ? Target(0, 1) : variant == 1 ? new CoreTarget(1, 1, 0, 0, 1, 0) : new CoreTarget(128, 1, 16, 0, 0, 0x10000000);
            var snapshot = new TopologySnapshot(new[] { Target(0), bad }, 128, "APOB", 0x1A);
            Assert.NotNull(snapshot.Error); Assert.Empty(snapshot.Targets);
        }
        [Fact]
        public void ProfileRoundTripDoesNotRetargetLegacyKeys()
        {
            var snapshot = new TopologySnapshot(new[] { Target(8) }, 16, "APOB", 0x1A);
            var profile = new Profile { CoMargins = new Dictionary<int, int> { [8] = -25 } };
            snapshot.Bind(profile);
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(profile);
            var read = Newtonsoft.Json.JsonConvert.DeserializeObject<Profile>(json);
            Assert.Null(snapshot.Validate(read)); Assert.Equal(-25, read.CoMargins[8]); Assert.Equal(2, read.SchemaVersion);
            Assert.Equal(1, Newtonsoft.Json.JsonConvert.DeserializeObject<Profile>("{\"CoMargins\":{\"8\":-25}}").SchemaVersion);
        }
        [Fact]
        public void UnsupportedTopologyOnlyBlocksRequestsNeedingCoreTargets()
        {
            var snapshot = new TopologySnapshot(Array.Empty<CoreTarget>(), 8, "unsupported", 0x1A, "unknown map");
            Assert.Null(snapshot.Validate(new Profile { Fmax = 5000 }));
            Assert.NotNull(snapshot.Validate(new Profile { CoMargins = new Dictionary<int, int> { [0] = -20 } }));
        }
        [Fact]
        public void CoreOptionsDisableIndependentBackgroundPolling()
        {
            var options = CoreLibraryAdapter.CreateOptions();
            Assert.False(options.Sensors.Autorefresh); Assert.False(options.Timings.Autorefresh);
            Assert.False(options.Aod.Autorefresh); Assert.False(options.Wmi.Autorefresh);
        }
        [Fact]
        public void ReviewedConversionRequiresEveryPhysicalTargetAndNeverEditsSource()
        {
            var snapshot = new TopologySnapshot(new[] { Target(0), Target(8) }, 16, "APOB", 0x1A);
            var old = new Profile
            {
                SchemaVersion = 2,
                TopologySignature = "old",
                CoMargins = new Dictionary<int, int> { [3] = -25 },
                CoreBindings = new Dictionary<int, string> { [3] = "ccd:1/ccx:0/core:0" }
            };
            var review = new ProfileTargetReview(old, snapshot);
            Assert.True(review.CanConvert);
            var converted = review.ConvertAfterReview();
            Assert.Equal(-25, converted.CoMargins[8]); Assert.False(converted.CoMargins.ContainsKey(3));
            Assert.Equal(-25, old.CoMargins[3]); Assert.Equal("old", old.TopologySignature);
            old.CoreBindings[3] = "ccd:5/ccx:0/core:0";
            Assert.False(new ProfileTargetReview(old, snapshot).CanConvert);
        }
        [Fact]
        public void InvalidLoadedValuesAreNotClampedIntoValidRequests()
        {
            Assert.NotNull(TuningService.ValidateProfileInput(new Profile { Fmax = 5000.5M }));
            Assert.NotNull(TuningService.ValidateProfileInput(new Profile { PptWatts = 1001 }));
            Assert.Null(TuningService.ValidateProfileInput(new Profile { Fmax = 5950 }));
        }
        [Fact]
        public void OlderTwoComplexLayoutUsesItsExplicitCcxMask()
        {
            var snapshot = new TopologySnapshot(new[] { new CoreTarget(4, 0, 0, 1, 0, 0x01000000) }, 8, "APOB", 0x17);
            Assert.Null(snapshot.Error); Assert.Equal(0x01000000U, snapshot.Find(4).Mask);
            Assert.NotNull(new TopologySnapshot(new[] { new CoreTarget(4, 0, 0, 1, 0, 0x00400000) }, 8, "APOB", 0x17).Error);
        }
        private sealed class Preflight : ITuningTransport, IProfilePreflight
        {
            public string ValidateProfile(Profile p) => "topology changed";
            public bool IsSupported(TuningRequest r, out string reason) { throw new Exception("Must not consult hardware"); }
            public TuningReply Write(TuningRequest r) => throw new Exception("Must not write");
            public TuningReply Read(TuningRequest r) => throw new Exception("Must not read");
        }
        [Fact]
        public void BindingFailureStopsAnEntireMixedFeatureProfileBeforeIo()
        {
            var result = new ProfileApplier().Apply(new Profile
            {
                Fmax = 5950,
                PptWatts = 200,
                CoMargins = new Dictionary<int, int> { [0] = -25 }
            }, new Preflight());
            Assert.False(result.Success); Assert.Equal(TuningOutcome.Invalid, result.Rows.Single().Outcome);
        }
    }

    public class PStateVidWriterTests
    {
        private sealed class Affinity : IProcessorAffinity
        {
            public int Pins, Restores;
            public bool Fail;
            public IDisposable Pin(ProcessorTarget target) { if (Fail) throw new Win32Exception(5); Pins++; return new Scope(() => Restores++); }
        }
        private sealed class Scope : IDisposable { private readonly Action close; public Scope(Action close) { this.close = close; } public void Dispose() => close(); }
        private static readonly ProcessorTarget[] targets = { new ProcessorTarget(0, 0), new ProcessorTarget(1, 3) };
        [Theory]
        [InlineData(PStateVidEncoding.Svi2, 0U)]
        [InlineData(PStateVidEncoding.Svi2, 255U)]
        [InlineData(PStateVidEncoding.Svi3, 0U)]
        [InlineData(PStateVidEncoding.Svi3, 255U)]
        [InlineData(PStateVidEncoding.Svi3, 256U)]
        [InlineData(PStateVidEncoding.Svi3, 511U)]
        public void CodecPreservesEveryUnrelatedBit(PStateVidEncoding encoding, uint vid)
        {
            foreach (ulong raw in new[] { 0UL, ulong.MaxValue, 0xB6C7D8E9F1234567UL })
            {
                ulong updated = PStateVidMath.WithVid(raw, encoding, vid);
                Assert.Equal(vid, PStateVidMath.Decode(updated, encoding));
                Assert.Equal(raw & ~PStateVidMath.Mask(encoding), updated & ~PStateVidMath.Mask(encoding));
            }
        }
        [Theory]
        [InlineData(PStateVidEncoding.Unknown, 1U)]
        [InlineData(PStateVidEncoding.Svi2, 256U)]
        [InlineData(PStateVidEncoding.Svi3, 512U)]
        public void InvalidRequestPerformsNoReadsOrWrites(PStateVidEncoding encoding, uint vid)
        {
            var result = PStateVidWriter.Apply(targets, new Affinity(), 0, PStateLayout.Zen5, encoding, vid,
                a => throw new Exception("No read expected"), (a, v) => throw new Exception("No write expected"));
            Assert.Equal(PStateWriteStatus.InvalidInput, result.Status);
        }
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public void TransactionReportsFailuresAndRestoresAffinity(int failure)
        {
            var affinity = new Affinity { Fail = failure == 5 };
            ulong original = failure == 1 ? 0 : 0xB6C7D8E9F1234567UL;
            ulong stored = original; int reads = 0, writes = 0;
            var result = PStateVidWriter.Apply(new[] { targets[0] }, affinity, 0, PStateLayout.Zen5, PStateVidEncoding.Svi3, 256,
                a => { reads++; if (failure == 0 || failure == 3 && reads == 2) return null; return failure == 4 && reads == 2 ? original : stored; },
                (a, v) => { writes++; stored = v; return failure != 2; });
            var expected = new[] { PStateWriteStatus.ReadFailed, PStateWriteStatus.Disabled, PStateWriteStatus.WriteFailed,
                PStateWriteStatus.ReadbackFailed, PStateWriteStatus.ReadbackMismatch, PStateWriteStatus.AffinityFailed };
            Assert.Equal(expected[failure], result.Status);
            Assert.Equal(affinity.Pins, affinity.Restores);
            if (failure == 0 || failure == 1 || failure == 5) Assert.Equal(0, writes);
        }
        [Fact]
        public void FreshPerTargetBitsAndPartialProgressArePreserved()
        {
            var affinity = new Affinity(); int writes = 0;
            ulong original = 0x80000000A1234567UL, stored = original;
            var result = PStateVidWriter.Apply(targets, affinity, 7, PStateLayout.FidDid, PStateVidEncoding.Svi3, 256,
                a => stored, (a, value) => { Assert.Equal(PStateMath.BaseMsr + 7, a); Assert.Equal(original & ~PStateVidMath.Mask(PStateVidEncoding.Svi3), value & ~PStateVidMath.Mask(PStateVidEncoding.Svi3)); stored = value; return ++writes == 1; });
            Assert.Equal(PStateWriteStatus.WriteFailed, result.Status); Assert.Equal(1, result.Completed);
            Assert.Equal(2, affinity.Restores);
        }
        [Theory]
        [InlineData("")]
        [InlineData("-1")]
        [InlineData("1.5")]
        [InlineData("0x20")]
        [InlineData("512")]
        [InlineData("99999999999999")]
        public void PastedInvalidTextDoesNotBecomeAVid(string text) => Assert.False(PStateVidMath.TryParse(PStateVidEncoding.Svi3, text, out _));
    }

    public class DriverMaintenanceTests
    {
        [Theory]
        [InlineData(@"System32\Drivers\inpoutx64.sys")]
        [InlineData(@"system32\drivers\inpoutx64.sys")]
        [InlineData(@"\SystemRoot\System32\Drivers\inpoutx64.sys")]
        [InlineData(@"C:\Windows\System32\Drivers\inpoutx64.sys")]
        [InlineData(@"\??\C:\Windows\System32\Drivers\inpoutx64.sys")]
        [InlineData(@"\\?\C:\Windows\System32\Drivers\inpoutx64.sys")]
        [InlineData("  \"System32\\Drivers\\inpoutx64.sys\"  ")]
        [InlineData("System32/Drivers/inpoutx64.sys")]
        public void DriverServicePathsResolveWithoutUsingTheApplicationDirectory(string path)
            => Assert.Equal(@"C:\Windows\System32\Drivers\inpoutx64.sys", DriverImagePath.Resolve(path, @"C:\Windows"), ignoreCase: true);
        [Fact]
        public void DriverServicePathExpandsWindowsEnvironmentVariable()
            => Assert.Equal(System.IO.Path.Combine(Environment.GetEnvironmentVariable("SystemRoot"), @"System32\Drivers\inpoutx64.sys"),
                DriverImagePath.Resolve(@"%SystemRoot%\System32\Drivers\inpoutx64.sys", @"C:\Unused"), ignoreCase: true);
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(@"inpoutx64.sys")]
        [InlineData(@"C:inpoutx64.sys")]
        [InlineData(@"\Drivers\inpoutx64.sys")]
        [InlineData(@"%UnknownDriverRoot%\inpoutx64.sys")]
        public void AmbiguousDriverPathsCannotBecomeCurrentDirectoryPaths(string path)
            => Assert.Throws<System.IO.InvalidDataException>(() => DriverImagePath.Resolve(path, @"C:\Windows"));
        [Fact]
        public void DiagnosticsExposeOriginalPathAndUnavailableHash()
        {
            string report = new DriverStateSnapshot { RawImagePath = @"System32\Drivers\inpoutx64.sys" }.ToString();
            Assert.Contains(@"Registry ImagePath: System32\Drivers\inpoutx64.sys", report);
            Assert.Contains("SHA256: Unavailable", report);
        }
        private sealed class Scope : IDisposable { public void Dispose() { } }
        private sealed class Fake : IDriverMaintenanceEnvironment
        {
            public DriverStateSnapshot State = new DriverStateSnapshot { Present = true, OwnershipEstablished = true, UnknownConsumers = false, Path = "expected", ExpectedPath = "expected" };
            public readonly List<string> Calls = new List<string>();
            public bool StopOk = true, DeleteOk = true, FileOk = true;
            public IDisposable EnterMaintenance() { Calls.Add("gate"); return new Scope(); }
            public DriverStateSnapshot Inspect() { Calls.Add("inspect"); return State; }
            public bool Stop(TimeSpan timeout) { Assert.True(timeout <= TimeSpan.FromSeconds(3)); Calls.Add("stop"); return StopOk; }
            public bool DeleteService() { Calls.Add("service"); return DeleteOk; }
            public bool DeleteFile() { Calls.Add("file"); return FileOk; }
        }
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        [InlineData(5)]
        public void IneligibleStatesNeverStopOrDelete(int state)
        {
            var fake = new Fake();
            if (state == 0) fake.State.OwnershipEstablished = false;
            if (state == 1) fake.State.OtherInstances = 1;
            if (state == 2) fake.State.LocalHandleOpen = true;
            if (state == 3) fake.State.UnknownConsumers = true;
            if (state == 4) fake.State.Path = "untrusted";
            if (state == 5) fake.State.Error = "access denied";
            Assert.StartsWith("Skipped", new DriverMaintenanceService(fake).Remove(true));
            Assert.DoesNotContain("stop", fake.Calls); Assert.DoesNotContain("file", fake.Calls);
        }
        [Fact]
        public void NormalExitNeverCleansUp() { var fake = new Fake(); new DriverMaintenanceService(fake).Remove(false); Assert.Empty(fake.Calls); }
        [Theory]
        [InlineData(0, "stop")]
        [InlineData(1, "service")]
        [InlineData(2, "file")]
        [InlineData(3, "file")]
        public void CleanupStopsAtFirstFailureAndReportsPartialCompletion(int failure, string final)
        {
            var fake = new Fake { StopOk = failure != 0, DeleteOk = failure != 1, FileOk = failure != 2 };
            string report = new DriverMaintenanceService(fake).Remove(true);
            Assert.Equal(final, fake.Calls.Last());
            if (failure == 3) Assert.Equal("Driver service and owned file removed.", report);
        }
        [Fact]
        public void AbsentServiceNeedsNoDeletion() { var fake = new Fake(); fake.State.Present = false; new DriverMaintenanceService(fake).Remove(true); Assert.DoesNotContain("stop", fake.Calls); }
    }

    public class LocalizationTests
    {
        [Theory]
        [InlineData("en", "Details", "Details")]
        [InlineData("zh-CN", "Details", "详情")]
        [InlineData("zh-CN", "Core 8", "核心 8")]
        [InlineData("zh-CN", "Write VID", "写入 VID")]
        [InlineData("fr-FR", "Apply", "Apply")]
        [InlineData("zh-CN", "FMax 5950 MHz", "FMax 5950 MHz")]
        public void ResourcesHaveExplicitEnglishFallbackAndKeepTechnicalValues(string culture, string source, string expected)
            => Assert.Equal(expected, UiText.T(source, CultureInfo.GetCultureInfo(culture)));
    }
}
