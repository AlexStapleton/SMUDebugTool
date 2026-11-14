using System;
using System.Windows.Forms;

namespace ZenStatesDebugTool
{
    public sealed class ApplyResultsForm : Form
    {
        public ApplyResultsForm(TuningResult result)
        {
            Text = $"{UiText.T("Apply results")} — {result.Label}";
            UiLayout.Configure(this, new System.Drawing.Size(650, 300));
            Width = 1100;
            Height = 500;
            MinimumSize = new System.Drawing.Size(650, 300);
            var grid = TuningResultsPanel.CreateGrid(true);
            grid.DataSource = TuningResultFormatter.DisplayRows(result, false);
            var summary = new Label { Text = TuningResultFormatter.FriendlySummary(result), Dock = DockStyle.Top, Height = 36, AutoEllipsis = true, TextAlign = System.Drawing.ContentAlignment.MiddleLeft };
            var diagnostic = new TextBox { Text = TuningResultFormatter.Report(result), Dock = DockStyle.Bottom, Height = 160, Multiline = true, ReadOnly = true, WordWrap = false, ScrollBars = ScrollBars.Both, Visible = false };
            var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
            var technical = new CheckBox { Text = "Technical details", AutoSize = true, Margin = new Padding(6, 8, 12, 0) };
            technical.CheckedChanged += (s, e) => diagnostic.Visible = technical.Checked;
            var copy = new Button { Text = "Copy report", AutoSize = true };
            copy.Click += (s, e) => { try { Clipboard.SetText(TuningResultFormatter.Report(result)); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Copy report"); } };
            actions.Controls.Add(technical); actions.Controls.Add(copy);
            Controls.Add(grid);
            Controls.Add(diagnostic);
            Controls.Add(summary);
            Controls.Add(actions);
            UiText.Apply(this);
        }
        internal static void ShowDiagnosticLog(Form owner, string text)
        {
            var form = new Form { Text = UiText.T("Activity log"), Width = 900, Height = 500 };
            UiLayout.Configure(form, new System.Drawing.Size(400, 200));
            form.Controls.Add(new TextBox { Text = text, Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = false, ScrollBars = ScrollBars.Both });
            form.Show(owner);
        }
    }
}
