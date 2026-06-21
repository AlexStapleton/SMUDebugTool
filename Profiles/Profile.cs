using System.Collections.Generic;

namespace ZenStatesDebugTool.Profiles
{
    public class CurveShaperTier
    {
        public int Low { get; set; }
        public int Medium { get; set; }
        public int High { get; set; }
    }

    public class Profile
    {
        public string Name { get; set; }

        // Core index -> CO margin value.
        public Dictionary<int, int> CoMargins { get; set; } = new Dictionary<int, int>();

        // Exactly 5 tiers in order: min(0), low(1), med(2), high(3), max(4).
        // Null = "do not apply Curve Shaper".
        public List<CurveShaperTier> CurveShaperTiers { get; set; }

        public decimal? Fmax { get; set; }

        // PBO power limits (units verified against the DLL later).
        public int? PptWatts { get; set; }
        public int? TdcAmps { get; set; }
        public int? EdcAmps { get; set; }
        public int? PboScalar { get; set; }
    }
}
