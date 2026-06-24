using ZenStatesDebugTool;
using Xunit;

namespace ProfileCore.Tests
{
    public class CurveShaperCodecTests
    {
        [Theory]
        [InlineData(0u, 0)]
        [InlineData(5u, 5)]
        [InlineData(0xFFu, -1)]   // 255 -> -1
        [InlineData(0x80u, -128)] // 128 -> -128
        [InlineData(0x7Fu, 127)]
        public void UnpackMargin_treats_byte_as_signed(uint raw, int expected)
        {
            Assert.Equal(expected, CurveShaperCodec.UnpackMargin(raw));
        }

        [Fact]
        public void Pack_then_Unpack_round_trips_including_negatives()
        {
            uint word = CurveShaperCodec.Pack(-5, 10, -30);
            CurveShaperCodec.Unpack(word, out int low, out int med, out int high);
            Assert.Equal(-5, low);
            Assert.Equal(10, med);
            Assert.Equal(-30, high);
        }

        [Fact]
        public void Pack_places_margins_in_bits_8_16_24()
        {
            // low=1, med=2, high=3 -> 0x03020100
            Assert.Equal(0x03020100u, CurveShaperCodec.Pack(1, 2, 3));
        }

        [Fact]
        public void IsAllZero_true_for_null_and_zeros_false_otherwise()
        {
            Assert.True(CurveShaperCodec.IsAllZero(null));
            Assert.True(CurveShaperCodec.IsAllZero(new uint[] { 0, 0, 0 }));
            Assert.False(CurveShaperCodec.IsAllZero(new uint[] { 0, 1, 0 }));
        }
    }
}
