using System;
using System.Windows.Forms;

namespace ZenStatesDebugTool
{
    public sealed class TuningResultsPanel : Panel
    {
        private readonly Label summary;
        public TuningResult Result { get; private set; }
        public TuningResultsPanel(TextBox log)
        {
            Dock = DockStyle.Fill;
            var bar = new Panel { Dock = DockStyle.Top, Height = 46 };
            var details = new Button { Text = "Details", Dock = DockStyle.Right, Width = 65 };
            summary = new Label { Text = "No apply results yet.", Dock = DockStyle.Fill, AutoEllipsis = true };
            details.Click += (sender, args) => { if (Result != null) new ApplyResultsForm(Result).Show(FindForm()); };
            bar.Controls.Add(summary);
            bar.Controls.Add(details);
            log.Dock = DockStyle.Fill;
            Controls.Add(log);
            Controls.Add(bar);
        }
        public void ShowResult(TuningResult result)
        {
            Result = result;
            summary.Text = result.Summary;
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
}
