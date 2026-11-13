using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ZenStatesDebugTool.Profiles;

namespace ZenStatesDebugTool
{
    public sealed class CoreTarget
    {
        public int Slot { get; }
        public int LogicalIndex { get; }
        public int Ccd { get; }
        public int Ccx { get; }
        public int Core { get; }
        public uint Mask { get; }
        public string Identity => $"ccd:{Ccd}/ccx:{Ccx}/core:{Core}";
        public CoreTarget(int slot, int logicalIndex, int ccd, int ccx, int core, uint mask)
        { Slot = slot; LogicalIndex = logicalIndex; Ccd = ccd; Ccx = ccx; Core = core; Mask = mask; }
    }

    public sealed class TopologySnapshot
    {
        public IReadOnlyList<CoreTarget> Targets { get; }
        public int SlotCount { get; }
        public string Source { get; }
        public string Error { get; }
        public string Signature { get; }
        public TopologySnapshot(IEnumerable<CoreTarget> targets, int slotCount, string source, uint family, string error = null)
        {
            var list = targets.OrderBy(t => t.Slot).ToList();
            SlotCount = slotCount; Source = source;
            Error = error;
            int perComplex = family == 0x17 ? 4 : 8;
            if (slotCount < 0 || slotCount > 128 || list.Any(t => t.Slot < 0 || t.Slot >= slotCount || t.Ccd < 0 || t.Ccd > 15 || t.Ccx < 0 || t.Ccx > 1 || t.Core < 0 || t.Core > 7)
                || list.Select(t => t.Slot).Distinct().Count() != list.Count || list.Select(t => t.Mask).Distinct().Count() != list.Count
                || list.Select(t => t.Identity).Distinct().Count() != list.Count || list.Select(t => t.LogicalIndex).Distinct().Count() != list.Count
                || list.Any(t => t.LogicalIndex < 0 || t.Core >= perComplex || family != 0x17 && t.Ccx != 0 ||
                    t.Slot != t.Ccd * 8 + t.Ccx * perComplex + t.Core ||
                    t.Mask != ((uint)t.Ccd << 28 | (uint)t.Core << 20 | (family == 0x17 ? (uint)t.Ccx << 24 : 0))))
                Error = "Topology has duplicate or unrepresentable targets.";
            Targets = new ReadOnlyCollection<CoreTarget>(Error == null ? list : new List<CoreTarget>());
            using (var hash = SHA256.Create())
                Signature = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(family + ":" + string.Join(";", Targets.Select(t => t.Identity + ":" + t.Mask.ToString("X8")))))).Replace("-", "").ToLowerInvariant();
        }
        public CoreTarget Find(int slot) => Targets.FirstOrDefault(t => t.Slot == slot);
        public string Validate(Profile profile)
        {
            if (profile.SchemaVersion < 1 || profile.SchemaVersion > 2) return "Unsupported profile schema version.";
            if (profile.CoMargins == null || profile.CoMargins.Count == 0) return null;
            if (Error != null) return Error;
            if (profile.SchemaVersion == 2 && (profile.TopologySignature != Signature || profile.CoreBindings == null))
                return "Profile topology differs from this CPU; load and review the target bindings before saving a new profile.";
            foreach (var entry in profile.CoMargins)
            {
                if (entry.Key < 0 || entry.Key >= SlotCount) return "Profile references an unknown physical core slot.";
                if (profile.SchemaVersion == 2 && (!profile.CoreBindings.TryGetValue(entry.Key, out string id) || Find(entry.Key)?.Identity != id))
                    return "Profile core binding is absent or differs from the current physical core.";
            }
            return null;
        }
        public void Bind(Profile profile)
        {
            profile.SchemaVersion = 2;
            profile.TopologySignature = Signature;
            profile.CoreBindings = (profile.CoMargins ?? new Dictionary<int, int>()).Keys.ToDictionary(k => k,
                k => Find(k)?.Identity ?? throw new InvalidOperationException("Cannot save an unresolved core target."));
        }
    }
}
