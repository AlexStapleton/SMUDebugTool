using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Newtonsoft.Json;

namespace ZenStatesDebugTool
{
    public interface INativeCoView
    {
        bool IsDisposed { get; }
        CancellationToken ClosingToken { get; }
        void Activate();
        void Show(Form owner);
        void NoteHardwareWrite();
        void NoteChange(TuningResult result);
        void InvalidateCapture();
    }
    public sealed class GuidedCoOptimizerForm : Form, INativeCoView
    {
        private readonly GuidedCoServices services;
        private readonly NativeCoSession records = new NativeCoSession();
        private readonly CancellationTokenSource closing = new CancellationTokenSource();
        private readonly System.Windows.Forms.Timer render = new System.Windows.Forms.Timer { Interval = 200 };
        private NativeVidMonitor monitor;
        private GuidedCoSession session;
        private WorkloadRecipe recipe;
        private IReadOnlyList<WorkloadTarget> targets = new WorkloadTarget[0];
        private bool initializing, stopped;
        private string notice;
        private DateTime noticeUntil;
        private readonly Label phase, status, readiness, metrics;
        private readonly TextBox executable, report, profileName;
        private readonly ComboBox starting, coreTarget;
        private readonly NumericUpDown bound, budget, stopTemperature, testSeconds, repeats;
        private readonly FlowLayoutPanel setup, advanced;
        private readonly Button primary, pause, restore, keep, saveProfile, choose, retry, newSession, finishBalance, validate, coreCheck;
        private readonly DataGridView cores;
        private readonly TabControl tabs;
        private GuidedCoPhase? displayedPhase;
        public CancellationToken ClosingToken => closing.Token;
        void INativeCoView.Show(Form owner) => Show(owner);
        public GuidedCoOptimizerForm(GuidedCoServices services)
        {
            this.services = services;
            Text = "CO optimizer — guided session"; Size = new Size(1120, 850); UiLayout.Configure(this, new Size(740, 580));
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(10) };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int row = 0; row < 6; row++) layout.RowStyles.Add(new RowStyle(row == 4 ? SizeType.Percent : SizeType.AutoSize, row == 4 ? 100 : 0));
            var description = new Label { Text = "Prepare → Reference → Balance under load → Validate fixed offsets → Decide. VID is a request; equal VID does not prove stability.", AutoSize = true, Dock = DockStyle.Top };
            layout.Controls.Add(description, 0, 0);
            var heading = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true };
            phase = new Label { Text = "Checking readiness…", AutoSize = true };
            readiness = new Label { AutoSize = true, Text = "Mapping native cores and checking the remembered workload. No setters or workload launch on opening." };
            heading.Controls.Add(phase); heading.Controls.Add(readiness); layout.Controls.Add(heading, 0, 1);
            setup = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true };
            choose = ActionButton("Choose Cinebench…", ChooseAsync);
            executable = new TextBox { Text = services.InitialExecutable ?? "", ReadOnly = true, Width = 420 };
            starting = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 210 }; starting.Items.AddRange(new object[] { "Start from CO 0", "Improve current offsets" }); starting.SelectedIndex = 0;
            bound = Number(-999, -1, -40); budget = Number(1, 40, 20);
            setup.Controls.Add(choose); setup.Controls.Add(executable); setup.Controls.Add(starting);
            AddField(setup, "Negative CO bound", bound); AddField(setup, "Budget (minutes)", budget);
            var details = new Button { Text = "Test settings", AutoSize = true };
            details.Click += (s, e) => { advanced.Visible = !advanced.Visible; };
            setup.Controls.Add(details);
            setup.Controls.Add(new Label { Text = "The budget covers balancing. Reference and validation runs take additional time. Other tuning settings stay fixed.", AutoSize = true });
            var setupHolder = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            advanced = new FlowLayoutPanel { AutoSize = true, WrapContents = true, Visible = false };
            stopTemperature = Number(40, 95, 90); testSeconds = Number(10, 600, 60); repeats = Number(2, 5, 3);
            AddField(advanced, "Stop at CPU °C", stopTemperature); AddField(advanced, "Test seconds", testSeconds); AddField(advanced, "Scored repeats", repeats);
            advanced.Controls.Add(new Label { AutoSize = true, Text = "Software stop bounds, not stability advice. Native sampling: 100 ms; display: 200 ms. Broader correctness checks remain incomplete." });
            setupHolder.Controls.Add(setup); setupHolder.Controls.Add(advanced); layout.Controls.Add(setupHolder, 0, 2);
            var actions = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
            primary = ActionButton("Start reference", PrimaryAsync); primary.Name = "guidedPrimary";
            pause = ActionButton("Pause", () => { session.Pause(); return Task.CompletedTask; }); pause.Name = "guidedPause";
            finishBalance = ActionButton("Finish balancing & validate", async () => { session.Pause(true); await WaitIdleAsync(); if (session.Phase == GuidedCoPhase.ReadyToValidate) await session.ValidateAsync(); });
            validate = ActionButton("Validate current candidate", () => session.Phase == GuidedCoPhase.ReadyToValidate ? session.StartBalancingAsync() : session.ValidateAsync());
            restore = ActionButton("Stop & restore original", () => session.RestoreAsync());
            keep = ActionButton("Keep temporarily", () => session.KeepAsync());
            saveProfile = ActionButton("Save experimental profile", () => session.KeepAsync(profileName.Text.Trim()));
            retry = ActionButton("Retry readiness", InitializeAsync);
            newSession = ActionButton("New session", () => { session.Dispose(); records.Reset(); CreateSession(); recipe = null; return ValidateRecipeAsync(); });
            foreach (var button in new[] { primary, pause, finishBalance, validate, restore, keep, retry, newSession }) actions.Controls.Add(button);
            profileName = new TextBox { Text = "CO experiment " + DateTime.Now.ToString("yyyy-MM-dd HH-mm"), Width = 240, MaxLength = 80 };
            var profileActions = new FlowLayoutPanel { AutoSize = true, WrapContents = false }; profileActions.Controls.Add(saveProfile); profileActions.Controls.Add(profileName);
            actions.Controls.Add(profileActions); layout.Controls.Add(actions, 0, 3);
            tabs = new TabControl { Dock = DockStyle.Fill };
            var perCore = new TabPage("Per-core evidence"); cores = new DataGridView { Dock = DockStyle.Fill, ReadOnly = true, AutoGenerateColumns = false,
                AllowUserToAddRows = false, RowHeadersVisible = false, AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize };
            foreach (string name in new[] { "Core", "CCD", "ReferenceCO", "CurrentCO", "VIDVolts", "ReportedMHz", "Samples", "Eligibility" }) cores.Columns.Add(new DataGridViewTextBoxColumn {
                Name = name, HeaderText = name, DataPropertyName = name, AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, MinimumWidth = name == "Eligibility" ? 250 : 75,
                FillWeight = name == "Eligibility" ? 300 : 100, DefaultCellStyle = new DataGridViewCellStyle { WrapMode = DataGridViewTriState.True } });
            perCore.Controls.Add(cores);
            var comparison = new TabPage("Comparison and report"); report = new TextBox { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, WordWrap = true, ScrollBars = ScrollBars.Vertical }; comparison.Controls.Add(report);
            var coverage = new TabPage("Additional core coverage"); var coveragePanel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true };
            coveragePanel.Controls.Add(new Label { AutoSize = true, Text = "Core / SMT rendering coverage is separate from all-processor VID balancing. Correctness, idle and transition tests remain incomplete." });
            coreTarget = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 330 };
            coreCheck = ActionButton("Run selected core render", async () => {
                var target = coreTarget.SelectedItem as WorkloadTarget; if (target == null) throw new InvalidOperationException("No reviewed per-core target is available.");
                string path = executable.Text; int duration = (int)testSeconds.Value;
                var selected = await Task.Run(() => WorkloadRecipe.Cinebench2024(path, duration, Math.Max(600, duration + 120), target, target.SingleThread));
                await session.CoreCoverageAsync(selected);
            });
            coveragePanel.Controls.Add(coreTarget); coveragePanel.Controls.Add(coreCheck); coverage.Controls.Add(coveragePanel);
            tabs.TabPages.Add(perCore); tabs.TabPages.Add(comparison); tabs.TabPages.Add(coverage); layout.Controls.Add(tabs, 0, 4);
            var footer = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true };
            metrics = new Label { AutoSize = true }; status = new Label { AutoSize = true, Text = "Read-only preparation." };
            footer.Controls.Add(ActionButton("Save session report", SaveReportAsync)); footer.Controls.Add(metrics); footer.Controls.Add(status); layout.Controls.Add(footer, 0, 5); Controls.Add(layout);
            void Wrap() {
                int width = Math.Max(240, ClientSize.Width - 55);
                foreach (var label in new[] { description, readiness, status, metrics }) label.MaximumSize = new Size(width, 0);
                setup.MaximumSize = advanced.MaximumSize = new Size(width, 0);
                starting.Width = Math.Max(210, Math.Min(width - 20, TextRenderer.MeasureText("Improve current offsets", Font).Width + 35));
                profileName.Width = Math.Max(150, Math.Min(width - saveProfile.PreferredSize.Width - 45, TextRenderer.MeasureText("CO experiment 2026-11-14 20-30", Font).Width + 25));
                foreach (var number in new[] { bound, budget, stopTemperature, testSeconds, repeats }) number.Width = Math.Max(80, TextRenderer.MeasureText("-999", Font).Width + 26);
                foreach (DataGridViewColumn column in cores.Columns) {
                    int headerWord = UiText.T(column.Name).Split(' ').Max(word => TextRenderer.MeasureText(word, Font).Width) + 18;
                    int valueWidth = TextRenderer.MeasureText(column.Name == "VIDVolts" ? "Unavailable" : column.Name == "Eligibility" ? "Comparable peers" : column.Name.EndsWith("CO") ? "-999" : "1000", Font).Width + 18;
                    column.MinimumWidth = Math.Max(column.Name == "Eligibility" ? 220 : 50, Math.Max(headerWord, valueWidth));
                }
            }
            SizeChanged += (s, e) => Wrap(); FontChanged += (s, e) => Wrap(); Wrap();
            CreateSession(); render.Tick += (s, e) => Render(); render.Start();
            Shown += async (s, e) => await InitializeAsync();
            FormClosed += (s, e) => Stop(); Disposed += (s, e) => Stop(); UpdateActions();
        }
        private NumericUpDown Number(int min, int max, int value) => new NumericUpDown { Minimum = min, Maximum = max, Value = value, Width = 80 };
        private static void AddField(FlowLayoutPanel row, string text, Control value)
        { var field = new FlowLayoutPanel { AutoSize = true, WrapContents = false }; field.Controls.Add(new Label { Text = text, AutoSize = true }); field.Controls.Add(value); row.Controls.Add(field); }
        private Button ActionButton(string text, Func<Task> action)
        {
            var button = new Button { Text = text, AutoSize = true };
            bool running = false;
            button.Click += async (s, e) => {
                if (running) return; running = true;
                try { var pending = action(); UpdateActions(); await pending; }
                catch (Exception ex) { if (!IsDisposed) ShowNotice(ex.Message); }
                finally { running = false; if (!IsDisposed) UpdateActions(); }
            };
            return button;
        }
        private void CreateSession() => session = new GuidedCoSession(services, records, () => monitor?.Latest ?? new CoreVidReading[0], () => monitor?.Window ?? new CoreVidReading[0],
            () => monitor?.Error, () => monitor?.Clear(), closing.Token);
        public async Task InitializeAsync()
        {
            if (initializing || session.Busy || stopped) return; initializing = true; UpdateActions();
            try {
                if (monitor == null) { var source = await services.CreateSource(); if (stopped) return; monitor = new NativeVidMonitor(source, records); monitor.Start(100); }
                targets = await services.LoadTargets(); if (stopped) return;
                coreTarget.DataSource = targets.Where(t => t.Mask != 0).ToList();
                await ValidateRecipeAsync();
                if (!stopped) readiness.Text = "Native physical mapping ready; all-processor load selected automatically. Temperature/WHEA gates run before any setter.";
            } catch (Exception ex) { if (!stopped) { readiness.Text = "Readiness unavailable: " + ex.Message; status.Text = "No offsets were changed."; } }
            finally { initializing = false; if (!stopped) UpdateActions(); }
        }
        private async Task ChooseAsync()
        {
            using (var dialog = new OpenFileDialog { Filter = "Cinebench 2024.1.0 executable|*.exe" }) if (dialog.ShowDialog(this) == DialogResult.OK) {
                executable.Text = dialog.FileName; string selected = dialog.FileName;
                await Task.Run(() => WorkloadPreferences.Save(selected)); await ValidateRecipeAsync();
            }
        }
        private async Task ValidateRecipeAsync()
        {
            recipe = null;
            if (string.IsNullOrWhiteSpace(executable.Text)) { status.Text = "Choose Cinebench once to finish preparation."; return; }
            string path = executable.Text; int duration = (int)testSeconds.Value;
            try { var checkedRecipe = await Task.Run(() => WorkloadRecipe.Cinebench2024(path, duration, Math.Max(600, duration + 120), new WorkloadTarget("All processors", 0), false)); if (!stopped && executable.Text == path) { recipe = checkedRecipe; status.Text = "Workload version checked. The negative limit bounds the experiment; it is not a stable target."; } }
            catch (Exception ex) { if (!stopped) status.Text = "Workload not ready: " + ex.Message; }
        }
        private async Task PrimaryAsync()
        {
            if (session.Phase == GuidedCoPhase.Ready) {
                await ValidateRecipeAsync(); if (recipe == null) return;
                advanced.Visible = false;
                await session.StartReferenceAsync(new GuidedCoOptions(recipe, (int)bound.Value, TimeSpan.FromMinutes((double)budget.Value), stopTemperature.Value, starting.SelectedIndex == 0, (int)repeats.Value));
            } else if (session.Phase == GuidedCoPhase.Paused) await session.ResumeAsync();
            else if (session.Phase == GuidedCoPhase.ReadyToBalance) await session.StartBalancingAsync();
            else if (session.Phase == GuidedCoPhase.ReadyToValidate) await session.ValidateAsync();
            else if (session.Phase == GuidedCoPhase.Recovery || session.Phase == GuidedCoPhase.Faulted) await session.ReadCurrentAsync();
        }
        private async Task WaitIdleAsync() { while (session.Busy && !stopped) await Task.Delay(50); }
        private void ShowNotice(string value) { notice = value; noticeUntil = DateTime.UtcNow.AddSeconds(6); status.Text = value; }
        private void UpdateActions()
        {
            if (stopped || session == null) return;
            bool idle = !session.Busy && !initializing, pending = services.Recovery.IsPending;
            var current = session.Phase;
            bool prepared = monitor != null && monitor.Error == null && recipe != null;
            primary.Text = UiText.T(current == GuidedCoPhase.Ready ? (starting.SelectedIndex == 0 ? "Save offsets & measure CO 0 reference" : "Save offsets & measure current reference") : current == GuidedCoPhase.Paused ? "Resume session" : current == GuidedCoPhase.ReadyToBalance ? "Start supervised balancing" : current == GuidedCoPhase.ReadyToValidate ? "Validate fixed candidate" : "Read current state");
            primary.Visible = current != GuidedCoPhase.Reference && current != GuidedCoPhase.Balancing && current != GuidedCoPhase.Validating && current != GuidedCoPhase.Results && current != GuidedCoPhase.Finished;
            primary.Enabled = idle && (current != GuidedCoPhase.Ready || prepared) && (current != GuidedCoPhase.Faulted && current != GuidedCoPhase.Recovery || pending);
            pause.Visible = session.Busy; pause.Enabled = session.Busy;
            finishBalance.Visible = current == GuidedCoPhase.Balancing; finishBalance.Enabled = session.Busy;
            validate.Visible = session.HasReference && idle && (current == GuidedCoPhase.ReadyToBalance || current == GuidedCoPhase.ReadyToValidate || current == GuidedCoPhase.Results); validate.Enabled = validate.Visible;
            validate.Text = UiText.T(current == GuidedCoPhase.ReadyToValidate ? "Continue balancing" : "Validate current candidate");
            restore.Visible = pending; restore.Enabled = !initializing;
            keep.Visible = pending && idle; keep.Enabled = idle && (current != GuidedCoPhase.Recovery && current != GuidedCoPhase.Faulted || session.RecoveryReviewed);
            saveProfile.Visible = current == GuidedCoPhase.Results && idle; saveProfile.Enabled = saveProfile.Visible;
            profileName.Visible = saveProfile.Visible;
            retry.Visible = idle && !pending && (!prepared || current == GuidedCoPhase.Faulted);
            newSession.Visible = idle && !pending && (current == GuidedCoPhase.Finished || current == GuidedCoPhase.Faulted);
            setup.Enabled = advanced.Enabled = idle && current == GuidedCoPhase.Ready;
            choose.Enabled = setup.Enabled;
            setup.Visible = current == GuidedCoPhase.Ready;
            if (!setup.Visible) advanced.Visible = false;
            coreCheck.Enabled = idle && session.HasReference && coreTarget.Items.Count > 0 && (current == GuidedCoPhase.Results || current == GuidedCoPhase.ReadyToValidate);
            AcceptButton = primary.Visible && primary.Enabled ? primary : saveProfile.Visible && saveProfile.Enabled ? saveProfile : null;
        }
        private void Render()
        {
            if (stopped) return; UpdateActions();
            if (displayedPhase != session.Phase) {
                if (session.Phase == GuidedCoPhase.Results) tabs.SelectedIndex = 1;
                else if (session.Phase == GuidedCoPhase.Balancing || session.Phase == GuidedCoPhase.Recovery) tabs.SelectedIndex = 0;
                displayedPhase = session.Phase;
            }
            phase.Text = PhaseName(session.Phase) + " · reference " + session.ReferenceRuns + "/" + session.Repeats + " · candidate " + session.CandidateRuns + "/" + session.Repeats + " · steps " + session.Steps;
            if (DateTime.UtcNow < noticeUntil) status.Text = notice;
            else if (session.Phase != GuidedCoPhase.Ready) status.Text = session.Status;
            session.RefreshEvidence(); var evidence = session.Evidence;
            var live = monitor?.Latest ?? new CoreVidReading[0];
            if (evidence != null) cores.DataSource = evidence.Cores.Select(c => new { Core = c.Slot, CCD = c.Ccd < 0 ? "Unknown" : c.Ccd.ToString(),
                ReferenceCO = session.ReferenceOffsets != null && session.ReferenceOffsets.TryGetValue(c.Slot, out int reference) ? reference.ToString() : "Not captured", CurrentCO = c.Co?.ToString() ?? "Unavailable",
                VIDVolts = (c.Vid ?? live.LastOrDefault(r => r.Slot == c.Slot && StopwatchFresh(r))?.VidVolts)?.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture) ?? "Unavailable",
                ReportedMHz = (c.Mhz ?? live.LastOrDefault(r => r.Slot == c.Slot && StopwatchFresh(r))?.ReportedMhz)?.ToString("0", System.Globalization.CultureInfo.InvariantCulture) ?? "Unavailable",
                Samples = c.Samples, Eligibility = c.Reason + (c.Vid.HasValue ? " · window mean" : " · live VID; mean unavailable") }).ToList();
            else if (session.CurrentOffsets != null) cores.DataSource = session.CurrentOffsets.Select(c => new { Core = c.Key, CCD = live.LastOrDefault(r => r.Slot == c.Key)?.Ccd.ToString() ?? "Unknown", ReferenceCO = "Not captured", CurrentCO = c.Value.ToString(),
                VIDVolts = live.LastOrDefault(r => r.Slot == c.Key && StopwatchFresh(r))?.VidVolts?.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture) ?? "Unavailable",
                ReportedMHz = live.LastOrDefault(r => r.Slot == c.Key && StopwatchFresh(r))?.ReportedMhz?.ToString("0", System.Globalization.CultureInfo.InvariantCulture) ?? "Unavailable", Samples = 0, Eligibility = "Current CO readback captured; reference context requires review" }).ToList();
            else if (monitor != null) cores.DataSource = live.Select(r => new { Core = r.Slot, CCD = r.Ccd.ToString(), ReferenceCO = "Not captured", CurrentCO = "Not captured", VIDVolts = StopwatchFresh(r) ? r.VidVolts?.ToString("0.0000", System.Globalization.CultureInfo.InvariantCulture) ?? "Unavailable" : "Unavailable",
                ReportedMHz = StopwatchFresh(r) ? r.ReportedMhz?.ToString("0", System.Globalization.CultureInfo.InvariantCulture) ?? "Unavailable" : "Unavailable", Samples = 1, Eligibility = r.Issue ?? "Read-only readiness; no reference yet" }).ToList();
            metrics.Text = monitor == null ? "Native source unavailable." : "Requested 100 ms; achieved " + monitor.LastCadenceMs.ToString("0.0") + " ms; CPU " + (session.Temperature?.ToString("0.0") ?? "—") + " °C.";
            report.Text = session.Comparison + "\r\n" + session.Coverage + "\r\n\r\n" + session.Status + "\r\n\r\nOnly CO endpoints and editor context are checked. Unobserved external changes cannot be excluded. Profiles are CO-only experiments; startup selection is unchanged.";
        }
        private static string PhaseName(GuidedCoPhase phase)
        {
            switch (phase) {
                case GuidedCoPhase.Ready: return UiText.T("Prepare"); case GuidedCoPhase.Reference: return UiText.T("Measuring reference");
                case GuidedCoPhase.ReadyToBalance: return UiText.T("Reference ready"); case GuidedCoPhase.Balancing: return UiText.T("Balancing under load");
                case GuidedCoPhase.Paused: return UiText.T("Paused"); case GuidedCoPhase.ReadyToValidate: return UiText.T("Ready for validation");
                case GuidedCoPhase.Validating: return UiText.T("Validating fixed offsets"); case GuidedCoPhase.Results: return UiText.T("Review candidate");
                case GuidedCoPhase.Finished: return UiText.T("Decision complete"); default: return UiText.T("Review recovery");
            }
        }
        private static bool StopwatchFresh(CoreVidReading value)
        { long now = System.Diagnostics.Stopwatch.GetTimestamp(); return value.Timestamp <= now && (now - value.Timestamp) / (double)System.Diagnostics.Stopwatch.Frequency <= .5; }
        private async Task SaveReportAsync()
        {
            using (var dialog = new SaveFileDialog { Filter = "Session report (*.json)|*.json", FileName = "guided-co-session.json" }) if (dialog.ShowDialog(this) == DialogResult.OK) {
                string path = dialog.FileName;
                var summary = new { Phase = session.Phase.ToString(), session.Status, session.Steps, session.ReferenceRuns, session.CandidateRuns, session.Comparison, session.Coverage };
                var latest = monitor?.Latest ?? new CoreVidReading[0];
                await Task.Run(() => {
                    string json = JsonConvert.SerializeObject(new { SchemaVersion = 2, Summary = summary,
                        Runs = Newtonsoft.Json.Linq.JToken.Parse(records.Export()), TrialJournal = Newtonsoft.Json.Linq.JToken.Parse(services.Journal.Export()), NativeSource = latest }, Formatting.Indented);
                    if (Encoding.UTF8.GetByteCount(json) > 16 * 1024 * 1024) throw new InvalidOperationException("Guided report exceeds 16 MiB.");
                    string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                    try { File.WriteAllText(temporary, json, new UTF8Encoding(false)); if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path); }
                    finally { if (File.Exists(temporary)) File.Delete(temporary); }
                });
                if (!stopped) ShowNotice("Report saved. It cannot apply or resume tuning.");
            }
        }
        public void NoteHardwareWrite() { session.NoteHardwareWrite(); }
        public void NoteChange(TuningResult result) { if (result.Rows.Any(r => r.IsAccepted)) session.NoteHardwareWrite(); }
        public void InvalidateCapture() => monitor?.Clear();
        private void Stop()
        {
            if (stopped) return; stopped = true; closing.Cancel(); session.Dispose(); monitor?.Dispose(); render.Stop(); render.Dispose(); records.Interrupt();
        }
    }
}
