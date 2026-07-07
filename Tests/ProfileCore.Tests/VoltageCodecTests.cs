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
    }
}
