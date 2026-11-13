using System;
using System.Collections.Generic;
using System.Globalization;

namespace ZenStatesDebugTool
{
    public static class PStateVidMath
    {
        public static ulong Mask(PStateVidEncoding encoding) => encoding == PStateVidEncoding.Svi2 ? 0xFFUL << 14 :
            encoding == PStateVidEncoding.Svi3 ? (0xFFUL << 14) | (1UL << 32) : 0;
        public static uint Decode(ulong raw, PStateVidEncoding encoding) => (uint)((raw >> 14 & 0xFF) |
            (encoding == PStateVidEncoding.Svi3 ? (raw >> 32 & 1) << 8 : 0));
        public static string Validate(PStateVidEncoding encoding, uint vid)
        {
            if (encoding == PStateVidEncoding.Unknown) return "VID editing is unavailable for this encoding.";
            return vid > (encoding == PStateVidEncoding.Svi3 ? 511U : 255U) ? "VID is outside the register field range." : null;
        }
        public static bool TryParse(PStateVidEncoding encoding, string text, out uint vid) =>
            uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out vid) && Validate(encoding, vid) == null;
        public static ulong WithVid(ulong raw, PStateVidEncoding encoding, uint vid)
        {
            string error = Validate(encoding, vid);
            if (error != null) throw new ArgumentException(error);
            return (raw & ~Mask(encoding)) | ((ulong)(vid & 0xFF) << 14) |
                (encoding == PStateVidEncoding.Svi3 ? (ulong)(vid >> 8 & 1) << 32 : 0);
        }
        public static double Voltage(uint vid, PStateVidEncoding encoding) =>
            VoltageCodec.VidToVoltage(vid, encoding == PStateVidEncoding.Svi3);
    }

    public static class PStateVidWriter
    {
        public static PStateWriteResult Apply(IReadOnlyList<ProcessorTarget> targets, IProcessorAffinity affinity,
            int stateId, PStateLayout layout, PStateVidEncoding encoding, uint vid,
            Func<uint, ulong?> read, Func<uint, ulong, bool> write)
        {
            string error = PStateVidMath.Validate(encoding, vid);
            if (error != null) return new PStateWriteResult { Status = PStateWriteStatus.InvalidInput, Error = error };
            return PStateFrequencyWriter.ApplyFields(targets, affinity, stateId, layout,
                original => PStateVidMath.WithVid(original, encoding, vid), PStateVidMath.Mask(encoding), read, write, null);
        }
    }
}
