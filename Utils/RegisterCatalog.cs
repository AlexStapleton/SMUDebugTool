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

        public static bool TryGet(RegisterKind kind, uint address, DecodeContext context, out RegisterDefinition def)
        {
            if (kind == RegisterKind.Msr && address >= PStateMath.BaseMsr
                && address < PStateMath.BaseMsr + PStateMath.Count)
            {
                def = PStateDefinition(address, context ?? DecodeContext.None);
                return true;
            }
            return TryGet(kind, address, out def);
        }

        // Used by the population helpers in later tasks.
        internal static void Add(RegisterDefinition def) => Map[(def.Kind, def.Address)] = def;

        private const uint PStateDef0 = 0xC0010064;

        private static void AddPStateDefs()
        {
            for (uint i = 0; i < PStateMath.Count; i++)
                Add(PStateDefinition(PStateDef0 + i, DecodeContext.None));
        }

        private static RegisterDefinition PStateDefinition(uint address, DecodeContext ctx)
        {
            var fields = new List<FieldDefinition>();
            if (PStateMath.FrequencyMask(ctx.PStateLayout) != 0)
            {
                fields.Add(new FieldDefinition("CpuFid", ctx.PStateLayout == PStateLayout.Zen5 ? 11 : 7, 0));
                if (ctx.PStateLayout == PStateLayout.FidDid)
                    fields.Add(new FieldDefinition("CpuDfsId", 13, 8));
                fields.Add(new FieldDefinition("CpuVid", 21, 14));
                if (ctx.PStateVidEncoding == PStateVidEncoding.Svi3)
                    fields.Add(new FieldDefinition("CpuVidHigh", 32, 32));
                fields.Add(new FieldDefinition("IddValue", 29, 22));
                fields.Add(new FieldDefinition("IddDiv", 31, 30));
                fields.Add(new FieldDefinition("PstateEn", 63, 63));
            }
            uint id = address - PStateDef0;
            return new RegisterDefinition(RegisterKind.Msr, address, $"PStateDef{id}",
                $"P-State {id} definition", fields,
                new List<Func<ulong, DecodeContext, string>>
                {
                    (raw, context) => $"Raw: 0x{raw:X16}",
                    Frequency, Voltage
                });
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
            var decoded = PStateMath.Decode(value, ctx.PStateLayout);
            double? mhz = PStateMath.FrequencyMhz(ctx.PStateLayout, decoded.CpuFid, decoded.CpuDfsId);
            return mhz.HasValue ? $"Frequency: {mhz.Value:0.##} MHz" : null;
        }

        private static string Voltage(ulong value, DecodeContext ctx)
        {
            if (ctx.VidToVoltage == null || ctx.PStateVidEncoding == PStateVidEncoding.Unknown
                || PStateMath.FrequencyMask(ctx.PStateLayout) == 0) return null;
            uint vid = (uint)(value >> 14 & 0xFF);
            if (ctx.PStateVidEncoding == PStateVidEncoding.Svi3)
                vid |= (uint)(value >> 32 & 1) << 8;
            return $"Voltage: {ctx.VidToVoltage(vid):0.000} V";
        }
    }
}
