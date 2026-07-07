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
    }
}
