using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace ZenStatesDebugTool
{
    public sealed class OwnedWorkloadProcess : IWorkloadRunner
    {
        public Task<WorkloadResult> RunAsync(WorkloadRecipe recipe, Func<string> healthIssue, CancellationToken cancellation)
            => Task.Factory.StartNew(() => Run(recipe, healthIssue, cancellation), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        private static WorkloadResult Run(WorkloadRecipe recipe, Func<string> healthIssue, CancellationToken cancellation)
        {
            var clock = Stopwatch.StartNew(); var output = new WorkloadOutput();
            IntPtr job = IntPtr.Zero, readPipe = IntPtr.Zero, writePipe = IntPtr.Zero, input = IntPtr.Zero, attributes = IntPtr.Zero, handles = IntPtr.Zero;
            var process = new ProcessInformation(); Task drain = null; StreamReader reader = null;
            bool attributesInitialized = false;
            string outcome = null, detail = null; int? exit = null;
            try {
                cancellation.ThrowIfCancellationRequested();
                string issue = healthIssue?.Invoke(); if (issue != null) throw new InvalidOperationException(issue);
                job = CreateJobObject(IntPtr.Zero, null); Check(job != IntPtr.Zero);
                var limits = new ExtendedLimits(); limits.Basic.LimitFlags = 0x2000; // KILL_ON_JOB_CLOSE
                if (recipe.AffinityMask != 0) { limits.Basic.LimitFlags |= 0x10; limits.Basic.Affinity = new UIntPtr(recipe.AffinityMask); }
                Check(SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf(limits)));
                var security = new SecurityAttributes { Length = Marshal.SizeOf(typeof(SecurityAttributes)), Inherit = true };
                Check(CreatePipe(out readPipe, out writePipe, ref security, 0)); Check(SetHandleInformation(readPipe, 1, 0));
                input = CreateFile("NUL", 0x80000000, 3, ref security, 3, 0, IntPtr.Zero); Check(input != new IntPtr(-1));
                IntPtr bytes = IntPtr.Zero; InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
                attributes = Marshal.AllocHGlobal(bytes); Check(InitializeProcThreadAttributeList(attributes, 1, 0, ref bytes)); attributesInitialized = true;
                handles = Marshal.AllocHGlobal(IntPtr.Size * 2); Marshal.WriteIntPtr(handles, writePipe); Marshal.WriteIntPtr(handles, IntPtr.Size, input);
                Check(UpdateProcThreadAttribute(attributes, 0, new IntPtr(0x20002), handles, new IntPtr(IntPtr.Size * 2), IntPtr.Zero, IntPtr.Zero));
                var startup = new StartupInfoEx(); startup.Startup.Size = Marshal.SizeOf(startup); startup.Startup.Flags = 0x100;
                startup.Startup.StdInput = input; startup.Startup.StdOutput = startup.Startup.StdError = writePipe; startup.Attributes = attributes;
                string command = QuoteArgument(recipe.Executable) + " " + recipe.Arguments;
                Check(CreateProcess(recipe.Executable, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero, true, 0x08000000 | 0x00080000 | 4,
                    IntPtr.Zero, Path.GetDirectoryName(recipe.Executable), ref startup, out process));
                // No application code can run or spawn before lifetime/affinity limits exist.
                Check(AssignProcessToJobObject(job, process.Process));
                cancellation.ThrowIfCancellationRequested();
                Close(ref writePipe); Close(ref input);
                reader = new StreamReader(new FileStream(new SafeFileHandle(readPipe, true), FileAccess.Read), new UTF8Encoding(false, false), true, 4096); readPipe = IntPtr.Zero;
                var capturedReader = reader;
                drain = Task.Factory.StartNew(() => { var buffer = new char[2048]; int count; while ((count = capturedReader.Read(buffer, 0, buffer.Length)) > 0) output.Add(buffer, count); output.Finish(); }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                Check(ResumeThread(process.Thread) != uint.MaxValue);
                while (true) {
                    if (cancellation.IsCancellationRequested) { outcome = "Cancelled"; detail = "Stopped by user; no further assisted writes."; break; }
                    if (clock.Elapsed >= recipe.Timeout) { outcome = "Timed out"; detail = "Workload exceeded its timeout."; break; }
                    issue = healthIssue?.Invoke() ?? output.Issue;
                    if (issue != null) { outcome = "Failed"; detail = issue; break; }
                    uint wait = WaitForSingleObject(process.Process, 50);
                    if (wait == 0) { Check(GetExitCodeProcess(process.Process, out uint code)); exit = unchecked((int)code); break; }
                    if (wait != 258) throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            } catch (OperationCanceledException) { outcome = "Cancelled"; detail = "Stopped before workload completion."; }
            catch (Exception ex) { outcome = "Failed"; detail = ex.Message; }
            finally {
                // A root exit also ends any owned descendants before completing the run.
                if (job != IntPtr.Zero) TerminateJobObject(job, 1);
                if (process.Process != IntPtr.Zero) { TerminateProcess(process.Process, 1); WaitForSingleObject(process.Process, 2000); }
                Close(ref writePipe); Close(ref input); Close(ref job);
                if (drain != null) {
                    try { if (!drain.Wait(3000)) { outcome = "Failed"; detail = "Output reader did not stop; result is incomplete."; } }
                    catch (Exception ex) { outcome = "Failed"; detail = "Output capture failed: " + ex.GetBaseException().Message; }
                }
                reader?.Dispose(); Close(ref readPipe); Close(ref process.Thread); Close(ref process.Process);
                if (attributes != IntPtr.Zero) { if (attributesInitialized) DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
                if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
            }
            if (outcome == null) {
                if (exit != 0 || output.Issue != null) { outcome = "Failed"; detail = output.Issue ?? "Workload exit code " + exit + "."; }
                else if (!output.Score.HasValue) { outcome = "Inconclusive"; detail = "No unique recognized CPU score. Inspect output; no stability claim."; }
                else { outcome = "Completed"; detail = "Benchmark completed; computational correctness and stability coverage remain pending."; }
            }
            return new WorkloadResult { Outcome = outcome, Detail = detail, ExitCode = exit, Score = outcome == "Completed" ? output.Score : null, Output = output.Tail, ElapsedSeconds = clock.Elapsed.TotalSeconds };
        }
        public static string QuoteArgument(string value)
        {
            var result = new StringBuilder("\""); int slashes = 0;
            foreach (char ch in value) {
                if (ch == '\\') { slashes++; continue; }
                if (ch == '"') result.Append('\\', slashes * 2 + 1); else result.Append('\\', slashes);
                result.Append(ch); slashes = 0;
            }
            return result.Append('\\', slashes * 2).Append('"').ToString();
        }
        private static void Check(bool success) { if (!success) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        private static void Close(ref IntPtr handle) { if (handle != IntPtr.Zero && handle != new IntPtr(-1)) CloseHandle(handle); handle = IntPtr.Zero; }
        [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
        [StructLayout(LayoutKind.Sequential)] private struct BasicLimits { public long ProcessTime, JobTime; public uint LimitFlags; public UIntPtr MinWorking, MaxWorking; public uint ActiveProcesses; public UIntPtr Affinity; public uint Priority, Scheduling; }
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }
        [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimits { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo { public int Size; public string Reserved, Desktop, Title; public uint X, Y, Width, Height, XChars, YChars, Fill, Flags; public ushort Show, ReservedBytes; public IntPtr ReservedData, StdInput, StdOutput, StdError; }
        [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo Startup; public IntPtr Attributes; }
        [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetInformationJobObject(IntPtr job, int kind, ref ExtendedLimits limits, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateJobObject(IntPtr job, uint code);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CreatePipe(out IntPtr read, out IntPtr write, ref SecurityAttributes attributes, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateFile(string path, uint access, uint share, ref SecurityAttributes security, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returned);
        [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CreateProcess(string app, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInformation process);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint timeout);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetExitCodeProcess(IntPtr process, out uint code);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr process, uint code);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    }
}
