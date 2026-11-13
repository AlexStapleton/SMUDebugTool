using System;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ZenStatesDebugTool
{
    public sealed class DriverMaintenanceForm : Form
    {
        public DriverMaintenanceForm(IDriverMaintenanceEnvironment environment, Func<Task> closeHardware)
        {
            UiLayout.Configure(this, new System.Drawing.Size(400, 150));
            Text = "Driver diagnostics and maintenance"; Width = 700; Height = 430;
            var report = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both };
            var actions = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true };
            var refresh = new Button { Text = "Refresh", AutoSize = true };
            var copy = new Button { Text = "Copy report", AutoSize = true };
            var remove = new Button { Text = "End session and remove owned driver", AutoSize = true };
            bool maintaining = false, reading = false;
            FormClosing += (s, e) => { if (maintaining) e.Cancel = true; };
            async Task Read()
            {
                if (reading) return;
                reading = true; refresh.Enabled = false;
                try { var state = await Task.Run(() => environment.Inspect()); if (!IsDisposed) report.Text = state.ToString(); }
                finally { reading = false; if (!IsDisposed) refresh.Enabled = true; }
            }
            refresh.Click += async (s, e) => { try { await Read(); } catch (Exception ex) { report.Text = ex.Message; } };
            copy.Click += (s, e) => { try { Clipboard.SetText(report.Text); } catch (Exception ex) { report.Text = ex.Message; } };
            remove.Click += async (s, e) =>
            {
                if (MessageBox.Show(this, "This ends the hardware session. Close other hardware tools first. External driver consumers cannot be enumerated reliably. Only a driver installation recorded as owned by this application can be removed. Continue?", Text, MessageBoxButtons.YesNo) != DialogResult.Yes) return;
                actions.Enabled = false;
                maintaining = true;
                try
                {
                    await closeHardware();
                    string result = await Task.Run(() => new DriverMaintenanceService(environment).Remove(true, true));
                    report.Text = result + Environment.NewLine + (await Task.Run(() => environment.Inspect()));
                }
                catch (Exception ex) { report.Text = ex.Message; }
                finally { maintaining = false; if (!IsDisposed) actions.Enabled = true; }
            };
            Shown += async (s, e) => { try { await Read(); } catch (Exception ex) { report.Text = ex.Message; } };
            actions.Controls.Add(refresh); actions.Controls.Add(copy); actions.Controls.Add(remove);
            Controls.Add(report); Controls.Add(actions);
        }
    }
}
