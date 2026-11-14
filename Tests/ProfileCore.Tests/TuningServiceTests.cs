using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Newtonsoft.Json;
using Xunit;
using ZenStatesDebugTool;
using ZenStatesDebugTool.Profiles;

namespace ProfileCore.Tests
{
    public class TuningServiceTests
    {
        [Fact]
        public void PptOnlyActionHasOneHumanResultWithoutClaimingReadback()
        {
            var fake = new Fake();
            var result = new TuningService(fake).Apply(new Profile { PptWatts = 75 });
            Assert.Equal(1, fake.Writes); Assert.Equal(0, fake.Reads);
            var row = Assert.Single(TuningResultFormatter.DisplayRows(result, true));
            Assert.Equal("PPT", row.Setting); Assert.Equal("75 W", row.Requested);
            Assert.Equal("Sent; readback unavailable", row.Result);
            Assert.Contains("PPT 75 W", TuningResultFormatter.FriendlySummary(result));
            Assert.DoesNotContain("Confirmed", TuningResultFormatter.FriendlySummary(result));
        }
        [Fact]
        public void OverviewGroupsSuccessfulCoresButPreservesIndividualErrorsAndFullDetails()
        {
            var result = new TuningResult();
            for (int i = 0; i < 16; i++) result.Rows.Add(new TuningRow { Request = Co(-25, i), Outcome = i < 14 ? TuningOutcome.Verified : TuningOutcome.Mismatch });
            var overview = TuningResultFormatter.DisplayRows(result, true);
            Assert.Equal(3, overview.Count);
            Assert.Equal("14 cores", overview[0].Requested);
            Assert.Contains("Core 14", overview[1].Setting); Assert.Contains("Core 15", overview[2].Setting);
            Assert.Equal(16, TuningResultFormatter.DisplayRows(result, false).Count);
        }
        private sealed class Fake : ITuningTransport
        {
            public int Writes, Reads;
            public Func<TuningRequest, bool> Supported = r => true;
            public Func<TuningRequest, TuningReply> OnWrite = r => new TuningReply { Status = 1 };
            public Func<TuningRequest, TuningReply> OnRead = r => new TuningReply { Status = 1, Value = r.Value, Direct = true };
            public bool IsSupported(TuningRequest r, out string reason) { reason = "Unsupported or disabled."; return Supported(r); }
            public TuningReply Write(TuningRequest r) { Writes++; return OnWrite(r); }
            public TuningReply Read(TuningRequest r) { Reads++; return OnRead(r); }
        }
        private static TuningRequest Co(int value = -25, int core = 0) => new TuningRequest { Kind = TuningKind.CurveOptimizer, Value = value, Index = core };

        [Theory]
        [InlineData(-999)]
        [InlineData(-25)]
        [InlineData(-1)]
        [InlineData(0)]
        [InlineData(30)]
        [InlineData(999)]
        public void DirectFreshCoReadbackVerifiesSignedAndZeroValues(int value)
        {
            var fake = new Fake();
            var result = new TuningService(fake).Apply(new[] { Co(value) });
            Assert.Equal(TuningOutcome.Verified, result.Rows.Single().Outcome);
            Assert.Equal(1, fake.Writes); Assert.Equal(1, fake.Reads);
            Assert.True(result.Success); Assert.Equal(0, result.ExitCode);
        }
        [Fact]
        public void MismatchIsFailureAndKeepsRequestedValue()
        {
            var fake = new Fake { OnRead = r => new TuningReply { Status = 1, Direct = true, Value = 0 } };
            var result = new TuningService(fake).Apply(new[] { Co() });
            Assert.Equal(TuningOutcome.Mismatch, result.Rows[0].Outcome);
            Assert.Equal(-25, result.Rows[0].Request.Value);
            Assert.Equal(1, result.ExitCode);
        }
        [Theory]
        [InlineData(false, 1, 5800)]
        [InlineData(true, 254, 5800)]
        [InlineData(true, 1, 0)]
        public void FmaxUnavailableFailedOrDerivedReadCannotVerify(bool direct, uint status, int value)
        {
            var fake = new Fake { OnRead = r => new TuningReply { Direct = direct, Status = status, Value = value } };
            var result = new TuningService(fake).Apply(new Profile { Fmax = 5800 });
            Assert.Equal(TuningOutcome.AcceptedUnverified, result.Rows.Single().Outcome);
            Assert.True(result.Success);
            if (status != 1 || value == 0) Assert.Null(result.Rows[0].Read.Value);
        }
        [Fact]
        public void MissingGetterIsAcceptedUnverified()
        {
            var fake = new Fake { OnRead = r => null };
            Assert.Equal(TuningOutcome.AcceptedUnverified, new TuningService(fake).Apply(new[] { Co() }).Rows[0].Outcome);
        }
        [Theory]
        [InlineData(0x30)]
        [InlineData(0x31)]
        [InlineData(0x32)]
        [InlineData(0x33)]
        [InlineData(0xFC)]
        [InlineData(0xFF)]
        public void FatalWriteStopsBatchWithoutRetry(uint status)
        {
            var fake = new Fake { OnWrite = r => new TuningReply { Status = status } };
            var result = new TuningService(fake).Apply(new[] { Co(), Co(-15, 1) });
            Assert.Equal(1, fake.Writes); Assert.Equal(0, fake.Reads);
            Assert.Equal(TuningOutcome.Rejected, result.Rows[0].Outcome);
            Assert.Equal(status, result.Rows[0].Write.Status);
            Assert.Equal(TuningOutcome.NotAttempted, result.Rows[1].Outcome);
            Assert.False(result.Complete); Assert.Equal(1, result.ExitCode);
        }
        [Theory]
        [InlineData(0xFD)]
        [InlineData(0xFE)]
        public void OrdinaryRejectionContinuesIndependentSettings(uint status)
        {
            var fake = new Fake { OnWrite = r => new TuningReply { Status = r.Index == 0 ? status : 1 } };
            var result = new TuningService(fake).Apply(new[] { Co(), Co(-15, 1) });
            Assert.Equal(2, fake.Writes); Assert.Equal(1, fake.Reads);
            Assert.True(result.Complete); Assert.False(result.Success);
            Assert.Equal(TuningOutcome.Verified, result.Rows[1].Outcome);
        }
        [Fact]
        public void GetterStatusIsSeparateAndFatalGetterPreservesAcceptedWrite()
        {
            var fake = new Fake { OnRead = r => new TuningReply { Status = 0x33 } };
            var result = new TuningService(fake).Apply(new[] { Co(), Co(-15, 1) });
            Assert.Equal(1U, result.Rows[0].Write.Status);
            Assert.Equal(0x33U, result.Rows[0].Read.Status);
            Assert.Equal(TuningOutcome.AcceptedUnverified, result.Rows[0].Outcome);
            Assert.Equal(TuningOutcome.NotAttempted, result.Rows[1].Outcome);
        }
        [Theory]
        [InlineData("{\"Fmax\":5800.5}")]
        [InlineData("{\"Fmax\":9001,\"CoMargins\":{\"0\":-25}}")]
        [InlineData("{\"PptWatts\":-1,\"CoMargins\":{\"0\":-25}}")]
        [InlineData("{\"CoMargins\":{\"0\":-1000}}")]
        [InlineData("{\"CoMargins\":{\"128\":-25}}")]
        [InlineData("{\"CurveShaperTiers\":[{\"Low\":-51}]}")]
        public void InvalidJsonValuesRejectWholeBatchBeforeAnyHardwareAccess(string json)
        {
            var fake = new Fake { Supported = r => throw new Exception("Hardware must not be consulted") };
            var result = new TuningService(fake).Apply(JsonConvert.DeserializeObject<Profile>(json));
            Assert.Contains(result.Rows, r => r.Outcome == TuningOutcome.Invalid);
            Assert.Equal(0, fake.Writes); Assert.Equal(1, result.ExitCode);
        }
        [Fact]
        public void FullProfileUsesAllFeaturesButOnlyCoAndFmaxGetReadback()
        {
            var fake = new Fake();
            var profile = new Profile
            {
                CoMargins = new Dictionary<int, int> { [0] = -25, [8] = 0 },
                Fmax = 5700,
                CurveShaperTiers = Enumerable.Range(0, 5).Select(i => new CurveShaperTier { Low = -i, Medium = 0, High = i }).ToList(),
                PptWatts = 200,
                TdcAmps = 150,
                EdcAmps = 200,
                PboScalar = 2
            };
            var result = new ProfileApplier().Apply(profile, fake);
            Assert.Equal(12, fake.Writes); Assert.Equal(3, fake.Reads);
            Assert.Equal(3, result.Rows.Count(r => r.Outcome == TuningOutcome.Verified));
            Assert.Equal(9, result.Rows.Count(r => r.Outcome == TuningOutcome.AcceptedUnverified));
            Assert.True(result.Success);
        }
        [Fact]
        public void NoOpAndUnsupportedOnlyAreNotReportedAsApplied()
        {
            var fake = new Fake { Supported = r => false };
            var result = new TuningService(fake).Apply(new Profile { Fmax = 5000, PptWatts = 0 });
            Assert.All(result.Rows, r => Assert.Equal(TuningOutcome.Skipped, r.Outcome));
            Assert.Equal("No applicable settings.", result.Summary);
            Assert.Equal("No applicable settings.", new TuningService(fake).Apply(new Profile()).Summary);
            Assert.Equal(0, fake.Writes);
        }
        [Fact]
        public void CancellationAfterAcceptanceKeepsWriteAndDoesNotSendNextSetting()
        {
            using (var cancellation = new CancellationTokenSource())
            {
                var fake = new Fake { OnWrite = r => { cancellation.Cancel(); return new TuningReply { Status = 1 }; } };
                var result = new TuningService(fake).Apply(new[] { Co(), Co(-15, 1) }, cancellation.Token);
                Assert.True(result.Rows[0].IsAccepted);
                Assert.Equal(TuningOutcome.AcceptedUnverified, result.Rows[0].Outcome);
                Assert.Equal(TuningOutcome.NotAttempted, result.Rows[1].Outcome);
                Assert.Equal(0, fake.Reads); Assert.Equal(1, fake.Writes);
            }
        }
        [Fact]
        public void ExceptionKeepsEarlierResultsAndStopsBatch()
        {
            var fake = new Fake { OnWrite = r => r.Index == 1 ? throw new InvalidOperationException("driver failure") : new TuningReply { Status = 1 } };
            var result = new TuningService(fake).Apply(new[] { Co(), Co(-10, 1), Co(-5, 2) });
            Assert.Equal(new[] { TuningOutcome.Verified, TuningOutcome.Error, TuningOutcome.NotAttempted }, result.Rows.Select(r => r.Outcome));
        }
        [Fact]
        public void LogIsOneCompletePhysicalLineIncludingFailuresAndRawCodes()
        {
            var fake = new Fake { OnWrite = r => new TuningReply { Status = 0xFE, Route = "MP1", Command = 0x35 } };
            var line = TuningResultFormatter.LogLine("test\nprofile", new TuningService(fake).Apply(new[] { Co() }));
            Assert.DoesNotContain("\n", line); Assert.DoesNotContain("\r", line);
            Assert.Contains("0xFE", line); Assert.Contains("0x35", line); Assert.Contains("Success=False", line);
        }
        [Fact]
        public void CurveShaperCacheUpdatesOnlyAcceptedTiersAfterPartialFailure()
        {
            var old = new[] { CurveShaperCodec.Pack(-1, -1, -1), 42U, 43U, 44U, 45U };
            var fake = new Fake { OnWrite = r => new TuningReply { Status = r.Index == 0 ? 1U : 0xFCU } };
            var result = new TuningService(fake).Apply(new Profile
            {
                CurveShaperTiers = new List<CurveShaperTier> {
                new CurveShaperTier { Low = -25, Medium = -10, High = 0 }, new CurveShaperTier { Low = -20 }, new CurveShaperTier { Low = -15 } }
            });
            var merged = CurveShaperCodec.MergeAccepted(old, result);
            Assert.Equal(CurveShaperCodec.Pack(-25, -10, 0), merged[0]);
            Assert.Equal(42U, merged[1]); Assert.Equal(43U, merged[2]);
            Assert.Equal(CurveShaperCodec.Pack(-1, -1, -1), old[0]);
            Assert.Equal(TuningOutcome.AcceptedUnverified, result.Rows[0].Outcome);
            Assert.Equal(TuningOutcome.Rejected, result.Rows[1].Outcome);
            Assert.Equal(TuningOutcome.NotAttempted, result.Rows[2].Outcome);
        }
        [Theory]
        [InlineData(1, "OK")]
        [InlineData(0x30, "mutex timeout")]
        [InlineData(0x31, "mailbox ready timeout")]
        [InlineData(0x32, "message write timeout")]
        [InlineData(0x33, "PCI failure")]
        [InlineData(0xFC, "busy")]
        [InlineData(0xFD, "prerequisites not met")]
        [InlineData(0xFE, "unknown command")]
        [InlineData(0xFF, "failed")]
        [InlineData(0xAB, "unrecognized status")]
        public void RawStatusHasUsefulExplanation(uint status, string text)
        {
            Assert.Equal(text, TuningResultFormatter.StatusText(status));
            var row = new TuningRow { Request = Co(), Write = new TuningReply { Status = status } };
            Assert.Contains($"0x{status:X2}", TuningResultFormatter.Row(row));
        }
    }
}
