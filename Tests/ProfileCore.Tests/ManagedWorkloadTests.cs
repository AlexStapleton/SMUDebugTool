using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.Windows.Forms;
using Microsoft.CSharp;
using Xunit;
using ZenStatesDebugTool;
using ZenStatesDebugTool.Profiles;

namespace ProfileCore.Tests
{
    public class ManagedWorkloadTests
    {
        private static readonly Lazy<string> Fixture = new Lazy<string>(() => {
            string directory = Path.Combine(Path.GetTempPath(), "SMUDebugTool workload fixture " + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "Cinebench.exe");
            using (var compiler = new CSharpCodeProvider()) {
                var options = new CompilerParameters(new[] { "System.dll" }, path) { GenerateExecutable = true, CompilerOptions = "/platform:x64" };
                var result = compiler.CompileAssemblyFromFile(options, Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "WorkloadFixture.txt"));
                if (result.Errors.HasErrors) throw new Exception(string.Join("\n", result.Errors.Cast<CompilerError>()));
            }
            return path;
        });
        private static WorkloadRecipe Recipe(string args, int timeout = 10, ulong mask = 0) => new WorkloadRecipe(Fixture.Value, args, "Fixture only", TimeSpan.FromSeconds(timeout), mask);
        private static string TempFile() => Path.Combine(Path.GetTempPath(), "SMUDebugTool workload " + Guid.NewGuid().ToString("N") + ".txt");
        private static async Task WaitFile(string path)
        {
            var clock = Stopwatch.StartNew(); while (!File.Exists(path) && clock.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(20);
            Assert.True(File.Exists(path));
        }
        private static bool Running(int pid) { try { using (var p = Process.GetProcessById(pid)) return !p.HasExited; } catch (ArgumentException) { return false; } }
        [Theory]
        [InlineData("score", "Completed", 0)] [InlineData("exit", "Failed", 7)] [InlineData("error", "Failed", 0)] [InlineData("no-score", "Inconclusive", 0)]
        public async Task OwnedFixtureDistinguishesExitErrorAndMissingScore(string args, string outcome, int code)
        {
            var result = await new OwnedWorkloadProcess().RunAsync(Recipe(args), () => null, CancellationToken.None);
            Assert.Equal(outcome, result.Outcome); Assert.Equal(code, result.ExitCode);
            Assert.Equal(outcome == "Completed" ? (decimal?)1250.50m : null, result.Score);
        }
        [Fact]
        public async Task CancellationKillsOnlyTheOwnedTreeAndLeavesUnrelatedFixtureAlive()
        {
            string leaf = TempFile(), child = TempFile(), unrelated = TempFile();
            using (var existing = Process.Start(new ProcessStartInfo(Fixture.Value, "wait " + OwnedWorkloadProcess.QuoteArgument(unrelated)) { UseShellExecute = false, CreateNoWindow = true }))
            using (var stop = new CancellationTokenSource()) {
                try {
                    await WaitFile(unrelated);
                    var task = new OwnedWorkloadProcess().RunAsync(Recipe("child " + OwnedWorkloadProcess.QuoteArgument(leaf) + " " + OwnedWorkloadProcess.QuoteArgument(child)), () => null, stop.Token);
                    await WaitFile(leaf); int pid = int.Parse(File.ReadAllText(child)); stop.Cancel(); var result = await task;
                    Assert.Equal("Cancelled", result.Outcome); Assert.False(Running(pid)); Assert.False(existing.HasExited);
                } finally { if (!existing.HasExited) existing.Kill(); existing.WaitForExit(3000); }
            }
        }
        [Fact]
        public async Task TimeoutAndHealthFailureStopOwnedWorkload()
        {
            string marker = TempFile();
            var timeout = await new OwnedWorkloadProcess().RunAsync(Recipe("wait " + OwnedWorkloadProcess.QuoteArgument(marker), 3), () => null, CancellationToken.None);
            Assert.Equal("Timed out", timeout.Outcome); Assert.False(Running(int.Parse(File.ReadAllText(marker))));
            int checks = 0;
            var failed = await new OwnedWorkloadProcess().RunAsync(Recipe("wait " + OwnedWorkloadProcess.QuoteArgument(TempFile())), () => Interlocked.Increment(ref checks) > 3 ? "Native readings stale" : null, CancellationToken.None);
            Assert.Equal("Failed", failed.Outcome); Assert.Contains("stale", failed.Detail);
        }
        [Fact]
        public async Task RootExitKillsDescendantsAndAffinityIsRestrictedBeforeExecution()
        {
            string leaf = TempFile(), child = TempFile();
            ulong available; using (var current = Process.GetCurrentProcess()) available = unchecked((ulong)current.ProcessorAffinity.ToInt64());
            ulong mask = available & (~available + 1);
            var result = await new OwnedWorkloadProcess().RunAsync(Recipe("root-exit " + OwnedWorkloadProcess.QuoteArgument(leaf) + " " + OwnedWorkloadProcess.QuoteArgument(child), 10, mask), () => null, CancellationToken.None);
            Assert.Equal("Completed", result.Outcome); Assert.Contains("AFFINITY=" + mask.ToString("X"), result.Output);
            Assert.False(Running(int.Parse(File.ReadAllText(child))));
        }
        [Fact]
        public async Task LiteralArgumentsSurviveWithoutAShellAndOutputIsBounded()
        {
            string literal = "space & $ ` quote\" slash\\";
            var result = await new OwnedWorkloadProcess().RunAsync(Recipe("quote " + OwnedWorkloadProcess.QuoteArgument(literal)), () => null, CancellationToken.None);
            Assert.Equal("Completed", result.Outcome); Assert.Contains("ARG=" + literal, result.Output);
            var flood = await new OwnedWorkloadProcess().RunAsync(Recipe("flood"), () => null, CancellationToken.None);
            Assert.Equal("Failed", flood.Outcome); Assert.True(flood.Output.Length <= 65536); Assert.Contains("exceeded", flood.Detail);
        }
        [Fact]
        public void ScoreParserHandlesChunksAndRejectsDuplicateOrPartialResults()
        {
            var output = new WorkloadOutput();
            foreach (char c in "CB 1234.50 (CPU)\r\n") output.Add(new[] { c }, 1);
            Assert.Equal(1234.50m, output.Score);
            var second = "CB 1234.50 (CPU)\n".ToCharArray(); output.Add(second, second.Length); Assert.Null(output.Score);
            var malformed = new WorkloadOutput(); var invalid = "CB 0 (CPU)\nCB 23 (GPU)\nCB 1,200 (CPU)\n".ToCharArray(); malformed.Add(invalid, invalid.Length); Assert.Null(malformed.Score);
        }
        [Fact]
        public void SingleCoreResultAndInstalledCinebenchErrorStringsAreRecognized()
        {
            var output = new WorkloadOutput(); var score = "CB 120.50 (CPU Single Core)\n".ToCharArray(); output.Add(score, score.Length); Assert.Equal(120.50m, output.Score);
            foreach (string text in new[] { "FATAL: fixture failure", "Error starting rendering", "Error starting pre-rendering", "Operation canceled by user." }) {
                var failed = new WorkloadOutput(); var line = (text + "\n").ToCharArray(); failed.Add(line, line.Length); Assert.NotNull(failed.Issue);
            }
        }
        [Fact]
        public void AdapterRecordsIdentityAndRejectsVersionOrTimingMismatch()
        {
            var recipe = WorkloadRecipe.Cinebench2024(Fixture.Value, 60, 600, new WorkloadTarget("Core 3", 8, true), true);
            Assert.Contains("g_CinebenchCpu1Test=true", recipe.Arguments); Assert.Contains("SHA256", recipe.Identity); Assert.Equal(8UL, recipe.AffinityMask);
            Assert.Throws<ArgumentException>(() => WorkloadRecipe.Cinebench2024(Fixture.Value, 60, 70, new WorkloadTarget("All", 0), false));
            Assert.Throws<InvalidOperationException>(() => WorkloadRecipe.Cinebench2024(typeof(object).Assembly.Location, 60, 600, new WorkloadTarget("All", 0), false));
        }
        private sealed class Transport : ITuningTransport
        {
            public readonly Dictionary<int, int> Co = new Dictionary<int, int> { [0] = -20, [1] = -25 };
            public int Writes; public int BadRead = -1; public int Reject = -1; public Action AfterWrite;
            public bool IsSupported(TuningRequest r, out string reason) { reason = null; return true; }
            public TuningReply Read(TuningRequest r) => new TuningReply { Status = 1, Direct = true, Value = r.Kind == TuningKind.Scalar ? 1 : r.Index == BadRead ? (decimal?)null : Co[r.Index] };
            public TuningReply Write(TuningRequest r) { Writes++; if (r.Index == Reject) return new TuningReply { Status = 0xFF }; Co[r.Index] = (int)r.Value; AfterWrite?.Invoke(); return new TuningReply { Status = 1 }; }
        }
        [Fact]
        public async Task AdapterRefusesAnExistingInstanceWithoutTouchingIt()
        {
            string marker = TempFile();
            using (var existing = Process.Start(new ProcessStartInfo(Fixture.Value, "wait " + OwnedWorkloadProcess.QuoteArgument(marker)) { UseShellExecute = false, CreateNoWindow = true })) {
                try {
                    await WaitFile(marker);
                    Assert.Throws<InvalidOperationException>(() => WorkloadRecipe.Cinebench2024(Fixture.Value, 60, 600, new WorkloadTarget("All", 0), false));
                    Assert.False(existing.HasExited);
                } finally { if (!existing.HasExited) existing.Kill(); existing.WaitForExit(3000); }
            }
        }
        [Fact]
        public void BatchPreflightAndReadbackFailurePreventFurtherWrites()
        {
            var transport = new Transport { BadRead = 1 }; var expected = new Dictionary<int, int>(transport.Co); var zero = expected.ToDictionary(c => c.Key, c => 0);
            Assert.Throws<InvalidOperationException>(() => CoBatchTransaction.Apply(transport, expected, zero, CancellationToken.None)); Assert.Equal(0, transport.Writes);
            transport.BadRead = -1; transport.Reject = 0;
            var result = CoBatchTransaction.Apply(transport, expected, zero, CancellationToken.None);
            Assert.Equal(1, transport.Writes); Assert.Equal(TuningOutcome.NotAttempted, result.Rows[1].Outcome);
        }
        [Fact]
        public void CompleteZeroBatchWritesEveryPhysicalCoreAndVerifiesZero()
        {
            var transport = new Transport(); var expected = new Dictionary<int, int>(transport.Co);
            var result = CoBatchTransaction.Apply(transport, expected, expected.ToDictionary(c => c.Key, c => 0), CancellationToken.None);
            Assert.Equal(2, transport.Writes); Assert.All(result.Rows, row => Assert.Equal(TuningOutcome.Verified, row.Outcome)); Assert.All(transport.Co.Values, value => Assert.Equal(0, value));
        }
        [Fact]
        public void CancelledBatchRetainsPartialEvidenceAndStopsRemainingCores()
        {
            using (var stop = new CancellationTokenSource()) {
                var transport = new Transport(); var expected = new Dictionary<int, int>(transport.Co); transport.AfterWrite = () => stop.Cancel();
                var result = CoBatchTransaction.Apply(transport, expected, expected.ToDictionary(c => c.Key, c => 0), stop.Token);
                Assert.Equal(1, transport.Writes); Assert.Equal(2, result.Rows.Count); Assert.Equal(TuningOutcome.AcceptedUnverified, result.Rows[0].Outcome); Assert.Equal(TuningOutcome.NotAttempted, result.Rows[1].Outcome);
            }
        }
        private static NativeCoContext Context(int co = -20) => new NativeCoContext("Synthetic CPU BIOS SMU", "Synthetic topology", new Profile(), new Dictionary<int, int> { [0] = co }, 1);
        private sealed class FakeRunner : IWorkloadRunner
        {
            public int Launches; public string Outcome = "Completed";
            public async Task<WorkloadResult> RunAsync(WorkloadRecipe recipe, Func<string> health, CancellationToken cancellation) { Launches++; health?.Invoke(); await Task.Delay(10, cancellation); return new WorkloadResult { Outcome = Outcome, Score = Outcome == "Completed" ? (decimal?)100 : null, Output = "Synthetic only" }; }
        }
        [Fact]
        public async Task ZeroBaselinePreservesOriginalThenVerifiesBeforeLaunchAndKeepsRecovery()
        {
            var store = new CoRecoveryStore(TempFile()); var session = new NativeCoSession(); var runner = new FakeRunner(); int co = -20;
            var controller = new ManagedCoRun(session, runner, () => Task.FromResult(Context(co)), (plan, ct) => {
                Assert.Equal(-20, store.Read().OriginalCo[0]); Assert.Equal(-20, plan.Expected[0]); co = plan.Requested[0];
                var result = new TuningResult(); result.Rows.Add(new TuningRow { Outcome = TuningOutcome.Verified }); return Task.FromResult(result);
            }, store);
            var result = await controller.RunAsync("Baseline", true, Recipe("score"), () => null, CancellationToken.None);
            Assert.True(result.Completed); Assert.Equal(0, co); Assert.Equal(1, runner.Launches); Assert.True(store.IsPending); Assert.False(controller.AssistanceBlocked);
            Assert.Equal(0, session.Trials.Single().Start.ObservedCo[0]);
            result.Output = "changed"; Assert.Equal("Synthetic only", session.Trials.Single().ManagedWorkload.Output);
        }
        [Fact]
        public async Task IncompleteResetNeverLaunchesAndFailureDiscardsRecordedSuggestions()
        {
            var store = new CoRecoveryStore(TempFile()); var session = new NativeCoSession(); var runner = new FakeRunner();
            var controller = new ManagedCoRun(session, runner, () => Task.FromResult(Context()), (plan, ct) => Task.FromResult(new TuningResult()), store);
            var failed = await controller.RunAsync("Baseline", true, Recipe("score"), () => null, CancellationToken.None);
            Assert.Equal("Failed", failed.Outcome); Assert.Equal(0, runner.Launches); Assert.True(store.IsPending); Assert.True(controller.AssistanceBlocked);
            var completed = await controller.RunAsync("Baseline", false, Recipe("score"), () => null, CancellationToken.None, (c, start) => new CoStepProposal(0, -20, "Synthetic recorded evidence"));
            Assert.True(completed.Completed); Assert.NotNull(controller.SuggestedStep); Assert.False(controller.AssistanceBlocked);
            runner.Outcome = "Cancelled";
            await controller.RunAsync("Candidate", false, Recipe("score"), () => null, CancellationToken.None, (c, start) => new CoStepProposal(0, -20, "Synthetic"));
            Assert.True(controller.AssistanceBlocked); Assert.Null(controller.SuggestedStep); Assert.Equal("Cancelled", session.Trials.Last().State);
        }
        [Fact]
        public void RecoverySurvivesRestartDoesNotOverwriteOriginalAndBlocksAutomaticApply()
        {
            string path = TempFile(); var store = new CoRecoveryStore(path); store.Preserve(Context()); store.Preserve(Context(-21));
            Assert.Equal(-20, new CoRecoveryStore(path).Read().OriginalCo[0]); Assert.Throws<InvalidOperationException>(() => store.AcquireAutomaticApply());
            var mismatch = new NativeCoContext("Different CPU", "Synthetic topology", new Profile(), new Dictionary<int, int> { [0] = 0 }, 1);
            Assert.Throws<InvalidOperationException>(() => store.Clear(mismatch)); Assert.True(store.IsPending);
            store.Clear(Context()); using (store.AcquireAutomaticApply()) Assert.Throws<IOException>(() => store.Preserve(Context()));
            File.WriteAllText(path, "corrupt"); Assert.True(store.IsPending); Assert.Throws<InvalidOperationException>(() => store.AcquireAutomaticApply());
        }
        [Theory]
        [InlineData("Failed")] [InlineData("Cancelled")] [InlineData("Timed out")] [InlineData("Inconclusive")]
        public async Task EveryUnsuccessfulOutcomeBlocksAssistanceAndCarriesNoScore(string outcome)
        {
            var session = new NativeCoSession(); var runner = new FakeRunner { Outcome = outcome };
            var controller = new ManagedCoRun(session, runner, () => Task.FromResult(Context()), (p, ct) => throw new Exception("Must not write"), new CoRecoveryStore(TempFile()));
            var result = await controller.RunAsync("Candidate", false, Recipe("score"), () => null, CancellationToken.None);
            Assert.Equal(outcome, result.Outcome); Assert.True(controller.AssistanceBlocked); Assert.Null(session.Trials.Single().Score); Assert.Null(controller.SuggestedStep);
        }
        [Fact]
        public async Task ChangedContextSuppressesScoreAndStalePreflightLaunchesNothing()
        {
            int captures = 0; var session = new NativeCoSession(); var runner = new FakeRunner();
            var controller = new ManagedCoRun(session, runner, () => Task.FromResult(Context(++captures == 1 ? -20 : -21)), (p, ct) => throw new Exception("Must not write"), new CoRecoveryStore(TempFile()));
            var result = await controller.RunAsync("Candidate", false, Recipe("score"), () => null, CancellationToken.None);
            Assert.Equal("Inconclusive", result.Outcome); Assert.Null(result.Score); Assert.True(controller.AssistanceBlocked);
            await controller.RunAsync("Baseline", true, Recipe("score"), () => "Native data stale", CancellationToken.None); Assert.Equal(1, runner.Launches);
        }
        [Fact]
        public async Task CancelledPreflightNeverMutatesAndFullSessionNeverResetsCo()
        {
            var session = new NativeCoSession(); var runner = new FakeRunner(); var store = new CoRecoveryStore(TempFile());
            var controller = new ManagedCoRun(session, runner, () => Task.FromResult(Context()), (p, ct) => throw new Exception("Must not write"), store);
            var result = await controller.RunAsync("Baseline", true, Recipe("score"), () => null, new CancellationToken(true));
            Assert.Equal("Cancelled", result.Outcome); Assert.False(store.IsPending); Assert.Equal(0, runner.Launches);
            var start = DateTimeOffset.UtcNow;
            for (int i = 0; i < 100; i++) { session.Begin("Baseline", "Synthetic", Context(), start); session.MarkEnd(start.AddSeconds(1)); session.End(Context(), null); }
            await controller.RunAsync("Baseline", true, Recipe("score"), () => null, CancellationToken.None);
            Assert.False(store.IsPending); Assert.Equal(0, runner.Launches);
        }
        private sealed class BlockingRunner : IWorkloadRunner
        {
            public bool Started;
            public async Task<WorkloadResult> RunAsync(WorkloadRecipe recipe, Func<string> health, CancellationToken cancellation)
            {
                Started = true;
                try { await Task.Delay(Timeout.Infinite, cancellation); } catch (OperationCanceledException) { }
                return new WorkloadResult { Outcome = "Cancelled", Detail = "Fixture cancelled" };
            }
        }
        [Fact]
        public async Task ManagedUiKeepsStopEnabledAndClosingCancelsWithoutStartingHardware()
        {
            var done = new TaskCompletionSource<bool>(); string fixturePath = Fixture.Value;
            var thread = new Thread(() => {
                try {
                    var runner = new BlockingRunner(); bool owns = false; var session = new NativeCoSession();
                    Func<CancellationToken, IReadOnlyList<CoreVidReading>> source = ct => new[] { new CoreVidReading(0, 0, "Synthetic", Stopwatch.GetTimestamp(), 1, 1.1m, 5000, 4950, null) };
                    using (var form = new NativeCoWorkbenchForm(session, () => Task.FromResult(source), () => Task.FromResult(Context()), p => throw new Exception("Must not write"),
                        new ManagedCoServices { Recovery = new CoRecoveryStore(TempFile()), Runner = runner, InitialExecutable = fixturePath,
                            Apply = (p, ct) => { var r = new TuningResult(); r.Rows.Add(new TuningRow { Outcome = TuningOutcome.Verified }); return Task.FromResult(r); },
                            Targets = () => Task.FromResult<IReadOnlyList<WorkloadTarget>>(new[] { new WorkloadTarget("All processors", 0) }), SetHardwareOwner = value => owns = value })) {
                        form.Opacity = 0; form.ShowInTaskbar = false; form.Font = new Font(form.Font.FontFamily, form.Font.Size * 2); form.Show();
                        form.StartMonitorAsync().GetAwaiter().GetResult();
                        var tabs = Descendants(form).OfType<TabControl>().Single(); tabs.SelectedIndex = 2;
                        Descendants(form).OfType<Button>().Single(b => b.Text == "Read load targets").PerformClick();
                        Descendants(form).OfType<RadioButton>().Single(b => b.Text == "Baseline: keep current CO").Checked = true;
                        var task = form.RunManagedAsync("Baseline"); var clock = Stopwatch.StartNew();
                        while (!runner.Started && !task.IsCompleted && clock.ElapsedMilliseconds < 5000) { Application.DoEvents(); Thread.Sleep(5); }
                        Assert.True(runner.Started); Assert.True(owns);
                        Assert.True(Descendants(form).OfType<Button>().Single(b => b.Text == "Stop owned test").Enabled);
                        form.Close();
                        while (!task.IsCompleted && clock.ElapsedMilliseconds < 8000) { Application.DoEvents(); Thread.Sleep(5); }
                        Assert.True(task.IsCompleted); task.GetAwaiter().GetResult(); Assert.False(owns); Assert.False(session.IsRecording);
                    }
                    done.SetResult(true);
                } catch (Exception ex) { done.SetException(ex); }
            }) { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA); thread.Start(); await done.Task;
        }
        private static IEnumerable<Control> Descendants(Control root)
        {
            foreach (Control child in root.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; }
        }
    }
}
