using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xunit;
using ZenStatesDebugTool;

namespace ProfileCore.Tests
{
    public class CpuOverviewTests
    {
        private static uint Bits(float value) => BitConverter.ToUInt32(BitConverter.GetBytes(value), 0);
        [Theory]
        [InlineData(1f)] [InlineData(2f)] [InlineData(1.5f)] [InlineData(10f)]
        public void ScalarDecodesFloatAndAlwaysUsesFreshSixZeroArguments(float value)
        {
            var commands = new TuningCommands { ScalarGetRsmu = 0x6D };
            var reply = commands.Execute(new TuningRequest { Kind = TuningKind.Scalar, Value = 3 }, true, 0, (route, id, args) => {
                Assert.Equal("RSMU", route); Assert.Equal(0x6DU, id); Assert.Equal(new uint[6], args);
                return new TuningReply { Status = 1, RawArguments = new[] { Bits(value) } };
            });
            Assert.Equal((decimal)value, reply.Value); Assert.True(reply.Direct); Assert.Null(reply.ReadIssue);
        }
        [Theory]
        [InlineData(float.NaN)] [InlineData(float.PositiveInfinity)] [InlineData(0f)] [InlineData(0.5f)] [InlineData(11f)]
        public void InvalidScalarDoesNotTurnIntoDefaultOrConfirmation(float value)
        {
            var row = new TuningService(new ScalarTransport(value)).Apply(new[] { new TuningRequest { Kind = TuningKind.Scalar, Value = 2 } }).Rows.Single();
            Assert.Equal(TuningOutcome.AcceptedUnverified, row.Outcome); Assert.Null(row.Read.Value);
            Assert.Contains("inconclusive", TuningResultFormatter.FriendlyOutcome(row));
        }
        private sealed class ScalarTransport : ITuningTransport
        {
            private readonly float value;
            public ScalarTransport(float value) { this.value = value; }
            public bool IsSupported(TuningRequest request, out string reason) { reason = null; return true; }
            public TuningReply Write(TuningRequest request) => new TuningReply { Status = 1 };
            public TuningReply Read(TuningRequest request) => new TuningCommands { ScalarGetRsmu = 0x6D }.Execute(request, true, 0,
                (r, c, a) => new TuningReply { Status = 1, RawArguments = new[] { Bits(value) } });
        }
        [Theory]
        [InlineData(2f, TuningOutcome.Verified)] [InlineData(3f, TuningOutcome.Mismatch)] [InlineData(1.5f, TuningOutcome.Mismatch)]
        [InlineData(2.0000002f, TuningOutcome.Mismatch)]
        public void ScalarRequiresExactFreshMultiplier(float value, TuningOutcome expected)
        {
            var row = new TuningService(new ScalarTransport(value)).Apply(new[] { new TuningRequest { Kind = TuningKind.Scalar, Value = 2 } }).Rows.Single();
            Assert.Equal(expected, row.Outcome); Assert.Equal(2, row.Request.Value);
        }
        [Theory]
        [InlineData(0xFE)] [InlineData(0xFC)]
        public void FailedScalarCannotExposeStaleValue(uint status)
        {
            var reply = new TuningCommands { ScalarGetRsmu = 7 }.Execute(new TuningRequest { Kind = TuningKind.Scalar }, true, 0,
                (r, c, a) => new TuningReply { Status = status, Value = 2, RawArguments = new[] { Bits(2) } });
            Assert.Null(reply.Value);
        }
        [Fact]
        public void MissingAndTruncatedScalarGetterNeverConfirms()
        {
            var request = new TuningRequest { Kind = TuningKind.Scalar, Value = 2 };
            Assert.Null(new TuningCommands().Execute(request, true, 0, (r, c, a) => throw new Exception("Must not send")));
            var reply = new TuningCommands { ScalarGetRsmu = 7 }.Execute(request, true, 0,
                (r, c, a) => new TuningReply { Status = 1, Value = 2, RawArguments = new uint[0] });
            Assert.Null(reply.Value); Assert.NotNull(reply.ReadIssue);
        }
        [Theory]
        [InlineData(40)] [InlineData(95)]
        public void TemperatureUsesOneRsmuPacketWithCelsiusAndNoReadback(int value)
        {
            int sends = 0;
            var reply = new TuningCommands { TemperatureRsmu = 0x59 }.Execute(new TuningRequest { Kind = TuningKind.Temperature, Value = value }, false, 0,
                (route, id, args) => { sends++; Assert.Equal("RSMU", route); Assert.Equal(0x59U, id); Assert.Equal((uint)value, args[0]); Assert.All(args.Skip(1), a => Assert.Equal(0U, a)); return new TuningReply { Status = 0xFE }; });
            Assert.Equal(1, sends); Assert.Equal(0xFEU, reply.Status);
        }
        [Theory]
        [InlineData(0)] [InlineData(39)] [InlineData(96)] [InlineData(150)]
        public void UnsafeOrZeroTemperatureInvalidatesEntireBatchBeforeWrites(int value)
        {
            var transport = new NoReads();
            var result = new TuningService(transport).Apply(new[] { new TuningRequest { Kind = TuningKind.Scalar, Value = 2 }, new TuningRequest { Kind = TuningKind.Temperature, Value = value } });
            Assert.True(result.HasErrors); Assert.Equal(0, transport.Writes);
        }
        private sealed class NoReads : ITuningTransport
        {
            public int Writes;
            public bool IsSupported(TuningRequest r, out string reason) { reason = null; return true; }
            public TuningReply Write(TuningRequest r) { Writes++; return new TuningReply { Status = 1 }; }
            public TuningReply Read(TuningRequest r) => throw new Exception("Temperature has no active getter");
        }
        [Fact]
        public void TemperatureAcceptanceIsUnverifiedAndUsesCorrectHumanUnit()
        {
            var fake = new NoReads();
            var row = new TuningService(fake).Apply(new[] { new TuningRequest { Kind = TuningKind.Temperature, Value = 80 } }).Rows.Single();
            Assert.Equal(1, fake.Writes); Assert.Equal(TuningOutcome.AcceptedUnverified, row.Outcome);
            Assert.Equal("°C", row.Request.Unit); Assert.Equal("Temperature ceiling", TuningResultFormatter.SettingName(row.Request));
        }
        [Fact]
        public void OverviewDoesNotSendUndefinedCommandsOrClaimZeros()
        {
            var rows = new OverviewReadPlan().Read((r, c, a) => throw new Exception("Must not send"), CancellationToken.None);
            Assert.All(rows, r => Assert.Equal("Unavailable", r.Value));
        }
        [Fact]
        public void OverviewKeepsEachStatusAndDoesNotCallFusedDefaultsActiveLimits()
        {
            var plan = new OverviewReadPlan { Scalar = 1, FusedPower = 2, FusedVddTdc = 3, Configured = 4, ConfiguredRoute = "MP1" };
            var rows = plan.Read((route, command, args) => {
                Assert.Equal(new uint[6], args);
                if (command == 4) Assert.Equal("MP1", route);
                return new TuningReply { Status = command == 2 ? 0xFEU : 1U,
                    RawArguments = new[] { command == 1 ? Bits(2) : command == 4 ? 170U << 16 | 95U : 999U } };
            }, CancellationToken.None);
            Assert.Equal("2", rows[0].Value); Assert.Equal("Unavailable", rows[1].Value); Assert.Equal("999", rows[2].Value);
            Assert.Equal("170", rows[4].Value); Assert.Equal("95", rows[5].Value);
            Assert.Contains("unknown command", rows[1].Evidence); Assert.Contains("not active", rows[1].Source);
        }
        [Fact]
        public void FatalReadStopsSubsequentCommandsWithoutRetry()
        {
            int calls = 0;
            var rows = new OverviewReadPlan { Scalar = 1, FusedPower = 2, Configured = 3 }.Read((r, c, a) => {
                calls++; return new TuningReply { Status = 0xFC };
            }, CancellationToken.None);
            Assert.Equal(1, calls); Assert.All(rows, row => Assert.Equal("Unavailable", row.Value));
            Assert.Contains("Not read", rows.Last().Evidence);
        }
        [Fact]
        public void CancellationBetweenReadsSendsNothingElse()
        {
            int calls = 0;
            using (var stop = new CancellationTokenSource()) {
                Assert.Throws<OperationCanceledException>(() => new OverviewReadPlan { Scalar = 1, FusedPower = 2 }.Read((r, c, a) => {
                    calls++; stop.Cancel(); return new TuningReply { Status = 1 };
                }, stop.Token));
            }
            Assert.Equal(1, calls);
        }
        [Fact]
        public void SnapshotDetachesCollectionsAndExplainsItsEvidence()
        {
            var rows = new List<OverviewReading> { new OverviewReading("Core rail VID", 1.2m, "V", "Rail request", "Not per-core") };
            var commands = new List<string> { "RSMU test: defined" };
            var snapshot = new CpuOverviewSnapshot(DateTime.UtcNow, DateTime.UtcNow, "Synthetic", false, rows, commands);
            rows.Clear(); commands.Clear(); Assert.Single(snapshot.Readings); Assert.Single(snapshot.Commands);
            Assert.Contains("not active limits", snapshot.Report); Assert.Contains("no tuning writes", snapshot.Report);
        }
        [Theory]
        [InlineData(1)] [InlineData(2)]
        public async Task OverviewViewCanRefreshWithoutAnyWriteOrDriver(int scale)
        {
            var done = new TaskCompletionSource<bool>();
            var thread = new Thread(() => {
                try {
                    int reads = 0, writes = 0;
                    var snapshot = new CpuOverviewSnapshot(DateTime.UtcNow, DateTime.UtcNow, "Synthetic", true,
                        new[] { new OverviewReading("CPU temperature", 55, "°C", "Synthetic fixture", "Not hardware") }, new[] { "Defined only" });
                    using (var form = new CpuOverviewForm(() => { reads++; return Task.FromResult(snapshot); }, value => { writes++; return Task.FromResult(new TuningResult()); })) {
                        Assert.Equal(0, reads); Assert.Equal(0, writes);
                        form.Font = new Font(form.Font.FontFamily, form.Font.Size * scale);
                        form.Size = new Size(700, 500); form.CreateControl(); form.PerformLayout();
                        form.RefreshSnapshotAsync().GetAwaiter().GetResult();
                        Assert.Equal(1, reads); Assert.Equal(0, writes);
                        var grid = Descendants(form).OfType<DataGridView>().Single(); Assert.True(grid.ReadOnly); Assert.Equal(5, grid.Columns.Count);
                        Assert.Equal(DataGridViewColumnHeadersHeightSizeMode.AutoSize, grid.ColumnHeadersHeightSizeMode);
                        Assert.True(grid.Columns["Value"].MinimumWidth >= TextRenderer.MeasureText("Unavailable", grid.Font).Width);
                        Assert.True(Descendants(form).OfType<Button>().Single(b => b.Name == "applyTemperature").Enabled);
                    }
                    done.SetResult(true);
                } catch (Exception ex) { done.SetException(ex); }
            });
            thread.SetApartmentState(ApartmentState.STA); thread.Start(); await done.Task;
        }
        private static IEnumerable<Control> Descendants(Control root) => root.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Descendants(c)));
        [Fact]
        public async Task FailedRefreshClearsPreviousReadingsAndTemperatureEligibility()
        {
            var done = new TaskCompletionSource<bool>();
            var thread = new Thread(() => {
                try {
                    int calls = 0;
                    var snapshot = new CpuOverviewSnapshot(DateTime.UtcNow, DateTime.UtcNow, "Synthetic", true, new OverviewReading[0], new string[0]);
                    using (var form = new CpuOverviewForm(() => ++calls == 1 ? Task.FromResult(snapshot) : Task.FromException<CpuOverviewSnapshot>(new Exception("Disconnected")), _ => throw new Exception("No write"))) {
                        form.RefreshSnapshotAsync().GetAwaiter().GetResult();
                        var apply = Descendants(form).OfType<Button>().Single(b => b.Name == "applyTemperature"); Assert.True(apply.Enabled);
                        form.RefreshSnapshotAsync().GetAwaiter().GetResult(); Assert.False(apply.Enabled);
                        Assert.Null(Descendants(form).OfType<DataGridView>().Single().DataSource);
                    }
                    done.SetResult(true);
                } catch (Exception ex) { done.SetException(ex); }
            });
            thread.SetApartmentState(ApartmentState.STA); thread.Start(); await done.Task;
        }
        [Fact]
        public async Task PendingRefreshIsSingleFlightAndClosingDoesNotWaitOrWrite()
        {
            var done = new TaskCompletionSource<bool>();
            var thread = new Thread(() => {
                try {
                    int calls = 0;
                    var pending = new TaskCompletionSource<CpuOverviewSnapshot>();
                    var form = new CpuOverviewForm(() => { calls++; return pending.Task; }, _ => throw new Exception("No write"));
                    var first = form.RefreshSnapshotAsync(); Assert.False(first.IsCompleted);
                    form.RefreshSnapshotAsync().GetAwaiter().GetResult(); Assert.Equal(1, calls);
                    form.Dispose(); Assert.False(first.IsCompleted);
                    pending.SetResult(new CpuOverviewSnapshot(DateTime.UtcNow, DateTime.UtcNow, "Synthetic", false, new OverviewReading[0], new string[0]));
                    first.GetAwaiter().GetResult(); done.SetResult(true);
                } catch (Exception ex) { done.SetException(ex); }
            });
            thread.SetApartmentState(ApartmentState.STA); thread.Start(); await done.Task;
        }
    }
}
