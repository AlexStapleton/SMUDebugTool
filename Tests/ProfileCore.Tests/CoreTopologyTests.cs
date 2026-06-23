using ZenStatesDebugTool;
using Xunit;

namespace ProfileCore.Tests
{
    public class CoreTopologyTests
    {
        // --- IsCoreEnabled ---

        [Fact]
        public void AllEnabled_WhenDisableMapIsZero()
        {
            var map = new uint[] { 0x0 };
            for (int i = 0; i < 8; i++)
                Assert.True(CoreTopology.IsCoreEnabled(map, i));
        }

        [Fact]
        public void DisabledBit_MarksCoreDisabled()
        {
            // Bit 2 set => core 2 disabled, the rest of the group enabled.
            var map = new uint[] { 0x4 };
            Assert.True(CoreTopology.IsCoreEnabled(map, 0));
            Assert.True(CoreTopology.IsCoreEnabled(map, 1));
            Assert.False(CoreTopology.IsCoreEnabled(map, 2));
            Assert.True(CoreTopology.IsCoreEnabled(map, 3));
        }

        [Fact]
        public void SecondGroup_UsesSecondMapEntry()
        {
            // Group 0 fully enabled, group 1 has core 8 (bit 0 of entry 1) disabled.
            var map = new uint[] { 0x0, 0x1 };
            Assert.True(CoreTopology.IsCoreEnabled(map, 7));
            Assert.False(CoreTopology.IsCoreEnabled(map, 8));
            Assert.True(CoreTopology.IsCoreEnabled(map, 9));
        }

        [Fact]
        public void OutOfRangeOrNull_IsNotEnabled()
        {
            Assert.False(CoreTopology.IsCoreEnabled(null, 0));
            Assert.False(CoreTopology.IsCoreEnabled(new uint[] { 0x0 }, 8)); // mapIndex 1 is out of range
        }

        // --- EncodeCoreMarginBitmask ---

        [Fact]
        public void Apu_UsesFlatCoreIndex()
        {
            Assert.Equal(0u, CoreTopology.EncodeCoreMarginBitmask(isApu: true, coreIndex: 0));
            Assert.Equal(5u, CoreTopology.EncodeCoreMarginBitmask(isApu: true, coreIndex: 5));
        }

        [Theory]
        // core 0, CCD 0 -> mask 0 << 20
        [InlineData(0, 0u)]
        // core 3, CCD 0 -> (0<<8 | 3) << 20 = 3 << 20
        [InlineData(3, 3u << 20)]
        // core 8, CCD 1 -> (1<<8 | 0) << 20 = 0x100 << 20
        [InlineData(8, 0x100u << 20)]
        // core 9, CCD 1 -> (1<<8 | 1) << 20 = 0x101 << 20
        [InlineData(9, 0x101u << 20)]
        public void Desktop_PacksCcdAndLocalCore(int coreIndex, uint expected)
        {
            Assert.Equal(expected, CoreTopology.EncodeCoreMarginBitmask(isApu: false, coreIndex));
        }
    }
}
