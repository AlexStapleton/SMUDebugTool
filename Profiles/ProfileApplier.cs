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
            // Units: PPT in watts, TDC/EDC in amps, passed straight through as uint.
            // TDC/EDC target the VDD (core) rail, matching desktop PBO (Ryzen Master).
            // A value of 0 means "leave unchanged", so a CO-only profile does not
            // accidentally clamp the power/current limits to zero.
            if (p.PptWatts.HasValue && p.PptWatts.Value > 0
                && cpu.SetPPTLimit((uint)p.PptWatts.Value) != SMU.Status.OK)
                r.Fail("Failed to set PPT limit.");
            if (p.TdcAmps.HasValue && p.TdcAmps.Value > 0
                && cpu.SetTDCVDDLimit((uint)p.TdcAmps.Value) != SMU.Status.OK)
                r.Fail("Failed to set TDC limit.");
            if (p.EdcAmps.HasValue && p.EdcAmps.Value > 0
                && cpu.SetEDCVDDLimit((uint)p.EdcAmps.Value) != SMU.Status.OK)
                r.Fail("Failed to set EDC limit.");
            if (p.PboScalar.HasValue && p.PboScalar.Value > 0
                && cpu.SetPBOScalar((uint)p.PboScalar.Value) != SMU.Status.OK)
                r.Fail("Failed to set PBO scalar.");
        }

        // Delegates to the shared CoreTopology helper so the UI and headless apply paths
        // can never disagree about core enable/encode logic.
        private static bool IsCoreEnabled(Cpu cpu, int coreIndex)
        {
            return CoreTopology.IsCoreEnabled(cpu.info.topology.coreDisableMap, coreIndex);
        }

        private static uint EncodeCoreMarginBitmask(Cpu cpu, int coreIndex, int coresPerCCD = 8)
        {
            bool isApu = cpu.smu.SMU_TYPE >= SMU.SmuType.TYPE_APU0 && cpu.smu.SMU_TYPE <= SMU.SmuType.TYPE_APU2;
            return CoreTopology.EncodeCoreMarginBitmask(isApu, coreIndex, coresPerCCD);
        }
    }
}
