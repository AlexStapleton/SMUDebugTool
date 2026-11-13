using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace ZenStatesDebugTool
{
    public sealed class ProcessorTarget
    {
        public ushort Group { get; }
        public int Processor { get; }
        public ProcessorTarget(ushort group, int processor)
        {
            if (processor < 0 || processor > 63) throw new ArgumentOutOfRangeException(nameof(processor));
            Group = group;
            Processor = processor;
        }
        public override string ToString() => $"group {Group}, processor {Processor}";
    }

    public interface IProcessorAffinity
    {
        // A failed pin must throw before returning a scope. Dispose restores affinity.
        IDisposable Pin(ProcessorTarget target);
    }

    public enum PStateWriteStatus
    {
        Success, InvalidInput, ReadFailed, Disabled, PrepareFailed, WriteFailed,
        ReadbackFailed, ReadbackMismatch, AffinityFailed, OperationFailed
    }

    public sealed class PStateWriteResult
    {
        public PStateWriteStatus Status { get; internal set; }
        public bool Success => Status == PStateWriteStatus.Success;
        public int Completed { get; internal set; }
        public ProcessorTarget Target { get; internal set; }
        public ulong? Original { get; internal set; }
        public ulong? Requested { get; internal set; }
        public ulong? Observed { get; internal set; }
        public string Error { get; internal set; }
    }

    // Hardware-independent transaction, run under Hardware.Sync by HardwareService.
    public static class PStateFrequencyWriter
    {
        public static PStateWriteResult Apply(IReadOnlyList<ProcessorTarget> targets,
            IProcessorAffinity affinity, int stateId, PStateLayout layout, uint fid, uint did,
            Func<uint, ulong?> read, Func<uint, ulong, bool> write, Func<bool> prepare)
        {
            var result = new PStateWriteResult { Status = PStateWriteStatus.InvalidInput };
            result.Error = PStateMath.Validate(layout, fid, did);
            if (result.Error != null) return result;
            if (stateId < 0 || stateId >= PStateMath.Count || targets == null || targets.Count == 0)
            {
                result.Error = "Select a P-state and at least one processor target.";
                return result;
            }
            uint address = PStateMath.BaseMsr + (uint)stateId;
            foreach (var target in targets)
            {
                result.Target = target;
                result.Original = result.Requested = result.Observed = null;
                try
                {
                    using (affinity.Pin(target))
                    {
                        result.Original = read(address);
                        if (!result.Original.HasValue)
                        {
                            result.Status = PStateWriteStatus.ReadFailed;
                            result.Error = "Could not read the P-state register.";
                            return result;
                        }
                        if (!PStateMath.Decode(result.Original.Value, layout).Enabled)
                        {
                            result.Status = PStateWriteStatus.Disabled;
                            result.Error = "The P-state is disabled (PstateEn=0).";
                            return result;
                        }
                        PStateMath.TryWithFrequency(result.Original.Value, layout, fid, did, out ulong updated, out _);
                        result.Requested = updated;
                        if (prepare != null && !prepare())
                        {
                            result.Status = PStateWriteStatus.PrepareFailed;
                            result.Error = "Could not apply the TSC workaround.";
                            return result;
                        }
                        if (!write(address, updated))
                        {
                            result.Status = PStateWriteStatus.WriteFailed;
                            result.Error = "The driver rejected the P-state write.";
                            return result;
                        }
                        result.Observed = read(address);
                        if (!result.Observed.HasValue)
                        {
                            result.Status = PStateWriteStatus.ReadbackFailed;
                            result.Error = "The write completed, but the register could not be read back.";
                            return result;
                        }
                        ulong mask = PStateMath.FrequencyMask(layout);
                        if ((result.Observed.Value & mask) != (updated & mask))
                        {
                            result.Status = PStateWriteStatus.ReadbackMismatch;
                            result.Error = "The frequency read back does not match the requested value.";
                            return result;
                        }
                    }
                    result.Completed++;
                }
                catch (Win32Exception ex)
                {
                    result.Status = PStateWriteStatus.AffinityFailed;
                    result.Error = ex.Message;
                    return result;
                }
                catch (Exception ex)
                {
                    result.Status = PStateWriteStatus.OperationFailed;
                    result.Error = ex.Message;
                    return result;
                }
            }
            result.Status = PStateWriteStatus.Success;
            return result;
        }
    }
}
