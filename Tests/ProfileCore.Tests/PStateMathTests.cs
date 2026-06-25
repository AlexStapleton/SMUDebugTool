using ZenStatesDebugTool;
using Xunit;

namespace ProfileCore.Tests
{
    public class PStateMathTests
    {
        // eax for CpuFid=0xA8(168), CpuDfsId=8, CpuVid=0x28(40): (40<<14)|(8<<8)|168 = 0x000A08A8.
        private const uint SampleEax = 0x000A08A8;

        [Fact]
        public void Decode_extracts_fields()
        {
            PStateValues v = PStateMath.Decode(SampleEax);
            Assert.Equal(168u, v.CpuFid);
            Assert.Equal(8u, v.CpuDfsId);
            Assert.Equal(40u, v.CpuVid);
            Assert.Equal(0u, v.IddVal);
            Assert.Equal(0u, v.IddDiv);
        }

        [Fact]
        public void Encode_round_trips_with_decode()
        {
            PStateValues v = PStateMath.Decode(SampleEax);
            uint encoded = PStateMath.Encode(v.IddDiv, v.IddVal, v.CpuVid, v.CpuDfsId, v.CpuFid);
            Assert.Equal(SampleEax, encoded);
        }

        [Fact]
        public void FrequencyMhz_matches_legacy_formula()
        {
            Assert.Equal(4200.0, PStateMath.FrequencyMhz(168, 8));
        }

        [Fact]
        public void FrequencyMhz_returns_zero_when_did_zero()
        {
            Assert.Equal(0.0, PStateMath.FrequencyMhz(168, 0));
        }
    }
}
