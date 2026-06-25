namespace ZenStatesDebugTool
{
    // Decoded fields of an AMD PStateDef MSR (MSRC001_006[4..B]) EAX word.
    public struct PStateValues
    {
        public uint IddDiv;
        public uint IddVal;
        public uint CpuVid;
        public uint CpuDfsId;
        public uint CpuFid;
    }

    // Pure P-state bit math, extracted verbatim from SettingsForm so it can be unit-tested.
    public static class PStateMath
    {
        public static PStateValues Decode(uint eax)
        {
            return new PStateValues
            {
                IddDiv = eax >> 30,
                IddVal = eax >> 22 & 0xFF,
                CpuVid = eax >> 14 & 0xFF,
                CpuDfsId = eax >> 8 & 0x3F,
                CpuFid = eax & 0xFF,
            };
        }

        public static uint Encode(uint iddDiv, uint iddVal, uint cpuVid, uint cpuDfsId, uint cpuFid)
        {
            return (iddDiv & 0xFF) << 30
                 | (iddVal & 0xFF) << 22
                 | (cpuVid & 0xFF) << 14
                 | (cpuDfsId & 0xFF) << 8
                 | (cpuFid & 0xFF);
        }

        // MHz from FID/DID using the same expression the UI has always used.
        // Returns 0 when did == 0 (avoids divide-by-zero; the old UI seeded did=1).
        public static double FrequencyMhz(uint cpuFid, uint cpuDfsId)
        {
            if (cpuDfsId == 0) return 0;
            return (cpuFid * 25 / (cpuDfsId * 12.5)) * 100;
        }
    }
}
