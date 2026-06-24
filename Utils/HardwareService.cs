using System.Collections.Generic;
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

        public HardwareService(Cpu cpu)
        {
            this.cpu = cpu;
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
            var readings = new List<MsrReading>();
            lock (Hardware.Sync)
            {
                uint msr = start;
                while (msr <= end)
                {
                    uint eax = 0, edx = 0;
                    if (cpu.ReadMsr(msr, ref eax, ref edx))
                        readings.Add(new MsrReading(msr, eax, edx));
                    msr += 1;
                }
            }
            return readings;
        }
    }
}
