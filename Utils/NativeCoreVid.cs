using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using ZenStates.Core;

namespace ZenStatesDebugTool
{
    public sealed class NativeCoreBinding
    {
        public CoreTarget Core { get; }
        public ProcessorTarget Processor { get; }
        public uint ApicId { get; }
        public IReadOnlyList<ProcessorTarget> Siblings { get; }
        public NativeCoreBinding(CoreTarget core, ProcessorTarget processor, uint apicId, IEnumerable<ProcessorTarget> siblings = null)
        { Core = core; Processor = processor; ApicId = apicId; Siblings = Array.AsReadOnly((siblings ?? new[] { processor }).ToArray()); }
    }
    public sealed class NativeThreadIdentity
    {
        public ProcessorTarget Processor { get; }
        public uint ApicId { get; }
        public uint CoreId { get; }
        public int ThreadsPerCore { get; }
        public NativeThreadIdentity(ProcessorTarget processor, uint apicId, uint coreId, int threadsPerCore)
        { Processor = processor; ApicId = apicId; CoreId = coreId; ThreadsPerCore = threadsPerCore; }
    }
    public static class NativeCoreMap
    {
        // Restrict to the reviewed single-package desktop APIC layout. Never map by list position.
        public static IReadOnlyList<NativeCoreBinding> Resolve(TopologySnapshot topology, IEnumerable<NativeThreadIdentity> identities)
        {
            if (topology == null || topology.Error != null || topology.Targets.Count == 0) throw new InvalidOperationException("Validated physical topology is unavailable.");
            var threads = identities.ToList();
            if (threads.Count == 0 || threads.Any(t => t == null || t.Processor == null || t.ThreadsPerCore != 1 && t.ThreadsPerCore != 2) ||
                threads.Select(t => t.ApicId).Distinct().Count() != threads.Count || threads.Select(t => t.Processor.ToString()).Distinct().Count() != threads.Count || threads.Select(t => t.ThreadsPerCore).Distinct().Count() != 1)
                throw new InvalidOperationException("Logical-processor identity is incomplete or ambiguous.");
            int smt = threads[0].ThreadsPerCore;
            if (threads.Any(t => t.CoreId != t.ApicId / smt || topology.Find((int)t.CoreId) == null))
                throw new InvalidOperationException("APIC core identities do not match physical SMU slots; native tuning is unavailable.");
            var groups = threads.GroupBy(t => t.CoreId).ToList();
            if (groups.Count != topology.Targets.Count || groups.Any(g => g.Count() != smt || g.Select(t => t.ApicId % smt).Distinct().Count() != smt))
                throw new InvalidOperationException("SMT siblings or physical cores are missing; native tuning is unavailable.");
            return Array.AsReadOnly(groups.OrderBy(g => g.Key).Select(g => {
                var first = g.OrderBy(t => t.ApicId).First(); return new NativeCoreBinding(topology.Find((int)g.Key), first.Processor, first.ApicId, g.OrderBy(t => t.ApicId).Select(t => t.Processor));
            }).ToArray());
        }
    }
    public sealed class CoreVidReading
    {
        public int Slot { get; }
        public int Ccd { get; }
        public string Identity { get; }
        public long Timestamp { get; }
        public ulong? RawStatus { get; }
        public decimal? VidVolts { get; }
        public decimal? ReportedMhz { get; }
        public double? EffectiveMhz { get; }
        public string Issue { get; }
        public bool Busy => EffectiveMhz.HasValue && ReportedMhz > 0 && EffectiveMhz >= (double)ReportedMhz.Value * 0.8;
        public CoreVidReading(int slot, int ccd, string identity, long timestamp, ulong? raw, decimal? vid, decimal? reported, double? effective, string issue)
        { Slot = slot; Ccd = ccd; Identity = identity; Timestamp = timestamp; RawStatus = raw; VidVolts = vid; ReportedMhz = reported; EffectiveMhz = effective; Issue = issue; }
    }
    public static class NativeVidCodec
    {
        public const uint StatusMsr = 0xC0010293, AperfMsr = 0xC00000E8;
        public static bool Decode(ulong raw, uint family, out decimal vid, out decimal mhz)
        {
            vid = mhz = 0;
            if (raw == 0 || raw == ulong.MaxValue || family != 0x19 && family != 0x1A) return false;
            // Hardware status VID [21:14] is the legacy status request encoding,
            // NOT the SVI3 rail code or P-state definition's split nine-bit VID.
            vid = 1.55m - ((raw >> 14) & 0xFF) * 0.00625m;
            if (family == 0x1A) mhz = (raw & 0xFFF) * 5m;
            else {
                uint did = (uint)(raw >> 8 & 0x3F);
                if (did == 0) return false;
                mhz = (raw & 0xFF) * 200m / did;
            }
            return vid > 0.1m && vid <= 1.55m && mhz >= 80 && mhz <= 9000;
        }
    }
    // Read-only, synchronous worker: each pin is restored before visiting the next core.
    public sealed class NativeVidReader
    {
        private readonly IReadOnlyList<NativeCoreBinding> bindings;
        public IReadOnlyList<NativeCoreBinding> Bindings => bindings;
        private readonly IProcessorAffinity affinity;
        private readonly Func<uint, ulong?> read;
        private readonly uint family;
        private readonly Func<long> timestamp;
        private bool failed;
        private readonly Dictionary<int, Tuple<ulong, long>> previous = new Dictionary<int, Tuple<ulong, long>>();
        public NativeVidReader(IReadOnlyList<NativeCoreBinding> bindings, IProcessorAffinity affinity, Func<uint, ulong?> read, uint family, Func<long> timestamp = null)
        { this.bindings = bindings; this.affinity = affinity; this.read = read; this.family = family; this.timestamp = timestamp ?? Stopwatch.GetTimestamp; }
        public IReadOnlyList<CoreVidReading> Read(CancellationToken cancellation)
        {
            if (failed) throw new InvalidOperationException("Native capture stopped after an affinity/driver exception. Reopen the workbench before retrying.");
            var readings = new List<CoreVidReading>();
            foreach (var binding in bindings) {
                cancellation.ThrowIfCancellationRequested();
                string identity = binding.Core.Identity + "; APIC " + binding.ApicId + "; Windows " + binding.Processor;
                ulong? raw = null; long stamp = timestamp();
                try {
                    using (affinity.Pin(binding.Processor)) {
                        raw = read(NativeVidCodec.StatusMsr); cancellation.ThrowIfCancellationRequested();
                        ulong? aperf = read(NativeVidCodec.AperfMsr); stamp = timestamp();
                        if (!raw.HasValue || !aperf.HasValue || !NativeVidCodec.Decode(raw.Value, family, out decimal vid, out decimal mhz)) {
                            previous.Remove(binding.Core.Slot);
                            readings.Add(new CoreVidReading(binding.Core.Slot, binding.Core.Ccd, identity, stamp, raw, null, null, null, "MSR read/encoding unavailable.")); continue;
                        }
                        double? effective = null;
                        if (previous.TryGetValue(binding.Core.Slot, out var prior) && stamp > prior.Item2 && aperf >= prior.Item1) {
                            double elapsed = (stamp - prior.Item2) / (double)Stopwatch.Frequency;
                            double measured = (aperf.Value - prior.Item1) / elapsed / 1000000.0;
                            if (elapsed <= 2 && measured >= 0 && measured <= 10000) effective = measured;
                        }
                        previous[binding.Core.Slot] = Tuple.Create(aperf.Value, stamp);
                        readings.Add(new CoreVidReading(binding.Core.Slot, binding.Core.Ccd, identity, stamp, raw, vid, mhz, effective, effective.HasValue ? null : "Effective clock needs a fresh counter interval."));
                    }
                } catch (OperationCanceledException) { throw; }
                catch (Exception ex) {
                    failed = true;
                    previous.Clear();
                    readings.RemoveAll(r => r.Slot == binding.Core.Slot);
                    readings.Add(new CoreVidReading(binding.Core.Slot, binding.Core.Ccd, identity, stamp, raw, null, null, null, ex.Message));
                    // Do not continue after an affinity restore or driver exception.
                    break;
                }
            }
            return readings.AsReadOnly();
        }
        public static NativeVidReader Create(Cpu cpu, TopologySnapshot topology)
        {
            if (cpu == null || !cpu.CanAccessCurrentProcessorMsr ||
                (cpu.info.codeName != Cpu.CodeName.GraniteRidge || (uint)cpu.info.family != 0x1A || cpu.info.model != 0x44) &&
                (cpu.info.codeName != Cpu.CodeName.Raphael || (uint)cpu.info.family != 0x19 || cpu.info.model != 0x61))
                throw new InvalidOperationException("Native VID capture currently supports the reviewed Raphael/Granite Ridge desktop layouts and requires the existing MSR driver.");
            var pin = new ProcessorAffinity(); var identities = new List<NativeThreadIdentity>();
            foreach (var target in ProcessorAffinity.GetActiveTargets()) {
                Hardware.ThrowIfClosing();
                using (pin.Pin(target)) {
                    uint eax = 0, ebx = 0, ecx = 0, edx = 0;
                    if (!cpu.Cpuid(0x8000001E, ref eax, ref ebx, ref ecx, ref edx)) throw new InvalidOperationException("Per-processor CPUID read failed.");
                    identities.Add(new NativeThreadIdentity(target, eax, ebx & 0xFF, (int)((ebx >> 8 & 0xFF) + 1)));
                }
            }
            return new NativeVidReader(NativeCoreMap.Resolve(topology, identities), pin,
                address => cpu.TryReadCurrentProcessorMsr(address, out ulong value) ? (ulong?)value : null, (uint)cpu.info.family);
        }
    }
    public sealed class CoreVidWindow
    {
        private readonly Queue<CoreVidReading> values = new Queue<CoreVidReading>();
        public void Add(IEnumerable<CoreVidReading> readings, long now)
        {
            foreach (var reading in readings) values.Enqueue(reading);
            while (values.Count > 16000 || values.Count > 0 && (now - values.Peek().Timestamp) / (double)Stopwatch.Frequency > 5) values.Dequeue();
        }
        public IReadOnlyList<CoreVidReading> Recent => values.ToArray();
        public void Clear() => values.Clear();
    }
    public sealed class CoStepProposal
    {
        public int Slot { get; }
        public int Previous { get; }
        public int Requested => Previous - 1;
        public string Reason { get; }
        public CoStepProposal(int slot, int previous, string reason)
        {
            if (slot < 0 || slot >= 128 || previous < -998 || previous > 0) throw new ArgumentOutOfRangeException();
            Slot = slot; Previous = previous; Reason = reason;
        }
    }
    public static class NativeCoStepTransaction
    {
        public static TuningResult Apply(ITuningTransport transport, CoStepProposal proposal, CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            var request = new TuningRequest { Kind = TuningKind.CurveOptimizer, Index = proposal.Slot, Value = proposal.Requested };
            if (!transport.IsSupported(request, out _)) throw new InvalidOperationException("Physical CO target is unavailable. No command sent.");
            var scalar = transport.Read(new TuningRequest { Kind = TuningKind.Scalar });
            if (scalar?.Status != 1 || !scalar.Direct || scalar.ReadIssue != null || scalar.Value < 1 || scalar.Value > 10 || !scalar.Value.HasValue)
                throw new InvalidOperationException("A usable PBO scalar readback is required; manual OC/unknown mode is not supported. No command sent.");
            cancellation.ThrowIfCancellationRequested();
            var before = transport.Read(request);
            if (before?.Status != 1 || !before.Direct || before.ReadIssue != null || before.Value != proposal.Previous)
                throw new InvalidOperationException("CO changed or cannot be read before the trial. No command sent.");
            return new TuningService(transport).Apply(new[] { request }, cancellation);
        }
    }
    public static class CoStepPlanner
    {
        public static CoStepProposal Suggest(IEnumerable<CoreVidReading> readings, IReadOnlyDictionary<int, int> observedCo, int minimumCo, long now)
        {
            if (minimumCo < -999 || minimumCo >= 0) throw new ArgumentOutOfRangeException(nameof(minimumCo));
            var groups = readings.Where(r => now >= r.Timestamp && (now - r.Timestamp) / (double)Stopwatch.Frequency <= 5).GroupBy(r => r.Slot).ToList();
            if (groups.Any(g => g.OrderBy(r => r.Timestamp).Last().Issue != null || (now - g.Max(r => r.Timestamp)) / (double)Stopwatch.Frequency > 0.5)) return null;
            var candidates = new List<Tuple<int, int, decimal, decimal, decimal>>();
            foreach (var group in groups) {
                var samples = group.OrderBy(r => r.Timestamp).ToArray(); var last = samples.Last();
                // Busy is an effective/reported clock heuristic, not a CPU-utilization counter.
                if ((now - last.Timestamp) / (double)Stopwatch.Frequency > 0.5 || samples.Length < 20 ||
                    (last.Timestamp - samples[0].Timestamp) / (double)Stopwatch.Frequency < 2 || samples.Any(s => s.Issue != null || !s.Busy || !s.VidVolts.HasValue || !s.ReportedMhz.HasValue) ||
                    !observedCo.TryGetValue(group.Key, out int co) || co < -999 || co > 999) continue;
                decimal frequency = samples.Average(s => s.ReportedMhz.Value);
                if (samples.Max(s => s.ReportedMhz.Value) - samples.Min(s => s.ReportedMhz.Value) > frequency * 0.03m) continue;
                candidates.Add(Tuple.Create(group.Key, last.Ccd, samples.Average(s => s.VidVolts.Value), frequency, (decimal)co));
            }
            foreach (var core in candidates.OrderByDescending(c => c.Item3)) {
                if (core.Item5 - 1 < minimumCo || core.Item5 > 0) continue;
                var peers = candidates.Where(p => p.Item1 != core.Item1 && p.Item2 == core.Item2 && Math.Abs(p.Item4 - core.Item4) <= core.Item4 * 0.03m).Select(p => p.Item3).OrderBy(v => v).ToArray();
                if (peers.Length < 2) continue;
                decimal median = peers[peers.Length / 2];
                if (core.Item3 - median >= 0.0125m)
                    return new CoStepProposal(core.Item1, (int)core.Item5, "Higher requested VID than comparable busy peers in the same CCD. One-step trial only; not a performance or stability verdict.");
            }
            return null;
        }
    }
}
