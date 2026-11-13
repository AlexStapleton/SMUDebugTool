using System;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace ZenStatesDebugTool
{
    public partial class SettingsForm
    {
        private TextBox pstateVid, pstateVidVoltage;
        private Button pstateVidWrite;
        private void InitPStateVidEditor()
        {
            var controls = new PStateVidControls(tableLayoutPanel5);
            pstateVid = controls.Value; pstateVidVoltage = controls.Voltage; pstateVidWrite = controls.Write;
            pstateVid.TextChanged += (s, e) => UpdatePStateVidPreview();
            pstateVidWrite.Click += WritePStateVid_Click;
        }
        private void UpdatePStateVidPreview()
        {
            bool valid = PStateVidMath.TryParse(decodeContext.PStateVidEncoding, pstateVid.Text, out uint vid);
            pstateVidVoltage.Text = valid ? PStateVidMath.Voltage(vid, decodeContext.PStateVidEncoding).ToString("0.0000", CultureInfo.InvariantCulture) + " V (VID)" : "N/A";
            pstateVidWrite.Enabled = valid && !pstateVid.ReadOnly && hardware.CanWritePStates && pstateIdBox.SelectedItem is PStateItem item && pstateReadId == item.Id;
        }
        private async void WritePStateVid_Click(object sender, EventArgs e)
        {
            if (!(pstateIdBox.SelectedItem is PStateItem item) || pstateReadId != item.Id || pstateVid.ReadOnly ||
                !PStateVidMath.TryParse(decodeContext.PStateVidEncoding, pstateVid.Text, out uint vid)) return;
            double voltage = PStateVidMath.Voltage(vid, decodeContext.PStateVidEncoding);
            if (MessageBox.Show(this, $"Write P{item.Id} VID {vid} ({voltage:0.0000} V encoded) on all active processors?\r\n\r\nThis changes a requested register parameter, not measured voltage. Unsuitable settings can crash or damage the system. Frequency and other fields will be preserved.", "Confirm P-state VID change", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            pstateReadId = null; pstateVidWrite.Enabled = btnPstateWrite.Enabled = false;
            try
            {
                var result = await RunHardwareAsync(() => hardware.WritePStateVid(item.Id, decodeContext.PStateLayout, decodeContext.PStateVidEncoding, vid));
                if (shuttingDown) return;
                SetStatusText($"P{item.Id} VID: {result.Status}; {result.Completed} processors verified. {result.Error}");
                PrependResult($"VID requested register: {result.Requested?.ToString("X16") ?? "unavailable"}; readback: {result.Observed?.ToString("X16") ?? "unavailable"}\r\n");
                if (result.Success) BtnPstateRead_Click(sender, e);
            }
            catch (Exception ex) { if (!shuttingDown) SetStatusText(ex.Message); }
        }
    }
}
