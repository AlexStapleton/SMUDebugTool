using System.Collections.Generic;
using System.Reflection;
using ZenStates.Core;

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
                if (!p.Name.StartsWith("SMU_MSG_")) continue;
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
        public static System.Func<uint, double> GetVidToVoltage(CpuCodeName codeName)
        {
            if (IsSvi3(codeName))
                return v => ZenStates.Core.Utils.VidToVoltageSVI3(v);
            return v => ZenStates.Core.Utils.VidToVoltage(v);
        }

        private static bool IsSvi3(CpuCodeName c)
        {
            switch (c)
            {
                case CpuCodeName.Raphael:
                case CpuCodeName.GraniteRidge:
                case CpuCodeName.DragonRange:
                case CpuCodeName.Phoenix:
                case CpuCodeName.Phoenix2:
                case CpuCodeName.HawkPoint:
                case CpuCodeName.StrixPoint:
                case CpuCodeName.StrixHalo:
                case CpuCodeName.KrackanPoint:
                case CpuCodeName.KrackanPoint2:
                case CpuCodeName.Genoa:
                case CpuCodeName.Bergamo:
                case CpuCodeName.Turin:
                case CpuCodeName.TurinD:
                case CpuCodeName.StormPeak:    // Zen4 Threadripper
                case CpuCodeName.ShimadaPeak:  // Zen5 Threadripper
                    return true;
                default:
                    return false;
            }
        }

        // Projects the library's PM-table structure into the pure SensorInfo dict.
        // Returns null when the layout is undefined for this firmware.
        public static Dictionary<uint, SensorInfo> GetPmTableStructure(Cpu cpu)
        {
            if (cpu?.RyzenSmu == null || !cpu.RyzenSmu.IsPmTableLayoutDefined)
                return null;

            var result = new Dictionary<uint, SensorInfo>();
            foreach (var kv in cpu.RyzenSmu.GetPmTableStructure())
                result[kv.Key] = new SensorInfo(kv.Value.Name, kv.Value.Scale);
            return result;
        }
    }
}
