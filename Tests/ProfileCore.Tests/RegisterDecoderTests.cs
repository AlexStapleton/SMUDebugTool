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

        [Theory]
        [InlineData(0, -1)]   // lo < 0
        [InlineData(3, 5)]    // hi < lo
        [InlineData(64, 0)]   // hi > 63
        public void Extract_throws_on_invalid_range(int hi, int lo)
        {
            Assert.Throws<System.ArgumentOutOfRangeException>(() => RegisterDecoder.Extract(0xFFUL, hi, lo));
        }

        [Fact]
        public void Decode_unknown_register_returns_empty_string()
        {
            Assert.Equal("", RegisterDecoder.Decode(RegisterKind.Msr, 0xDEADBEEF, 0x12345678UL));
        }

        // CpuFid=0xA8(168), CpuDfsId=8, CpuVid=0x28(40), PstateEn=1 (bit 63).
        // eax = (40<<14)|(8<<8)|168 = 0x000A08A8 ; edx = 0x80000000 (PstateEn).
        private const ulong SamplePStateDef = (0x80000000UL << 32) | 0x000A08A8UL;

        private static readonly DecodeContext Svi2Ctx =
            new DecodeContext { VidToVoltage = v => 1.55 - 0.00625 * v };

        [Fact]
        public void Decode_pstatedef_shows_name_fields_and_derived_values()
        {
            string s = RegisterDecoder.Decode(RegisterKind.Msr, 0xC0010064, SamplePStateDef, Svi2Ctx);

            Assert.Contains("PStateDef0 (0xC0010064) - P-State 0 definition", s);
            Assert.Contains("CpuFid [7:0] = 0xA8 (168)", s);
            Assert.Contains("CpuDfsId [13:8] = 0x8 (8)", s);
            Assert.Contains("CpuVid [21:14] = 0x28 (40)", s);
            Assert.Contains("PstateEn [63] = 0x1 (1)", s);
            Assert.Contains("-> Frequency: 4200 MHz", s);
            Assert.Contains("-> Voltage: 1.300 V", s);
        }

        [Fact]
        public void Decode_pstatedef_skips_voltage_when_no_context_helper()
        {
            string s = RegisterDecoder.Decode(RegisterKind.Msr, 0xC0010064, SamplePStateDef);
            Assert.Contains("-> Frequency: 4200 MHz", s);
            Assert.DoesNotContain("Voltage:", s);
        }

        [Fact]
        public void Decode_pstatedef_address_offsets_resolve()
        {
            // 0xC001006B is PStateDef7.
            string s = RegisterDecoder.Decode(RegisterKind.Msr, 0xC001006B, SamplePStateDef, Svi2Ctx);
            Assert.Contains("PStateDef7 (0xC001006B)", s);
        }

        [Fact]
        public void Decode_hwcr_resolves_name_and_fields()
        {
            string s = RegisterDecoder.Decode(RegisterKind.Msr, 0xC0010015, 0x02000000UL);
            Assert.Contains("HWCR (0xC0010015) - Hardware Configuration", s);
            Assert.Contains("Cpb (boost) Dis [25] = 0x1 (1)", s);
        }

        [Fact]
        public void Decode_cpuid_leaf1_eax_resolves_family_model()
        {
            // eax for a Zen part, e.g. 0x00A20F12: ExtFamily=0xA, BaseFamily=0xF, ExtModel=2, Stepping=2.
            string s = RegisterDecoder.Decode(RegisterKind.Cpuid, 0x00000001, 0x00A20F12UL);
            Assert.Contains("CPUID_1_EAX (0x00000001)", s);
            Assert.Contains("Stepping [3:0] = 0x2 (2)", s);
            Assert.Contains("BaseFamily [11:8] = 0xF (15)", s);
            Assert.Contains("ExtFamily [27:20] = 0xA (10)", s);
        }
    }
}
