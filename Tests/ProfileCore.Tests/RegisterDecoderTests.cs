using ZenStatesDebugTool;
using Xunit;

namespace ProfileCore.Tests
{
    public class RegisterDecoderTests
    {
        [Theory]
        [InlineData(0xA8UL, 7, 0, 0xA8UL)]          // low byte
        [InlineData(0x80000000_00000000UL, 63, 63, 1UL)] // top bit
        [InlineData(0x000A08A8UL, 13, 8, 8UL)]      // CpuDfsId field
        [InlineData(0x000A08A8UL, 21, 14, 40UL)]    // CpuVid field
        [InlineData(0xFFFFFFFF_FFFFFFFFUL, 63, 0, 0xFFFFFFFF_FFFFFFFFUL)] // full width
        public void Extract_returns_expected_bits(ulong value, int hi, int lo, ulong expected)
        {
            Assert.Equal(expected, RegisterDecoder.Extract(value, hi, lo));
        }
    }
}
