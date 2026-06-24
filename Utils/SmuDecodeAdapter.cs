using System;
using System.Collections.Generic;
using System.Reflection;
using ZenStates.Core;
using static ZenStates.Core.Cpu;

namespace ZenStatesDebugTool
{
    // Bridges the pure decode core to ZenStates-Core. Reflection + library calls
    // live here so the core stays hardware-free and unit-testable.
    public static class SmuDecodeAdapter
    {
        // Reads every SMU_MSG_* uint property from the three mailboxes.
        public static IEnumerable<KeyValuePair<string, uint>> ReadMessages(SMU smu)
        {
            if (smu == null) yield break;
            foreach (var pair in ReadMailbox(smu.Rsmu))
                yield return pair;
            foreach (var pair in ReadMailbox(smu.Mp1Smu))
                yield return pair;
            foreach (var pair in ReadMailbox(smu.Hsmp))
                yield return pair;
        }

        private static IEnumerable<KeyValuePair<string, uint>> ReadMailbox(object mailbox)
        {
            if (mailbox == null) yield break;
            foreach (PropertyInfo p in mailbox.GetType().GetProperties())
            {
                if (!p.Name.StartsWith("SMU_MSG_", StringComparison.Ordinal)) continue;
                if (p.PropertyType != typeof(uint)) continue;
                uint value;
                try { value = (uint)p.GetValue(mailbox); }
                catch { continue; }
                yield return new KeyValuePair<string, uint>(
                    p.Name.Substring("SMU_MSG_".Length), value);
            }
        }

        // Generation-aware VID -> voltage, reusing the library's own conversions.
        // SVI3 set = Zen4 and later; everything else uses SVI2.
        public static Func<uint, double> GetVidToVoltage(CodeName codeName)
        {
            if (IsSvi3(codeName))
                return ZenStates.Core.Utils.VidToVoltageSVI3;
            return ZenStates.Core.Utils.VidToVoltage;
        }

        private static bool IsSvi3(CodeName c)
        {
            switch (c)
            {
                case CodeName.Raphael:
                case CodeName.GraniteRidge:
                case CodeName.DragonRange:
                case CodeName.Phoenix:
                case CodeName.Phoenix2:
                case CodeName.HawkPoint:
                case CodeName.StrixPoint:
                case CodeName.StrixHalo:
                case CodeName.KrackanPoint:
                case CodeName.KrackanPoint2:
                case CodeName.Genoa:
                case CodeName.Bergamo:
                case CodeName.Turin:
                case CodeName.TurinD:
                case CodeName.StormPeak:    // Zen4 Threadripper
                case CodeName.ShimadaPeak:  // Zen5 Threadripper
                    return true;
                default:
                    return false;
            }
        }

        // Projects the available PM-table sensor mapping into the pure SensorInfo dict,
        // keyed by float-array element index. Two sources are merged:
        //   1. The named offsets ZenStates' PowerTable.tableDef (PTDef) defines for this
        //      firmware (FCLK/MCLK/UCLK/SoC/voltages) - available for far more CPUs than
        //      the full sensor map, including ones with no full layout.
        //   2. The full per-index sensor map (only the handful of versions ZenStates
        //      fully maps); these are richer, so they win on conflict.
        // Returns null when neither source yields anything.
        public static Dictionary<uint, SensorInfo> GetPmTableStructure(Cpu cpu)
        {
            if (cpu?.RyzenSmu == null)
                return null;

            var result = PmTableLabeling.BuildNamedOffsetMap(ReadPtDefOffsets(cpu));

            if (cpu.RyzenSmu.IsPmTableLayoutDefined)
                foreach (var kv in cpu.RyzenSmu.GetPmTableStructure())
                    result[kv.Key] = new SensorInfo(kv.Value.Name, kv.Value.Scale);

            return result.Count > 0 ? result : null;
        }

        // The named byte offsets ZenStates uses for this firmware live in the private
        // PowerTable.tableDef (PTDef) struct. Read them by reflection so the labels track
        // whatever offsets the bundled library defines.
        private static IEnumerable<KeyValuePair<string, int>> ReadPtDefOffsets(Cpu cpu)
        {
            object powerTable = cpu.powerTable;
            if (powerTable == null) yield break;

            FieldInfo defField = powerTable.GetType()
                .GetField("tableDef", BindingFlags.NonPublic | BindingFlags.Instance);
            object def = defField?.GetValue(powerTable);
            if (def == null) yield break;

            // PTDef field name -> display label.
            var fields = new[]
            {
                new KeyValuePair<string, string>("offsetFclk", "FCLK"),
                new KeyValuePair<string, string>("offsetUclk", "UCLK"),
                new KeyValuePair<string, string>("offsetMclk", "MCLK"),
                new KeyValuePair<string, string>("offsetVddcrSoc", "VDDCR_SOC"),
                new KeyValuePair<string, string>("offsetVddMisc", "VDD_MISC"),
                new KeyValuePair<string, string>("offsetCldoVddp", "CLDO_VDDP"),
                new KeyValuePair<string, string>("offsetCldoVddgIod", "CLDO_VDDG_IOD"),
                new KeyValuePair<string, string>("offsetCldoVddgCcd", "CLDO_VDDG_CCD"),
                new KeyValuePair<string, string>("offsetCoresPower", "Cores Power"),
            };

            Type defType = def.GetType();
            foreach (var f in fields)
            {
                FieldInfo fi = defType.GetField(f.Key);
                if (fi == null || fi.FieldType != typeof(int)) continue;
                int byteOffset;
                try { byteOffset = (int)fi.GetValue(def); }
                catch { continue; }
                yield return new KeyValuePair<string, int>(f.Value, byteOffset);
            }
        }
    }
}
