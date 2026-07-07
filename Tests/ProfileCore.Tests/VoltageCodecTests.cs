using ZenStatesDebugTool;
using Xunit;

namespace ProfileCore.Tests
{
    public class VoltageCodecTests
    {
        [Theory]
        [InlineData(191u, true, 1.200)]   // SVI3: 0.245 + 191*0.005 = 1.200
        [InlineData(0u, true, 0.245)]     // SVI3 floor
        [InlineData(56u, false, 1.200)]   // SVI2: 1.55 - 56/160 = 1.200
        [InlineData(0u, false, 1.550)]    // SVI2: vid 0 = 1.55 V
        public void VidToVoltage_matches_generation_formula(uint vid, bool svi3, double expected)
        {
            Assert.Equal(expected, VoltageCodec.VidToVoltage(vid, svi3), 3);
        }

        [Theory]
        [InlineData(1.200, true, 191u)]    // SVI3 known value
        [InlineData(1.200, false, 56u)]    // SVI2 known value
        [InlineData(1.550, true, 255u)]    // SVI3: (1.55-0.245)/0.005 = 261 -> clamp 255
        [InlineData(0.100, true, 0u)]      // below SVI3 floor -> 0
        [InlineData(2.000, false, 0u)]     // SVI2: (1.55-2.0)*160 < 0 -> clamp 0
        public void VoltageToVid_converts_and_clamps(double volts, bool svi3, uint expected)
        {
            Assert.Equal(expected, VoltageCodec.VoltageToVid(volts, svi3));
        }

        [Theory]
        [InlineData(0.900, true)]
        [InlineData(1.350, true)]
        [InlineData(0.900, false)]
        [InlineData(1.350, false)]
        public void VoltageToVid_then_VidToVoltage_round_trips_within_one_step(double volts, bool svi3)
        {
            uint vid = VoltageCodec.VoltageToVid(volts, svi3);
            double back = VoltageCodec.VidToVoltage(vid, svi3);
            Assert.True(System.Math.Abs(back - volts) <= 0.00625, $"got {back} for {volts}");
        }

        [Theory]
        [InlineData("AMD Ryzen 9 9950X3D 16-Core Processor", true)]
        [InlineData("AMD Ryzen 7 5800X3D 8-Core Processor", true)]
        [InlineData("amd ryzen 7 7800x3d", true)]   // case-insensitive
        [InlineData("AMD Ryzen 9 5950X 16-Core Processor", false)]
        [InlineData("AMD Ryzen 9 9950X 16-Core Processor", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsX3D_detects_vcache_parts_by_name(string cpuName, bool expected)
        {
            Assert.Equal(expected, VoltageCodec.IsX3D(cpuName));
        }
    }
}
