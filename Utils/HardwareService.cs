using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using ZenStates.Core;

namespace ZenStatesDebugTool
{
    // Result of a single MSR read.
    public struct MsrReadResult
    {
        public bool Ok;
        public uint Eax;
        public uint Edx;
        public MsrReadResult(bool ok, uint eax, uint edx) { Ok = ok; Eax = eax; Edx = edx; }
    }

    // One successful row of an MSR range scan.
    public struct MsrReading
    {
        public uint Address;
        public uint Eax;
        public uint Edx;
        public MsrReading(uint address, uint eax, uint edx) { Address = address; Eax = eax; Edx = edx; }
    }

    // Facade over the CPU/SMU hardware that owns the Hardware.Sync locking, so callers
    // gather inputs and render output without touching cpu/Hardware directly. This is the
    // MSR slice (Phase 2a); PCI/CPUID/OC/PBO are added in later slices.
    public sealed class HardwareService
    {
        private readonly Cpu cpu;
        private readonly AmdFamily17 pstateDriver;

        public HardwareService(Cpu cpu)
        {
            this.cpu = cpu;
            // Cpu.WriteMsr broadcasts one value to every logical processor. Bind the
            // existing single-processor driver so a frequency edit preserves each
            // target's own VID/reserved bits. Fail closed if a future DLL changes it.
            pstateDriver = typeof(Cpu).GetField("_pawnAmd", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(cpu) as AmdFamily17;
        }

        public bool CanWritePStates => pstateDriver != null && Environment.Is64BitProcess;

        public MsrReadResult ReadPState(int stateId)
        {
            if (stateId < 0 || stateId >= PStateMath.Count) throw new ArgumentOutOfRangeException(nameof(stateId));
            return OnPStateThread(() =>
            {
                lock (Hardware.Sync)
                {
                    Hardware.ThrowIfClosing();
                    var targets = ProcessorAffinity.GetActiveTargets();
                    if (targets.Count == 0) throw new InvalidOperationException("No active processors found.");
                    using (new ProcessorAffinity().Pin(targets[0]))
                        return ReadMsr(PStateMath.BaseMsr + (uint)stateId);
                }
            });
        }

        public PStateWriteResult WritePStateFrequency(int stateId, PStateLayout layout, uint fid, uint did)
        {
            return OnPStateThread(() =>
            {
                lock (Hardware.Sync)
                {
                    Hardware.ThrowIfClosing();
                    if (!CanWritePStates)
                        return new PStateWriteResult
                        {
                            Status = PStateWriteStatus.OperationFailed,
                            Error = "Per-processor P-state writes are unavailable with this core library/process."
                        };
                    try
                    {
                        return PStateFrequencyWriter.Apply(ProcessorAffinity.GetActiveTargets(), new ProcessorAffinity(), stateId,
                            layout, fid, did, ReadPStateRegister, (address, value) => pstateDriver.WriteMsr(address, value),
                            PreparePStateWrite);
                    }
                    catch (Exception ex)
                    {
                        return new PStateWriteResult { Status = PStateWriteStatus.OperationFailed, Error = ex.Message };
                    }
                }
            });
        }

        private static T OnPStateThread<T>(Func<T> operation)
        {
            // Windows 11 threads may initially span groups. A dedicated short-lived
            // thread keeps group pinning (including a failed restore) off the UI/pool
            // thread. Acquire Hardware.Sync inside the worker, never while joining it.
            T result = default;
            Exception failure = null;
            var worker = new Thread(() =>
            {
                try { result = operation(); }
                catch (Exception ex) { failure = ex; }
            })
            { IsBackground = true, Name = "PStateIO" };
            worker.Start();
            worker.Join();
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
            return result;
        }

        private ulong? ReadPStateRegister(uint address)
        {
            return pstateDriver.ReadMsr(address, out ulong value) ? (ulong?)value : null;
        }

        private bool PreparePStateWrite()
        {
            // Existing P0/TSC workaround, applied on the same pinned processor.
            const uint hwcr = 0xC0010015;
            const ulong tscFrequencySelect = 1UL << 21;
            ulong? original = ReadPStateRegister(hwcr);
            return original.HasValue && ((original.Value & tscFrequencySelect) != 0
                || pstateDriver.WriteMsr(hwcr, original.Value | tscFrequencySelect));
        }

        public MsrReadResult ReadMsr(uint msr)
        {
            uint eax = 0, edx = 0;
            bool ok = Hardware.Locked(() => cpu.ReadMsr(msr, ref eax, ref edx));
            return new MsrReadResult(ok, eax, edx);
        }

        public bool WriteMsr(uint msr, uint eax, uint edx)
        {
            return Hardware.Locked(() => cpu.WriteMsr(msr, eax, edx));
        }

        // Reads start..end inclusive under a single lock; returns only successful reads.
        // Preserves the original scan loop semantics (while msr <= end; msr++).
        public List<MsrReading> ScanMsrRange(uint start, uint end)
        {
            if (end < start || (ulong)end - start >= 65536) throw new ArgumentException("Scan 1–65536 MSRs at a time.");
            var readings = new List<MsrReading>();
            lock (Hardware.Sync)
            {
                Hardware.ThrowIfClosing();
                for (ulong address = start; address <= end; address++)
                {
                    Hardware.ThrowIfClosing();
                    uint msr = (uint)address;
                    uint eax = 0, edx = 0;
                    if (cpu.ReadMsr(msr, ref eax, ref edx))
                        readings.Add(new MsrReading(msr, eax, edx));

                }
            }
            return readings;
        }
    }
}
