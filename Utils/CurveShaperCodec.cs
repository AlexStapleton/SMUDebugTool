using System.Collections.Generic;
using ZenStatesDebugTool.Profiles;

namespace ZenStatesDebugTool
{
    // Pure Curve Shaper margin packing/unpacking, extracted verbatim from SettingsForm.
    // A tier word packs three signed-byte margins: low in bits 8-15, med 16-23, high 24-31.
    public static class CurveShaperCodec
    {
        // One margin byte (0-255) -> signed margin (-128..127). Matches the old
        // ConvertMarginToInt; the unchecked cast is required (default context narrows uint->sbyte).
        public static int UnpackMargin(uint value) => (sbyte)(unchecked(value));

        public static void Unpack(uint tierWord, out int low, out int med, out int high)
        {
            low = UnpackMargin(tierWord >> 8 & 0xFF);
            med = UnpackMargin(tierWord >> 16 & 0xFF);
            high = UnpackMargin(tierWord >> 24 & 0xFF);
        }

        public static uint Pack(int low, int med, int high)
        {
            return ((uint)(byte)(sbyte)low << 8)
                 | ((uint)(byte)(sbyte)med << 16)
                 | ((uint)(byte)(sbyte)high << 24);
        }

        public static bool IsAllZero(uint[] values)
        {
            if (values == null) return true;
            foreach (uint v in values)
                if (v != 0) return false;
            return true;
        }

        // Packs a profile's Curve Shaper tiers into the uint[5] word format that
        // GetAllCurveShaperMargins returns (low/med/high in bits 8/16/24 per tier), so the
        // startup-applied values can seed the session fallback. Returns null when the profile
        // carries no Curve Shaper, meaning "nothing to seed".
        public static uint[] PackProfileTiers(IList<CurveShaperTier> tiers)
        {
            if (tiers == null || tiers.Count == 0) return null;
            var words = new uint[5];
            for (int tier = 0; tier < 5 && tier < tiers.Count; tier++)
            {
                CurveShaperTier t = tiers[tier];
                if (t != null)
                    words[tier] = Pack(t.Low, t.Medium, t.High);
            }
            return words;
        }

        // Chooses what to show in the grid: a live hardware read normally, but the last-applied
        // values when the CPU reports all-zeros (some CPUs don't report Curve Shaper margins
        // back, so a blind hardware read would wipe values that are actually set).
        public static uint[] ResolveDisplay(uint[] hardware, uint[] lastApplied)
        {
            return (IsAllZero(hardware) && lastApplied != null) ? lastApplied : hardware;
        }
    }
}
