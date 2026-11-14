using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Xunit;
using ZenStatesDebugTool;
using ZenStatesDebugTool.Profiles;

namespace ProfileCore.Tests
{
    public class TuningEditSessionTests
    {
        private static TuningRequest Ppt(int value) => new TuningRequest { Kind = TuningKind.Ppt, Value = value };
        private static TuningRequest Co(int index, int value) => new TuningRequest { Kind = TuningKind.CurveOptimizer, Index = index, Value = value };
        private static TuningEditSession Session(Profile baseline)
        {
            var session = new TuningEditSession();
            foreach (var r in TuningService.RequestsFromProfile(baseline)) session.SetBaseline(r, "Initial editor value; not a CPU read");
            return session;
        }
        private sealed class Transport : ITuningTransport
        {
            public List<TuningRequest> Writes = new List<TuningRequest>();
            public bool IsSupported(TuningRequest request, out string reason) { reason = null; return true; }
            public TuningReply Write(TuningRequest request) { Writes.Add(request); return new TuningReply { Status = 1 }; }
            public TuningReply Read(TuningRequest request) => new TuningReply { Status = 1, Value = request.Value, Direct = true };
        }
        private static Profile Full() => new Profile {
            SchemaVersion = 2, TopologySignature = "physical-map", CoreBindings = new Dictionary<int, string> { [0] = "ccd:0/ccx:0/core:0", [8] = "ccd:1/ccx:0/core:0" },
            CoMargins = new Dictionary<int, int> { [0] = -25, [8] = -20 }, Fmax = 5950,
            CurveShaperTiers = Enumerable.Range(0, 5).Select(_ => new CurveShaperTier { Low = -10, Medium = -15, High = -20 }).ToList(),
            PptWatts = 0, TdcAmps = 0, EdcAmps = 0, PboScalar = 0
        };
        [Fact]
        public void EditingOnlyPptSendsExactlyOneRequest()
        {
            var baseline = Full(); var session = Session(baseline);
            var current = TuningSnapshots.Profile(baseline); current.PptWatts = 75;
            var changes = session.Changes(current);
            var transport = new Transport();
            var result = new TuningService(transport).Apply(changes);
            var sent = Assert.Single(transport.Writes);
            Assert.Equal(TuningKind.Ppt, sent.Kind); Assert.Equal(75, sent.Value);
            Assert.Single(result.Rows); Assert.Empty(changes.CoMargins); Assert.Null(changes.Fmax); Assert.Null(changes.CurveShaperTiers);
        }
        [Fact]
        public void ChangedCurveShaperBandSendsOneAtomicTierWithOriginalIndex()
        {
            var baseline = Full(); var session = Session(baseline);
            var current = TuningSnapshots.Profile(baseline); current.CurveShaperTiers[3].High = -22;
            var changes = session.Changes(current);
            Assert.Equal(5, changes.CurveShaperTiers.Count);
            Assert.Null(changes.CurveShaperTiers[0]); Assert.Null(changes.CurveShaperTiers[4]);
            var transport = new Transport(); new TuningService(transport).Apply(changes);
            var sent = Assert.Single(transport.Writes);
            Assert.Equal(3, sent.Index); Assert.Equal(-10, sent.Value); Assert.Equal(-15, sent.Medium); Assert.Equal(-22, sent.High);
        }
        [Fact]
        public void CoreDeltaRetainsPhysicalBindingAndSchema()
        {
            var baseline = Full(); var session = Session(baseline);
            var current = TuningSnapshots.Profile(baseline); current.CoMargins[8] = -28;
            var changes = session.Changes(current);
            Assert.Equal(2, changes.SchemaVersion); Assert.Equal("physical-map", changes.TopologySignature);
            Assert.Equal(8, Assert.Single(changes.CoMargins).Key);
            Assert.Equal("ccd:1/ccx:0/core:0", Assert.Single(changes.CoreBindings).Value);
            changes.CoreBindings[8] = "mutated";
            Assert.Equal("ccd:1/ccx:0/core:0", current.CoreBindings[8]);
        }
        [Fact]
        public void SavingMetadataDoesNotApplyOrClearPendingChanges()
        {
            var session = Session(new Profile { PptWatts = 0 });
            var saved = new Profile { PptWatts = 75 };
            session.RememberSaved(saved); saved.PptWatts = 100;
            Assert.True(session.IsEdited(Ppt(75)));
            Assert.Contains("75", session.SavedText(Ppt(75)));
            Assert.Equal(0, session.Baseline(Ppt(75)).Value);
            Assert.Equal("Unavailable", session.ReportedText(Ppt(75)));
        }
        [Theory]
        [InlineData(TuningOutcome.Verified, true)]
        [InlineData(TuningOutcome.AcceptedUnverified, true)]
        [InlineData(TuningOutcome.Mismatch, false)]
        [InlineData(TuningOutcome.Rejected, false)]
        [InlineData(TuningOutcome.Skipped, false)]
        [InlineData(TuningOutcome.Invalid, false)]
        [InlineData(TuningOutcome.NotAttempted, false)]
        [InlineData(TuningOutcome.Error, false)]
        public void OnlyAcceptedSuccessfulOutcomesAdvanceTheBaseline(TuningOutcome outcome, bool advances)
        {
            var session = Session(new Profile { PptWatts = 0 });
            var result = new TuningResult();
            result.Rows.Add(new TuningRow { Request = Ppt(75), Outcome = outcome,
                Write = new TuningReply { Status = outcome == TuningOutcome.Rejected ? 0xFFu : 1u } });
            session.RecordResult(result);
            Assert.Equal(!advances, session.IsEdited(Ppt(75)));
            if (outcome == TuningOutcome.AcceptedUnverified) {
                Assert.Equal("Last sent; unverified", session.BaselineSource(Ppt(75)));
                Assert.Equal("Unavailable", session.ReportedText(Ppt(75)));
            }
        }
        [Fact]
        public void PartialResultClearsOnlySuccessfulEdits()
        {
            var baseline = new Profile { CoMargins = new Dictionary<int, int> { [0] = 0, [8] = 0 } };
            var session = Session(baseline); var current = TuningSnapshots.Profile(baseline);
            current.CoMargins[0] = -20; current.CoMargins[8] = -25;
            var result = new TuningResult();
            result.Rows.Add(new TuningRow { Request = Co(0, -20), Outcome = TuningOutcome.Verified, Write = new TuningReply { Status = 1 } });
            result.Rows.Add(new TuningRow { Request = Co(8, -25), Outcome = TuningOutcome.Rejected, Write = new TuningReply { Status = 0xFF } });
            session.RecordResult(result);
            Assert.Equal(8, Assert.Single(session.Changes(current).CoMargins).Key);
        }
        [Fact]
        public void ResultForOlderRequestDoesNotClearNewerEdit()
        {
            var session = Session(new Profile { PptWatts = 0 });
            var result = new TuningResult();
            result.Rows.Add(new TuningRow { Request = Ppt(75), Outcome = TuningOutcome.AcceptedUnverified, Write = new TuningReply { Status = 1 } });
            session.RecordResult(result);
            Assert.True(session.IsEdited(Ppt(90))); Assert.Equal(75, session.Baseline(Ppt(90)).Value);
        }
        [Fact]
        public void MismatchRecordsCpuValueWithoutClaimingRequestedValueWasConfirmed()
        {
            var session = Session(new Profile { CoMargins = new Dictionary<int, int> { [0] = 0 } });
            var result = new TuningResult();
            result.Rows.Add(new TuningRow { Request = Co(0, -25), Outcome = TuningOutcome.Mismatch, Write = new TuningReply { Status = 1 },
                Read = new TuningReply { Status = 1, Direct = true, Value = -20 } });
            session.RecordResult(result);
            Assert.True(session.IsEdited(Co(0, -25)));
            Assert.Contains("-20", session.ReportedText(Co(0, -25)));
            Assert.Equal(0, session.Baseline(Co(0, -25)).Value);
        }
        [Fact]
        public void CpuObservationIsIndependentOfSavedValueAndRequestMutation()
        {
            var session = Session(new Profile { PptWatts = 0 });
            session.RememberSaved(new Profile { PptWatts = 75 });
            var request = Ppt(90); session.Observe(request, "CPU readback", true); request.Value = 100;
            Assert.Equal(90, session.Baseline(Ppt(90)).Value);
            Assert.Contains("90", session.ReportedText(Ppt(75))); Assert.Contains("75", session.SavedText(Ppt(90)));
        }
        [Theory]
        [InlineData(false, 1)]
        [InlineData(true, 255)]
        public void DerivedOrRejectedReadbacksDoNotBecomeCpuObservations(bool direct, uint status)
        {
            var session = Session(new Profile { PptWatts = 0 }); var result = new TuningResult();
            result.Rows.Add(new TuningRow { Request = Ppt(75), Outcome = TuningOutcome.AcceptedUnverified, Write = new TuningReply { Status = 1 },
                Read = new TuningReply { Value = 75, Direct = direct, Status = status } });
            session.RecordResult(result);
            Assert.Equal("Unavailable", session.ReportedText(Ppt(75)));
        }
        [Fact]
        public void ZeroLimitIsExplicitlySkippedAndDoesNotClearThePendingZeroEdit()
        {
            var session = Session(new Profile { PptWatts = 75 }); var transport = new Transport();
            var result = new TuningService(transport).Apply(session.Changes(new Profile { PptWatts = 0 }));
            session.RecordResult(result);
            Assert.Empty(transport.Writes); Assert.True(session.IsEdited(Ppt(0)));
            Assert.Equal(TuningOutcome.Skipped, Assert.Single(result.Rows).Outcome);
        }
        [Fact]
        public void UnchangedProfileProducesAnEmptyDelta()
        {
            var baseline = Full(); var session = Session(baseline);
            Assert.Empty(TuningService.RequestsFromProfile(session.Changes(TuningSnapshots.Profile(baseline))));
        }
        [Fact]
        public void ProfileSnapshotCopiesNestedValuesAndNullTiers()
        {
            var original = Full(); original.CurveShaperTiers[1] = null;
            var copy = TuningSnapshots.Profile(original);
            original.CurveShaperTiers[0].High = 5; original.CoMargins[8] = -99; original.CoreBindings[8] = "changed";
            Assert.Equal(-20, copy.CurveShaperTiers[0].High); Assert.Null(copy.CurveShaperTiers[1]);
            Assert.Equal(-20, copy.CoMargins[8]); Assert.Equal("ccd:1/ccx:0/core:0", copy.CoreBindings[8]);
        }
        [Fact]
        public void HistoryBoundsEntriesAndProtectsEveryResultFromMutation()
        {
            var history = new TuningActionHistory();
            for (int i = 0; i < 25; i++) {
                var result = new TuningResult { Label = "Action " + i, StartedUtc = new DateTime(2026, 1, 1, 0, i, 0, DateTimeKind.Utc) };
                result.Rows.Add(new TuningRow { Request = Ppt(i), Outcome = TuningOutcome.AcceptedUnverified, Reason = "original",
                    Write = new TuningReply { Status = 1, TargetIdentity = "physical-core" }, Read = new TuningReply { Value = i } });
                history.Add(result); result.Label = "changed"; result.Rows[0].Request.Value = 999; result.Rows[0].Write.TargetIdentity = "changed";
            }
            Assert.Equal(20, history.Entries.Count);
            Assert.Equal("Action 24", history.Entries[0].Result.Label); Assert.Equal("Action 5", history.Entries[19].Result.Label);
            var selected = history.Entries[0].Result;
            Assert.Equal(24, selected.Rows[0].Request.Value); Assert.Equal(24, selected.Rows[0].Read.Value);
            Assert.Equal("physical-core", selected.Rows[0].Write.TargetIdentity); Assert.Equal("original", selected.Rows[0].Reason);
            Assert.Equal(24, selected.StartedUtc.Minute);
            selected.Rows.Clear(); Assert.Single(history.Entries[0].Result.Rows);
        }
    }
}
