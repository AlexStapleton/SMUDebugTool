// Hardware-free Release probe. Runs production code against immutable synthetic data.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;
using ZenStatesDebugTool;
using ZenStatesDebugTool.Profiles;

public static class PerformanceProbe
{
    static readonly BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    static readonly List<object> Results = new List<object>();
    static object sink;
    static void Set(object target, string name, object value) { target.GetType().GetField(name, Hidden).SetValue(target, value); }
    static void Property(object target, string name, object value) { Set(target, "<" + name + ">k__BackingField", value); }
    static object Field(object target, string name) { return target.GetType().GetField(name, Hidden).GetValue(target); }
    sealed class Clock : IGuidedCoClock {
        public long Timestamp { get; set; }
        public DateTimeOffset UtcNow { get { return DateTimeOffset.UtcNow; } }
        public Task Delay(int milliseconds, CancellationToken cancellation) { throw new InvalidOperationException("Probe cannot tune or launch a workload."); }
    }
    static CoreVidReading[] Samples(int cores, int frames, long now, decimal delta = 0) {
        return Enumerable.Range(0, frames).SelectMany(frame => Enumerable.Range(0, cores).Select(core =>
            new CoreVidReading(core, core / 8, "Synthetic core " + core, now - (long)((frames - 1 - frame) * .02 * Stopwatch.Frequency), 1,
                core % 8 == 0 ? 1.15m + delta : 1.10m, 5000, 4950, null))).ToArray();
    }
    static void Measure(string name, int iterations, Action action) {
        for (int i = 0; i < 8; i++) action();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long allocated = AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize;
        int collections = GC.CollectionCount(0); var elapsed = Stopwatch.StartNew();
        for (int i = 0; i < iterations; i++) action();
        elapsed.Stop();
        Results.Add(new { Name = name, Iterations = iterations, TotalMs = elapsed.Elapsed.TotalMilliseconds,
            MicrosecondsPerOperation = elapsed.Elapsed.TotalMilliseconds * 1000 / iterations,
            AllocatedBytesPerOperation = (AppDomain.CurrentDomain.MonitoringTotalAllocatedMemorySize - allocated) / (double)iterations,
            Gen0Collections = GC.CollectionCount(0) - collections });
    }
    static void RenderProbe(int cores, bool changing) {
        long now = Stopwatch.GetTimestamp(); var clock = new Clock { Timestamp = now };
        var offsets = Enumerable.Range(0, cores).ToDictionary(c => c, c => 0);
        var context = new NativeCoContext("Synthetic", "Synthetic mapping", new Profile(), offsets, cores);
        var samples = Samples(cores, 151, now); IReadOnlyList<CoreVidReading> latest = samples.Skip(samples.Length - cores).ToArray();
        var recovery = new CoRecoveryStore(Path.Combine(Path.GetTempPath(), "sdt-perf-" + Guid.NewGuid().ToString("N"), "recovery.json"));
        var services = new GuidedCoServices { Recovery = recovery, Journal = new CoTrialJournal(CoRecoveryStore.DefaultPath + ".synthetic-unused"),
            CreateSource = () => { throw new Exception("Probe cannot initialize hardware"); }, CreateSafety = () => { throw new Exception("Probe cannot initialize safety"); } };
        var records = new NativeCoSession();
        using (var form = new GuidedCoOptimizerForm(services)) {
            Set(form, "stopped", true); form.ShowInTaskbar = false; form.Opacity = 0; form.ClientSize = new Size(1050, 750); form.Show();
            ((System.Windows.Forms.Timer)Field(form, "render")).Stop(); Set(form, "stopped", false);
            ((GuidedCoSession)Field(form, "session")).Dispose();
            var session = new GuidedCoSession(services, records, () => latest, () => samples, () => null, () => { }, CancellationToken.None, clock);
            Set(session, "expected", context); Set(session, "options", new GuidedCoOptions(new WorkloadRecipe(Assembly.GetEntryAssembly().Location,
                "g_CinebenchCpuXTest=true g_CinebenchMinimumTestDuration=60", "Synthetic", TimeSpan.FromSeconds(120)), -40, TimeSpan.FromMinutes(20), 90, true));
            Property(session, "Phase", GuidedCoPhase.ReadyToBalance); Property(session, "HasReference", true); Set(session, "referenceOffsets", offsets); Set(form, "session", session);
            var monitor = new NativeVidMonitor(ct => latest, records); Set(monitor, "latest", latest);
            ((CoreVidWindow)Field(monitor, "window")).Add(samples, now); Set(form, "monitor", monitor);
            var render = (Action)Delegate.CreateDelegate(typeof(Action), form, form.GetType().GetMethod("Render", Hidden)); int frame = 0;
            Measure("Guided render / " + cores + " cores / " + (changing ? "changing frame" : "unchanged frame"), 160, () => {
                if (changing) { samples = Samples(cores, 151, now, (frame++ % 2) * .00625m); latest = samples.Skip(samples.Length - cores).ToArray(); Set(monitor, "latest", latest); }
                render(); form.Update();
            });
            form.Close();
        }
    }
    [STAThread] public static int Main(string[] args) {
        if (args.Length < 1 || args.Length > 2) return 2;
        bool exportOnly = args.Length == 2 && args[1] == "export-only";
        AppDomain.MonitoringIsEnabled = true; Application.EnableVisualStyles();
        if (!exportOnly) foreach (int cores in new[] { 16, 64 }) {
            long now = Stopwatch.GetTimestamp(); var samples = Samples(cores, 251, now); var offsets = Enumerable.Range(0, cores).ToDictionary(c => c, c => 0);
            Measure("Eligibility / " + cores + " cores / 20 ms", 500, () => sink = CoEligibility.Analyze(samples, offsets, -40, now));
            var window = new CoreVidWindow(); window.Add(samples, now);
            Measure("Window snapshot / " + cores + " cores", 1000, () => sink = window.Recent);
        }
        var history = new NativeCoSession(); var co = Enumerable.Range(0, 64).ToDictionary(c => c, c => 0);
        var context = new NativeCoContext("Synthetic", "Synthetic", new Profile(), co, 64);
        for (int i = 0; i < 100; i++) { history.Begin("Baseline", "Synthetic", context, DateTimeOffset.UtcNow); history.Add(Samples(64, 1, Stopwatch.GetTimestamp())); history.MarkEnd(DateTimeOffset.UtcNow.AddSeconds(1)); history.End(context, 100,
            new WorkloadResult { Outcome = "Completed", Score = 100, Output = new string('x', 65536) }); }
        if (!exportOnly) {
        var count = typeof(NativeCoSession).GetProperty("TrialCount");
        Measure("History count / 100 runs / 64 cores", 500, () => sink = count == null ? history.Trials.Count : count.GetValue(history, null));
        var live = new NativeCoSession(); live.Begin("Baseline", "Synthetic", context, DateTimeOffset.UtcNow); var frameRows = Samples(64, 1, Stopwatch.GetTimestamp());
        Measure("Record frame / 64 cores", 1000, () => live.Add(frameRows));
        RenderProbe(16, false); RenderProbe(64, false); RenderProbe(16, true);
        } else {
            var journal = new CoTrialJournal(Path.Combine(Path.GetTempPath(), "sdt-perf-unused-" + Guid.NewGuid().ToString("N")));
            var nativeSnapshot = typeof(NativeCoSession).GetMethod("ExportSnapshot"); var journalSnapshot = typeof(CoTrialJournal).GetMethod("ExportSnapshot");
            var streaming = typeof(NativeCoSession).Assembly.GetType("ZenStatesDebugTool.AtomicJsonExport");
            string report = Path.Combine(Path.GetTempPath(), "sdt-perf-export-" + Guid.NewGuid().ToString("N") + ".json");
            try {
                Measure("Export / 100 runs / 64 cores / 64 KiB transcripts", 5, () => {
                    object payload = new { SchemaVersion = 2, Runs = nativeSnapshot == null ? (object)Newtonsoft.Json.Linq.JToken.Parse(history.Export()) : nativeSnapshot.Invoke(history, null),
                        TrialJournal = journalSnapshot == null ? (object)Newtonsoft.Json.Linq.JToken.Parse(journal.Export()) : journalSnapshot.Invoke(journal, null) };
                    if (streaming != null) streaming.GetMethod("Write").Invoke(null, new object[] { report, payload, 16L * 1024 * 1024 });
                    else { string json = JsonConvert.SerializeObject(payload, Formatting.Indented); if (System.Text.Encoding.UTF8.GetByteCount(json) > 16 * 1024 * 1024) throw new Exception("Report too large"); File.WriteAllText(report, json, new System.Text.UTF8Encoding(false)); }
                });
            } finally { if (File.Exists(report)) File.Delete(report); }
        }
        File.WriteAllText(args[0], JsonConvert.SerializeObject(new { SchemaVersion = 1, Runtime = Environment.Version.ToString(),
            Notes = "Synthetic CPU-free microbenchmarks; includes managed allocation and WinForms costs, not hardware latency or Cinebench performance. Same fixture/workload before and after. Process-wide allocation counter; warmed up; no statistical confidence claim.", Results }, Formatting.Indented));
        GC.KeepAlive(sink); return 0;
    }
}
