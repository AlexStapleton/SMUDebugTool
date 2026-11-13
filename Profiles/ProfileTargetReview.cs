using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace ZenStatesDebugTool.Profiles
{
    public sealed class ProfileTargetReview
    {
        public sealed class Row
        {
            public int SavedSlot { get; set; }
            public int Margin { get; set; }
            public string SavedIdentity { get; set; }
            public string CurrentIdentity { get; set; }
            public int? CurrentSlot { get; set; }
            public string Resolution => CurrentSlot.HasValue ? "Matched physical target" : "Unresolved; no conversion";
        }
        public IReadOnlyList<Row> Rows { get; }
        public bool CanConvert { get; }
        private readonly Profile source;
        private readonly TopologySnapshot topology;
        public ProfileTargetReview(Profile profile, TopologySnapshot topology)
        {
            source = profile; this.topology = topology;
            Rows = (profile.CoMargins ?? new Dictionary<int, int>()).OrderBy(x => x.Key).Select(entry =>
            {
                string id = null;
                profile.CoreBindings?.TryGetValue(entry.Key, out id);
                var target = profile.SchemaVersion == 1 ? topology.Find(entry.Key) : topology.Targets.SingleOrDefault(t => t.Identity == id);
                return new Row
                {
                    SavedSlot = entry.Key,
                    Margin = entry.Value,
                    SavedIdentity = id ?? "Legacy physical slot " + entry.Key,
                    CurrentIdentity = target?.Identity,
                    CurrentSlot = target?.Slot
                };
            }).ToList().AsReadOnly();
            CanConvert = profile.SchemaVersion >= 1 && profile.SchemaVersion <= 2 && topology.Error == null && Rows.All(r => r.CurrentSlot.HasValue)
                && Rows.Select(r => r.CurrentSlot).Distinct().Count() == Rows.Count;
        }
        public Profile ConvertAfterReview()
        {
            if (!CanConvert) throw new InvalidOperationException("Every requested core must have an unambiguous physical match.");
            var converted = JsonConvert.DeserializeObject<Profile>(JsonConvert.SerializeObject(source));
            converted.CoMargins = Rows.ToDictionary(r => r.CurrentSlot.Value, r => r.Margin);
            topology.Bind(converted);
            return converted;
        }
    }
}
