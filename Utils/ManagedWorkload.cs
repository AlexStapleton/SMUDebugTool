using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ZenStatesDebugTool
{
    public static class WorkloadPreferences
    {
        private static string PathName => Path.Combine(Path.GetDirectoryName(CoRecoveryStore.DefaultPath), "cinebench-path.txt");
        public static string Load()
        {
            try { return new FileInfo(PathName).Length <= 4096 ? File.ReadAllText(PathName) : ""; } catch { return ""; }
        }
        public static void Save(string value)
        {
            if (value?.Length > 4096) throw new ArgumentException("Executable path is too long.");
            Directory.CreateDirectory(Path.GetDirectoryName(PathName));
            string temporary = PathName + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { File.WriteAllText(temporary, value ?? "", new UTF8Encoding(false)); if (File.Exists(PathName)) File.Replace(temporary, PathName, null); else File.Move(temporary, PathName); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
    public sealed class WorkloadTarget
    {
        public string Name { get; }
        public ulong Mask { get; }
        public bool SingleThread { get; }
        public WorkloadTarget(string name, ulong mask, bool singleThread = false) { Name = name; Mask = mask; SingleThread = singleThread; }
        public override string ToString() => Name;
    }
    public sealed class WorkloadRecipe
    {
        public string Executable { get; }
        public string Arguments { get; }
        public string Identity { get; }
        public TimeSpan Timeout { get; }
        public ulong AffinityMask { get; }
        public WorkloadRecipe(string executable, string arguments, string identity, TimeSpan timeout, ulong affinityMask = 0)
        {
            if (!Path.IsPathRooted(executable) || executable.StartsWith(@"\\", StringComparison.Ordinal) || !string.Equals(Path.GetExtension(executable), ".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(executable)) throw new ArgumentException("Select an existing local executable.");
            if (timeout < TimeSpan.FromSeconds(1) || timeout > TimeSpan.FromHours(2) || arguments?.Length > 4096) throw new ArgumentException("Invalid workload bounds.");
            Executable = Path.GetFullPath(executable); Arguments = arguments ?? ""; Identity = identity; Timeout = timeout; AffinityMask = affinityMask;
        }
        public static WorkloadRecipe Cinebench2024(string path, int durationSeconds, int timeoutSeconds, WorkloadTarget target, bool singleThread)
        {
            if (durationSeconds < 10 || durationSeconds > 3600 || timeoutSeconds < durationSeconds + 30 || timeoutSeconds > 7200) throw new ArgumentException("Use 10–3600 seconds and a timeout at least 30 seconds longer.");
            var version = FileVersionInfo.GetVersionInfo(path);
            if (!string.Equals(version.ProductName, "Cinebench", StringComparison.OrdinalIgnoreCase) || version.FileMajorPart != 2024 || version.FileMinorPart != 1 || version.FileBuildPart != 0)
                throw new InvalidOperationException("This adapter requires Cinebench 2024.1.0; other versions need a separate adapter review.");
            foreach (var existing in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(path)))
                using (existing) {
                    string existingPath;
                    try { existingPath = existing.MainModule?.FileName; }
                    catch (InvalidOperationException) { continue; }
                    catch (System.ComponentModel.Win32Exception) { throw new InvalidOperationException("Cannot identify an existing Cinebench instance. Close it before a managed test."); }
                    if (existingPath == null || string.Equals(Path.GetFullPath(path), existingPath, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException("Close the existing instance of this Cinebench installation before starting a managed test. It will not be attached to or stopped.");
                }
            string fingerprint;
            using (var stream = File.OpenRead(path)) using (var sha = SHA256.Create()) fingerprint = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
            string args = "g_CinebenchCpuXTest=" + (singleThread ? "false" : "true") + " g_CinebenchCpu1Test=" + (singleThread ? "true" : "false") +
                " g_CinebenchAllTests=false g_CinebenchMinimumTestDuration=" + durationSeconds.ToString(CultureInfo.InvariantCulture) + " g_console=true";
            return new WorkloadRecipe(path, args, "Cinebench 2024.1.0 / " + (singleThread ? "single thread" : "multi thread") + " / " + target.Name + " / " + durationSeconds + " s / SHA256 " + fingerprint,
                TimeSpan.FromSeconds(timeoutSeconds), target.Mask);
        }
    }
    public sealed class WorkloadResult
    {
        public string Outcome { get; set; }
        public int? ExitCode { get; set; }
        public decimal? Score { get; set; }
        public string Detail { get; set; }
        public string Output { get; set; }
        public double ElapsedSeconds { get; set; }
        public bool Completed => Outcome == "Completed";
    }
    public interface IWorkloadRunner
    {
        Task<WorkloadResult> RunAsync(WorkloadRecipe recipe, Func<string> healthIssue, CancellationToken cancellation);
    }
    // Chunked parsing bounds both unterminated lines and the retained transcript.
    public sealed class WorkloadOutput
    {
        private readonly object gate = new object();
        private readonly StringBuilder tail = new StringBuilder(), line = new StringBuilder();
        private long total; private bool oversizedLine;
        private decimal? score; private int scores; private string failure;
        private static readonly Regex ScoreLine = new Regex(@"^CB\s+([0-9]+(?:\.[0-9]+)?)\s+\(CPU(?: Single Core| \((?:Multi|Single) Core\))?\)\s*$", RegexOptions.CultureInvariant);
        private static readonly Regex FailureLine = new Regex(@"\b(?:FATAL ERROR|FATAL(?=:)|Rendering failed|Error starting (?:pre-)?rendering|Operation canceled by user|Application Error|computation error)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        public string Issue { get { lock (gate) return total > 4 * 1024 * 1024 ? "Workload output exceeded 4 MiB of text." : failure; } }
        public string Tail { get { lock (gate) return tail.ToString(); } }
        public decimal? Score { get { lock (gate) return scores == 1 ? score : null; } }
        public void Add(char[] buffer, int count)
        {
            lock (gate) {
                total += count; tail.Append(buffer, 0, count); if (tail.Length > 65536) tail.Remove(0, tail.Length - 65536);
                for (int i = 0; i < count; i++) {
                    char ch = buffer[i];
                    if (ch == '\r' || ch == '\n') { ParseLine(); line.Clear(); oversizedLine = false; }
                    else if (line.Length < 4096) line.Append(ch); else oversizedLine = true;
                }
            }
        }
        public void Finish() { lock (gate) { ParseLine(); line.Clear(); } }
        private void ParseLine()
        {
            string value = line.ToString();
            if (FailureLine.IsMatch(value)) failure = "Workload reported an error: " + value.Substring(0, Math.Min(300, value.Length));
            if (oversizedLine) return;
            var match = ScoreLine.Match(value);
            if (match.Success && decimal.TryParse(match.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out decimal parsed) && parsed > 0 && parsed <= 10000000) { score = parsed; scores++; }
        }
    }
}
