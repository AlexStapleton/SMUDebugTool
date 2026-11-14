using System;
using System.Linq;
using System.Windows.Forms;
using ZenStatesDebugTool.Profiles;

namespace ZenStatesDebugTool
{
    public sealed class TuningPreviewForm : Form
    {
        public Profile ApprovedProfile { get; private set; }
        public TuningPreviewForm(Profile requested, TuningEditSession session, string scope)
        {
            var snapshot = TuningSnapshots.Profile(requested);
            Text = UiText.T("Apply preview") + " — " + UiText.T(scope);
            UiLayout.Configure(this, new System.Drawing.Size(720, 330));
            var requests = TuningService.RequestsFromProfile(snapshot);
            Width = 1150; Height = Math.Min(600, Math.Max(330, 210 + requests.Count * 26));
            string validation = TuningService.ValidateProfileInput(snapshot);
            var description = new Label { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(6), MaximumSize = new System.Drawing.Size(ClientSize.Width, 0),
                Text = UiText.T("Review the requested settings. CPU reports are last known observations, not live measurements. Cancel sends no commands.") +
                    (validation == null ? "" : Environment.NewLine + validation) };
            var grid = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false,
                AllowUserToDeleteRows = false, AutoGenerateColumns = false, RowHeadersVisible = false,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells, BackgroundColor = System.Drawing.SystemColors.Control };
            grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True;
            foreach (var name in new[] { "Setting", "Previous", "Source", "Saved", "CpuReported", "Requested", "Action" })
                grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, DataPropertyName = name, HeaderText = UiText.T(name),
                    AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = 75, FillWeight = name == "CpuReported" ? 170 : 100 });
            grid.DataSource = requests.Select(r => new {
                Setting = TuningResultFormatter.SettingName(r), Previous = TuningEditSession.ValueText(session.Baseline(r)),
                Source = UiText.T(session.BaselineSource(r)), Saved = session.SavedText(r), CpuReported = session.ReportedText(r),
                Requested = TuningEditSession.ValueText(r), Action = UiText.T(r.Kind >= TuningKind.Ppt && r.Value == 0 ? "Leave unchanged (0)" : "Send request")
            }).ToList();
            var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            var cancel = new Button { Name = "buttonCancelPreview", Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            var apply = new Button { Name = "buttonApplyPreview", Text = "Apply these settings", AutoSize = true, Enabled = validation == null && requests.Count > 0 };
            apply.Click += (s, e) => { ApprovedProfile = TuningSnapshots.Profile(snapshot); DialogResult = DialogResult.OK; Close(); };
            actions.Controls.Add(cancel); actions.Controls.Add(apply);
            CancelButton = cancel;
            Controls.Add(grid); Controls.Add(description); Controls.Add(actions);
            ClientSizeChanged += (s, e) => description.MaximumSize = new System.Drawing.Size(ClientSize.Width, 0);
            Shown += (s, e) => { ActiveControl = cancel; cancel.Focus(); };
            UiText.Apply(this);
        }
    }
}
