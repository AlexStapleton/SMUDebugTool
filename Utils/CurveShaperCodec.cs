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
    }
}
