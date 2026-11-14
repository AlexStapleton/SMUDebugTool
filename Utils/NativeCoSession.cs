using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using ZenStatesDebugTool.Profiles;

namespace ZenStatesDebugTool
{
    public sealed class NativeCoContext
    {
        public string Hardware { get; }
        public string Topology { get; }
        public int ExpectedCores { get; }
        public IReadOnlyDictionary<int, int> ObservedCo { get; }
        private readonly Profile editor;
        public Profile EditorRequests => TuningSnapshots.Profile(editor);
        public NativeCoContext(string hardware, string topology, Profile requests, IDictionary<int, int> co, int expectedCores)
        { Hardware = hardware; Topology = topology; ExpectedCores = expectedCores; editor = TuningSnapshots.Profile(requests); ObservedCo = new System.Collections.ObjectModel.ReadOnlyDictionary<int, int>(new Dictionary<int, int>(co)); }
    }
    public sealed class NativeCoreSummary
    {
        public int Slot { get; set; }
        public int Ccd { get; set; }
        public long Samples { get; set; }
        public long BusySamples { get; set; }
        public decimal MeanVid { get; set; }
        public decimal? BusyMeanVid { get; set; }
        public double? MeanEffectiveMhz { get; set; }
    }
    public sealed class NativeCoTrial
    {
        public Guid Id { get; set; }
        public string Role { get; set; }
        public string Workload { get; set; }
        public string State { get; set; }
        public DateTimeOffset StartedUtc { get; set; }
        public DateTimeOffset? EndedUtc { get; set; }
        public decimal? Score { get; set; }
        public NativeCoContext Start { get; set; }
        public NativeCoContext End { get; set; }
        public List<string> Changes { get; set; } = new List<string>();
        public List<NativeCoreSummary> Cores { get; set; } = new List<NativeCoreSummary>();
        public string Context { get; set; }
        [JsonIgnore] public string Display => Role + " / " + StartedUtc.ToLocalTime().ToString("HH:mm:ss") + " / " + (Score?.ToString(CultureInfo.InvariantCulture) ?? "no score") + " / " + (Context ?? State);
    }
    public sealed class NativeCoSession
    {
        private readonly object gate = new object();
        private readonly List<NativeCoTrial> trials = new List<NativeCoTrial>();
        private readonly Dictionary<int, long> effectiveCounts = new Dictionary<int, long>();
        private NativeCoTrial active;
        private long activeStarted;
        private static NativeCoTrial Copy(NativeCoTrial trial) => new NativeCoTrial {
            Id = trial.Id, Role = trial.Role, Workload = trial.Workload, State = trial.State, StartedUtc = trial.StartedUtc, EndedUtc = trial.EndedUtc,
            Score = trial.Score, Start = trial.Start, End = trial.End, Context = trial.Context, Changes = new List<string>(trial.Changes),
            Cores = trial.Cores.Select(c => new NativeCoreSummary { Slot = c.Slot, Ccd = c.Ccd, Samples = c.Samples, BusySamples = c.BusySamples, MeanVid = c.MeanVid, BusyMeanVid = c.BusyMeanVid, MeanEffectiveMhz = c.MeanEffectiveMhz }).ToList()
        };
        public IReadOnlyList<NativeCoTrial> Trials { get { lock (gate) return trials.Select(Copy).ToArray(); } }
        public bool IsRecording { get { lock (gate) return active != null; } }
        public void Reset()
        {
            lock (gate) {
                if (active != null) throw new InvalidOperationException("End the active run before starting a new session.");
                trials.Clear(); effectiveCounts.Clear();
            }
        }
        public void Begin(string role, string workload, NativeCoContext context, DateTimeOffset now)
        {
            lock (gate) {
                if (active != null || trials.Count >= 100) throw new InvalidOperationException("End the active run, or export/start a new session after 100 runs.");
                if ((role != "Baseline" && role != "Candidate") || string.IsNullOrWhiteSpace(workload) || workload.Length > 256 || context == null) throw new ArgumentException("A role, workload and native context are required.");
                active = new NativeCoTrial { Id = Guid.NewGuid(), Role = role, Workload = workload.Trim(), State = "Recording", StartedUtc = now, Start = context };
                activeStarted = Stopwatch.GetTimestamp();
                effectiveCounts.Clear(); trials.Add(active);
            }
        }
        public void Add(IEnumerable<CoreVidReading> readings)
        {
            lock (gate) {
                if (active?.State != "Recording") return;
                foreach (var reading in readings) {
                    if (!reading.VidVolts.HasValue || reading.Timestamp < activeStarted) continue;
                    var row = active.Cores.FirstOrDefault(c => c.Slot == reading.Slot);
                    if (row == null) { row = new NativeCoreSummary { Slot = reading.Slot, Ccd = reading.Ccd }; active.Cores.Add(row); }
                    row.Samples++; row.MeanVid += (reading.VidVolts.Value - row.MeanVid) / row.Samples;
                    if (reading.Busy && reading.Issue == null) { row.BusySamples++; row.BusyMeanVid = (row.BusyMeanVid ?? 0) + (reading.VidVolts.Value - (row.BusyMeanVid ?? 0)) / row.BusySamples; }
                    if (reading.EffectiveMhz.HasValue) {
                        effectiveCounts.TryGetValue(reading.Slot, out long count); effectiveCounts[reading.Slot] = ++count;
                        row.MeanEffectiveMhz = (row.MeanEffectiveMhz ?? 0) + (reading.EffectiveMhz.Value - (row.MeanEffectiveMhz ?? 0)) / count;
                    }
                }
            }
        }
        public void MarkEnd(DateTimeOffset now)
        {
            lock (gate) {
                if (active?.State != "Recording" || now <= active.StartedUtc || now - active.StartedUtc > TimeSpan.FromDays(1)) throw new InvalidOperationException("No valid active run to end.");
                active.State = "Ending"; active.EndedUtc = now;
            }
        }
        public void End(NativeCoContext end, decimal? score)
        {
            lock (gate) {
                if (active?.State != "Ending" || end == null || score <= 0 || score > 10000000) throw new InvalidOperationException("Invalid run end or score.");
                active.End = end; active.Score = score; active.State = "Completed";
                active.Context = active.Changes.Count > 0 ? "Mixed: tuning activity" : Context(active.Start, end);
                active = null;
            }
        }
        public void Interrupt()
        {
            lock (gate) {
                if (active == null) return;
                active.State = "Interrupted"; active.Context = "Interrupted"; active.EndedUtc = active.EndedUtc ?? DateTimeOffset.UtcNow; active = null;
            }
        }
        public void NoteChange(string message)
        {
            lock (gate) if (active?.State == "Recording" && active.Changes.Count < 100) active.Changes.Add((message ?? "Tuning activity").Substring(0, Math.Min(message?.Length ?? 15, 500)));
        }
        public static string Context(NativeCoContext start, NativeCoContext end)
        {
            if (start.Hardware != end.Hardware || start.Topology != end.Topology) return "Hardware/topology changed";
            var a = TuningService.RequestsFromProfile(start.EditorRequests); var b = TuningService.RequestsFromProfile(end.EditorRequests);
            if (a.Count != b.Count || a.Zip(b, TuningSnapshots.Same).Any(same => !same)) return "Editor requests changed";
            if (start.ExpectedCores <= 0 || start.ExpectedCores != end.ExpectedCores || start.ObservedCo.Count != start.ExpectedCores || start.ObservedCo.Count != end.ObservedCo.Count || start.ObservedCo.Any(v => !end.ObservedCo.TryGetValue(v.Key, out int observed) || observed != v.Value)) return "CO readback incomplete/changed";
            return "CO endpoints unchanged";
        }
        public string Export()
        {
            lock (gate) return JsonConvert.SerializeObject(new { SchemaVersion = 1, Source = "Native MSR per-core VID; no external imports", Notes = "VID is a request, not measured per-core voltage. Busy uses an effective/reported clock heuristic. Only CO endpoints are read back; no stability guarantee.", Trials = trials }, Formatting.Indented);
        }
        public static string Compare(NativeCoTrial baseline, NativeCoTrial candidate)
        {
            if (baseline == null || candidate == null || baseline.Role != "Baseline" || candidate.Role != "Candidate" || baseline.State != "Completed" || candidate.State != "Completed") return "Select a completed candidate with an earlier baseline.";
            var text = new StringBuilder("Native measurements; manual workload/score. No stability verdict.\r\n");
            bool sameOtherSettings = TuningService.RequestsFromProfile(baseline.Start.EditorRequests).Where(r => r.Kind != TuningKind.CurveOptimizer).Select(r => r.Setting + ":" + r.Requested)
                .SequenceEqual(TuningService.RequestsFromProfile(candidate.Start.EditorRequests).Where(r => r.Kind != TuningKind.CurveOptimizer).Select(r => r.Setting + ":" + r.Requested));
            bool comparable = baseline.Context == "CO endpoints unchanged" && candidate.Context == "CO endpoints unchanged" && baseline.Workload == candidate.Workload && baseline.Start.Hardware == candidate.Start.Hardware && baseline.Start.Topology == candidate.Start.Topology && sameOtherSettings;
            text.AppendLine("Baseline: " + baseline.Context + "; Candidate: " + candidate.Context);
            if (!comparable) text.AppendLine("Comparison inconclusive: workload/context is mixed, incomplete or different.");
            else if (baseline.Score.HasValue && candidate.Score.HasValue) text.AppendLine("Score delta: " + ((candidate.Score.Value / baseline.Score.Value - 1) * 100).ToString("+0.##;-0.##;0", CultureInfo.InvariantCulture) + "% (single run; repeat to establish variation).");
            text.AppendLine("Unobserved external changes cannot be excluded. Lower VID alone is not an improvement verdict.");
            foreach (var before in baseline.Cores.OrderBy(c => c.Slot)) {
                var after = candidate.Cores.FirstOrDefault(c => c.Slot == before.Slot); if (after == null) continue;
                text.AppendLine($"Core {before.Slot}, CCD {before.Ccd}: busy VID {before.BusyMeanVid?.ToString("0.0000", CultureInfo.InvariantCulture) ?? "unavailable"} → {after.BusyMeanVid?.ToString("0.0000", CultureInfo.InvariantCulture) ?? "unavailable"} V; effective {before.MeanEffectiveMhz?.ToString("0", CultureInfo.InvariantCulture) ?? "unavailable"} → {after.MeanEffectiveMhz?.ToString("0", CultureInfo.InvariantCulture) ?? "unavailable"} MHz; samples {before.Samples}/{after.Samples}.");
            }
            return text.ToString();
        }
    }
    public sealed class NativeVidMonitor : IDisposable
    {
        private readonly object gate = new object();
        private readonly CoreVidWindow window = new CoreVidWindow();
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private readonly NativeCoSession session;
        private readonly Func<CancellationToken, IReadOnlyList<CoreVidReading>> read;
        private IReadOnlyList<CoreVidReading> latest = new CoreVidReading[0];
        private Task worker;
        private string error;
        private double duration;
        private double cadence;
        private long lastSuccess;
        private long generation;
        private int disposed;
        public NativeVidMonitor(Func<CancellationToken, IReadOnlyList<CoreVidReading>> read, NativeCoSession session) { this.read = read; this.session = session; }
        public void Start(int intervalMs)
        {
            if (worker != null || intervalMs < 20 || intervalMs > 1000) throw new InvalidOperationException("Invalid monitoring start.");
            worker = Task.Factory.StartNew(() => {
                try {
                    while (!stop.IsCancellationRequested) {
                        long epoch; lock (gate) epoch = generation;
                        long before = Stopwatch.GetTimestamp(); var next = read(stop.Token); long after = Stopwatch.GetTimestamp();
                        lock (gate) {
                            if (epoch != generation) continue;
                            if (next.Count > 0) latest = next;
                            duration = (after - before) * 1000.0 / Stopwatch.Frequency; window.Add(next, after);
                            if (next.Count > 0 && next.All(r => r.VidVolts.HasValue)) { if (lastSuccess > 0) cadence = (before - lastSuccess) * 1000.0 / Stopwatch.Frequency; lastSuccess = before; }
                        }
                        session.Add(next);
                        int wait = Math.Max(1, intervalMs - (int)Math.Min(int.MaxValue, (after - before) * 1000.0 / Stopwatch.Frequency));
                        if (stop.Token.WaitHandle.WaitOne(wait)) break;
                    }
                } catch (OperationCanceledException) { }
                catch (Exception ex) { lock (gate) { error = ex.Message; latest = new CoreVidReading[0]; window.Clear(); } }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        public IReadOnlyList<CoreVidReading> Latest { get { lock (gate) return latest.ToArray(); } }
        public IReadOnlyList<CoreVidReading> Window { get { lock (gate) return window.Recent; } }
        public string Error { get { lock (gate) return error; } }
        public double LastDurationMs { get { lock (gate) return duration; } }
        public double LastCadenceMs { get { lock (gate) return cadence; } }
        public void Clear() { lock (gate) { generation++; latest = new CoreVidReading[0]; window.Clear(); lastSuccess = 0; cadence = 0; } }
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) != 0) return; stop.Cancel(); if (worker == null) stop.Dispose(); else worker.ContinueWith(_ => stop.Dispose(), TaskScheduler.Default); }
    }
}
