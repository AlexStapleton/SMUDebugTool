using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using ZenStates.Core;
using ZenStatesDebugTool.Profiles;

namespace ZenStatesDebugTool
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            string[] args = Environment.GetCommandLineArgs();

            // A single scan decides both whether the flag is present and which
            // profile name (if any) follows it, so the two can never disagree.
            if (TryGetApplyProfile(args, out string profileName))
            {
                StartHeadlessWatchdog();
                Environment.Exit(RunHeadlessApply(profileName));
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += ApplicationThreadException;
            try
            {
                Form MainForm = new SettingsForm();
                string appString = $"{Application.ProductName} {Application.ProductVersion.Substring(0, Application.ProductVersion.LastIndexOf('.'))}";
#if DEBUG
                appString += " (debug)";
#endif
                MainForm.Text = appString;
                Application.Run(MainForm);
            }
            catch (Exception ex)
            {
                // Surface ANY startup failure instead of exiting silently. The original
                // code only caught ApplicationException, so e.g. a resource-load failure
                // in InitializeComponent (outside the form's own try) killed the process
                // with no window and no message.
                MessageBox.Show(ex.ToString(), "SMUDebugTool failed to start");
            }
        }

        // True if "--applyprofile" is present. profileName is set to the token that
        // follows it, or null when no (non-flag) name follows.
        private static bool TryGetApplyProfile(string[] args, out string profileName)
        {
            profileName = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--applyprofile", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                        profileName = args[i + 1];
                    return true;
                }
            }
            return false;
        }

        // 0 = success, 1 = failure. No UI in this path.
        private static int RunHeadlessApply(string profileName)
        {
            string profilesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "profiles");
            string logPath = Path.Combine(profilesPath, "apply.log");

            if (string.IsNullOrEmpty(profileName))
            {
                Log(logPath, "No profile name supplied to --applyprofile; doing nothing.");
                return 1;
            }

            Cpu cpu = null;
            try
            {
                var manager = new ProfileManager(profilesPath);
                var profile = manager.Load(profileName);
                if (profile == null)
                {
                    Log(logPath, $"Profile '{profileName}' not found.");
                    return 1;
                }

                cpu = new Cpu();
                var result = new ProfileApplier().Apply(profile, cpu);
                Log(logPath, $"Applied '{profileName}'. Success={result.Success}. "
                    + string.Join("; ", result.Messages));
                return result.Success ? 0 : 1;
            }
            catch (Exception ex)
            {
                Log(logPath, $"Error applying '{profileName}': {ex.Message}");
                return 1;
            }
            finally
            {
                // Best-effort dispose, bounded: on some systems (e.g. while the GUI instance
                // also holds the driver) Cpu.Dispose can block, which would leave this headless
                // logon process alive as a "Running" scheduled task. Don't wait forever — the
                // apply is already done and logged, and the OS reclaims the handle on exit.
                DisposeWithTimeout(cpu, TimeSpan.FromSeconds(3));
            }
        }

        // Best-effort Dispose that never blocks the caller longer than `timeout`.
        private static void DisposeWithTimeout(IDisposable disposable, TimeSpan timeout)
        {
            if (disposable == null) return;
            var thread = new Thread(() => { try { disposable.Dispose(); } catch { } })
            {
                IsBackground = true,
                Name = "HeadlessApplyDispose"
            };
            thread.Start();
            thread.Join(timeout);
        }

        // Hard backstop: a headless logon apply must never linger as a "Running" task. If the
        // whole operation (driver init, SMU access, dispose, or shutdown) hasn't finished in
        // time, force-terminate. On a normal run Environment.Exit fires first and this
        // background thread dies with the process.
        private static void StartHeadlessWatchdog()
        {
            var watchdog = new Thread(() =>
            {
                Thread.Sleep(TimeSpan.FromSeconds(45));
                try { Process.GetCurrentProcess().Kill(); } catch { }
            })
            {
                IsBackground = true,
                Name = "HeadlessApplyWatchdog"
            };
            watchdog.Start();
        }

        private static void Log(string logPath, string message)
        {
            try
            {
                var dir = Path.GetDirectoryName(logPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);
                File.AppendAllText(logPath, $"{DateTime.Now:s} {message}{Environment.NewLine}");
            }
            catch { /* logging must never throw in the headless path */ }
        }

        static void ApplicationThreadException(object sender, System.Threading.ThreadExceptionEventArgs e)
        {
            MessageBox.Show(e.Exception.Message, Properties.Resources.Error);
        }
    }
}
