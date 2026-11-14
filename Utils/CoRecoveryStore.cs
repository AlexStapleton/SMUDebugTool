using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace ZenStatesDebugTool
{
    public sealed class CoRecoveryRecord
    {
        public int SchemaVersion { get; set; } = 1;
        public string Machine { get; set; }
        public string Hardware { get; set; }
        public string Topology { get; set; }
        public Dictionary<int, int> OriginalCo { get; set; }
        public DateTimeOffset CreatedUtc { get; set; }
    }
    public sealed class CoRecoveryStore
    {
        public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SMUDebugTool", "native-co-recovery.json");
        private readonly string path;
        public CoRecoveryStore(string path) { this.path = Path.GetFullPath(path); }
        public bool IsPending {
            get {
                try { File.GetAttributes(path); return true; }
                catch (FileNotFoundException) { return false; } catch (DirectoryNotFoundException) { return false; }
                catch { return true; } // Unreadable recovery cannot silently enable automatic writes.
            }
        }
        public IDisposable AcquireAutomaticApply()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try { if (IsPending) throw new InvalidOperationException("Automatic apply blocked by pending CO recovery. Restore or explicitly keep current offsets in Native CO workbench."); return lease; }
            catch { lease.Dispose(); throw; }
        }
        public CoRecoveryRecord Read()
        {
            if (!IsPending) return null;
            if (new FileInfo(path).Length > 131072) throw new InvalidOperationException("CO recovery record is oversized; automatic apply remains blocked.");
            var record = JsonConvert.DeserializeObject<CoRecoveryRecord>(File.ReadAllText(path));
            if (record?.SchemaVersion != 1 || record.Machine != Environment.MachineName || string.IsNullOrEmpty(record.Hardware) || string.IsNullOrEmpty(record.Topology) ||
                record.OriginalCo == null || record.OriginalCo.Count == 0 || record.OriginalCo.Count > 128 || record.OriginalCo.Any(c => c.Key < 0 || c.Key >= 128 || c.Value < -999 || c.Value > 999))
                throw new InvalidOperationException("CO recovery record is invalid or from another machine; automatic apply remains blocked.");
            return record;
        }
        public static void RequireComplete(NativeCoContext context)
        {
            if (context == null || context.ExpectedCores <= 0 || context.ObservedCo.Count != context.ExpectedCores) throw new InvalidOperationException("Complete direct CO readback is required; no commands sent.");
        }
        public void Preserve(NativeCoContext context)
        {
            RequireComplete(context);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) {
                if (IsPending) { Match(Read(), context); return; }
                var record = new CoRecoveryRecord { Machine = Environment.MachineName, Hardware = context.Hardware, Topology = context.Topology,
                    OriginalCo = context.ObservedCo.ToDictionary(c => c.Key, c => c.Value), CreatedUtc = DateTimeOffset.UtcNow };
                string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try { File.WriteAllText(temporary, JsonConvert.SerializeObject(record, Formatting.Indented), new UTF8Encoding(false)); File.Move(temporary, path); }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
        public static void Match(CoRecoveryRecord record, NativeCoContext context)
        {
            RequireComplete(context);
            if (record == null || record.Hardware != context.Hardware || record.Topology != context.Topology || !record.OriginalCo.Keys.OrderBy(k => k).SequenceEqual(context.ObservedCo.Keys.OrderBy(k => k)))
                throw new InvalidOperationException("Recovery hardware/topology does not match; no commands sent.");
        }
        public void Clear(NativeCoContext context)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) {
                if (!IsPending) return; Match(Read(), context); File.Delete(path);
            }
        }
    }
    public sealed class CoBatchPlan
    {
        public IReadOnlyDictionary<int, int> Expected { get; }
        public IReadOnlyDictionary<int, int> Requested { get; }
        public CoBatchPlan(IReadOnlyDictionary<int, int> expected, IReadOnlyDictionary<int, int> requested)
        { Expected = new System.Collections.ObjectModel.ReadOnlyDictionary<int, int>(expected.ToDictionary(c => c.Key, c => c.Value)); Requested = new System.Collections.ObjectModel.ReadOnlyDictionary<int, int>(requested.ToDictionary(c => c.Key, c => c.Value)); }
    }
    public static class CoBatchTransaction
    {
        public static TuningResult Apply(ITuningTransport transport, IReadOnlyDictionary<int, int> expected, IReadOnlyDictionary<int, int> requested, System.Threading.CancellationToken cancellation)
        {
            if (expected.Count == 0 || !expected.Keys.OrderBy(k => k).SequenceEqual(requested.Keys.OrderBy(k => k))) throw new InvalidOperationException("Complete matching CO targets are required.");
            cancellation.ThrowIfCancellationRequested();
            var scalar = transport.Read(new TuningRequest { Kind = TuningKind.Scalar });
            if (scalar?.Status != 1 || !scalar.Direct || scalar.ReadIssue != null || !scalar.Value.HasValue || scalar.Value < 1 || scalar.Value > 10) throw new InvalidOperationException("Usable direct PBO scalar evidence is required before a CO batch.");
            var requests = requested.OrderBy(c => c.Key).Select(c => new TuningRequest { Kind = TuningKind.CurveOptimizer, Index = c.Key, Value = c.Value }).ToArray();
            // Validate the whole batch and all current values before the first write.
            foreach (var request in requests) {
                cancellation.ThrowIfCancellationRequested();
                if (request.Value < -999 || request.Value > 999 || !transport.IsSupported(request, out _)) throw new InvalidOperationException("CO batch target/value is unavailable.");
                var reply = transport.Read(request);
                if (reply?.Status != 1 || !reply.Direct || reply.ReadIssue != null || reply.Value != expected[request.Index]) throw new InvalidOperationException("CO changed before batch; no commands sent.");
            }
            var result = new TuningResult(); result.Rows.AddRange(requests.Select(r => new TuningRow { Request = r, Reason = "Not attempted after stop or failed verification." }));
            for (int i = 0; i < requests.Length; i++) {
                if (cancellation.IsCancellationRequested) break;
                var one = new TuningService(transport).Apply(new[] { requests[i] }, cancellation); result.Rows[i] = one.Rows.Single();
                if (one.Rows.Any(r => r.Outcome != TuningOutcome.Verified)) break;
            }
            return result;
        }
    }
}
