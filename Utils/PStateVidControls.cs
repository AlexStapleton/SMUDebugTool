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
}
