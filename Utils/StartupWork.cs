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

namespace ZenStatesDebugTool
{
    // Coalesces demand from repeated tab selections. Failed/cancelled discovery can retry.
    public sealed class DeferredStartupWork<T>
    {
        private readonly object gate = new object();
        private readonly Func<T> read;
        private Task<T> pending;
        public DeferredStartupWork(Func<T> read) { this.read = read ?? throw new ArgumentNullException(nameof(read)); }
        public Task<T> GetAsync(CancellationToken closing)
        {
            closing.ThrowIfCancellationRequested();
            lock (gate) {
                if (pending == null || pending.IsFaulted || pending.IsCanceled)
                    pending = Task.Run(() => { closing.ThrowIfCancellationRequested(); var value = read(); closing.ThrowIfCancellationRequested(); return value; }, closing);
                return pending;
            }
        }
    }
    public sealed class StartupTiming
    {
        public string Stage { get; set; }
        public double StartMs { get; set; }
        public double? DurationMs { get; set; }
    }
    public sealed class StartupTimings
    {
        public static StartupTimings Current { get; } = new StartupTimings();
        public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SMUDebugTool", "startup-timings.json");
        private readonly object gate = new object(), fileGate = new object();
        private readonly List<StartupTiming> entries = new List<StartupTiming>();
        private readonly Func<long> timestamp;
        private readonly double frequency;
        private readonly long began;
        public StartupTimings() : this(Stopwatch.GetTimestamp, Stopwatch.Frequency) { }
        public StartupTimings(Func<long> timestamp, double frequency)
        {
            if (timestamp == null || frequency <= 0) throw new ArgumentException("A monotonic clock is required.");
            this.timestamp = timestamp; this.frequency = frequency; began = timestamp();
        }
        private double Ms(long tick) => Math.Round((tick - began) * 1000.0 / frequency, 1);
        public void Mark(string stage) { lock (gate) if (entries.Count < 128) entries.Add(new StartupTiming { Stage = stage, StartMs = Ms(timestamp()) }); }
        public IDisposable Measure(string stage) => new Scope(this, stage);
        private sealed class Scope : IDisposable
        {
            private readonly StartupTimings owner;
            private readonly string stage;
            private readonly long start;
            private int disposed;
            public Scope(StartupTimings owner, string stage) { this.owner = owner; this.stage = stage; start = owner.timestamp(); }
            public void Dispose()
            {
                if (Interlocked.Exchange(ref disposed, 1) != 0) return;
                lock (owner.gate) if (owner.entries.Count < 128) owner.entries.Add(new StartupTiming { Stage = stage,
                    StartMs = owner.Ms(start), DurationMs = Math.Round((owner.timestamp() - start) * 1000.0 / owner.frequency, 1) });
            }
        }
        public IReadOnlyList<StartupTiming> Snapshot()
        {
            lock (gate) return entries.OrderBy(e => e.StartMs).Select(e => new StartupTiming { Stage = e.Stage, StartMs = e.StartMs, DurationMs = e.DurationMs }).ToArray();
        }
        public string Report() => "Startup milestones and durations (ms). Overlapping stages must not be added together.\r\n\r\n" +
            string.Join("\r\n", Snapshot().Select(e => e.Stage + ": " + (e.DurationMs.HasValue ? e.DurationMs.Value.ToString("0.0", CultureInfo.InvariantCulture) + " ms; started at " : "at ") + e.StartMs.ToString("0.0", CultureInfo.InvariantCulture) + " ms")) +
            "\r\n\r\nLatest report: " + DefaultPath + "\r\nNo hardware identifiers, profile values or driver paths are included.";
        public Task PersistAsync(string path = null) => Task.Run(() => {
            try {
                lock (fileGate) {
                    string destination = path ?? DefaultPath;
                    string json = JsonConvert.SerializeObject(new { SchemaVersion = 1, AppVersion = typeof(StartupTimings).Assembly.GetName().Version.ToString(), Stages = Snapshot() }, Formatting.Indented);
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(destination)));
                    string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try { File.WriteAllText(temporary, json, new UTF8Encoding(false)); if (File.Exists(destination)) File.Replace(temporary, destination, null); else File.Move(temporary, destination); }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                }
            } catch (Exception ex) { Debug.WriteLine("Startup timing report: " + ex.Message); }
        });
    }
}
