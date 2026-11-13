using System;
using System.Collections.Generic;
using System.Linq;
using ZenStates.Core;
using ZenStates.Core.Hardware;

namespace ZenStatesDebugTool
{
    public static class CoreLibraryAdapter
    {
        public const string SourceRevision = "3bb1dad5b6cccc14f063d6eacbcf891a309944f3";
        public static CoreOptions CreateOptions(Action<string> progress = null) => new CoreOptions
        {
            InitProgress = progress,
            Timings = new CoreOptions.ModuleSettings { Enabled = true, Autorefresh = false },
            Sensors = new CoreOptions.ModuleSettings { Enabled = true, Autorefresh = false },
            Aod = new CoreOptions.ModuleSettings { Enabled = true, Autorefresh = false },
            Wmi = new CoreOptions.ModuleSettings { Enabled = true, Autorefresh = false },
            IoModule = new CoreOptions.ModuleSettings { Enabled = true, Autorefresh = false }
        };
        public static Cpu CreateCpu(Action<string> progress = null) => new Cpu(CreateOptions(progress));
        public static TopologySnapshot Topology(Cpu cpu)
        {
            uint family = (uint)cpu.info.family;
            bool apu = cpu.smu.SMU_TYPE >= SMU.SmuType.TYPE_APU0 && cpu.smu.SMU_TYPE <= SMU.SmuType.TYPE_APU2;
            var targets = new List<CoreTarget>();
            var map = cpu.info.apob?.CoreMap;
            int slots = Math.Min(128, (cpu.info.topology.coreDisableMap?.Length ?? 0) * 8);
            if (apu || family != 0x17 && family != 0x19 && family != 0x1A)
                return new TopologySnapshot(targets, slots, "unsupported", family, "Per-core targeting is not validated for this CPU layout.");
            int perComplex = family == 0x17 ? 4 : 8;
            if (map != null && map.Cores.Count > 0)
            {
                if (map.Cores.Count != cpu.info.topology.cores)
                    return new TopologySnapshot(targets, slots, "APOB", family, "APOB enabled-core count disagrees with CPUID.");
                if (map.InstanceId != 0) return new TopologySnapshot(targets, slots, "APOB", family, "Multi-instance APOB targeting is not supported.");
                foreach (var core in map.Cores)
                {
                    if (core.ThreadEnabled == null || !core.ThreadEnabled.Any(t => t) || core.PhysicalCore >= perComplex || (family != 0x17 && core.PhysicalCcx != 0))
                        return new TopologySnapshot(targets, slots, "APOB", family, "APOB contains an invalid enabled-core layout.");
                    int slot = core.PhysicalCcd * 8 + core.PhysicalCcx * perComplex + core.PhysicalCore;
                    uint mask = cpu.MakeCoreMask((uint)core.PhysicalCore, (uint)core.PhysicalCcd, (uint)core.PhysicalCcx);
                    targets.Add(new CoreTarget(slot, core.LogicalIndex, core.PhysicalCcd, core.PhysicalCcx, core.PhysicalCore, mask));
                }
                slots = Math.Max(slots, targets.Max(t => t.Slot) + 1);
                return new TopologySnapshot(targets, slots, "APOB", family);
            }
            uint enabledCcds = cpu.info.topology.ccdEnableMap;
            uint contiguous = cpu.info.topology.ccds >= 32 ? uint.MaxValue : (1U << (int)cpu.info.topology.ccds) - 1;
            if (enabledCcds != 0 && enabledCcds != contiguous)
                return new TopologySnapshot(targets, slots, "unsupported legacy", family, "Non-contiguous physical CCDs require a valid APOB map.");
            for (int slot = 0; slot < slots; slot++)
                if (CoreTopology.IsCoreEnabled(cpu.info.topology.coreDisableMap, slot))
                {
                    int ccd = slot / 8, ccx = slot % 8 / perComplex, core = slot % perComplex;
                    targets.Add(new CoreTarget(slot, targets.Count, ccd, ccx, core, cpu.MakeCoreMask((uint)core, (uint)ccd, (uint)ccx)));
                }
            if (targets.Count != cpu.info.topology.cores)
                return new TopologySnapshot(targets, slots, "unsupported legacy", family, "Fuse-map enabled-core count disagrees with CPUID.");
            return new TopologySnapshot(targets, slots, "validated legacy", family);
        }
    }
}
