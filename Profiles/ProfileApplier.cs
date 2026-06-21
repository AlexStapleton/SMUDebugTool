using System.Collections.Generic;
using ZenStates.Core;

namespace ZenStatesDebugTool.Profiles
{
    public class ApplyResult
    {
        public bool Success { get; private set; } = true;
        public List<string> Messages { get; } = new List<string>();
        public void Fail(string m) { Success = false; Messages.Add(m); }
        public void Info(string m) { Messages.Add(m); }
    }

    public class ProfileApplier
    {
        public ApplyResult Apply(Profile profile, Cpu cpu)
        {
            var result = new ApplyResult();
            if (profile == null) { result.Fail("Profile is null."); return result; }
            if (cpu == null) { result.Fail("CPU not available."); return result; }

            ApplyCoMargins(profile, cpu, result);
            ApplyFmax(profile, cpu, result);
            ApplyCurveShaper(profile, cpu, result);
            ApplyPboLimits(profile, cpu, result); // filled in a later task
            return result;
        }

        private void ApplyCoMargins(Profile p, Cpu cpu, ApplyResult r)
        {
            if (p.CoMargins == null || p.CoMargins.Count == 0) return;
            if (cpu.smu.Rsmu.SMU_MSG_SetDldoPsmMargin == 0) { r.Info("CO not supported; skipped."); return; }
            foreach (var kv in p.CoMargins)
            {
                if (!IsCoreEnabled(cpu, kv.Key)) continue;
                cpu.SetPsmMarginSingleCore(EncodeCoreMarginBitmask(cpu, kv.Key), kv.Value);
            }
        }

        private void ApplyFmax(Profile p, Cpu cpu, ApplyResult r)
        {
            if (!p.Fmax.HasValue) return;
            if (!cpu.SetFMax((uint)p.Fmax.Value)) r.Fail("Failed to set fmax.");
        }

        private void ApplyCurveShaper(Profile p, Cpu cpu, ApplyResult r)
        {
            if (p.CurveShaperTiers == null) return;
            for (int tier = 0; tier < p.CurveShaperTiers.Count && tier < 5; tier++)
            {
                var t = p.CurveShaperTiers[tier];
                if (t == null) continue;
                if (cpu.SetCurveShaperMargin(marginHigh: t.High, marginMedium: t.Medium,
                        marginLow: t.Low, tier) != SMU.Status.OK)
                    r.Fail($"Failed to set Curve Shaper tier {tier}.");
            }
        }

        private void ApplyPboLimits(Profile p, Cpu cpu, ApplyResult r)
        {
            // Filled in a later task after verifying SetPPTLimit / SetTDCSOCLimit /
            // SetEDCSOCLimit / SetPBOScalar signatures and units against the DLL.
        }

        // Replicated from SettingsForm so the apply path is form-independent.
        private static bool IsCoreEnabled(Cpu cpu, int coreIndex)
        {
            int mapIndex = coreIndex / 8;
            int coreInGroup = coreIndex % 8;
            return mapIndex >= 0
                && mapIndex < cpu.info.topology.coreDisableMap.Length
                && ((~cpu.info.topology.coreDisableMap[mapIndex] >> coreInGroup) & 1) == 1;
        }

        private static uint EncodeCoreMarginBitmask(Cpu cpu, int coreIndex, int coresPerCCD = 8)
        {
            if (cpu.smu.SMU_TYPE >= SMU.SmuType.TYPE_APU0 && cpu.smu.SMU_TYPE <= SMU.SmuType.TYPE_APU2)
                return (uint)coreIndex;
            int ccdIndex = coreIndex / coresPerCCD;
            int localCoreIndex = coreIndex % coresPerCCD;
            int ccdMask = ccdIndex << 8;
            int mask = ccdMask | localCoreIndex;
            return (uint)(mask << 20);
        }
    }
}
