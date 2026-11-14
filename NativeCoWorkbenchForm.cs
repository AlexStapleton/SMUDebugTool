using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ZenStatesDebugTool
{
    public sealed class NativeCoWorkbenchForm : Form
    {
        private readonly NativeCoSession session;
        private readonly Func<Task<Func<CancellationToken, IReadOnlyList<CoreVidReading>>>> createSource;
        private readonly Func<Task<NativeCoContext>> captureContext;
        private readonly Func<CoStepProposal, Task<TuningResult>> applyStep;
        private NativeVidMonitor monitor;
        private readonly System.Windows.Forms.Timer render = new System.Windows.Forms.Timer { Interval = 200 };
        private readonly DataGridView cores, runs;
        private readonly TextBox workload, report;
        private readonly NumericUpDown minimum, interval, score;
        private readonly Button startMonitor, baseline, candidate, end, step, save, reset;
        private readonly Label status, cadence;
        private bool busy;
        private bool stopped;
        public NativeCoWorkbenchForm(NativeCoSession session, Func<Task<Func<CancellationToken, IReadOnlyList<CoreVidReading>>>> createSource,
            Func<Task<NativeCoContext>> captureContext, Func<CoStepProposal, Task<TuningResult>> applyStep)
        {
            this.session = session; this.createSource = createSource; this.captureContext = captureContext; this.applyStep = applyStep;
            Text = "Native CO workbench"; Size = new Size(1050, 780); UiLayout.Configure(this, new Size(720, 560));
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(8) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            foreach (int row in Enumerable.Range(0, 4)) layout.RowStyles.Add(new RowStyle(row == 2 ? SizeType.Percent : SizeType.AutoSize, row == 2 ? 100 : 0));
            var explanation = new Label { AutoSize = true, Dock = DockStyle.Top, Text = "Per-core VID requests and effective clocks. VID is not measured voltage. A CO trial needs separate performance and stability tests." };
            layout.Controls.Add(explanation, 0, 0);
            var controls = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true };
            startMonitor = Button("Start monitoring", async () => await StartMonitorAsync());
            interval = new NumericUpDown { Minimum = 20, Maximum = 1000, Value = 100, Increment = 20, Width = 65 };
            minimum = new NumericUpDown { Name = "minimumCo", Minimum = -999, Maximum = -1, Value = -40, Width = 65 };
            step = Button("Trial next eligible core (-1 CO)", async () => await ApplyOneStepAsync());
            controls.Controls.Add(startMonitor); controls.Controls.Add(new Label { Text = "Requested interval (ms)", AutoSize = true }); controls.Controls.Add(interval);
            controls.Controls.Add(new Label { Text = "User CO lower bound", AutoSize = true }); controls.Controls.Add(minimum); controls.Controls.Add(step); layout.Controls.Add(controls, 0, 1);
            cadence = new Label { AutoSize = true, Text = "No native samples yet." }; controls.Controls.Add(cadence);
            cores = new DataGridView { Name = "nativeCoreReadings", Dock = DockStyle.Fill, ReadOnly = true, AutoGenerateColumns = false, AllowUserToAddRows = false, RowHeadersVisible = false,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize };
            foreach (string column in new[] { "Core", "CCD", "VIDVolts", "ReportedMHz", "EffectiveMHz", "AgeMs", "Evidence" })
                cores.Columns.Add(new DataGridViewTextBoxColumn { Name = column, HeaderText = column, DataPropertyName = column, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                    MinimumWidth = column == "Evidence" ? 220 : 70, FillWeight = column == "Evidence" ? 260 : 100, DefaultCellStyle = new DataGridViewCellStyle { WrapMode = DataGridViewTriState.True } });
            var tabs = new TabControl { Dock = DockStyle.Fill };
            var liveTab = new TabPage("Native readings"); liveTab.Controls.Add(cores);
            var runsTab = new TabPage("Runs and comparison");
            var runLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            runLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            runLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); runLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 35)); runLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 65));
            runsTab.Controls.Add(runLayout); tabs.TabPages.Add(liveTab); tabs.TabPages.Add(runsTab); layout.Controls.Add(tabs, 0, 2);
            var record = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true };
            workload = new TextBox { Text = "Cinebench 2024 CPU multi-core", Width = 280, MaxLength = 256 };
            score = new NumericUpDown { Minimum = 0, Maximum = 10000000, DecimalPlaces = 2, Width = 100 };
            baseline = Button("Start baseline", async () => await BeginAsync("Baseline")); candidate = Button("Start candidate", async () => await BeginAsync("Candidate")); end = Button("End run", async () => await EndAsync());
            record.Controls.Add(workload); record.Controls.Add(baseline); record.Controls.Add(candidate); record.Controls.Add(new Label { Text = "Score (0 = unset)", AutoSize = true }); record.Controls.Add(score); record.Controls.Add(end);
            runLayout.Controls.Add(record, 0, 0);
            runs = new DataGridView { Name = "nativeRuns", Dock = DockStyle.Fill, ReadOnly = true, AutoGenerateColumns = false, AllowUserToAddRows = false,
                RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize };
            foreach (string column in new[] { "Role", "State", "Score", "Context" })
                runs.Columns.Add(new DataGridViewTextBoxColumn { Name = column, HeaderText = column, DataPropertyName = column, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                    MinimumWidth = column == "Context" ? 200 : 80, FillWeight = column == "Context" ? 250 : 100, DefaultCellStyle = new DataGridViewCellStyle { WrapMode = DataGridViewTriState.True } });
            runs.SelectionChanged += (s, e) => ShowRun(); runLayout.Controls.Add(runs, 0, 1);
            report = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, WordWrap = true };
            runLayout.Controls.Add(report, 0, 2);
            var footer = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true };
            save = Button("Save session report", async () => await SaveAsync());
            reset = Button("New session (clear runs)", () => { session.Reset(); report.Clear(); RefreshRuns(); status.Text = "Started an empty session. Previous unsaved runs were cleared."; return Task.CompletedTask; });
            var evidence = Button("Show native read evidence", () => {
                tabs.SelectedTab = runsTab;
                var rows = monitor?.Latest;
                report.Text = "Native source: MSR C0010293 VID [21:14]; APERF C00000E8. Each core is read sequentially.\r\n" +
                    (rows == null || rows.Count == 0 ? "No native samples available." : string.Join("\r\n", rows.Select(r => "Core " + r.Slot + ": " + r.Identity + "; raw status " + (r.RawStatus?.ToString("X16") ?? "unavailable") + "; timestamp " + r.Timestamp + "; " + (r.Issue ?? "read succeeded"))));
                return Task.CompletedTask;
            });
            status = new Label { AutoSize = true, Text = "Start monitoring, then record a baseline before running Cinebench." };
            footer.Controls.Add(save); footer.Controls.Add(reset); footer.Controls.Add(evidence); footer.Controls.Add(status); layout.Controls.Add(footer, 0, 3); Controls.Add(layout);
            void Wrap() { explanation.MaximumSize = status.MaximumSize = cadence.MaximumSize = new Size(Math.Max(300, ClientSize.Width - 40), 0); }
            SizeChanged += (s, e) => Wrap(); Wrap();
            void FitColumns() {
                foreach (DataGridView grid in new[] { cores, runs })
                    foreach (DataGridViewColumn column in grid.Columns)
                        column.MinimumWidth = Math.Max(column.Name == "Evidence" || column.Name == "Context" ? 220 : 65,
                            TextRenderer.MeasureText(column.Name == "VIDVolts" || column.Name.Contains("MHz") ? "Unavailable" : column.HeaderText, Font).Width + 20);
            }
            FontChanged += (s, e) => FitColumns(); FitColumns();
            render.Tick += (s, e) => Render(); render.Start();
            FormClosed += (s, e) => StopCapture();
            Disposed += (s, e) => StopCapture();
            RefreshRuns(); SetBusy(false);
        }
        private static Button Button(string text, Func<Task> action)
        {
            var button = new Button { Text = text, AutoSize = true };
            button.Click += async (s, e) => await action(); return button;
        }
        private void StopCapture()
        {
            if (stopped) return; stopped = true;
            render.Stop(); render.Dispose(); monitor?.Dispose(); session.Interrupt();
        }
        private void SetBusy(bool value)
        {
            busy = value; startMonitor.Enabled = !value && monitor == null; interval.Enabled = monitor == null && !value;
            minimum.Enabled = !value; workload.Enabled = !value && !session.IsRecording;
            baseline.Enabled = candidate.Enabled = !value && monitor != null && !session.IsRecording;
            end.Enabled = !value && session.IsRecording; step.Enabled = !value && monitor != null; save.Enabled = !value;
            reset.Enabled = !value && !session.IsRecording;
        }
        public async Task StartMonitorAsync()
        {
            if (busy || monitor != null || IsDisposed) return;
            SetBusy(true); status.Text = "Validating native VID source and physical core mapping…";
            try {
                var source = await createSource(); if (IsDisposed || Disposing) return;
                monitor = new NativeVidMonitor(source, session); monitor.Start((int)interval.Value);
                status.Text = "Monitoring native requests. Wait for steady load before applying a one-step trial.";
            } catch (Exception ex) { if (!IsDisposed) status.Text = ex.Message; }
            finally { if (!IsDisposed) SetBusy(false); }
        }
        public async Task BeginAsync(string role)
        {
            if (busy || monitor == null || session.IsRecording || IsDisposed) return;
            SetBusy(true); status.Text = "Capturing starting CO state; start Cinebench after recording begins…";
            try {
                string recipe = workload.Text; var context = await captureContext(); if (IsDisposed) return;
                session.Begin(role, recipe, context, DateTimeOffset.UtcNow); score.Value = 0; RefreshRuns(); status.Text = role + " recording. Start your benchmark now.";
            } catch (Exception ex) { if (!IsDisposed) status.Text = ex.Message; }
            finally { if (!IsDisposed) SetBusy(false); }
        }
        public async Task EndAsync()
        {
            if (busy || !session.IsRecording || IsDisposed) return;
            SetBusy(true);
            try {
                decimal? result = score.Value > 0 ? (decimal?)score.Value : null;
                session.MarkEnd(DateTimeOffset.UtcNow); var context = await captureContext(); if (IsDisposed) return;
                session.End(context, result); RefreshRuns(); status.Text = "Run recorded. Select a candidate to compare with its preceding baseline.";
            } catch (Exception ex) { session.Interrupt(); if (!IsDisposed) status.Text = ex.Message; }
            finally { if (!IsDisposed) SetBusy(false); }
        }
        public async Task ApplyOneStepAsync()
        {
            if (busy || monitor == null || IsDisposed) return;
            SetBusy(true);
            try {
                var context = await captureContext(); if (IsDisposed) return;
                var latest = monitor.Latest;
                if (monitor.Error != null || context.ExpectedCores <= 0 || context.ObservedCo.Count != context.ExpectedCores || latest.Count != context.ExpectedCores || latest.Any(r => r.Issue != null || !context.ObservedCo.ContainsKey(r.Slot))) throw new InvalidOperationException("Fresh, complete native readings and CO readback are required.");
                var proposal = CoStepPlanner.Suggest(monitor.Window, context.ObservedCo, (int)minimum.Value, Stopwatch.GetTimestamp());
                if (proposal == null) { status.Text = "No qualified trial: need steady busy samples and comparable peers in the same CCD. No commands sent."; return; }
                var result = await applyStep(proposal); monitor.Clear();
                if (!IsDisposed) status.Text = TuningResultFormatter.FriendlySummary(result) + ". Core " + proposal.Slot + ": " + proposal.Previous + " → " + proposal.Requested + " CO. Run a separate candidate test to assess the change.";
            } catch (Exception ex) { monitor?.Clear(); if (!IsDisposed) status.Text = ex.Message; }
            finally { if (!IsDisposed) SetBusy(false); }
        }
        public void NoteChange(TuningResult result)
        {
            if (result.Rows.Any(r => r.IsAccepted)) { session.NoteChange(DateTimeOffset.UtcNow.ToString("O") + " " + result.Label); monitor?.Clear(); }
        }
        public void NoteHardwareWrite()
        {
            session.NoteChange(DateTimeOffset.UtcNow.ToString("O") + " App hardware-write operation requested."); monitor?.Clear();
        }
        public void InvalidateCapture() => monitor?.Clear();
        private void RefreshRuns()
        {
            runs.DataSource = session.Trials.ToList();
            if (runs.Rows.Count > 0) { runs.ClearSelection(); runs.Rows[runs.Rows.Count - 1].Selected = true; runs.CurrentCell = runs.Rows[runs.Rows.Count - 1].Cells[0]; }
        }
        private void ShowRun()
        {
            var selected = runs.CurrentRow?.DataBoundItem as NativeCoTrial; if (selected == null) return;
            var previous = session.Trials.LastOrDefault(t => t.Role == "Baseline" && t.State == "Completed" && t.StartedUtc < selected.StartedUtc);
            report.Text = selected.Role == "Candidate" ? NativeCoSession.Compare(previous, selected) :
                selected.Display + "\r\n" + selected.Workload + "\r\nStart CO reads: " + selected.Start.ObservedCo.Count + ". Editor values are requests; only CO endpoints are checked. End the run to finalize native summaries.";
        }
        private void Render()
        {
            if (busy || monitor == null || IsDisposed) return;
            long now = Stopwatch.GetTimestamp();
            cadence.Text = $"Requested {interval.Value} ms; successful batch interval {monitor.LastCadenceMs:0.0} ms; read pass {monitor.LastDurationMs:0.0} ms. UI: 200 ms.";
            cores.DataSource = monitor.Latest.Select(r => new {
                Core = r.Slot, CCD = r.Ccd, VIDVolts = r.VidVolts?.ToString("0.0000", CultureInfo.InvariantCulture) ?? "Unavailable",
                ReportedMHz = r.ReportedMhz?.ToString("0", CultureInfo.InvariantCulture) ?? "Unavailable", EffectiveMHz = r.EffectiveMhz?.ToString("0", CultureInfo.InvariantCulture) ?? "Unavailable",
                AgeMs = ((now - r.Timestamp) * 1000.0 / Stopwatch.Frequency).ToString("0", CultureInfo.InvariantCulture),
                Evidence = (now - r.Timestamp) / (double)Stopwatch.Frequency > 0.5 ? "Stale; unavailable for tuning" : r.Issue ?? (r.Busy ? "Busy clock heuristic; VID is a request" : "Low activity; excluded from tuning window")
            }).ToList();
            if (monitor.Error != null) { status.Text = "Capture stopped: " + monitor.Error; step.Enabled = baseline.Enabled = candidate.Enabled = false; }
        }
        private async Task SaveAsync()
        {
            if (busy || IsDisposed) return;
            using (var dialog = new SaveFileDialog { Filter = "Session report (*.json)|*.json", FileName = "native-co-session.json" }) {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                SetBusy(true);
                try {
                    string path = dialog.FileName, json = session.Export();
                    await Task.Run(() => {
                        if (Encoding.UTF8.GetByteCount(json) > 8 * 1024 * 1024) throw new InvalidOperationException("Session report exceeds 8 MiB.");
                        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                        try { File.WriteAllText(temporary, json, new UTF8Encoding(false)); if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path); }
                        finally { if (File.Exists(temporary)) File.Delete(temporary); }
                    });
                    if (!IsDisposed) status.Text = "Session report saved. It cannot apply or resume tuning.";
                } catch (Exception ex) { if (!IsDisposed) status.Text = ex.Message; }
                finally { if (!IsDisposed) SetBusy(false); }
            }
        }
    }
}
