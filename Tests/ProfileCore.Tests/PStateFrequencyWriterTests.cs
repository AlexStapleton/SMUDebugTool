using System;
using System.Collections.Generic;
using System.ComponentModel;
using ZenStatesDebugTool;
using Xunit;

namespace ProfileCore.Tests
{
    public class PStateFrequencyWriterTests
    {
        private sealed class Affinity : IProcessorAffinity
        {
            public ProcessorTarget Current;
            public int Pins, Restores;
            public int FailPin = -1;
            public bool FailRestore;
            public readonly List<string> Selected = new List<string>();
            public IDisposable Pin(ProcessorTarget target)
            {
                if (Pins++ == FailPin) throw new Win32Exception(5);
                var previous = Current;
                Current = target;
                Selected.Add(target.ToString());
                return new Scope(() =>
                {
                    Restores++;
                    Current = previous;
                    if (FailRestore) throw new Win32Exception(5);
                });
            }
        }

        private sealed class Scope : IDisposable
        {
            private readonly Action restore;
            public Scope(Action restore) { this.restore = restore; }
            public void Dispose() => restore();
        }

        private sealed class Rig
        {
            public readonly Affinity Affinity = new Affinity();
            public readonly List<ProcessorTarget> Targets = new List<ProcessorTarget>
            {
                new ProcessorTarget(0, 3), new ProcessorTarget(2, 63)
            };
            public readonly Dictionary<int, ulong> Values = new Dictionary<int, ulong>
            {
                [3] = 0x80000001000A3384UL, [63] = 0xFEDCBA9876543384UL
            };
            public readonly List<ulong> Written = new List<ulong>();
            public readonly List<uint> Addresses = new List<uint>();
            public bool FailRead, FailWrite, FailPrepare, FailReadback, Mismatch, ThrowRead;
            public int Reads, Prepares;
            public ulong? Read(uint address)
            {
                Addresses.Add(address);
                Assert.NotNull(Affinity.Current);
                Reads++;
                if (ThrowRead) throw new InvalidOperationException("read exception");
                if (FailRead || (FailReadback && Written.Count > 0)) return null;
                return Mismatch && Written.Count > 0 ? Values[Affinity.Current.Processor] ^ 1UL
                    : Values[Affinity.Current.Processor];
            }
            public bool Write(uint address, ulong value)
            {
                Addresses.Add(address);
                Assert.NotNull(Affinity.Current);
                if (FailWrite) return false;
                Values[Affinity.Current.Processor] = value;
                Written.Add(value);
                return true;
            }
            public PStateWriteResult Apply(int id = 7, uint fid = 1000)
                => PStateFrequencyWriter.Apply(Targets, Affinity, id, PStateLayout.Zen5, fid, 0,
                    Read, Write, () => { Prepares++; return !FailPrepare; });
        }

        [Fact]
        public void Writes_each_processors_own_register_and_verifies_the_actual_state_id()
        {
            var rig = new Rig();
            ulong first = rig.Values[3], second = rig.Values[63];
            var result = rig.Apply();
            Assert.True(result.Success);
            Assert.Equal(2, result.Completed);
            Assert.Equal(first & ~0xFFFUL, rig.Written[0] & ~0xFFFUL);
            Assert.Equal(second & ~0xFFFUL, rig.Written[1] & ~0xFFFUL);
            Assert.All(rig.Written, raw => Assert.Equal(1000UL, raw & 0xFFF));
            Assert.All(rig.Addresses, address => Assert.Equal(0xC001006Bu, address));
            Assert.Equal(new[] { "group 0, processor 3", "group 2, processor 63" }, rig.Affinity.Selected);
            Assert.Equal(2, rig.Affinity.Restores);
            Assert.Null(rig.Affinity.Current);
        }

        [Theory]
        [InlineData(-1, 1000u)]
        [InlineData(8, 1000u)]
        [InlineData(0, 4096u)]
        [InlineData(0, 15u)]
        public void Invalid_request_never_pins_reads_or_writes(int id, uint fid)
        {
            var rig = new Rig();
            Assert.Equal(PStateWriteStatus.InvalidInput, rig.Apply(id, fid).Status);
            Assert.Equal(0, rig.Affinity.Pins);
            Assert.Equal(0, rig.Reads);
            Assert.Equal(0, rig.Prepares);
            Assert.Empty(rig.Written);
        }

        [Theory]
        [InlineData(PStateWriteStatus.ReadFailed)]
        [InlineData(PStateWriteStatus.Disabled)]
        [InlineData(PStateWriteStatus.PrepareFailed)]
        [InlineData(PStateWriteStatus.WriteFailed)]
        [InlineData(PStateWriteStatus.ReadbackFailed)]
        [InlineData(PStateWriteStatus.ReadbackMismatch)]
        [InlineData(PStateWriteStatus.OperationFailed)]
        public void Failures_restore_affinity_and_never_report_success(PStateWriteStatus expected)
        {
            var rig = new Rig
            {
                FailRead = expected == PStateWriteStatus.ReadFailed,
                FailPrepare = expected == PStateWriteStatus.PrepareFailed,
                FailWrite = expected == PStateWriteStatus.WriteFailed,
                FailReadback = expected == PStateWriteStatus.ReadbackFailed,
                Mismatch = expected == PStateWriteStatus.ReadbackMismatch,
                ThrowRead = expected == PStateWriteStatus.OperationFailed
            };
            if (expected == PStateWriteStatus.Disabled) rig.Values[3] &= ~(1UL << 63);
            var result = rig.Apply();
            Assert.Equal(expected, result.Status);
            Assert.False(result.Success);
            Assert.Equal(0, result.Completed);
            Assert.Equal(1, rig.Affinity.Restores);
            Assert.Null(rig.Affinity.Current);
            if (expected != PStateWriteStatus.ReadbackFailed && expected != PStateWriteStatus.ReadbackMismatch)
                Assert.Empty(rig.Written);
        }

        [Fact]
        public void Failed_pin_does_not_touch_hardware()
        {
            var rig = new Rig();
            rig.Affinity.FailPin = 0;
            Assert.Equal(PStateWriteStatus.AffinityFailed, rig.Apply().Status);
            Assert.Equal(0, rig.Reads);
            Assert.Equal(0, rig.Prepares);
            Assert.Empty(rig.Written);
        }

        [Fact]
        public void Later_target_failure_reports_partial_completion_without_rollback()
        {
            var rig = new Rig();
            rig.Affinity.FailPin = 1;
            var result = rig.Apply();
            Assert.Equal(PStateWriteStatus.AffinityFailed, result.Status);
            Assert.Equal(1, result.Completed);
            Assert.Single(rig.Written);
            Assert.Equal(63, result.Target.Processor);
            Assert.Null(rig.Affinity.Current);
        }

        [Fact]
        public void Failed_restore_is_visible_even_after_verified_readback()
        {
            var rig = new Rig();
            rig.Affinity.FailRestore = true;
            var result = rig.Apply();
            Assert.Equal(PStateWriteStatus.AffinityFailed, result.Status);
            Assert.False(result.Success);
            Assert.Single(rig.Written);
            Assert.NotNull(result.Observed);
        }

        [Fact]
        public void No_processor_targets_means_no_hardware_calls()
        {
            var rig = new Rig();
            rig.Targets.Clear();
            Assert.Equal(PStateWriteStatus.InvalidInput, rig.Apply().Status);
            Assert.Equal(0, rig.Reads);
            Assert.Empty(rig.Written);
        }
    }
}
