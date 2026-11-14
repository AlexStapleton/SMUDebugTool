using System;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;
using ZenStatesDebugTool.Profiles;

namespace ZenStatesDebugTool
{
    public sealed class TuningResultsPanel : Panel
    {
        private readonly Label summary;
        private readonly DataGridView overview;
        private readonly Button details;
        private readonly ComboBox recent;
        private readonly TuningActionHistory history = new TuningActionHistory();
        public int HistoryCount => history.Entries.Count;
        public TuningResult Result { get; private set; }
        public TuningResultsPanel(TextBox log)
        {
            Dock = DockStyle.Fill;
            var bar = new Panel { Dock = DockStyle.Top, Height = Math.Max(26, Font.Height + 10) };
            FontChanged += (s, e) => bar.Height = Math.Max(26, Font.Height + 10);
            details = new Button { Text = "Details", Dock = DockStyle.Right, Width = 65, AutoSize = true, Enabled = false };
            var activity = new Button { Text = "Activity log", Dock = DockStyle.Right, AutoSize = true };
            summary = new Label
            {
                Text = "No apply results yet.",
                Dock = DockStyle.Fill,
                AutoEllipsis = true,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft
            };
            details.Click += (sender, args) => { if (Result != null) new ApplyResultsForm(Result).Show(FindForm()); };
            activity.Click += (sender, args) => ApplyResultsForm.ShowDiagnosticLog(FindForm(), log.Text);
            bar.Controls.Add(summary);
            bar.Controls.Add(activity);
            bar.Controls.Add(details);
            overview = CreateGrid(false);
            recent = new ComboBox { Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList,
                AccessibleName = "Recent actions (this session)", DropDownWidth = 900, Visible = false };
            recent.SelectedIndexChanged += (s, e) => { if (recent.SelectedItem is TuningHistoryEntry entry) DisplayResult(entry.Result); };
            log.Visible = false; // Keep the bounded diagnostic history without displaying it in the normal workflow.
            Controls.Add(log);
            Controls.Add(overview);
            Controls.Add(recent);
            Controls.Add(bar);
        }
        public void ShowResult(TuningResult result)
        {
            history.Add(result);
            PopulateHistory(0);
        }
        public void RefreshLanguage()
        {
            if (history.Entries.Count > 0) PopulateHistory(Math.Max(0, recent.SelectedIndex));
            else UiText.Apply(this);
        }
        private void PopulateHistory(int selected)
        {
            recent.BeginUpdate();
            try {
                recent.Items.Clear();
                foreach (var entry in history.Entries) recent.Items.Add(entry);
                recent.Visible = true; recent.SelectedIndex = selected;
            } finally { recent.EndUpdate(); }
        }
        private void DisplayResult(TuningResult result)
        {
            Result = result;
            summary.Text = TuningResultFormatter.FriendlySummary(result);
            overview.DataSource = TuningResultFormatter.DisplayRows(result, true);
            details.Enabled = true;
            UiText.Apply(this);
        }
        internal static DataGridView CreateGrid(bool includeObserved)
        {
            var grid = new DataGridView {
                Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false, AutoGenerateColumns = false, RowHeadersVisible = false,
                BackgroundColor = SystemColors.Control, BorderStyle = BorderStyle.None,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal
            };
            grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            foreach (string name in includeObserved ? new[] { "Setting", "Requested", "Result", "Observed" } : new[] { "Setting", "Requested", "Result" })
                grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, DataPropertyName = name, HeaderText = UiText.T(name),
                    AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, FillWeight = name == "Result" ? 150 : 100, MinimumWidth = 65 });
            return grid;
        }
        public static TuningResultsPanel Attach(TextBox log)
        {
            var parent = log.Parent;
            var table = parent as TableLayoutPanel;
            var position = table?.GetPositionFromControl(log);
            parent.Controls.Remove(log);
            var panel = new TuningResultsPanel(log);
            if (table != null) table.Controls.Add(panel, position.Value.Column, position.Value.Row);
            else parent.Controls.Add(panel);
            return panel;
        }
    }
    public sealed class PowerLimitEditor : FlowLayoutPanel
    {
        public PowerLimitEditor(TuningKind kind, NumericUpDown value, Func<Profile, Task> apply)
        {
            if (kind < TuningKind.Ppt || kind > TuningKind.Scalar) throw new ArgumentOutOfRangeException(nameof(kind));
            AutoSize = true; Dock = DockStyle.Fill; Margin = Padding.Empty; WrapContents = false;
            var button = new Button { Text = "Apply", AutoSize = true, Margin = new Padding(8, 1, 0, 1), AccessibleName = "Apply " + TuningResultFormatter.SettingName(new TuningRequest { Kind = kind }) };
            button.Click += async (sender, args) => {
                var profile = new Profile();
                int requested = (int)value.Value;
                switch (kind) {
                    case TuningKind.Ppt: profile.PptWatts = requested; break;
                    case TuningKind.Tdc: profile.TdcAmps = requested; break;
                    case TuningKind.Edc: profile.EdcAmps = requested; break;
                    case TuningKind.Scalar: profile.PboScalar = requested; break;
                }
                await apply(profile);
            };
            Controls.Add(value); Controls.Add(button);
        }
    }
}
