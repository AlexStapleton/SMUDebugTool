using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Forms;
using ZenStates.Core;

namespace ZenStatesDebugTool
{
    public partial class PCIRangeMonitor : Form
    {
        private readonly Cpu CPU;
        private readonly Timer RefreshTimer = new Timer();
        private readonly BindingList<AddressMonitorItem> list = new BindingList<AddressMonitorItem>();
        private readonly uint[] addresses;
        private uint[] previous;
        private readonly HashSet<int> changedRows = new HashSet<int>();
        private bool refreshing;
        private volatile bool closing;
        private class AddressMonitorItem
        {
            public string Address { get; set; }
            public string Value { get; set; }
            public string ValueFloat { get; set; }
            public string ValueBin { get; set; }
        }
        public PCIRangeMonitor(Cpu cpu, uint startAddress, uint endAddress)
        {
            addresses = AddressRange.MonitorAddresses(startAddress, endAddress);
            CPU = cpu;
            InitializeComponent();
            dataGridViewPCIRange.DataSource = list;
            RefreshTimer.Interval = 500;
            RefreshTimer.Tick += RefreshTimer_Tick;
        }
        private static AddressMonitorItem MakeItem(uint address, uint value) => new AddressMonitorItem
        {
            Address = $"0x{address:X8}",
            Value = $"0x{value:X8}",
            ValueFloat = BitConverter.ToSingle(BitConverter.GetBytes(value), 0).ToString("F4"),
            ValueBin = Convert.ToString(value, 2).PadLeft(32, '0')
        };

        private async void RefreshTimer_Tick(object sender, EventArgs e)
        {
            if (refreshing || closing || Hardware.IsClosing) return;
            refreshing = true;
            try
            {
                var current = await Task.Run(() =>
                {
                    uint[] values;
                    return Hardware.TryRead(() =>
                    {
                        var snapshot = new uint[addresses.Length];
                        for (int i = 0; i < addresses.Length; i++)
                        {
                            if (closing || Hardware.IsClosing) return null;
                            CPU.ReadDwordEx(addresses[i], ref snapshot[i]);
                        }
                        return snapshot;
                    }, out values) ? values : null;
                });
                if (closing || IsDisposed || current == null) return;
                list.RaiseListChangedEvents = false;
                if (previous == null)
                    for (int i = 0; i < current.Length; i++) list.Add(MakeItem(addresses[i], current[i]));
                else
                    for (int i = 0; i < current.Length; i++)
                        if (current[i] != previous[i])
                        {
                            list[i] = MakeItem(addresses[i], current[i]);
                            changedRows.Add(i);
                        }
                previous = current;
                list.RaiseListChangedEvents = true;
                list.ResetBindings();
                foreach (int index in changedRows)
                    dataGridViewPCIRange.Rows[index].DefaultCellStyle.BackColor = System.Drawing.Color.LightGoldenrodYellow;
            }
            catch (Exception ex) { if (!closing) Text = "PCI Range Monitor — " + ex.Message; }
            finally { refreshing = false; }
        }
        private void PCIRangeMonitor_FormClosing(object sender, FormClosingEventArgs e)
        {
            closing = true;
            RefreshTimer.Stop();
            RefreshTimer.Dispose();
        }
        private void PCIRangeMonitor_Shown(object sender, EventArgs e)
        {
            RefreshTimer.Start();
            RefreshTimer_Tick(sender, e);
        }
    }
}
