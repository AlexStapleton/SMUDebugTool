using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace ZenStatesDebugTool
{
    public sealed class TuningHistoryForm : Form
    {
        private sealed class HistoryRow
        {
            public string Time { get; set; }
            public string Action { get; set; }
            public string Result { get; set; }
            public TuningHistoryEntry Entry;
        }
        public TuningHistoryForm(IEnumerable<TuningHistoryEntry> entries, string diagnosticLog)
        {
            Text = "Recent actions"; Width = 950; Height = 500;
            UiLayout.Configure(this, new System.Drawing.Size(500, 300));
            var rows = entries.Select(entry => {
                var result = entry.Result;
                return new HistoryRow { Time = result.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), Action = result.Label,
                    Result = TuningResultFormatter.FriendlySummary(result), Entry = entry };
            }).ToList();
            var grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AutoGenerateColumns = false,
                AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                MultiSelect = false, BackgroundColor = System.Drawing.SystemColors.Control };
            grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            foreach (string name in new[] { "Time", "Action", "Result" })
                grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, DataPropertyName = name, HeaderText = UiText.T(name),
                    AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 100, FillWeight = name == "Result" ? 200 : 100 });
            grid.DataSource = rows;
            var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
            var details = new Button { Text = "Details", AutoSize = true, Enabled = rows.Count > 0 };
            void ShowDetails() { if (grid.CurrentRow?.DataBoundItem is HistoryRow row) new ApplyResultsForm(row.Entry.Result).Show(this); }
            details.Click += (s, e) => ShowDetails();
            grid.CellDoubleClick += (s, e) => { if (e.RowIndex >= 0) ShowDetails(); };
            var log = new Button { Text = "Activity log", AutoSize = true };
            log.Click += (s, e) => ApplyResultsForm.ShowDiagnosticLog(this, diagnosticLog);
            actions.Controls.Add(details); actions.Controls.Add(log);
            Controls.Add(grid); Controls.Add(actions); UiText.Apply(this);
        }
    }
}
