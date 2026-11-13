using System;
using System.Linq;
using System.Windows.Forms;

namespace ZenStatesDebugTool
{
    public sealed class ApplyResultsForm : Form
    {
        public ApplyResultsForm(TuningResult result)
        {
            Text = $"{result.Label} — {result.StartedUtc:u} — v{result.ApplicationVersion}: {result.Summary}";
            UiLayout.Configure(this, new System.Drawing.Size(650, 300));
            Width = 1100;
            Height = 500;
            MinimumSize = new System.Drawing.Size(650, 300);
            var grid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.DisplayedCells,
                DataSource = result.Rows.Select(r => new
                {
                    Setting = r.Request.Setting,
                    Requested = r.Request.Requested,
                    Unit = r.Request.Unit,
                    Outcome = TuningResult.OutcomeLabel(r.Outcome),
                    Observed = r.Read?.Value,
                    WriteRoute = r.Write?.Route,
                    WriteCommand = Hex(r.Write?.Command),
                    WriteStatus = Hex(r.Write?.Status),
                    ReadRoute = r.Read?.Route,
                    ReadCommand = Hex(r.Read?.Command),
                    ReadStatus = Hex(r.Read?.Status),
                    ReadbackSource = r.Read?.Source ?? "unavailable",
                    Target = Hex(r.Write?.TargetMask),
                    PhysicalTarget = r.Write?.TargetIdentity,
                    TimeUtc = r.TimestampUtc.ToString("O"),
                    r.Reason
                }).ToList()
            };
            grid.ColumnAdded += (s, e) => e.Column.HeaderText = UiText.T(e.Column.DataPropertyName);
            var copy = new Button { Text = "Copy report", Dock = DockStyle.Bottom, Height = 32 };
            copy.Click += (s, e) => { try { Clipboard.SetText(TuningResultFormatter.Report(result)); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Copy report"); } };
            Controls.Add(grid);
            Controls.Add(copy);
        }
        private static string Hex(uint? value) => value.HasValue ? $"0x{value:X2}" : "";
    }
}
