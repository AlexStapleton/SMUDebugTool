using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows.Forms;
using System.Globalization;
using System.Threading.Tasks;
using ZenStates.Core;
using ZenStates.Core.Hardware;
using ZenStates.Core.Hardware.Smu;

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
        private IReadOnlyDictionary<uint, SensorInfo> structure;
        private class PowerMonitorItem
        {
            public string Index { get; set; }
            public string Offset { get; set; }
            public string Value { get; set; }
            public string Max { get; set; }
            public string Name { get; set; }
            public string Scaled { get; set; }
        }

        private void FillInData(float[] table)
        {
            list.RaiseListChangedEvents = false;
            list.Clear();
            maxes = new float[table.Length];

            for (var i = 0; i < table.Length; i++)
            {
                var valueStr = table[i].ToString("F6", CultureInfo.InvariantCulture);
                maxes[i] = table[i];
                // The structure is keyed by float-array element index, not byte offset.
                string name = "";
                string scaled = "";
                if (structure != null && structure.TryGetValue((uint)i, out SensorInfo info))
                {
                    name = info.Name ?? "";
                    scaled = PmTableLabeling.FormatScaled(table[i], info);
                }

                list.Add(new PowerMonitorItem
                {
                    Index = $"{i:D4}",
                    Offset = $"0x{(i * 4):X4}",
                    Value = valueStr,
                    Max = valueStr,
                    Name = name,
                    Scaled = scaled,
                });
            }
            list.RaiseListChangedEvents = true;
            list.ResetBindings();
        }

        private void RefreshData(float[] table)
        {
            for (int index = 0; index < list.Count; index++)
            {
                var current = table[index];
                var item = list[index];

                // Current value is (re)formatted each tick; one float format is cheap.
                item.Value = current.ToString("F6", CultureInfo.InvariantCulture);

                if (structure != null && structure.TryGetValue((uint)index, out SensorInfo info))
                    item.Scaled = PmTableLabeling.FormatScaled(current, info);

                // Compare against the float max and only reformat the Max string when it grows.
                if (current > maxes[index])
                {
                    maxes[index] = current;
                    item.Max = item.Value;
                }
            }

            dataGridView1.Refresh();
        }

        private bool refreshing;
        private bool closing;
        private async void PowerCfgTimer_Tick(object sender, EventArgs e)
        {
            if (refreshing || closing || Hardware.IsClosing) return;
            refreshing = true;
            try
            {
                float[] snapshot = await Task.Run(() =>
                {
                    float[] values;
                    return Hardware.TryRead(() =>
                    {
                        if (CPU.RefreshPowerTable() != SMU.Status.OK) return null;
                        structure = SmuDecodeAdapter.GetPmTableStructure(CPU);
                        return (float[])CPU.powerTable.Table.Clone();
                    }, out values) ? values : null;
                });
                if (closing || IsDisposed || snapshot == null) return;
                if (list.Count != snapshot.Length) FillInData(snapshot);
                else RefreshData(snapshot);
                Text = $"PowerTableMonitor — PM table 0x{CPU.RyzenSmu.PmTableVersion:X} ({structure?.Count ?? 0} labels)";
            }
            catch (Exception ex) { if (!closing) Text = "PowerTableMonitor — " + ex.Message; }
            finally { refreshing = false; }
        }

        public PowerTableMonitor(Cpu cpu)
        {
            CPU = cpu;
            InitializeComponent();
            UiLayout.Configure(this, new System.Drawing.Size(500, 300));
            dataGridView1.DataSource = list;
            PowerCfgTimer.Interval = 2000;
            PowerCfgTimer.Tick += PowerCfgTimer_Tick;
        }

        private void PowerTableMonitor_FormClosing(object sender, FormClosingEventArgs e)
        {
            closing = true;
            PowerCfgTimer.Stop();
            PowerCfgTimer.Dispose();
            //CPU.powerTable.Dispose();
        }

        private void PowerTableMonitor_Shown(object sender, EventArgs e)
        {
            PowerCfgTimer.Start();
            PowerCfgTimer_Tick(sender, e);
        }

        private void ButtonApply_Click(object sender, EventArgs e)
        {
            PowerCfgTimer.Interval = Convert.ToInt32(numericUpDownInterval.Value);
        }
    }
}
