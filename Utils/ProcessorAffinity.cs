using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ZenStatesDebugTool
{
    // Processor groups are independent of NUMA node numbering. Pin to a single
    // active logical processor, and restore the caller's affinity on every exit.
    public sealed class ProcessorAffinity : IProcessorAffinity
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct GroupAffinity
        {
            public UIntPtr Mask;
            public ushort Group;
            public ushort Reserved0, Reserved1, Reserved2;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);
        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetThreadGroupAffinity(IntPtr thread, ref GroupAffinity affinity, out GroupAffinity previous);

        public static List<ProcessorTarget> FromGroupMasks(IEnumerable<KeyValuePair<ushort, ulong>> groups)
        {
            var targets = new List<ProcessorTarget>();
            foreach (var group in groups)
                for (int bit = 0; bit < 64; bit++)
                    if ((group.Value & (1UL << bit)) != 0)
                        targets.Add(new ProcessorTarget(group.Key, bit));
            return targets;
        }

        public static List<ProcessorTarget> GetActiveTargets()
        {
            if (IntPtr.Size != 8)
                throw new NotSupportedException("P-state editing requires the 64-bit application.");
            uint length = 0;
            GetLogicalProcessorInformationEx(4, IntPtr.Zero, ref length); // RelationGroup
            if (Marshal.GetLastWin32Error() != 122 || length == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            IntPtr buffer = Marshal.AllocHGlobal(checked((int)length));
            try
            {
                if (!GetLogicalProcessorInformationEx(4, buffer, ref length))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                // SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX: 8-byte header, then
                // GROUP_RELATIONSHIP: 24-byte header followed by 48-byte group info.
                int size = Marshal.ReadInt32(buffer, 4);
                int groups = (ushort)Marshal.ReadInt16(buffer, 10);
                if (Marshal.ReadInt32(buffer) != 4 || size > length || size < 32
                    || groups == 0 || groups > (size - 32) / 48)
                    throw new InvalidOperationException("Invalid processor-group topology returned by Windows.");
                var masks = new List<KeyValuePair<ushort, ulong>>();
                for (ushort group = 0; group < groups; group++)
                    masks.Add(new KeyValuePair<ushort, ulong>(group,
                        unchecked((ulong)Marshal.ReadInt64(buffer, 32 + group * 48 + 40))));
                return FromGroupMasks(masks);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        public IDisposable Pin(ProcessorTarget target)
        {
            if (IntPtr.Size != 8) throw new NotSupportedException("P-state editing requires a 64-bit process.");
            var next = new GroupAffinity { Group = target.Group, Mask = new UIntPtr(1UL << target.Processor) };
            if (!SetThreadGroupAffinity(GetCurrentThread(), ref next, out var previous))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return new Scope(previous);
        }

        private sealed class Scope : IDisposable
        {
            private GroupAffinity previous;
            private bool disposed;
            public Scope(GroupAffinity previous) { this.previous = previous; }
            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                if (!SetThreadGroupAffinity(GetCurrentThread(), ref previous, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
    }
}
