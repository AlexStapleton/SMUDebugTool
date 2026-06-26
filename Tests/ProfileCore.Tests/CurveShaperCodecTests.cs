using System.Collections.Generic;
using ZenStatesDebugTool;
using ZenStatesDebugTool.Profiles;
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

        [Fact]
        public void PackProfileTiers_packs_each_tier_into_the_margin_word_format()
        {
            var tiers = new List<CurveShaperTier>
            {
                new CurveShaperTier { Low = 1, Medium = 2, High = 3 },
                new CurveShaperTier(), new CurveShaperTier(),
                new CurveShaperTier(), new CurveShaperTier(),
            };

            uint[] words = CurveShaperCodec.PackProfileTiers(tiers);

            Assert.Equal(5, words.Length);
            Assert.Equal(0x03020100u, words[0]); // low=1, med=2, high=3
            Assert.Equal(0u, words[1]);
        }

        [Fact]
        public void PackProfileTiers_returns_null_when_there_are_no_tiers()
        {
            Assert.Null(CurveShaperCodec.PackProfileTiers(null));
            Assert.Null(CurveShaperCodec.PackProfileTiers(new List<CurveShaperTier>()));
        }

        [Fact]
        public void ResolveDisplay_prefers_a_live_hardware_read()
        {
            var hardware = new uint[] { 0x05000000, 0, 0, 0, 0 };
            var lastApplied = new uint[] { 0x09000000, 0, 0, 0, 0 };

            Assert.Same(hardware, CurveShaperCodec.ResolveDisplay(hardware, lastApplied));
        }

        [Fact]
        public void ResolveDisplay_falls_back_when_hardware_reports_all_zeros()
        {
            var hardware = new uint[5];
            var lastApplied = new uint[] { 0x09000000, 0, 0, 0, 0 };

            Assert.Same(lastApplied, CurveShaperCodec.ResolveDisplay(hardware, lastApplied));
        }

        [Fact]
        public void ResolveDisplay_keeps_zeros_when_there_is_no_fallback()
        {
            var hardware = new uint[5];
            Assert.Same(hardware, CurveShaperCodec.ResolveDisplay(hardware, null));
        }

        [Fact]
        public void Startup_profile_curve_shaper_survives_a_zero_hardware_readback()
        {
            // Reproduces the reported bug: the Default profile sets the Minimum tier to
            // 2/2/2, the headless startup task applies it, but this CPU reports all-zeros
            // from GetAllCurveShaperMargins. The grid must still show the applied values.
            var tiers = new List<CurveShaperTier>
            {
                new CurveShaperTier { Low = 2, Medium = 2, High = 2 },
                new CurveShaperTier(), new CurveShaperTier(),
                new CurveShaperTier(), new CurveShaperTier(),
            };

            uint[] seeded = CurveShaperCodec.PackProfileTiers(tiers);
            uint[] hardware = new uint[5]; // no read-back on this CPU

            uint[] shown = CurveShaperCodec.ResolveDisplay(hardware, seeded);

            CurveShaperCodec.Unpack(shown[0], out int low, out int med, out int high);
            Assert.Equal(2, low);
            Assert.Equal(2, med);
            Assert.Equal(2, high);
        }
    }
}
