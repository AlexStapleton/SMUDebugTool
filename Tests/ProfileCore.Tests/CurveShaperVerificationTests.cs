using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Xunit;
using ZenStatesDebugTool;
using ZenStatesDebugTool.Profiles;

namespace ProfileCore.Tests
{
    public class CurveShaperVerificationTests
    {
        private sealed class PartialRead : ITuningTransport
        {
            public bool MissingHigh;
            public bool IsSupported(TuningRequest request, out string reason) { reason = null; return true; }
            public TuningReply Write(TuningRequest request) => new TuningReply { Status = 1 };
            public TuningReply Read(TuningRequest request) => new TuningReply { Status = 1, Direct = true, Value = request.Value,
                Medium = MissingHigh ? (int?)request.Medium : null, High = MissingHigh ? null : (int?)request.High };
        }
        private static TuningRequest Cs(int index = 0, int low = 5, int medium = 5, int high = 5)
            => new TuningRequest { Kind = TuningKind.CurveShaper, Index = index, Value = low, Medium = medium, High = high };
        private sealed class Firmware : ITuningTransport
        {
            public readonly TuningCommands Commands = new TuningCommands { CurveShaperSetRsmu = 0xA6, CurveShaperGetRsmu = 0x84 };
            public readonly uint[] Stored = new uint[5];
            public int Writes, Reads;
            public uint WriteStatus = 1, ReadStatus = 1;
            public Func<uint[], uint[]> ReturnWords = words => words;
            public Action AfterWrite;
            public bool IsSupported(TuningRequest request, out string reason) { reason = null; return Commands.CurveShaperSetRsmu != 0; }
            public TuningReply Write(TuningRequest request) => Commands.Execute(request, false, 0, Send);
            public TuningReply Read(TuningRequest request) => Commands.Execute(request, true, 0, Send);
            private TuningReply Send(string route, uint command, uint[] args)
            {
                Assert.Equal("RSMU", route); Assert.Equal(6, args.Length);
                if (command == 0xA6) {
                    Writes++; Assert.Equal(0x80U, args[0] & 0x80U);
                    Assert.All(args.Skip(1), word => Assert.Equal(0U, word));
                    if (WriteStatus == 1) Stored[args[0] & 0x7F] = args[0] & 0xFFFFFF00U;
                    AfterWrite?.Invoke();
                    return new TuningReply { Status = WriteStatus };
                }
                Assert.Equal(0x84U, command); Reads++;
                Assert.All(args, word => Assert.Equal(0U, word));
                var words = new uint[6]; Array.Copy(Stored, words, Stored.Length);
                return new TuningReply { Status = ReadStatus, RawArguments = ReturnWords(words) };
            }
        }
        [Fact]
        public void ScreenshotProfileConfirmsAllFiveTiersIncludingZeroTiers()
        {
            var firmware = new Firmware();
            var result = new TuningService(firmware).Apply(new[] { Cs(0), Cs(1, 3, 3, 3), Cs(2, 0, 0, 0), Cs(3, 0, 0, 0), Cs(4, 0, 0, 0) });
            Assert.Equal(5, firmware.Writes); Assert.Equal(5, firmware.Reads);
            Assert.All(result.Rows, row => { Assert.Equal(TuningOutcome.Verified, row.Outcome); Assert.Equal(0xA6U, row.Write.Command); Assert.Equal(0x84U, row.Read.Command); });
            Assert.Equal("5/5/5 low/medium/high steps", TuningResultFormatter.DisplayRows(result, false)[0].Observed);
            Assert.Equal("0/0/0 low/medium/high steps", TuningResultFormatter.DisplayRows(result, false)[4].Observed);
        }
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(4)]
        public void EveryTierUsesItsOwnReturnedWordAndDecodesSignedBands(int tier)
        {
            var firmware = new Firmware();
            for (int i = 0; i < 5; i++) firmware.Stored[i] = CurveShaperCodec.Pack(i + 1, i + 2, i + 3);
            var result = new TuningService(firmware).Apply(new[] { Cs(tier, -50, -1, 30) });
            var row = Assert.Single(result.Rows);
            Assert.Equal(TuningOutcome.Verified, row.Outcome);
            Assert.Equal(-50, row.Read.Value); Assert.Equal(-1, row.Read.Medium); Assert.Equal(30, row.Read.High);
            Assert.Equal("tier:" + tier, row.Read.TargetIdentity);
        }
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        public void MismatchInAnyOneBandFailsVerificationAndKeepsRequestedTriple(int changedBand)
        {
            var firmware = new Firmware { ReturnWords = words => { words[0] = CurveShaperCodec.Pack(changedBand == 0 ? 6 : 5, changedBand == 1 ? 6 : 5, changedBand == 2 ? 6 : 5); return words; } };
            var result = new TuningService(firmware).Apply(new[] { Cs() });
            var row = Assert.Single(result.Rows);
            Assert.Equal(TuningOutcome.Mismatch, row.Outcome); Assert.Equal(1, result.ExitCode);
            Assert.Equal("5/5/5 low/medium/high", row.Request.Requested);
            Assert.Contains("6", TuningResultFormatter.Observed(row));
        }
        [Theory]
        [InlineData(0xFE)]
        [InlineData(0xFD)]
        [InlineData(0x33)]
        public void FailedGetterRetainsItsStatusAndNeverExposesPayloadAsObserved(uint status)
        {
            var firmware = new Firmware { ReadStatus = status };
            var row = Assert.Single(new TuningService(firmware).Apply(new[] { Cs() }).Rows);
            Assert.Equal(TuningOutcome.AcceptedUnverified, row.Outcome);
            Assert.Equal(1U, row.Write.Status); Assert.Equal(status, row.Read.Status);
            Assert.Null(row.Read.Value); Assert.Null(row.Read.Medium); Assert.Null(row.Read.High);
            Assert.Equal("Accepted; readback failed", TuningResultFormatter.FriendlyOutcome(row));
        }
        [Fact]
        public void MissingGetterDoesNotSendACommandOrClaimVerification()
        {
            var firmware = new Firmware(); firmware.Commands.CurveShaperGetRsmu = 0;
            var row = Assert.Single(new TuningService(firmware).Apply(new[] { Cs() }).Rows);
            Assert.Equal(1, firmware.Writes); Assert.Equal(0, firmware.Reads);
            Assert.Equal(TuningOutcome.AcceptedUnverified, row.Outcome); Assert.Null(row.Read);
        }
        [Theory]
        [InlineData(0)]
        [InlineData(5)]
        public void AllZeroGetterWithoutPriorEvidenceRemainsInconclusive(int requested)
        {
            var firmware = new Firmware { ReturnWords = words => new uint[6] };
            var row = Assert.Single(new TuningService(firmware).Apply(new[] { Cs(0, requested, requested, requested) }).Rows);
            Assert.Equal(1, firmware.Writes); Assert.Equal(1, firmware.Reads);
            Assert.Equal(TuningOutcome.AcceptedUnverified, row.Outcome); Assert.Null(row.Read.Value);
            Assert.Contains("all-zero", row.Reason);
            Assert.Equal("Accepted; readback inconclusive", TuningResultFormatter.FriendlyOutcome(row));
        }
        [Fact]
        public void UnusedSixthWordDoesNotTurnAnAllZeroTierBlockIntoProof()
        {
            var firmware = new Firmware { ReturnWords = words => new uint[] { 0, 0, 0, 0, 0, 0x12345678 } };
            var row = Assert.Single(new TuningService(firmware).Apply(new[] { Cs(0, 0, 0, 0) }).Rows);
            Assert.Equal(TuningOutcome.AcceptedUnverified, row.Outcome); Assert.Contains("all-zero", row.Read.ReadIssue);
        }
        [Fact]
        public void LowByteMetadataAloneDoesNotProveOffsetReadback()
        {
            var firmware = new Firmware { ReturnWords = words => new uint[] { 1, 2, 3, 4, 5, 0 } };
            var row = Assert.Single(new TuningService(firmware).Apply(new[] { Cs(0, 0, 0, 0) }).Rows);
            Assert.Equal(TuningOutcome.AcceptedUnverified, row.Outcome); Assert.Contains("all-zero margin", row.Read.ReadIssue);
        }
        [Fact]
        public void FreshAllZeroResetCanConfirmAfterGetterRespondedDuringSameApply()
        {
            var firmware = new Firmware();
            var result = new TuningService(firmware).Apply(new[] { Cs(), Cs(0, 0, 0, 0) });
            Assert.All(result.Rows, row => Assert.Equal(TuningOutcome.Verified, row.Outcome));
            Assert.Equal(2, firmware.Writes); Assert.Equal(2, firmware.Reads);
            Assert.Equal(0, result.Rows[1].Read.Value);
        }
        [Theory]
        [InlineData(0)]
        [InlineData(4)]
        public void ShortGetterBlocksCannotVerifyEvenWhenTheFirstWordMatches(int length)
        {
            var firmware = new Firmware { ReturnWords = words => words.Take(length).ToArray() };
            var row = Assert.Single(new TuningService(firmware).Apply(new[] { Cs() }).Rows);
            Assert.Equal(TuningOutcome.AcceptedUnverified, row.Outcome); Assert.Contains("fewer than five", row.Read.ReadIssue);
        }
        [Fact]
        public void OutOfContractBandsCannotBeMistakenForMatchingReadback()
        {
            var firmware = new Firmware { ReturnWords = words => { words[4] = 0x80808000U; return words; } };
            var row = Assert.Single(new TuningService(firmware).Apply(new[] { Cs() }).Rows);
            Assert.Equal(TuningOutcome.AcceptedUnverified, row.Outcome); Assert.Contains("outside", row.Read.ReadIssue);
        }
        [Fact]
        public void FatalGetterAndCancellationStopWithoutFurtherWrites()
        {
            var firmware = new Firmware { ReadStatus = 0x33 };
            var result = new TuningService(firmware).Apply(new[] { Cs(), Cs(1) });
            Assert.Equal(1, firmware.Writes); Assert.Equal(TuningOutcome.NotAttempted, result.Rows[1].Outcome);
            using (var cancellation = new CancellationTokenSource()) {
                firmware = new Firmware { AfterWrite = cancellation.Cancel };
                result = new TuningService(firmware).Apply(new[] { Cs(), Cs(1) }, cancellation.Token);
                Assert.Equal(1, firmware.Writes); Assert.Equal(0, firmware.Reads);
                Assert.Equal(TuningOutcome.AcceptedUnverified, result.Rows[0].Outcome);
                Assert.Equal(TuningOutcome.NotAttempted, result.Rows[1].Outcome);
            }
        }
        [Fact]
        public void MismatchRecordsActualBandsKeepsDraftPendingAndHistoryDetached()
        {
            var firmware = new Firmware { ReturnWords = words => { words[0] = CurveShaperCodec.Pack(5, 6, 5); return words; } };
            var requested = Cs(); var session = new TuningEditSession(); session.SetBaseline(requested, "Last sent; unverified");
            var result = new TuningService(firmware).Apply(new[] { requested }); session.RecordResult(result);
            Assert.True(session.IsEdited(requested)); Assert.Contains("5/6/5", session.ReportedText(requested));
            Assert.Equal(6, session.Baseline(requested).Medium);
            var cache = CurveShaperCodec.MergeAccepted(null, result); CurveShaperCodec.Unpack(cache[0], out _, out int medium, out _); Assert.Equal(6, medium);
            var history = new TuningHistoryEntry(result);
            result.Rows[0].Read.Medium = 99; result.Rows[0].Read.RawArguments[0] = 0;
            var saved = history.Result;
            Assert.Equal(6, saved.Rows[0].Read.Medium); Assert.NotEqual(0U, saved.Rows[0].Read.RawArguments[0]);
            Assert.Contains("5/6/5", TuningResultFormatter.Report(saved)); Assert.Contains("cmd=0x84", TuningResultFormatter.Report(saved));
        }
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void MissingEitherTemperatureBandDoesNotConfirmATier(bool missingHigh)
        {
            var row = Assert.Single(new TuningService(new PartialRead { MissingHigh = missingHigh }).Apply(new[] { Cs() }).Rows);
            Assert.Equal(TuningOutcome.AcceptedUnverified, row.Outcome);
            Assert.Equal("unavailable", TuningResultFormatter.Observed(row));
        }
    }
}
