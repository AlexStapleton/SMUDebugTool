using System;

namespace ZenStatesDebugTool
{
    // Pure volts<->VID conversion for manual OC core voltage. WinForms- and
    // hardware-free (no ZenStates dependency) so it unit tests like CurveShaperCodec.
    // The SMU takes a VID byte; the mapping differs by voltage interface generation:
    //   SVI3 (Zen 4/5): V = 0.245 + vid * 0.005
    //   SVI2 (Zen 2/3): V = 1.55  - vid / 160
    public static class VoltageCodec
    {
        public static double VidToVoltage(uint vid, bool svi3)
        {
            if (svi3)
                return 0.245 + vid * 0.005;
            return 1.55 - vid / 160.0;
        }

        // Inverse of VidToVoltage, rounded to the nearest VID and clamped to the
        // byte range the SMU accepts. Out-of-range voltages resolve to the nearest
        // encodable VID (e.g. >~1.52 V on SVI3 -> 255) rather than throwing.
        public static uint VoltageToVid(double volts, bool svi3)
        {
            double vid = svi3
                ? (volts - 0.245) / 0.005
                : (1.55 - volts) * 160.0;
            long rounded = (long)Math.Round(vid, MidpointRounding.AwayFromZero);
            if (rounded < 0) rounded = 0;
            if (rounded > 255) rounded = 255;
            return (uint)rounded;
        }

        // True for 3D V-Cache parts (name contains "X3D"), whose cache CCD shares
        // the single core-voltage rail and tolerates less vcore. The library exposes
        // no cleaner signal, so this is a name check. Null/empty -> false.
        public static bool IsX3D(string cpuName)
        {
            return !string.IsNullOrEmpty(cpuName)
                && cpuName.IndexOf("X3D", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
