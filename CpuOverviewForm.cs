using System;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ZenStatesDebugTool
{
    // Delegates make the entire view testable without constructing Cpu or opening a driver.
    public sealed class CpuOverviewForm : Form
    {
        private readonly Func<Task<CpuOverviewSnapshot>> refresh;
        private readonly Func<int, Task<TuningResult>> applyTemperature;
        private readonly DataGridView readings;
        private readonly TextBox commands;
        private readonly Label status;
        private readonly Button refreshButton, temperatureButton, copyButton;
        private readonly NumericUpDown temperature;
        private CpuOverviewSnapshot snapshot;
        private bool busy;
        public CpuOverviewForm(Func<Task<CpuOverviewSnapshot>> refresh, Func<int, Task<TuningResult>> applyTemperature)
        {
            this.refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
            this.applyTemperature = applyTemperature ?? throw new ArgumentNullException(nameof(applyTemperature));
            Text = "CPU overview and capabilities"; Size = new Size(1000, 650);
            UiLayout.Configure(this, new Size(620, 420));
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(8) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(new Label { AutoSize = true, Dock = DockStyle.Top, Text = "On-demand snapshot. VID is a request; rail telemetry is not per-core voltage. Command defined does not mean firmware verified.", MaximumSize = new Size(920, 0) }, 0, 0);
            var tabs = new TabControl { Dock = DockStyle.Fill };
            var valuesTab = new TabPage("Readings"); var commandsTab = new TabPage("Command map");
            readings = new DataGridView { Name = "cpuReadings", Dock = DockStyle.Fill, ReadOnly = true, AutoGenerateColumns = false,
                AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells, SelectionMode = DataGridViewSelectionMode.FullRowSelect };
            foreach (string column in new[] { "Name", "Value", "Unit", "Source", "Evidence" })
                readings.Columns.Add(new DataGridViewTextBoxColumn { Name = column, DataPropertyName = column, HeaderText = column,
                    AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = column == "Evidence" ? 220 : column == "Source" ? 160 : column == "Name" ? 130 : 55,
                    DefaultCellStyle = new DataGridViewCellStyle { WrapMode = DataGridViewTriState.True } });
            commands = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false };
            valuesTab.Controls.Add(readings); commandsTab.Controls.Add(commands); tabs.TabPages.Add(valuesTab); tabs.TabPages.Add(commandsTab);
            layout.Controls.Add(tabs, 0, 1);
            var thermal = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
            thermal.Controls.Add(new Label { Text = "Requested temperature ceiling (°C)", AutoSize = true, Margin = new Padding(3, 8, 3, 3) });
            temperature = new NumericUpDown { Name = "temperatureRequest", Minimum = 40, Maximum = 95, Value = 95, Width = 65, Enabled = false };
            temperatureButton = new Button { Name = "applyTemperature", Text = "Apply ceiling", AutoSize = true, Enabled = false };
            thermal.Controls.Add(temperature); thermal.Controls.Add(temperatureButton);
            thermal.Controls.Add(new Label { Text = "Software guard: 40–95 °C. Separate from profiles; runtime ceiling readback unavailable.", AutoSize = true, Margin = new Padding(3, 8, 3, 3) });
            layout.Controls.Add(thermal, 0, 2);
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
            refreshButton = new Button { Name = "refreshOverview", Text = "Refresh snapshot", AutoSize = true };
            copyButton = new Button { Text = "Copy report", AutoSize = true, Enabled = false };
            status = new Label { AutoSize = true, Text = "No snapshot captured.", Margin = new Padding(8, 8, 3, 3), MaximumSize = new Size(780, 0) };
            bar.Controls.Add(refreshButton); bar.Controls.Add(copyButton); bar.Controls.Add(status); layout.Controls.Add(bar, 0, 3);
            Controls.Add(layout);
            void SizeColumns()
            {
                foreach (var item in new[] { Tuple.Create("Name", "Temperature"), Tuple.Create("Value", "Unavailable"), Tuple.Create("Unit", "firmware"), Tuple.Create("Source", "rail telemetry"), Tuple.Create("Evidence", "prerequisites not met") })
                    readings.Columns[item.Item1].MinimumWidth = TextRenderer.MeasureText(item.Item2, readings.Font).Width + 16;
            }
            readings.FontChanged += (s, e) => SizeColumns(); SizeColumns();
            void WrapText()
            {
                int width = Math.Max(180, ClientSize.Width - 40);
                foreach (var label in new[] { layout.GetControlFromPosition(0, 0) }.Concat(thermal.Controls.Cast<Control>()).Concat(bar.Controls.Cast<Control>()).OfType<Label>())
                    label.MaximumSize = new Size(width, 0);
            }
            SizeChanged += (s, e) => WrapText(); WrapText();
            refreshButton.Click += async (s, e) => await RefreshSnapshotAsync();
            temperatureButton.Click += async (s, e) => await ApplyTemperatureAsync();
            copyButton.Click += (s, e) => { try { if (snapshot != null) Clipboard.SetText(snapshot.Report); } catch (Exception ex) { status.Text = ex.Message; } };
            Shown += async (s, e) => await RefreshSnapshotAsync();
        }
        private void SetBusy(bool value)
        {
            busy = value; refreshButton.Enabled = !value; copyButton.Enabled = !value && snapshot != null;
            temperature.Enabled = temperatureButton.Enabled = !value && snapshot?.CanSetTemperature == true;
        }
        public async Task RefreshSnapshotAsync()
        {
            if (busy || IsDisposed) return;
            SetBusy(true); status.Text = "Reading snapshot…";
            try {
                var next = await refresh(); if (IsDisposed || Disposing) return;
                snapshot = next; readings.DataSource = next.Readings.ToList(); commands.Text = next.Report;
                status.Text = "Captured " + next.FinishedUtc.ToLocalTime().ToString("HH:mm:ss") + "; values are snapshots, not continuous monitoring.";
            } catch (Exception ex) { if (!IsDisposed && !Disposing) { snapshot = null; readings.DataSource = null; commands.Text = ""; status.Text = ex.Message; } }
            finally { if (!IsDisposed && !Disposing) SetBusy(false); }
        }
        private async Task ApplyTemperatureAsync()
        {
            if (busy || snapshot?.CanSetTemperature != true || IsDisposed) return;
            int request = (int)temperature.Value;
            SetBusy(true); status.Text = "Applying temperature ceiling…";
            try {
                var result = await applyTemperature(request);
                if (!IsDisposed && !Disposing) status.Text = TuningResultFormatter.FriendlySummary(result) + "; displayed readings remain the previous snapshot. Refresh to read again.";
            } catch (Exception ex) { if (!IsDisposed && !Disposing) status.Text = ex.Message; }
            finally { if (!IsDisposed && !Disposing) SetBusy(false); }
        }
    }
}
