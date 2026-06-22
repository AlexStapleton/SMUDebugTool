using System;
using System.ComponentModel;
using System.Threading;
using System.Windows.Forms;
using ZenStates.Core;

namespace ZenStatesDebugTool
{
    public partial class PCIRangeMonitor : Form
    {
        private readonly Cpu CPU;
        readonly System.Windows.Forms.Timer RefreshTimer = new System.Windows.Forms.Timer();
        private readonly BindingList<AddressMonitorItem> list = new BindingList<AddressMonitorItem>();
        private readonly uint StartAddress;
        private readonly uint EndAddress;

        // Address i lives at list[i] and prevValues[i] (positional indexing), so a refresh
        // is a single O(n) pass with no per-item linear search.
        private readonly uint[] addresses;
        private uint[] prevValues;

        // Guards against a slow read overlapping the next timer tick.
        private int refreshing;

        private class AddressMonitorItem
        {
            public string Address { get; set; }
            public string Value { get; set; }
            public string ValueFloat { get; set; }
            public string ValueBin { get; set; }
        }

        public PCIRangeMonitor(Cpu cpu, uint startAddress, uint endAddress)
        {
            CPU = cpu;
            StartAddress = startAddress;
            EndAddress = endAddress;

            // Precompute the address list once; the grid stays index-aligned with it.
            int count = 0;
            for (var a = StartAddress; a < EndAddress; a += 4) count++;
            addresses = new uint[count];
            for (int i = 0; i < count; i++) addresses[i] = StartAddress + (uint)i * 4;
            prevValues = new uint[count];

            RefreshTimer.Interval = 500;
            RefreshTimer.Tick += new EventHandler(RefreshTimer_Tick);

            InitializeComponent();

            // Initial fill (one-time, synchronous).
            for (int i = 0; i < count; i++)
            {
                uint value = 0;
                CPU.ReadDwordEx(addresses[i], ref value);
                prevValues[i] = value;
                list.Add(MakeItem(addresses[i], value));
            }
            dataGridViewPCIRange.DataSource = list;
        }

        private static AddressMonitorItem MakeItem(uint addr, uint value)
        {
            return new AddressMonitorItem
            {
                Address = $"0x{addr:X8}",
                Value = $"0x{value:X8}",
                ValueFloat = $"{Convert.ToSingle(value):F4}",
                ValueBin = Convert.ToString(value, 2).PadLeft(32, '0')
            };
        }

        private void RefreshData()
        {
            // Skip this tick if the previous read is still running.
            if (Interlocked.CompareExchange(ref refreshing, 1, 0) != 0)
                return;

            Thread refreshThread = new Thread(() =>
            {
                try
                {
                    var current = new uint[addresses.Length];
                    for (int i = 0; i < addresses.Length; i++)
                    {
                        uint value = 0;
                        CPU.ReadDwordEx(addresses[i], ref value);
                        current[i] = value;
                    }

                    // All UI mutation happens here, on the form's thread, in one marshal.
                    if (!IsHandleCreated) return;
                    BeginInvoke((MethodInvoker)delegate
                    {
                        for (int i = 0; i < current.Length; i++)
                        {
                            if (current[i] == prevValues[i]) continue;

                            var item = list[i];
                            item.Value = $"0x{current[i]:X8}";
                            item.ValueFloat = $"{Convert.ToSingle(current[i]):F4}";
                            item.ValueBin = Convert.ToString(current[i], 2).PadLeft(32, '0');
                            dataGridViewPCIRange.Rows[i].DefaultCellStyle.BackColor =
                                System.Drawing.Color.LightGoldenrodYellow;
                        }
                        prevValues = current;
                        dataGridViewPCIRange.Refresh();
                    });
                }
                finally
                {
                    Interlocked.Exchange(ref refreshing, 0);
                }
            })
            {
                IsBackground = true
            };
            refreshThread.Start();
        }

        private void RefreshTimer_Tick(object sender, EventArgs e)
        {
            RefreshData();
        }

        private void PCIRangeMonitor_FormClosing(object sender, FormClosingEventArgs e)
        {
            RefreshTimer.Stop();
        }

        private void PCIRangeMonitor_Shown(object sender, EventArgs e)
        {
            RefreshTimer.Start();
        }
    }
}
