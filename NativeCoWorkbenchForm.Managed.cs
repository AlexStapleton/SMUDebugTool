using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ZenStatesDebugTool
{
    public sealed class ManagedCoServices
    {
        public CoRecoveryStore Recovery { get; set; }
        public Func<CoBatchPlan, CancellationToken, Task<TuningResult>> Apply { get; set; }
        public Func<Task<IReadOnlyList<WorkloadTarget>>> Targets { get; set; }
        public Action<bool> SetHardwareOwner { get; set; }
        public string InitialExecutable { get; set; }
        public Action<string> RememberExecutable { get; set; }
        public IWorkloadRunner Runner { get; set; } = new OwnedWorkloadProcess();
    }
    public sealed partial class NativeCoWorkbenchForm
    {
        private ManagedCoServices managedServices;
        private ManagedCoRun managedRun;
        private CancellationTokenSource managedCancellation;
        private bool recoveryNeedsReview;
        private TextBox executable, managedOutput;
        private ComboBox loadTarget;
        private NumericUpDown testDuration, testTimeout;
        private RadioButton zeroBaseline;
        private Button managedBaseline, managedCandidate, managedStop, restoreOriginal, keepCurrent, refreshTargets, browse, recordedTrial;
        private Label managedStatus;
        private Control[] managedSetup;
        private Button toggleSetup;
        private bool ManagedAssistanceBlocked => recoveryNeedsReview || managedRun?.AssistanceBlocked == true;
        private void InitializeManaged(TabControl tabs, ManagedCoServices services)
        {
            managedServices = services; if (services == null) return;
            recoveryNeedsReview = services.Recovery.IsPending;
            managedRun = new ManagedCoRun(session, services.Runner, captureContext, services.Apply, services.Recovery);
            var page = new TabPage("Managed tests") { AutoScroll = true };
            var panel = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, RowCount = 7, Padding = new Padding(6) };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int row = 0; row < 7; row++) panel.RowStyles.Add(new RowStyle(row == 6 ? SizeType.Percent : SizeType.AutoSize, row == 6 ? 100 : 0));
            var help = new Label { AutoSize = true, Dock = DockStyle.Top, Text = "Choose Cinebench 2024.1.0 once. The program launches and records each test. Core-focused renders provide workload coverage; they do not certify stability." };
            var helpRow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
            toggleSetup = Button("Hide test setup", () => { SetSetupVisible(!managedSetup[0].Visible); return Task.CompletedTask; });
            helpRow.Controls.Add(help); helpRow.Controls.Add(toggleSetup); panel.Controls.Add(helpRow, 0, 0);
            FlowLayoutPanel Row(int index) { var row = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true }; panel.Controls.Add(row, 0, index); return row; }
            var fileRow = Row(1); executable = new TextBox { Width = 500, ReadOnly = true, Text = services.InitialExecutable ?? "" };
            browse = Button("Choose Cinebench…", async () => {
                using (var dialog = new OpenFileDialog { Filter = "Cinebench executable (*.exe)|*.exe", CheckFileExists = true })
                    if (dialog.ShowDialog(this) == DialogResult.OK) {
                        string selected = dialog.FileName; executable.Text = selected;
                        try { await Task.Run(() => services.RememberExecutable?.Invoke(selected)); }
                        catch (Exception ex) { if (!IsDisposed) managedStatus.Text = "Path selected; preference could not be saved: " + ex.Message; }
                    }
            });
            fileRow.Controls.Add(browse); fileRow.Controls.Add(executable);
            var options = Row(2);
            testDuration = new NumericUpDown { Minimum = 10, Maximum = 3600, Value = 60, Width = 90 };
            testTimeout = new NumericUpDown { Minimum = 40, Maximum = 7200, Value = 600, Width = 90 };
            options.Controls.Add(new Label { Text = "Minimum test seconds", AutoSize = true }); options.Controls.Add(testDuration);
            options.Controls.Add(new Label { Text = "Timeout seconds", AutoSize = true }); options.Controls.Add(testTimeout);
            loadTarget = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 320 };
            refreshTargets = Button("Read load targets", async () => {
                if (busy || monitor == null) return; SetBusy(true);
                try { var targets = await services.Targets(); if (!IsDisposed) { loadTarget.DataSource = targets.ToList(); managedStatus.Text = "Load targets mapped from physical/APIC/SMT identities."; } }
                catch (Exception ex) { if (!IsDisposed) managedStatus.Text = ex.Message; }
                finally { if (!IsDisposed) SetBusy(false); }
            });
            options.Controls.Add(loadTarget); options.Controls.Add(refreshTargets);
            var baselineRow = Row(3);
            zeroBaseline = new RadioButton { Text = "Baseline: set every core to CO 0", Checked = true, AutoSize = true };
            baselineRow.Controls.Add(zeroBaseline); baselineRow.Controls.Add(new RadioButton { Text = "Baseline: keep current CO", AutoSize = true });
            managedSetup = new Control[] { fileRow, options, baselineRow };
            var actions = Row(4);
            managedBaseline = Button("Run baseline", async () => await RunManagedAsync("Baseline"));
            managedCandidate = Button("Run candidate", async () => await RunManagedAsync("Candidate"));
            managedStop = Button("Stop owned test", () => { managedCancellation?.Cancel(); managedStatus.Text = "Stopping the owned test…"; return Task.CompletedTask; });
            recordedTrial = Button("Apply recorded -1 CO trial", async () => await ApplyRecordedAsync());
            restoreOriginal = Button("Restore original CO", async () => await RecoverAsync(false));
            keepCurrent = Button("Keep current CO / clear recovery", async () => await RecoverAsync(true));
            foreach (var button in new[] { managedBaseline, recordedTrial, managedCandidate, managedStop, restoreOriginal, keepCurrent }) actions.Controls.Add(button);
            managedStatus = new Label { AutoSize = true, Dock = DockStyle.Top, Text = recoveryNeedsReview ? "Pending CO recovery: restore original offsets or explicitly keep current values before another experiment. Startup apply is paused." : "Start native monitoring, choose the executable and read load targets. No workload starts on opening." };
            panel.Controls.Add(managedStatus, 0, 5);
            managedOutput = new TextBox { Dock = DockStyle.Fill, MinimumSize = new Size(0, 150), ReadOnly = true, Multiline = true, ScrollBars = ScrollBars.Vertical, WordWrap = true };
            panel.Controls.Add(managedOutput, 0, 6); page.Controls.Add(panel); tabs.TabPages.Add(page);
            void WrapManaged() { help.MaximumSize = managedStatus.MaximumSize = new Size(Math.Max(200, ClientSize.Width - 70), 0); executable.Width = Math.Max(180, Math.Min(500, ClientSize.Width - 100)); }
            SizeChanged += (s, e) => WrapManaged(); WrapManaged();
            page.SizeChanged += (s, e) => panel.MinimumSize = new Size(0, page.ClientSize.Height);
        }
        private void SetSetupVisible(bool visible)
        {
            foreach (var control in managedSetup) control.Visible = visible;
            toggleSetup.Text = UiText.T(visible ? "Hide test setup" : "Show test setup");
        }
        private void UpdateManagedButtons()
        {
            if (managedServices == null) return;
            bool idle = !busy && !session.IsRecording;
            managedBaseline.Enabled = managedCandidate.Enabled = idle && monitor != null && !recoveryNeedsReview;
            browse.Enabled = testDuration.Enabled = testTimeout.Enabled = loadTarget.Enabled = zeroBaseline.Parent.Enabled = !busy;
            refreshTargets.Enabled = idle && monitor != null;
            managedStop.Enabled = managedCancellation != null;
            restoreOriginal.Enabled = keepCurrent.Enabled = idle && managedServices.Recovery.IsPending;
            recordedTrial.Enabled = idle && !ManagedAssistanceBlocked && managedRun.SuggestedStep != null;
        }
        private string ManagedHealth()
        {
            if (stopped || monitor == null) return "Native capture is closed or unavailable.";
            if (monitor.Error != null) return "Native capture failed: " + monitor.Error;
            var rows = monitor.Latest; long now = Stopwatch.GetTimestamp();
            if (rows.Count == 0 || rows.Any(r => !r.VidVolts.HasValue || (now - r.Timestamp) / (double)Stopwatch.Frequency > 2)) return "Essential native readings are missing or older than two seconds.";
            return null;
        }
        public async Task RunManagedAsync(string role)
        {
            if (managedServices == null || busy || monitor == null || session.IsRecording || IsDisposed) return;
            if (recoveryNeedsReview) { managedStatus.Text = "Resolve pending recovery before starting another experiment."; return; }
            SetBusy(true); managedCancellation = new CancellationTokenSource();
            try {
                managedServices.SetHardwareOwner(true); UpdateManagedButtons();
                var target = loadTarget.SelectedItem as WorkloadTarget; if (target == null) throw new InvalidOperationException("Read and choose a mapped load target first.");
                string path = executable.Text; int seconds = (int)testDuration.Value, timeout = (int)testTimeout.Value;
                managedStatus.Text = "Checking Cinebench identity and native capture…";
                var recipe = await Task.Run(() => WorkloadRecipe.Cinebench2024(path, seconds, timeout, target, target.SingleThread));
                managedCancellation.Token.ThrowIfCancellationRequested(); if (IsDisposed) return;
                SetSetupVisible(false);
                managedStatus.Text = "Running " + role.ToLowerInvariant() + ". Stop ends only this program's test. No CO changes are allowed during the scored run.";
                int bound = (int)minimum.Value;
                var result = await managedRun.RunAsync(role, role == "Baseline" && zeroBaseline.Checked, recipe, ManagedHealth, managedCancellation.Token,
                    (context, start) => CoStepPlanner.Suggest(monitor.Window.Where(r => r.Timestamp >= start), context.ObservedCo, bound, Stopwatch.GetTimestamp()));
                if (IsDisposed) return;
                RefreshRuns(); managedOutput.Text = result.Outcome + ": " + result.Detail + "\r\nScore: " + (result.Score?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unavailable") + "\r\n" + result.Output;
                managedStatus.Text = result.Outcome + ": " + result.Detail + (managedRun.AssistanceBlocked ? " Assisted writes paused until a successful managed test or recovery." : " Select the run in Runs and comparison.");
                if (managedServices.Recovery.IsPending) managedStatus.Text += " Original CO recovery is pending; startup apply stays paused until Restore or Keep current.";
                var proposal = managedRun.SuggestedStep;
                if (proposal != null) managedOutput.Text += "\r\nRecorded trial available: Core " + proposal.Slot + ", CO " + proposal.Previous + " → " + proposal.Requested + ". " + proposal.Reason;
            } catch (Exception ex) { if (!IsDisposed) managedStatus.Text = ex.Message; }
            finally { managedServices.SetHardwareOwner(false); managedCancellation.Dispose(); managedCancellation = null; if (!IsDisposed) SetBusy(false); }
        }
        private async Task RecoverAsync(bool keep)
        {
            if (busy || session.IsRecording) return; SetBusy(true);
            try {
                var before = await captureContext(); CoRecoveryStore.RequireComplete(before);
                workbenchClosing.Token.ThrowIfCancellationRequested();
                var record = await Task.Run(() => managedServices.Recovery.Read()); CoRecoveryStore.Match(record, before);
                if (!keep) {
                    var result = await managedServices.Apply(new CoBatchPlan(before.ObservedCo, record.OriginalCo), workbenchClosing.Token);
                    if (result.Rows.Count != record.OriginalCo.Count || result.Rows.Any(r => r.Outcome != TuningOutcome.Verified)) throw new InvalidOperationException("Restore incomplete; recovery retained. Inspect per-core results.");
                    before = await captureContext();
                    if (record.OriginalCo.Any(c => !before.ObservedCo.TryGetValue(c.Key, out int value) || value != c.Value)) throw new InvalidOperationException("Restore readback changed; recovery retained.");
                }
                workbenchClosing.Token.ThrowIfCancellationRequested();
                var complete = before; await Task.Run(() => managedServices.Recovery.Clear(complete));
                recoveryNeedsReview = false; managedRun.Recovered(); managedStatus.Text = keep ? "Kept current CO and cleared recovery. No profile was saved or promoted." : "Original CO restored and verified; recovery cleared.";
            } catch (Exception ex) { if (!IsDisposed) managedStatus.Text = ex.Message; }
            finally { if (!IsDisposed) SetBusy(false); }
        }
        private async Task ApplyRecordedAsync()
        {
            if (busy || ManagedAssistanceBlocked) return;
            var proposal = managedRun.SuggestedStep; var reference = managedRun.SuggestionContext;
            if (proposal == null || reference == null) return;
            SetBusy(true);
            try {
                if (proposal.Requested < minimum.Value) throw new InvalidOperationException("Recorded trial exceeds the current lower bound.");
                var current = await captureContext();
                workbenchClosing.Token.ThrowIfCancellationRequested();
                if (NativeCoSession.Context(reference, current) != "CO endpoints unchanged") throw new InvalidOperationException("Context changed after the test. Run another managed test before applying a recorded trial.");
                var result = await applyStep(proposal);
                if (result.Rows.Count != 1 || result.Rows[0].Outcome != TuningOutcome.Verified) { managedRun.BlockAssistance(); throw new InvalidOperationException("Recorded CO trial did not verify; assisted writes paused."); }
                managedStatus.Text = "Core " + proposal.Slot + ": CO " + proposal.Previous + " → " + proposal.Requested + " verified. Run a candidate with the same workload target/duration.";
            } catch (Exception ex) { managedRun.BlockAssistance(); if (!IsDisposed) managedStatus.Text = ex.Message; }
            finally { managedRun.DiscardSuggestion(); if (!IsDisposed) SetBusy(false); }
        }
    }
}
