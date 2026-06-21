using System;
using System.IO;
using System.Linq;
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
            string profileName = GetApplyProfileName(args);
            bool isApply = args.Any(a => a.ToLower() == "--applyprofile");

            if (isApply)
            {
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
            catch (ApplicationException ex)
            {
                Console.WriteLine(ex.Message);
                Application.Exit();
            }
        }

        // Returns the token after "--applyprofile", or null if none/absent.
        private static string GetApplyProfileName(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i].ToLower() == "--applyprofile")
                {
                    string next = args[i + 1];
                    return next.StartsWith("--") ? null : next;
                }
            }
            return null;
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
                cpu?.Dispose();
            }
        }

        private static void Log(string logPath, string message)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath));
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
