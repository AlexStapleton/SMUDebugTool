using ZenStatesDebugTool;
using Xunit;

namespace ProfileCore.Tests
{
    public class PStateMathTests
    {
        [Theory]
        [InlineData(0x17u, PStateLayout.FidDid)]
        [InlineData(0x19u, PStateLayout.FidDid)]
        [InlineData(0x1Au, PStateLayout.Zen5)]
        [InlineData(0u, PStateLayout.Unknown)]
        [InlineData(0x1Bu, PStateLayout.Unknown)]
        public void Layout_requires_a_known_family(uint family, PStateLayout expected)
            => Assert.Equal(expected, PStateMath.LayoutForFamily(family));

        [Theory]
        [InlineData(PStateLayout.FidDid, 0x80000000000A08A8UL, 168u, 8u, 4200.0)]
        [InlineData(PStateLayout.Zen5, 0x80000001000A33E8UL, 1000u, 0u, 5000.0)]
        [InlineData(PStateLayout.Zen5, 0x8000000000000FFFUL, 4095u, 0u, 20475.0)]
        public void Decode_and_frequency_use_the_family_layout(PStateLayout layout, ulong raw,
            uint fid, uint did, double mhz)
        {
            var value = PStateMath.Decode(raw, layout);
            Assert.True(value.Enabled);
            Assert.Equal(fid, value.CpuFid);
            Assert.Equal(did, value.CpuDfsId);
            Assert.Equal(mhz, PStateMath.FrequencyMhz(layout, fid, did));
        }

        [Theory]
        [InlineData(PStateLayout.FidDid, 256u, 8u)]
        [InlineData(PStateLayout.FidDid, 168u, 0u)]
        [InlineData(PStateLayout.FidDid, 168u, 64u)]
        [InlineData(PStateLayout.FidDid, 15u, 8u)]
        [InlineData(PStateLayout.Zen5, 4096u, 0u)]
        [InlineData(PStateLayout.Zen5, 15u, 0u)]
        [InlineData(PStateLayout.Zen5, 1000u, 8u)]
        [InlineData(PStateLayout.Unknown, 1000u, 0u)]
        public void Invalid_input_never_gets_masked_into_a_write(PStateLayout layout, uint fid, uint did)
        {
            const ulong original = 0xFEDCBA9876543210;
            Assert.Null(PStateMath.FrequencyMhz(layout, fid, did));
            Assert.False(PStateMath.TryWithFrequency(original, layout, fid, did, out var updated, out var error));
            Assert.Equal(original, updated);
            Assert.NotNull(error);
        }

        [Theory]
        [InlineData(PStateLayout.FidDid, 168u, 8u, 0x3FFFUL)]
        [InlineData(PStateLayout.Zen5, 1000u, 0u, 0xFFFUL)]
        public void Updates_preserve_every_non_frequency_bit(PStateLayout layout, uint fid, uint did, ulong mask)
        {
            foreach (ulong original in new[] { 0UL, ulong.MaxValue, 0xFEDCBA9876543210UL, 0x80000001000A3000UL })
            {
                Assert.True(PStateMath.TryWithFrequency(original, layout, fid, did, out var updated, out var error));
                Assert.Null(error);
                Assert.Equal(original & ~mask, updated & ~mask);
                Assert.Equal(fid, PStateMath.Decode(updated, layout).CpuFid);
                Assert.Equal(did, PStateMath.Decode(updated, layout).CpuDfsId);
            }
        }

        [Theory]
        [InlineData("")]
        [InlineData("-1")]
        [InlineData("+168")]
        [InlineData("1.5")]
        [InlineData("4294967296")]
        [InlineData(" 168")]
        [InlineData("0xA8")]
        public void Parsing_rejects_invalid_or_ambiguous_text(string text)
            => Assert.False(PStateMath.TryParse(PStateLayout.FidDid, text, "8", out _, out _, out _));

        [Fact]
        public void Zen5_text_input_does_not_require_a_divider()
        {
            Assert.True(PStateMath.TryParse(PStateLayout.Zen5, "1000", "N/A", out var fid, out var did, out _));
            Assert.Equal(1000u, fid);
            Assert.Equal(0u, did);
        }
    }
}
