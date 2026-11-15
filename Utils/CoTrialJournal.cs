using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace ZenStatesDebugTool
{
    public sealed class CoTrialEntry
    {
        public int Sequence { get; set; }
        public DateTimeOffset StartedUtc { get; set; }
        public string Action { get; set; }
        public Dictionary<int, int> Before { get; set; }
        public Dictionary<int, int> Requested { get; set; }
        public string Outcome { get; set; }
        public string Evidence { get; set; }
        public string Selection { get; set; }
    }
    public sealed class CoTrialJournal
    {
        private readonly object gate = new object();
        private readonly string path;
        private readonly List<CoTrialEntry> entries = new List<CoTrialEntry>();
        private string hardware, topology, phase;
        private string configuration;
        public int Count { get { lock (gate) return entries.Count; } }
        public bool Initialized { get { lock (gate) return hardware != null; } }
        public CoTrialJournal(string path) { this.path = Path.GetFullPath(path); }
        public void Start(NativeCoContext context)
        {
            lock (gate) {
                if (File.Exists(path)) File.Copy(path, path + ".previous", true);
                hardware = context.Hardware; topology = context.Topology; phase = "Reference"; configuration = null; entries.Clear(); Write();
            }
        }
        public void Configure(string value) { lock (gate) { configuration = value; Write(); } }
        public int Intent(string action, IReadOnlyDictionary<int, int> before, IReadOnlyDictionary<int, int> requested, string selection = null)
        {
            lock (gate) {
                if (entries.Count >= 1024 || hardware == null) throw new InvalidOperationException("Trial journal is unavailable or full; no new write permitted.");
                var entry = new CoTrialEntry { Sequence = entries.Count + 1, StartedUtc = DateTimeOffset.UtcNow, Action = action,
                    Before = before.ToDictionary(c => c.Key, c => c.Value), Requested = requested.ToDictionary(c => c.Key, c => c.Value), Outcome = "Intent saved; write outcome pending", Selection = selection };
                entries.Add(entry);
                try { Write(); } catch { entry.Outcome = "Journal persistence failed; setter prevented"; throw; }
                return entry.Sequence;
            }
        }
        public void Complete(int sequence, TuningResult result)
        {
            lock (gate) {
                var entry = entries.Single(e => e.Sequence == sequence);
                entry.Outcome = result.Rows.Count == entry.Requested.Count && result.Rows.All(r => r.Outcome == TuningOutcome.Verified) ? "Readback matched; stability untested" : "Partial / failed / unverified write";
                entry.Evidence = TuningResultFormatter.Report(result); if (entry.Evidence.Length > 12000) entry.Evidence = entry.Evidence.Substring(0, 12000);
                Write();
            }
        }
        public void Phase(string value) { lock (gate) { phase = value; Write(); } }
        public void Measurement(int sequence, string evidence)
        {
            lock (gate) { var entry = entries.Single(e => e.Sequence == sequence); entry.Selection = (entry.Selection ?? "") + "\nAfter settling: " + evidence; Write(); }
        }
        private void Write()
        {
            // Caller holds gate. Keep intent durability synchronous before each setter.
            var json = JsonConvert.SerializeObject(CurrentDocument(entries), Formatting.Indented);
            if (Encoding.UTF8.GetByteCount(json) > 8 * 1024 * 1024) throw new InvalidOperationException("Trial journal reached its 8 MiB limit; no new write permitted.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)); string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temporary, json, new UTF8Encoding(false)); if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        public string Export()
        { return JsonConvert.SerializeObject(ExportSnapshot(), Formatting.Indented); }
        private object CurrentDocument(IEnumerable<CoTrialEntry> snapshot) => new { SchemaVersion = 1, Hardware = hardware, Topology = topology, Configuration = configuration, Phase = phase, Entries = snapshot };
        public object ExportSnapshot()
        {
            lock (gate) return CurrentDocument(entries.Select(e => new CoTrialEntry {
                Sequence = e.Sequence, StartedUtc = e.StartedUtc, Action = e.Action, Before = new Dictionary<int, int>(e.Before), Requested = new Dictionary<int, int>(e.Requested),
                Outcome = e.Outcome, Evidence = e.Evidence, Selection = e.Selection
            }).ToArray());
        }
    }
}
