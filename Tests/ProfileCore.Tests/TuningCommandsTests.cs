using System;
using Xunit;
using ZenStatesDebugTool;

namespace ProfileCore.Tests
{
    public class TuningCommandsTests
    {
        [Theory]
        [InlineData(-999)]
        [InlineData(-25)]
        [InlineData(0)]
        [InlineData(30)]
        [InlineData(999)]
        public void CoUsesBundledPayloadAndMp1Priority(int value)
        {
            var commands = new TuningCommands { CoMp1 = 11, CoRsmu = 12 };
            var result = commands.Execute(new TuningRequest { Kind = TuningKind.CurveOptimizer, Value = value }, false, 0x10300000,
                (route, command, args) =>
                {
                    Assert.Equal("MP1", route); Assert.Equal(11U, command); Assert.Equal(6, args.Length);
                    Assert.Equal(0x10300000U | ZenStates.Core.Utils.MakePsmMarginArg(value), args[0]);
                    for (int i = 1; i < 6; i++) Assert.Equal(0U, args[i]);
                    return new TuningReply { Status = 0xFE };
                });
            Assert.Equal(0xFEU, result.Status); // No fallback/retry on command rejection.
        }
        [Theory]
        [InlineData(17, 18, "RSMU", 17)]
        [InlineData(0, 18, "MP1", 18)]
        public void FmaxRouteUsesRsmuThenMp1(uint rsmu, uint mp1, string expectedRoute, uint expectedCommand)
        {
            var commands = new TuningCommands { FmaxRsmu = rsmu, FmaxMp1 = mp1 };
            commands.Execute(new TuningRequest { Kind = TuningKind.Fmax, Value = 5800 }, false, 0,
                (route, command, args) => { Assert.Equal(expectedRoute, route); Assert.Equal(expectedCommand, command); Assert.Equal(5800U, args[0]); return new TuningReply { Status = 1 }; });
        }
        [Theory]
        [InlineData(-25)]
        [InlineData(0)]
        [InlineData(30)]
        public void DirectCoGetterDecodesSignedIntAndPreservesCoreMask(int observed)
        {
            var commands = new TuningCommands { CoGetRsmu = 19 };
            var reply = commands.Execute(new TuningRequest { Kind = TuningKind.CurveOptimizer }, true, 0x10200000,
                (route, command, args) => { Assert.Equal("RSMU", route); Assert.Equal(0x10200000U, args[0]); args[0] = unchecked((uint)observed); return new TuningReply { Status = 1 }; });
            Assert.True(reply.Direct); Assert.Equal((decimal)observed, reply.Value);
        }
        [Theory]
        [InlineData(true, 3)]
        [InlineData(false, 0)]
        public void ApuFmaxGetterUsesBundledArgument(bool apu, uint expected)
        {
            var commands = new TuningCommands { FmaxGetRsmu = 20, ApuFmaxArgument = apu };
            var reply = commands.Execute(new TuningRequest { Kind = TuningKind.Fmax }, true, 0,
                (route, command, args) => { Assert.Equal(expected, args[0]); args[0] = 5800; return new TuningReply { Status = 1 }; });
            Assert.Equal(5800, reply.Value);
        }
        [Fact]
        public void NoGetterSendsNothingAndFailedGetterDoesNotExposeStaleArgument()
        {
            var commands = new TuningCommands();
            Assert.Null(commands.Execute(new TuningRequest { Kind = TuningKind.Fmax }, true, 0, (r, c, a) => throw new Exception()));
            commands.CoGetRsmu = 2;
            var result = commands.Execute(new TuningRequest { Kind = TuningKind.CurveOptimizer }, true, 0x10000000, (r, c, a) => new TuningReply { Status = 0xFE });
            Assert.Null(result.Value);
        }
        [Fact]
        public void CoUsesRsmuWhenMp1IsAbsentAndSendsNothingWhenNeitherIsDefined()
        {
            var commands = new TuningCommands { CoRsmu = 12 };
            var request = new TuningRequest { Kind = TuningKind.CurveOptimizer, Value = -5 };
            var reply = commands.Execute(request, false, 0, (route, command, args) =>
            {
                Assert.Equal("RSMU", route); Assert.Equal(12U, command); return new TuningReply { Status = 1 };
            });
            Assert.Equal(12U, reply.Command);
            commands.CoRsmu = 0;
            Assert.Null(commands.Execute(request, false, 0, (r, c, a) => throw new Exception("Must not send")));
        }
        [Fact]
        public void PciRangeIsBoundedAndDoesNotWrapNearUintMax()
        {
            Assert.Equal(new[] { uint.MaxValue - 3 }, AddressRange.MonitorAddresses(uint.MaxValue - 3, uint.MaxValue));
            Assert.Throws<ArgumentException>(() => AddressRange.MonitorAddresses(0, uint.MaxValue));
            Assert.Throws<ArgumentException>(() => AddressRange.MonitorAddresses(8, 4));
        }
        [Fact]
        public async System.Threading.Tasks.Task PollerSkipsBusyDriverWithoutBlocking()
        {
            using (var started = new System.Threading.ManualResetEventSlim())
            using (var release = new System.Threading.ManualResetEventSlim())
            {
                var busy = System.Threading.Tasks.Task.Run(() => { lock (Hardware.Sync) { started.Set(); release.Wait(); } });
                Assert.True(started.Wait(5000));
                try { Assert.False(Hardware.TryRead(() => throw new Exception("Must not read"), out int ignored)); }
                finally { release.Set(); await busy; }
            }
        }
    }
}
