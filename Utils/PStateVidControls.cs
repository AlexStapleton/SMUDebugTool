using System.Windows.Forms;

namespace ZenStatesDebugTool
{
    public sealed class PStateVidControls
    {
        public TextBox Value { get; } = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, Name = "pstateVid" };
        public TextBox Voltage { get; } = new TextBox { Dock = DockStyle.Fill, ReadOnly = true, Name = "pstateVidVoltage" };
        public Button Write { get; } = new Button { Text = "Write VID", AutoSize = true, Enabled = false, Dock = DockStyle.Fill };
        public PStateVidControls(TableLayoutPanel table)
        {
            table.RowCount = 6;
            while (table.RowStyles.Count < 6) table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            table.Controls.Add(new Label { Text = "Requested VID", AutoSize = true }, 0, 4);
            table.Controls.Add(Value, 1, 4);
            table.Controls.Add(new Label { Text = "VID voltage (derived)", AutoSize = true }, 0, 5);
            table.Controls.Add(Voltage, 1, 5); table.SetColumnSpan(Voltage, 2);
            table.Controls.Add(Write, 3, 4);
        }
    }
    public static class PStateEditorLayout
    {
        public static void AttachBaseClock(ScrollableControl host, TableLayoutPanel table, NumericUpDown clock, Label current, Button apply)
        {
            host.SuspendLayout(); table.SuspendLayout();
            try {
                host.AutoScroll = true;
                table.AutoSize = true; table.AutoSizeMode = AutoSizeMode.GrowAndShrink; table.Dock = DockStyle.Top;
                table.ColumnStyles.Clear();
                table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40));
                table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60));
                table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0));
                table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                table.RowCount = 8;
                while (table.RowStyles.Count < 8) table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                for (int i = 0; i < table.RowStyles.Count; i++) table.RowStyles[i].SizeType = SizeType.AutoSize;
                var title = new Label { Text = "Base clock (BCLK)", AutoSize = true, Margin = new Padding(3, 14, 3, 3) };
                table.Controls.Add(title, 0, 6); table.SetColumnSpan(title, 4);
                current.AutoSize = true; current.Dock = DockStyle.Fill;
                clock.Dock = DockStyle.Fill;
                apply.AutoSize = true; apply.Dock = DockStyle.Fill; apply.Text = "Apply BCLK";
                table.Controls.Add(current, 0, 7); table.Controls.Add(clock, 1, 7); table.Controls.Add(apply, 3, 7);
            } finally { table.ResumeLayout(true); host.ResumeLayout(true); }
        }
    }
}
