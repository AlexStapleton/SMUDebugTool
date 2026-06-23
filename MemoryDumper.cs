using System;
using System.IO;
using ZenStates.Core;


namespace ZenStatesDebugTool
{
    public static class MemoryDumper
    {
        // Synchronous and buffered: DWORDs are packed into a 64 KB buffer and flushed in
        // bulk instead of one 4-byte FileStream.Write per DWORD. The caller is responsible
        // for running this off the UI thread (see SettingsForm.ButtonDump_Click).
        //
        // The reads go through the caller-supplied Cpu so the dump uses the app's single,
        // already-initialised hardware-access instance — not a second driver handle.
        public static void Dump32BitAddressSpaceAsBytes(Cpu cpu, string outputPath, uint startAddress, uint endAddress)
        {
            if (cpu == null) throw new ArgumentNullException(nameof(cpu));

            const uint Step = 4; // Read DWORDs
            const int BufferSize = 1 << 16; // 64 KB, a multiple of 4

            using (FileStream fs = new FileStream(
                outputPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: BufferSize))
            {
                byte[] buffer = new byte[BufferSize];
                int pos = 0;

                for (uint addr = startAddress; ; addr += Step)
                {
                    uint data = 0;
                    bool ok;

                    try
                    {
                        // Per-DWORD lock (rather than one lock around the whole dump) so
                        // open monitor dialogs can still interleave their reads instead of
                        // freezing for the entire dump; the driver call dwarfs the
                        // uncontended lock cost.
                        lock (Hardware.Sync)
                            ok = cpu.io.GetPhysLong((UIntPtr)addr, out data);
                    }
                    catch
                    {
                        ok = false;
                    }

                    // Mark unreadable regions clearly (0xFFFFFFFF), same as before.
                    if (!ok)
                        data = 0xFFFFFFFF;

                    // Little-endian split into the buffer.
                    buffer[pos++] = (byte)(data & 0xFF);
                    buffer[pos++] = (byte)((data >> 8) & 0xFF);
                    buffer[pos++] = (byte)((data >> 16) & 0xFF);
                    buffer[pos++] = (byte)((data >> 24) & 0xFF);

                    if (pos == buffer.Length)
                    {
                        fs.Write(buffer, 0, pos);
                        pos = 0;
                    }

                    // Break before incrementing past the end to avoid uint overflow when
                    // endAddress is near 0xFFFFFFFF.
                    if (addr > endAddress - Step)
                        break;
                }

                if (pos > 0)
                    fs.Write(buffer, 0, pos);
            }
        }
    }
}