using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Eventing.Reader;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ZenStatesDebugTool
{
    public interface ICoIncidentSource : IDisposable { string Issue { get; } }
    public interface ICoRuntimeSafety : IDisposable
    {
        string Issue(decimal stopTemperature);
        bool TemperatureStable { get; }
        decimal? Temperature { get; }
    }
    public sealed class CoTemperatureWindow
    {
        private readonly Queue<Tuple<long, decimal>> samples = new Queue<Tuple<long, decimal>>();
        public void Add(long timestamp, decimal value)
        {
            if (value <= 0 || value > 115) return;
            samples.Enqueue(Tuple.Create(timestamp, value));
            while (samples.Count > 10 || samples.Count > 0 && (timestamp - samples.Peek().Item1) / (double)Stopwatch.Frequency > 6) samples.Dequeue();
        }
        public decimal? Latest => samples.Count == 0 ? null : (decimal?)samples.Last().Item2;
        public string Issue(long now, decimal stop)
        {
            if (samples.Count == 0 || (now - samples.Last().Item1) / (double)Stopwatch.Frequency > 2) return "CPU temperature is missing or stale.";
            return Latest >= stop ? "CPU temperature reached the selected stop limit; no further tuning." : null;
        }
        public bool Stable => samples.Count >= 5 && (samples.Last().Item1 - samples.Peek().Item1) / (double)Stopwatch.Frequency >= 4 && samples.Max(s => s.Item2) - samples.Min(s => s.Item2) <= 2;
    }
    public sealed class WheaCoIncidentSource : ICoIncidentSource
    {
        private readonly EventLogWatcher watcher;
        private string issue;
        public string Issue => Volatile.Read(ref issue);
        public WheaCoIncidentSource()
        {
            var query = new EventLogQuery("System", PathType.LogName, "*[System[Provider[@Name='Microsoft-Windows-WHEA-Logger']]]");
            watcher = new EventLogWatcher(query, null, false);
            watcher.EventRecordWritten += (s, e) => {
                try {
                    using (var record = e.EventRecord) {
                        string message = e.EventException != null ? "WHEA observation failed: " + e.EventException.Message : record == null ? "WHEA event could not be read." :
                            "New WHEA hardware event " + record.Id + " (record " + record.RecordId + "). Cause/core attribution is unconfirmed.";
                        Interlocked.CompareExchange(ref issue, message, null);
                    }
                } catch (Exception ex) { Interlocked.CompareExchange(ref issue, "WHEA observation failed: " + ex.Message, null); }
            };
            try { watcher.Enabled = true; } catch { watcher.Dispose(); throw; }
        }
        public void Dispose() => watcher.Dispose();
    }
    public sealed class CoRuntimeSafety : ICoRuntimeSafety
    {
        private readonly object gate = new object();
        private readonly CoTemperatureWindow temperatures = new CoTemperatureWindow();
        private readonly ICoIncidentSource incidents;
        private readonly CancellationTokenSource stop = new CancellationTokenSource();
        private readonly Task worker;
        private string error;
        private int disposed;
        public CoRuntimeSafety(Func<decimal?> readTemperature, ICoIncidentSource incidents)
        {
            this.incidents = incidents;
            worker = Task.Factory.StartNew(() => {
                try {
                    while (!stop.IsCancellationRequested) {
                        var value = readTemperature();
                        if (value.HasValue) lock (gate) temperatures.Add(Stopwatch.GetTimestamp(), value.Value);
                        if (stop.Token.WaitHandle.WaitOne(1000)) break;
                    }
                } catch (Exception ex) { lock (gate) error = "Temperature read failed: " + ex.Message; }
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }
        public string Issue(decimal stopTemperature)
        { lock (gate) return incidents.Issue ?? error ?? temperatures.Issue(Stopwatch.GetTimestamp(), stopTemperature); }
        public decimal? Temperature { get { lock (gate) return temperatures.Latest; } }
        public bool TemperatureStable { get { lock (gate) return temperatures.Stable; } }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            stop.Cancel(); incidents.Dispose(); worker.ContinueWith(_ => stop.Dispose(), TaskScheduler.Default);
        }
    }
}
