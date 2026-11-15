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
            base.OnShown(e); loading = true; StartupTimings.Current.Mark("Startup window shown");
            DriverSession session = null; Cpu cpu = null;
            try
            {
                await Task.Run(() =>
                {
                    IDisposable currentStage = StartupTimings.Current.Measure("Driver initialization");
                    using (StartupTimings.Current.Measure("CPU initialization total"))
                    try {
                        cpu = DriverSession.CreateCpu(stage => {
                            currentStage.Dispose(); currentStage = StartupTimings.Current.Measure("Core: " + stage);
                            if (!cancelled && IsHandleCreated) try { BeginInvoke((Action)(() => { if (!cancelled && !IsDisposed) progress.Text = stage; })); } catch (InvalidOperationException) { }
                        }, out session);
                    } finally { currentStage.Dispose(); }
                });
                if (cancelled) return;
                SettingsForm main;
                using (StartupTimings.Current.Measure("Main window construction")) main = new SettingsForm(cpu, session) { Text = Application.ProductName + " " + Application.ProductVersion };
                cpu = null; session = null;
                main.FormClosed += (sender, args) => Close();
                Hide(); main.Show();
            }
            catch (Exception ex) { if (!cancelled) MessageBox.Show(this, ex.Message, "SMUDebugTool failed to start"); cancelled = true; }
            finally
            {
                try { if (cpu != null) await Task.Run(() => cpu.Dispose()); session?.Dispose(); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine(ex); cancelled = true; }
                loading = false; await StartupTimings.Current.PersistAsync(); if (cancelled) Close();
            }
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        { if (loading) { cancelled = true; progress.Text = "Waiting for initialization to finish..."; e.Cancel = true; } base.OnFormClosing(e); }
    }
}
