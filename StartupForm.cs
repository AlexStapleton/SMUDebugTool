using System;
using System.Threading.Tasks;
using System.Windows.Forms;
using ZenStates.Core;

namespace ZenStatesDebugTool
{
    public sealed class StartupForm : Form
    {
        private readonly Label progress = new Label { Dock = DockStyle.Fill, Text = "Initializing hardware...", TextAlign = System.Drawing.ContentAlignment.MiddleCenter };
        private bool loading;
        private volatile bool cancelled;
        public StartupForm()
        {
            UiLayout.Configure(this, new System.Drawing.Size(400, 150));
            Text = "SMUDebugTool"; Width = 420; Height = 110; StartPosition = FormStartPosition.CenterScreen;
            Controls.Add(progress);
        }
        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e); loading = true;
            DriverSession session = null; Cpu cpu = null;
            try
            {
                await Task.Run(() =>
                {
                    cpu = DriverSession.CreateCpu(stage =>
                    {
                        if (!cancelled && IsHandleCreated) try { BeginInvoke((Action)(() => progress.Text = stage)); } catch (InvalidOperationException) { }
                    }, out session);
                });
                if (cancelled) return;
                var main = new SettingsForm(cpu, session) { Text = Application.ProductName + " " + Application.ProductVersion };
                cpu = null; session = null;
                main.FormClosed += (sender, args) => Close();
                Hide(); main.Show();
            }
            catch (Exception ex) { if (!cancelled) MessageBox.Show(this, ex.Message, "SMUDebugTool failed to start"); cancelled = true; }
            finally
            {
                try { if (cpu != null) await Task.Run(() => cpu.Dispose()); session?.Dispose(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); cancelled = true; }
                loading = false; if (cancelled) Close();
            }
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        { if (loading) { cancelled = true; progress.Text = "Waiting for initialization to finish..."; e.Cancel = true; } base.OnFormClosing(e); }
    }
}
