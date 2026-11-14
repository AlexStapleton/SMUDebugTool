using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace ZenStatesDebugTool
{
    public sealed class DriverStateSnapshot
    {
        public bool Present, LocalHandleOpen, OwnershipEstablished, UnknownConsumers = true;
        public int OtherInstances;
        public string RawImagePath, Path, ExpectedPath, Status, Hash, Error;
        public override string ToString() => $"Service: inpoutx64\r\nStatus: {Status}\r\nRegistry ImagePath: {RawImagePath}\r\nPath: {Path}\r\nExpected path: {ExpectedPath}\r\nSHA256: {Hash ?? "Unavailable"}\r\nOwnership established: {OwnershipEstablished}\r\nLocal driver open: {LocalHandleOpen}\r\nOther participating instances: {OtherInstances}\r\nUnknown external consumers possible: {UnknownConsumers}\r\n{Error}";
    }
    internal static class DriverImagePath
    {
        // Driver ImagePath values such as System32\Drivers\foo.sys are Windows-relative,
        // never relative to the application's current working directory.
        internal static string Resolve(string imagePath, string windowsDirectory)
        {
            if (string.IsNullOrWhiteSpace(imagePath)) throw new InvalidDataException("Driver service ImagePath is missing.");
            string path = imagePath.Trim();
            if (path.Length >= 2 && path[0] == '"' && path[path.Length - 1] == '"') path = path.Substring(1, path.Length - 2);
            path = Environment.ExpandEnvironmentVariables(path).Replace('/', '\\');
            if (path.StartsWith(@"\??\", StringComparison.Ordinal) || path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path.Substring(4);
            if (path.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase))
                path = System.IO.Path.Combine(windowsDirectory, path.Substring(12));
            else if (path.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase))
                path = System.IO.Path.Combine(windowsDirectory, path);
            if (path.Length < 3 || !char.IsLetter(path[0]) || path[1] != ':' || path[2] != '\\')
                throw new InvalidDataException("Unsupported or ambiguous driver service ImagePath: " + imagePath);
            return System.IO.Path.GetFullPath(path);
        }
    }
    public interface IDriverMaintenanceEnvironment
    {
        IDisposable EnterMaintenance();
        DriverStateSnapshot Inspect();
        bool Stop(TimeSpan timeout);
        bool DeleteService();
        bool DeleteFile();
    }
    public sealed class DriverMaintenanceService
    {
        private readonly IDriverMaintenanceEnvironment environment;
        public DriverMaintenanceService(IDriverMaintenanceEnvironment environment) { this.environment = environment; }
        public string Remove(bool explicitRequest, bool sharedUseAcknowledged = false)
        {
            if (!explicitRequest) return "Skipped: shared drivers are retained on normal exit.";
            try
            {
                using (environment.EnterMaintenance())
                {
                    var state = environment.Inspect();
                    if (state.Error != null) return "Skipped: " + state.Error;
                    if (!state.Present) return "No installed driver service.";
                    if (state.LocalHandleOpen || state.OtherInstances != 0) return "Skipped: a participating instance still uses the driver.";
                    if (!state.OwnershipEstablished) return "Skipped: this application cannot establish ownership of the installed driver.";
                    if (!string.Equals(state.Path, state.ExpectedPath, StringComparison.OrdinalIgnoreCase)) return "Skipped: driver path differs from the expected system path.";
                    if (state.UnknownConsumers && !sharedUseAcknowledged) return "Skipped: external consumers cannot be ruled out.";
                    if (!environment.Stop(TimeSpan.FromSeconds(3))) return "Driver did not stop within the maintenance deadline; nothing deleted.";
                    if (!environment.DeleteService()) return "Driver stopped; service deletion failed. File retained.";
                    if (!environment.DeleteFile()) return "Service removed; driver file retained because deletion failed.";
                    return "Driver service and owned file removed.";
                }
            }
            catch (Exception ex) { return "Maintenance failed: " + ex.Message; }
        }
    }
    public sealed class DriverSession : IDisposable
    {
        internal const string Gate = @"Global\RyzenSDT.DriverMaintenance.v1";
        private readonly Mutex marker;
        private DriverSession(Mutex marker) { this.marker = marker; }
        public static IDisposable EnterGate()
        {
            var gate = new Mutex(false, Gate);
            bool acquired;
            try { acquired = gate.WaitOne(2500); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) { gate.Dispose(); throw new System.TimeoutException("Driver maintenance is busy; retry shortly."); }
            return new GateScope(gate);
        }
        public static DriverSession Open()
        {
            using (EnterGate()) return new DriverSession(new Mutex(false, @"Global\RyzenSDT.Active." + Process.GetCurrentProcess().Id));
        }
        public static ZenStates.Core.Cpu CreateCpu(Action<string> progress, out DriverSession session)
        {
            session = Open();
            try
            {
                using (EnterGate())
                {
                    var environment = new WindowsDriverMaintenanceEnvironment();
                    var before = environment.Inspect();
                    var cpu = CoreLibraryAdapter.CreateCpu(progress);
                    try { environment.RecordNewInstallation(before.Error == null && !before.Present); } catch { }
                    return cpu;
                }
            }
            catch { session.Dispose(); session = null; throw; }
        }
        public void Dispose() => marker.Dispose();
        private sealed class GateScope : IDisposable
        {
            private readonly Mutex gate;
            public GateScope(Mutex gate) { this.gate = gate; }
            public void Dispose() { gate.ReleaseMutex(); gate.Dispose(); }
        }
    }
    public sealed class WindowsDriverMaintenanceEnvironment : IDriverMaintenanceEnvironment
    {
        private const string Service = "inpoutx64";
        private const string OwnershipKey = @"SOFTWARE\AlexStapleton\SMUDebugTool\DriverOwnership";
        public bool LocalHandleOpen { get; set; }
        public IDisposable EnterMaintenance() => DriverSession.EnterGate();
        public DriverStateSnapshot Inspect()
        {
            var state = new DriverStateSnapshot
            {
                LocalHandleOpen = LocalHandleOpen,
                ExpectedPath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "inpoutx64.sys")
            };
            try
            {
                using (var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + Service))
                {
                    state.Present = key != null;
                    state.RawImagePath = key?.GetValue("ImagePath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                    if (state.Present)
                    {
                        try { state.Path = DriverImagePath.Resolve(state.RawImagePath, Environment.GetFolderPath(Environment.SpecialFolder.Windows)); }
                        catch (Exception ex) { state.Error = ex.Message; }
                    }
                }
                if (state.Present) using (var service = new ServiceController(Service)) state.Status = service.Status.ToString();
                else state.Status = "Not installed";
                state.Hash = state.Path != null && File.Exists(state.Path) ? FileHash(state.Path) : null;
                if (state.Present && state.Hash == null && state.Error == null) state.Error = "Driver file is missing or inaccessible at the resolved service path.";
                using (var marker = Registry.LocalMachine.OpenSubKey(OwnershipKey))
                    state.OwnershipEstablished = state.Hash != null && state.Hash == marker?.GetValue("Sha256") as string
                        && string.Equals(state.Path, marker?.GetValue("Path") as string, StringComparison.OrdinalIgnoreCase);
                using (var current = Process.GetCurrentProcess())
                    foreach (var process in Process.GetProcesses())
                        using (process)
                        {
                            if (process.Id == current.Id) continue;
                            try
                            {
                                if (Mutex.TryOpenExisting(@"Global\RyzenSDT.Active." + process.Id, out Mutex marker))
                                { marker.Dispose(); state.OtherInstances++; }
                            }
                            catch (UnauthorizedAccessException) { state.OtherInstances++; }
                        }
            }
            catch (Exception ex) { state.Error = ex.Message; }
            return state;
        }
        // Only record ownership if no service existed before our initialization. Never adopt an existing installation.
        public void RecordNewInstallation(bool wasAbsent)
        {
            if (!wasAbsent) return;
            var state = Inspect();
            if (state.Error != null || !state.Present || state.Hash == null || !string.Equals(state.Path, state.ExpectedPath, StringComparison.OrdinalIgnoreCase)) return;
            using (var marker = Registry.LocalMachine.CreateSubKey(OwnershipKey))
            { marker.SetValue("Path", state.Path); marker.SetValue("Sha256", state.Hash); }
        }
        private static string FileHash(string path)
        {
            using (var sha = SHA256.Create()) using (var stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        public bool Stop(TimeSpan timeout)
        {
            using (var controller = new ServiceController(Service))
            {
                if (controller.Status == ServiceControllerStatus.StopPending) return false;
                if (controller.Status == ServiceControllerStatus.Stopped) return true;
                controller.Stop(); controller.WaitForStatus(ServiceControllerStatus.Stopped, timeout); return true;
            }
        }
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenSCManager(string machine, string database, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DeleteService(IntPtr service);
        [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr handle);
        public bool DeleteService()
        {
            IntPtr manager = OpenSCManager(null, null, 1), service = IntPtr.Zero;
            try { if (manager == IntPtr.Zero) return false; service = OpenService(manager, Service, 0x10000); return service != IntPtr.Zero && DeleteService(service); }
            finally { if (service != IntPtr.Zero) CloseServiceHandle(service); if (manager != IntPtr.Zero) CloseServiceHandle(manager); }
        }
        public bool DeleteFile()
        {
            // Eligibility and canonical path were checked under the maintenance gate.
            var state = Inspect();
            string expected = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "drivers", "inpoutx64.sys");
            using (var marker = Registry.LocalMachine.OpenSubKey(OwnershipKey))
            {
                string hash = marker?.GetValue("Sha256") as string;
                if (hash == null || !File.Exists(expected) || FileHash(expected) != hash) return false;
            }
            File.Delete(expected);
            return true;
        }
    }
}
