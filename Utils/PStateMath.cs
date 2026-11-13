using System.Globalization;

namespace ZenStatesDebugTool
{
    public enum PStateLayout { Unknown, FidDid, Zen5 }

    public struct PStateValues
    {
        public uint CpuFid;
        public uint CpuDfsId;
        public bool Enabled;
    }

    // Frequency-only codec. Replace fields in the original register so VID, IDD,
    // PstateEn and reserved bits survive a frequency edit.
    public static class PStateMath
    {
        public const uint BaseMsr = 0xC0010064;
        public const int Count = 8;

        public static PStateLayout LayoutForFamily(uint family)
        {
            switch (family)
            {
                case 0x17:
                case 0x19: return PStateLayout.FidDid;
                case 0x1A: return PStateLayout.Zen5;
                default: return PStateLayout.Unknown;
            }
        }

        public static ulong FrequencyMask(PStateLayout layout)
        {
            switch (layout)
            {
                case PStateLayout.FidDid: return 0x3FFF;
                case PStateLayout.Zen5: return 0xFFF;
                default: return 0;
            }
        }

        public static PStateValues Decode(ulong value, PStateLayout layout)
        {
            return new PStateValues
            {
                CpuFid = (uint)(value & (layout == PStateLayout.Zen5 ? 0xFFFUL :
                    layout == PStateLayout.FidDid ? 0xFFUL : 0)),
                CpuDfsId = layout == PStateLayout.FidDid ? (uint)(value >> 8 & 0x3F) : 0,
                Enabled = (value & (1UL << 63)) != 0
            };
        }

        public static string Validate(PStateLayout layout, uint fid, uint did)
        {
            switch (layout)
            {
                case PStateLayout.FidDid:
                    if (fid < 0x10 || fid > 0xFF) return "FID must be between 16 and 255.";
                    if (did < 1 || did > 0x3F) return "DID must be between 1 and 63.";
                    return null;
                case PStateLayout.Zen5:
                    if (fid < 0x10 || fid > 0xFFF) return "FID must be between 16 and 4095.";
                    if (did != 0) return "DID is not used on this CPU.";
                    return null;
                default: return "P-state frequency editing is not supported for this CPU family.";
            }
        }

        public static bool TryParse(PStateLayout layout, string fidText, string didText,
            out uint fid, out uint did, out string error)
        {
            fid = did = 0;
            if (!uint.TryParse(fidText, NumberStyles.None, CultureInfo.InvariantCulture, out fid)
                || (layout == PStateLayout.FidDid &&
                    !uint.TryParse(didText, NumberStyles.None, CultureInfo.InvariantCulture, out did)))
            {
                error = "Enter whole decimal FID/DID values within the supported ranges.";
                return false;
            }
            error = Validate(layout, fid, did);
            return error == null;
        }

        public static double? FrequencyMhz(PStateLayout layout, uint fid, uint did)
        {
            if (Validate(layout, fid, did) != null) return null;
            return layout == PStateLayout.Zen5 ? fid * 5.0 : 200.0 * fid / did;
        }

        public static bool TryWithFrequency(ulong original, PStateLayout layout,
            uint fid, uint did, out ulong updated, out string error)
        {
            updated = original;
            error = Validate(layout, fid, did);
            if (error != null) return false;
            ulong bits = fid | (layout == PStateLayout.FidDid ? (ulong)did << 8 : 0);
            updated = (original & ~FrequencyMask(layout)) | bits;
            return true;
        }
    }
}
