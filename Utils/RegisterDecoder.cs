using System;

namespace ZenStatesDebugTool
{
    public static class RegisterDecoder
    {
        // Extract bits [hi:lo] (inclusive) from a 64-bit value.
        public static ulong Extract(ulong value, int hi, int lo)
        {
            if (lo < 0)
                throw new ArgumentOutOfRangeException(nameof(lo), "lo must be >= 0.");
            if (hi < lo || hi > 63)
                throw new ArgumentOutOfRangeException(nameof(hi), "hi must be in [lo, 63].");

            int width = hi - lo + 1;
            ulong mask = width >= 64 ? ulong.MaxValue : (1UL << width) - 1UL;
            return (value >> lo) & mask;
        }
    }
}
