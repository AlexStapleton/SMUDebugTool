using System;

namespace ZenStatesDebugTool
{
    public static class AddressRange
    {
        // Exclusive end, as used by the PCI monitor. Bounded to keep grid creation finite.
        public static uint[] MonitorAddresses(uint start, uint end)
        {
            if (end <= start) throw new ArgumentException("End address must be greater than start address.");
            ulong count = ((ulong)end - start + 3) / 4;
            if (count > 16384) throw new ArgumentException("Monitor at most 16384 registers (64 KiB) at a time.");
            var addresses = new uint[(int)count];
            for (int i = 0; i < addresses.Length; i++) addresses[i] = (uint)((ulong)start + (ulong)i * 4);
            return addresses;
        }
    }
}
