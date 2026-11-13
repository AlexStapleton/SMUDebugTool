using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ZenStatesDebugTool;
using Xunit;

namespace ProfileCore.Tests
{
    public class ProcessorAffinityTests
    {
        [Fact]
        public void Target_enumeration_keeps_group_ids_holes_and_processor_63()
        {
            var targets = ProcessorAffinity.FromGroupMasks(new[]
            {
                new KeyValuePair<ushort, ulong>(0, 0x9),
                new KeyValuePair<ushort, ulong>(2, 1UL << 63)
            });
            Assert.Equal(3, targets.Count);
            Assert.Equal(0, targets[0].Processor);
            Assert.Equal(3, targets[1].Processor);
            Assert.Equal((ushort)2, targets[2].Group);
            Assert.Equal(63, targets[2].Processor);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GroupAffinity
        {
            public UIntPtr Mask;
            public ushort Group, Reserved0, Reserved1, Reserved2;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessorNumber { public ushort Group; public byte Number, Reserved; }
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll")]
        private static extern bool GetThreadGroupAffinity(IntPtr thread, out GroupAffinity affinity);
        [DllImport("kernel32.dll")]
        private static extern void GetCurrentProcessorNumberEx(out ProcessorNumber number);

        [Fact]
        public void Windows_affinity_scope_selects_requested_cpu_and_restores_on_exception()
        {
            // This test only changes its worker thread's scheduling affinity. No CPU/driver/MSR I/O.
            Assert.True(Environment.Is64BitProcess);
            Assert.True(GetThreadGroupAffinity(GetCurrentThread(), out var before));
            var targets = ProcessorAffinity.GetActiveTargets();
            Assert.NotEmpty(targets);
            Assert.Throws<InvalidOperationException>((Action)(() =>
            {
                using (new ProcessorAffinity().Pin(targets[0]))
                {
                    GetCurrentProcessorNumberEx(out var actual);
                    Assert.Equal(targets[0].Group, actual.Group);
                    Assert.Equal(targets[0].Processor, actual.Number);
                    throw new InvalidOperationException();
                }
            }));
            Assert.True(GetThreadGroupAffinity(GetCurrentThread(), out var after));
            Assert.Equal(before.Group, after.Group);
            Assert.Equal(before.Mask, after.Mask);
        }
    }
}
