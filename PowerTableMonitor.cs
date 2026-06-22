using System;
using System.ComponentModel;
using System.Windows.Forms;
using System.Globalization;
using ZenStates.Core;

namespace ZenStatesDebugTool
{
    public partial class PowerTableMonitor : Form
    {
        private readonly Cpu CPU;
        readonly Timer PowerCfgTimer = new Timer();
        private readonly BindingList<PowerMonitorItem> list = new BindingList<PowerMonitorItem>();
        // Running per-row maximum kept as float, so the refresh never has to parse the
        // formatted Max string back into a number. Index-aligned with `list`.
        private float[] maxes;
        private class PowerMonitorItem
        {
            public string Index { get; set; }
            public string Offset { get; set; }
            public string Value { get; set; }
            public string Max { get; set; }
        }

        private void FillInData(float[] table)
        {
            list.Clear();
            maxes = new float[table.Length];

            for (var i = 0; i < table.Length; i++)
            {
                var valueStr = table[i].ToString("F6", CultureInfo.InvariantCulture);
                maxes[i] = table[i];
                list.Add(new PowerMonitorItem
                {
                    Index = $"{i:D4}",
                    Offset = $"0x{(i * 4):X4}",
                    Value = valueStr,
                    Max = valueStr
                });
            }
        }

        private void RefreshData(float[] table)
        {
            for (int index = 0; index < list.Count; index++)
            {
                var current = table[index];
                var item = list[index];

                // Current value is (re)formatted each tick; one float format is cheap.
                item.Value = current.ToString("F6", CultureInfo.InvariantCulture);

                // Compare against the float max and only reformat the Max string when it grows.
                if (current > maxes[index])
                {
                    maxes[index] = current;
                    item.Max = item.Value;
                }
            }

            dataGridView1.Refresh();
        }

        private void PowerCfgTimer_Tick(object sender, EventArgs e)
        {
            if (CPU.RefreshPowerTable() == SMU.Status.OK)
                RefreshData(CPU.powerTable.Table);
        }

        public PowerTableMonitor(Cpu cpu)
        {
            CPU = cpu;
            cpu.RefreshPowerTable();

            PowerCfgTimer.Interval = 2000;
            PowerCfgTimer.Tick += new EventHandler(PowerCfgTimer_Tick);

            InitializeComponent();

            dataGridView1.DataSource = list;

            FillInData(cpu.powerTable.Table);
        }

        private void PowerTableMonitor_FormClosing(object sender, FormClosingEventArgs e)
        {
            PowerCfgTimer.Stop();
            //CPU.powerTable.Dispose();
        }

        private void PowerTableMonitor_Shown(object sender, EventArgs e)
        {
            PowerCfgTimer.Start();
        }

        private void ButtonApply_Click(object sender, EventArgs e)
        {
            PowerCfgTimer.Interval = Convert.ToInt32(numericUpDownInterval.Value);
        }
    }
}
