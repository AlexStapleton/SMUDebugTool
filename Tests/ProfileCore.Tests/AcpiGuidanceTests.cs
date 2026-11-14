using System;
using System.Linq;
using Xunit;
using ZenStatesDebugTool;

namespace ProfileCore.Tests
{
    public class AcpiGuidanceTests
    {
        [Theory]
        [InlineData("Set Tcl: 00020002", "CAS latency", "memory clock cycles")]
        [InlineData("Set Trp", "Row precharge", "memory clock cycles")]
        [InlineData("Set TrfcSb", "Same-bank refresh", "memory clock cycles")]
        public void TimingCommandsHaveNamesAndCycleUnits(string command, string title, string unit)
        {
            var info = AcpiCommandHelp.For(command, 0x20002, true);
            Assert.Contains(title, info.Title); Assert.Equal(unit, info.Unit);
            Assert.Contains("example, not a recommendation", info.Example);
        }
        [Fact]
        public void LinearTclRangeChecksMinMaxAndStepWithoutGuessingDefault()
        {
            var info = AcpiCommandHelp.For("Set Tcl", 0x20002, true);
            var metadata = AcpiValueMetadata.Range(info.Id, info.Id, 20, 64, 2, 36);
            Assert.Empty(metadata.Choices); Assert.Equal((uint)36, metadata.Current);
            Assert.True(info.TryArgument("30", metadata, out uint value, out _)); Assert.Equal((uint)30, value);
            Assert.False(info.TryArgument("31", metadata, out _, out string error)); Assert.Contains("step 2", error);
            Assert.False(info.TryArgument("18", metadata, out _, out _)); Assert.False(info.TryArgument("66", metadata, out _, out _));
        }
        [Fact]
        public void DiscreteMetadataHonorsLengthAndPreservesValidZero()
        {
            uint[] buffer = { 0, 1, 2, 0, 99 };
            var data = AcpiValueMetadata.Discrete(5, 5, 3, buffer, 0); buffer[0] = 42;
            Assert.Equal(new uint[] { 0, 1, 2 }, data.Choices);
            Assert.Equal((uint)0, data.Current); Assert.False(data.Allows(99));
        }
        [Theory]
        [InlineData(0, 0, 0)]
        [InlineData(20, 10, 1)]
        [InlineData(10, 20, 0)]
        public void InvalidRangesRemainUnknown(uint min, uint max, uint step)
        {
            var metadata = AcpiValueMetadata.Range(2, 2, min, max, step, 0);
            Assert.False(metadata.HasDomain); Assert.Null(metadata.Current);
        }
        [Fact]
        public void WrongIdsAndOversizedListsAreNotUsed()
        {
            Assert.False(AcpiValueMetadata.Range(1, 2, 1, 10, 1, 5).HasDomain);
            Assert.False(AcpiValueMetadata.Discrete(1, 2, 1, new uint[] { 1 }, 1).HasDomain);
            Assert.False(AcpiValueMetadata.Discrete(1, 1, 101, new uint[101], 0).HasDomain);
            Assert.False(AcpiValueMetadata.Discrete(1, 1, 2, new uint[1], 0).HasDomain);
        }
        [Fact]
        public void ZeroRangeCurrentIsNotClaimedAsARealMeasurement()
        {
            Assert.Null(AcpiValueMetadata.Range(1, 1, 0, 100, 1, 0).Current);
        }
        [Theory]
        [InlineData(0x50001, "W")]
        [InlineData(0x50002, "A")]
        [InlineData(0x50003, "A")]
        public void KnownPowerAndCurrentInputsConvertDisplayedUnitsToFull32BitFirmwareArgument(uint id, string unit)
        {
            var info = AcpiCommandHelp.For("Set limit", id, true);
            var metadata = AcpiValueMetadata.Range(id, id, 0, 300000, 500, 150000);
            Assert.Equal(unit, info.Unit); Assert.Equal("150", info.Format(metadata.Current.Value));
            Assert.True(info.TryArgument("150.5", metadata, out uint value, out _)); Assert.Equal((uint)150500, value);
            Assert.False(info.TryArgument("150.25", metadata, out _, out _));
        }
        [Theory]
        [InlineData("")]
        [InlineData("-1")]
        [InlineData("30.5")]
        [InlineData("4294967296")]
        [InlineData("1,000")]
        [InlineData("1e2")]
        public void UnscaledArgumentsRejectMalformedOrFractionalValues(string text)
        {
            var info = AcpiCommandHelp.For("Set Tcl", 0x20002, true);
            Assert.False(info.TryArgument(text, AcpiValueMetadata.Unknown(info.Id), out _, out _));
        }
        [Fact]
        public void RawArgumentsSupportHexAndUint32WithoutSilentSixteenBitDrop()
        {
            var info = AcpiCommandHelp.For("Set opaque", 7, true);
            Assert.True(info.TryArgument("0xFFFFFFFF", AcpiValueMetadata.Unknown(7), out uint value, out _));
            Assert.Equal(uint.MaxValue, value);
            Assert.True(info.TryArgument("150000", AcpiValueMetadata.Unknown(7), out value, out _)); Assert.Equal((uint)150000, value);
            Assert.False(info.TryArgument("1", AcpiValueMetadata.Unknown(8), out _, out _));
        }
        [Fact]
        public void UnknownVoltageEncodingIsExplicitlyRaw()
        {
            var info = AcpiCommandHelp.For("Set VDDG IOD", 0x999, true);
            Assert.Equal("raw firmware units", info.Unit); Assert.Equal(1, info.Scale);
            Assert.Contains("not verified", info.Description); Assert.Contains("not volts", info.Description);
        }
        [Fact]
        public void FirmwareAcceptanceIsNotReportedAsLiveVerification()
        {
            var info = AcpiCommandHelp.For("Set Tcl", 0x20002, true);
            Assert.Contains("outcome is unknown", info.ResponseText(null, 30));
            Assert.Contains("outcome is unknown", info.ResponseText(new byte[5], 30));
            var accepted = new byte[200];
            Assert.Equal((uint)0, AcpiCommandHelp.ResponseStatus(accepted));
            Assert.Contains("Active hardware value is not verified", info.ResponseText(accepted, 30));
            Assert.Contains("restart may be required", info.ResponseText(accepted, 30));
            accepted[2] = 1; Assert.Contains("rejected", info.ResponseText(accepted, 30));
        }
        [Theory]
        [InlineData("tabPageCPU")]
        [InlineData("tabPageSmu")]
        [InlineData("tabPagePci")]
        [InlineData("tabPageMsr")]
        [InlineData("tabPageCPUID")]
        [InlineData("tabPagePbo")]
        [InlineData("tabPageCS")]
        [InlineData("tabPageWmi")]
        [InlineData("tabPagePstates")]
        [InlineData("tabPageInfo")]
        [InlineData("tabPageFreqOC")]
        public void EveryExistingTabHasSpecificGuidance(string name)
        {
            var guide = TabGuidance.For(name);
            Assert.True(guide.Summary.Length > 20); Assert.True(guide.Instructions.Length > 100);
            Assert.DoesNotContain("Input formats and meanings depend", guide.Instructions);
        }
        [Fact]
        public void FriendlyCommandNamesRetainRawIdentityForRoutingAndDiagnostics()
        {
            var item = new WmiCmdListItem("Set Tcl: 00020002", 0x20002, true, 1, true);
            Assert.Equal((uint)0x20002, item.value); Assert.Equal((uint)1, item.PropertyFlag);
            Assert.Contains("CAS latency", item.ToString()); Assert.DoesNotContain("00020002", item.ToString());
            Assert.Equal("Set Tcl: 00020002", item.text);
        }
    }
}
