using System;
using System.Text;

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

        // Returns a formatted, human-readable block for a recognized register,
        // or "" when the register is unknown. Never throws.
        public static string Decode(RegisterKind kind, uint address, ulong value, DecodeContext context = null)
        {
            if (!RegisterCatalog.TryGet(kind, address, out RegisterDefinition def))
                return "";

            DecodeContext ctx = context ?? DecodeContext.None;
            var sb = new StringBuilder();
            sb.AppendLine($"{def.Name} (0x{address:X8}) - {def.Description}");

            foreach (FieldDefinition f in def.Fields)
            {
                ulong fieldVal;
                // A bad bit range is a catalog-data bug, not a runtime condition;
                // skip the field rather than failing the whole decode.
                try { fieldVal = Extract(value, f.HighBit, f.LowBit); }
                catch (ArgumentOutOfRangeException) { continue; }

                string bits = f.HighBit == f.LowBit ? $"{f.HighBit}" : $"{f.HighBit}:{f.LowBit}";
                sb.AppendLine($"  {f.Name} [{bits}] = 0x{fieldVal:X} ({fieldVal})");
            }

            foreach (var derive in def.Derived)
            {
                string line;
                // Derived delegates do arithmetic / call external helpers (e.g. voltage);
                // a failure there must not abort the rest of the decode.
                try { line = derive(value, ctx); }
                catch (Exception) { line = null; }
                if (!string.IsNullOrEmpty(line))
                    sb.AppendLine($"  -> {line}");
            }

            return sb.ToString();
        }
    }
}
