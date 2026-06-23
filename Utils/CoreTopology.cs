namespace ZenStatesDebugTool
{
    // Pure core-topology math, kept free of ZenStates.Core types so it can be unit tested
    // and shared by both the UI (SettingsForm) and the headless apply path (ProfileApplier).
    // Previously these two methods were copy-pasted into both, with a comment warning they
    // must stay in sync — a silent "wrong core gets the margin" bug waiting to happen.
    public static class CoreTopology
    {
        // True if the physical core at coreIndex is enabled, per the SMU core-disable bitmap
        // (one bit per core, grouped in 8s; a SET disable-bit means the core is DISABLED).
        public static bool IsCoreEnabled(uint[] coreDisableMap, int coreIndex)
        {
            if (coreDisableMap == null) return false;
            int mapIndex = coreIndex / 8;
            int coreInGroup = coreIndex % 8;
            return mapIndex >= 0
                && mapIndex < coreDisableMap.Length
                && ((~coreDisableMap[mapIndex] >> coreInGroup) & 1) == 1;
        }

        // Builds the per-core bitmask the SMU Set/GetDldoPsmMargin commands expect.
        // APUs address cores by a flat index; desktop/HEDT parts pack CCD and the in-CCD
        // core index into the upper bits.
        public static uint EncodeCoreMarginBitmask(bool isApu, int coreIndex, int coresPerCCD = 8)
        {
            if (isApu)
                return (uint)coreIndex;

            int ccdIndex = coreIndex / coresPerCCD;
            int localCoreIndex = coreIndex % coresPerCCD;
            int ccdMask = ccdIndex << 8;
            int mask = ccdMask | localCoreIndex;
            return (uint)(mask << 20);
        }
    }
}
