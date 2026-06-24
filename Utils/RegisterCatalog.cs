using System;
using System.Collections.Generic;

namespace ZenStatesDebugTool
{
    public static class RegisterCatalog
    {
        // Keyed by (Kind, Address/Leaf). Populated in later tasks.
        private static readonly Dictionary<(RegisterKind, uint), RegisterDefinition> Map =
            new Dictionary<(RegisterKind, uint), RegisterDefinition>();

        static RegisterCatalog()
        {
            AddPStateDefs();
            AddMiscMsrs();
            AddCpuidLeaves();
        }

        public static bool TryGet(RegisterKind kind, uint address, out RegisterDefinition def)
            => Map.TryGetValue((kind, address), out def);

        // Used by the population helpers in later tasks.
        internal static void Add(RegisterDefinition def) => Map[(def.Kind, def.Address)] = def;

        private const uint PStateDef0 = 0xC0010064;

        private static void AddPStateDefs()
        {
            for (uint i = 0; i < 8; i++)
            {
                uint addr = PStateDef0 + i;
                Add(new RegisterDefinition(
                    RegisterKind.Msr, addr,
                    $"PStateDef{i}", $"P-State {i} definition",
                    new List<FieldDefinition>
                    {
                        new FieldDefinition("CpuFid", 7, 0),
                        new FieldDefinition("CpuDfsId", 13, 8),
                        new FieldDefinition("CpuVid", 21, 14),
                        new FieldDefinition("IddValue", 29, 22),
                        new FieldDefinition("IddDiv", 31, 30),
                        new FieldDefinition("PstateEn", 63, 63),
                    },
                    new List<Func<ulong, DecodeContext, string>>
                    {
                        Frequency,
                        Voltage,
                    }));
            }
        }

        private static void AddMiscMsrs()
        {
            Add(new RegisterDefinition(
                RegisterKind.Msr, 0xC0010015, "HWCR", "Hardware Configuration",
                new List<FieldDefinition>
                {
                    new FieldDefinition("SmmLock", 0, 0),
                    new FieldDefinition("TlbCacheDis", 3, 3),
                    new FieldDefinition("CpbDis", 25, 25),
                    new FieldDefinition("EffFreqReadOnlyLock", 30, 30),
                }));

            Add(new RegisterDefinition(
                RegisterKind.Msr, 0xC0010061, "PStateCurLim", "P-State Current Limit",
                new List<FieldDefinition>
                {
                    new FieldDefinition("CurPstateLimit", 2, 0),
                    new FieldDefinition("PstateMaxVal", 6, 4),
                }));

            Add(new RegisterDefinition(
                RegisterKind.Msr, 0xC0010062, "PStateCtl", "P-State Control",
                new List<FieldDefinition>
                {
                    new FieldDefinition("PstateCmd", 2, 0),
                }));

            Add(new RegisterDefinition(
                RegisterKind.Msr, 0xC0010063, "PStateStat", "P-State Status",
                new List<FieldDefinition>
                {
                    new FieldDefinition("CurPstate", 2, 0),
                }));
        }

        private static void AddCpuidLeaves()
        {
            // Decodes the EAX output of CPUID leaf 0x00000001 (family/model/stepping).
            Add(new RegisterDefinition(
                RegisterKind.Cpuid, 0x00000001, "CPUID_1_EAX", "Family/Model/Stepping (EAX)",
                new List<FieldDefinition>
                {
                    new FieldDefinition("Stepping", 3, 0),
                    new FieldDefinition("BaseModel", 7, 4),
                    new FieldDefinition("BaseFamily", 11, 8),
                    new FieldDefinition("ExtModel", 19, 16),
                    new FieldDefinition("ExtFamily", 27, 20),
                }));
        }

        private static string Frequency(ulong value, DecodeContext ctx)
        {
            uint fid = (uint)RegisterDecoder.Extract(value, 7, 0);
            uint did = (uint)RegisterDecoder.Extract(value, 13, 8);
            if (did == 0) return null; // avoid divide-by-zero; nothing to show
            double mhz = (fid * 25.0 / (did * 12.5)) * 100.0;
            return $"Frequency: {mhz:0} MHz";
        }

        private static string Voltage(ulong value, DecodeContext ctx)
        {
            if (ctx?.VidToVoltage == null) return null;
            uint vid = (uint)RegisterDecoder.Extract(value, 21, 14);
            return $"Voltage: {ctx.VidToVoltage(vid):0.000} V";
        }
    }
}
