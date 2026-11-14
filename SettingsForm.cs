using Microsoft.VisualBasic.Devices;
using Microsoft.Win32;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Management;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using ZenStates.Core;
using ZenStates.Core.Hardware;
using ZenStates.Core.Hardware.Smu;
using ZenStatesDebugTool.Profiles;
using ZenStatesDebugTool.Properties;
using Application = System.Windows.Forms.Application;
using static ZenStates.Core.Cpu;
using System.Diagnostics;
using ZenStates.Core.Drivers;

namespace ZenStatesDebugTool
{
    public partial class SettingsForm : Form
    {
        //private static readonly int Threads = Convert.ToInt32(Environment.GetEnvironmentVariable("NUMBER_OF_PROCESSORS"));
        private BackgroundWorker backgroundWorker1;
        private readonly NUMAUtil _numaUtil;
        private readonly Cpu cpu;
        private readonly HardwareService hardware;
        private readonly DriverSession driverSession;
        private DecodeContext decodeContext = DecodeContext.None;
        private IReadOnlyDictionary<uint, string> smuNameMap;
        List<SmuAddressSet> matches = new List<SmuAddressSet>();
        private readonly Mailbox testMailbox = new Mailbox();
        private readonly string wmiAMDACPI = "AMD_ACPI";
        private readonly string wmiScope = "root\\wmi";
        private readonly string profilesPath;
        private readonly string defaultsPath;
        private ProfileManager profileManager;
        private readonly ProfileApplier profileApplier = new ProfileApplier();
        private ComboBox comboBoxProfiles;
        private ComboBox comboBoxStartupProfile;
        private NumericUpDown numericUpDownPpt, numericUpDownTdc, numericUpDownEdc, numericUpDownPboScalar;
        private ManagementObject classInstance;
        private string instanceName;
        private ManagementBaseObject pack;
        private const string profilesFolderName = "profiles";
        private const string filename = "co_profile.txt";
        private readonly Dictionary<int, NumericUpDown> coControls = new Dictionary<int, NumericUpDown>();
        // Cache for the designer's per-core "checkBoxN" controls so we don't do a recursive
        // Controls.Find tree-walk for every core on each access.
        private readonly Dictionary<int, CheckBox> coreCheckBoxes = new Dictionary<int, CheckBox>();

        public SettingsForm(Cpu initializedCpu, DriverSession session)
        {
            InitializeComponent();
            cpu = initializedCpu;
            driverSession = session;
            UiLayout.Configure(this, new Size(720, 550));
            _numaUtil = new NUMAUtil();
            textBoxResult.Text = $@"Detected NUMA nodes. ({_numaUtil.HighestNumaNode + 1})" + textBoxResult.Text;

            try
            {
                profilesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, profilesFolderName);
                defaultsPath = Path.Combine(profilesPath, filename);

                profileManager = new ProfileManager(profilesPath);
                profileManager.EnsureDirectory();
                profileManager.MigrateLegacyIfNeeded();

                hardware = new HardwareService(cpu);

                InitForm();
                InitApplyResultsUi();
                InitTuningEdits();
                ShowLastStartupApplyResult();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, Resources.Error);
                // Startup workers have not been launched until OnShown.
                cpu?.Dispose();
                driverSession?.Dispose();
                shutdownComplete = true;
                Shown += (sender, args) => Close();
            }
        }

        private void ExitApplication() { Close(); }

        private bool shutdownComplete;
        private bool shuttingDown;
        private bool hardwareOperation;
        private readonly HardwareOperationRunner operationRunner = new HardwareOperationRunner();
        private TuningResultsPanel applyResultsPanel;

        protected override async void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (shutdownComplete) return;
            e.Cancel = true;
            if (shuttingDown) return;
            shuttingDown = true;
            tabControl1.Enabled = false;
            foreach (Form owned in OwnedForms) owned.Close();
            SetStatusText("Waiting for the current hardware command to finish...");
            try { await Hardware.ShutdownAsync(() => { cpu?.Dispose(); driverSession?.Dispose(); }); }
            catch (Exception ex) { PrependResult("Driver shutdown: " + ex.Message + Environment.NewLine); }
            shutdownComplete = true;
            Close();
        }

        private void InitApplyResultsUi()
        {
            applyResultsPanel = TuningResultsPanel.Attach(textBoxResult);
        }

        private async Task<T> RunHardwareAsync<T>(Func<T> action)
        {
            if (hardwareOperation || (backgroundWorker1?.IsBusy ?? false))
                throw new InvalidOperationException("Another hardware operation is running.");
            Hardware.ThrowIfClosing();
            hardwareOperation = true;
            bool wasEnabled = tabControl1.Enabled;
            tabControl1.Enabled = false;
            try { return await operationRunner.RunAsync(() => { Hardware.ThrowIfClosing(); return action(); }); }
            finally { hardwareOperation = false; if (!shuttingDown && !IsDisposed) tabControl1.Enabled = wasEnabled; }
        }

        private Task ApplyTuningAsync(Profile profile) => ApplyTuningAsync(profile, "Apply selected settings");
        private async Task ApplyTuningAsync(Profile profile, string scope)
        {
            if (!_hardwareReady || shuttingDown) { SetStatusText("Hardware is still loading or closing."); return; }
            if (hardwareOperation || (backgroundWorker1?.IsBusy ?? false)) { SetStatusText("Another hardware operation is running."); return; }
            try
            {
                profile = TuningSnapshots.Profile(profile);
                if (TuningService.RequestsFromProfile(profile).Count == 0) { SetStatusText(UiText.T("No pending changes to apply.")); return; }
                string validation = TuningService.ValidateProfileInput(profile) ?? hardware.Topology.Validate(profile);
                if (validation != null) { HandleError(validation); return; }
                using (var preview = new TuningPreviewForm(profile, editControls.Session, scope))
                {
                    if (preview.ShowDialog(this) != DialogResult.OK) { SetStatusText(UiText.T("Apply cancelled; no commands sent.")); return; }
                    profile = preview.ApprovedProfile;
                }
                SetStatusText("Applying settings...");
                var result = await RunHardwareAsync(() => profileApplier.Apply(profile, cpu, Hardware.ClosingToken));
                if (shuttingDown || IsDisposed) return;
                result.Label = UiText.T(scope) + (string.IsNullOrEmpty(profile.Name) ? "" : " — " + profile.Name);
                editControls.RecordResult(result);
                applyResultsPanel.ShowResult(result);
                SetStatusText(TuningResultFormatter.FriendlySummary(result));
                PrependResult(TuningResultFormatter.Report(result) + Environment.NewLine);
                // Retain only tiers whose own write was accepted, even in a partially failed batch.
                _lastAppliedCurveShaper = CurveShaperCodec.MergeAccepted(_lastAppliedCurveShaper, result);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!shuttingDown) HandleError(ex.Message); }
        }

        private void InitTestMailbox(uint msgAddr, uint rspAddr, uint argAddr)
        {
            testMailbox.SMU_ADDR_MSG = msgAddr;
            testMailbox.SMU_ADDR_RSP = rspAddr;
            testMailbox.SMU_ADDR_ARG = argAddr;
            ResetSmuAddresses();
        }

        private void InitTestMailbox(Mailbox mailbox)
        {
            testMailbox.SMU_ADDR_MSG = mailbox.SMU_ADDR_MSG;
            testMailbox.SMU_ADDR_RSP = mailbox.SMU_ADDR_RSP;
            testMailbox.SMU_ADDR_ARG = mailbox.SMU_ADDR_ARG;
            ResetSmuAddresses();
        }

        private void ResetSmuAddresses()
        {
            textBoxCMDAddress.Text = $"0x{Convert.ToString(testMailbox.SMU_ADDR_MSG, 16).ToUpper()}";
            textBoxRSPAddress.Text = $"0x{Convert.ToString(testMailbox.SMU_ADDR_RSP, 16).ToUpper()}";
            textBoxARGAddress.Text = $"0x{Convert.ToString(testMailbox.SMU_ADDR_ARG, 16).ToUpper()}";
        }

        private void DisplaySystemInfo()
        {
            try
            {
                cpuInfoLabel.Text = cpu.systemInfo.CpuName;
                modelInfoLabel.Text = $"{cpu.systemInfo.Model:X2}";
                packageTypeInfoLabel.Text = cpu.info.packageType.ToString();
                mbVendorInfoLabel.Text = cpu.systemInfo.MbVendor;
                mbModelInfoLabel.Text = cpu.systemInfo.MbName;
                biosInfoLabel.Text = cpu.systemInfo.BiosVersion;
                smuInfoLabel.Text = cpu.systemInfo.SmuVersion.ToString();
                firmwareInfoLabel.Text = $"{cpu.systemInfo.PatchLevel:X8}";
                cpuIdLabel.Text = $"{cpu.systemInfo.CpuId} ({cpu.info.codeName})";
                smuNameMap = SmuCommandNames.Build(SmuDecodeAdapter.ReadMessages(cpu.smu));
                configInfoLabel.Text = $"{cpu.info.topology.ccds} CCD / {cpu.info.topology.ccxs} CCX / {cpu.systemInfo.PhysicalCoreCount} physical cores";
            }
            catch { }
        }

        private void InitForm()
        {
            /*if (cpu.Status == Utils.LibStatus.PARTIALLY_OK)
            {
                if (cpu.LastError != null)
                    MessageBox.Show(cpu.LastError.Message, Resources.Error);
            }*/

            if (cpu.smu.Version == 0)
            {
                MessageBox.Show("Error getting SMU version!\n" +
                    "Default SMU addresses are not responding to commands.",
                    "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            if (!Directory.Exists(profilesPath))
            {
                MessageBox.Show("Profiles directory does not exist, created one for you.");
                Directory.CreateDirectory(profilesPath);
            }

            InitTestMailbox(cpu.smu.Rsmu);
            decodeContext = SmuDecodeAdapter.GetDecodeContext(cpu.info);
            DisplaySystemInfo();
            InitPStateEditor();

            PopulateMailboxesList(comboBoxMailboxSelect.Items);

            // UI layout is built synchronously here (it only touches in-memory topology),
            // but the slow SMU command round-trips and WMI enumeration are deferred to a
            // background load (see OnShown/StartHardwareLoad) so the window appears promptly
            // instead of freezing while every per-core margin etc. is read on the UI thread.
            InitCoreControl();
            InitPboLayout();
            BuildFrequencyTab();
            InitStartupProfileUi();

            // Curve Shaper only exists on CPUs whose RSMU mailbox defines the messages
            // (Zen 4/5 desktop; e.g. 9950X3D yes, 5800X3D no). On unsupported CPUs every
            // Curve Shaper command would fail with UNKNOWN_CMD, so drop the tab and skip
            // all Curve Shaper reads/writes instead of surfacing errors.
            _curveShaperSupported = cpu.smu.Rsmu.SMU_MSG_SetCurveShaperMargin != 0
                                 && cpu.smu.Rsmu.SMU_MSG_GetCurveShaperMargin != 0;
            // Removed before BuildViewMenu below so the View menu doesn't offer it either.
            if (!_curveShaperSupported)
                tabControl1.TabPages.Remove(tabPageCS);

            comboBoxMailboxSelect.SelectedIndex = 0;

            ToolTip toolTip = new ToolTip();
            // Checked state mirrors IsProchotEnabled(): checked = PROCHOT (thermal
            // throttling) ON. Uncheck + Apply to DISABLE throttling. The old tooltip read
            // as "checking disables throttling", which is backwards.
            toolTip.SetToolTip(checkBoxPROCHOT,
                "Checked = PROCHOT (thermal throttling) enabled. Uncheck and click Apply to " +
                "disable temperature throttling - useful on extreme cooling, but risks overheating.");

            // FMax (SetBoostLimitFrequencyAllCores) isn't available on CPUs before Zen 4
            // (e.g. 5800X3D) - neither mailbox defines the message, so the command fails with
            // UNKNOWN_CMD. Disable the input and Apply button so a click can't silently no-op;
            // the profile-apply path skips it the same way (ProfileApplier.ApplyFmax).
            _fmaxSupported = ProfileApplier.IsFmaxSupported(cpu);
            if (!_fmaxSupported)
            {
                numericUpDownFmax.Enabled = false;
                buttonApplyFMax.Enabled = false;
                toolTip.SetToolTip(numericUpDownFmax, "FMax is not supported on this CPU.");
                toolTip.SetToolTip(buttonApplyFMax, "FMax is not supported on this CPU.");
            }

            // Built last, after every tab (including the dynamic Freq (OC) tab) exists, so the
            // controller's canonical list and View menu cover them all.
            BuildViewMenu();

            SetStatusText($"{cpu.info.codeName}. Loading hardware values...");
        }

        private MenuStrip menuStrip;
        private TabVisibilityController tabController;

        // Adds the top menu bar with a View menu for showing/hiding tabs and choosing the
        // startup tab, then applies the persisted preferences.
        private void BuildViewMenu()
        {
            menuStrip = new MenuStrip();
            var viewMenu = new ToolStripMenuItem("View");
            menuStrip.Items.Add(viewMenu);
            var tools = new ToolStripMenuItem("Tools");
            tools.DropDownItems.Add("Driver diagnostics and maintenance", null, (sender, args) =>
            {
                var environment = new WindowsDriverMaintenanceEnvironment { LocalHandleOpen = !Hardware.IsClosing };
                var maintenance = new DriverMaintenanceForm(environment, async () =>
                {
                    shuttingDown = true;
                    tabControl1.Enabled = false;
                    foreach (Form owned in OwnedForms) if (!(owned is DriverMaintenanceForm)) owned.Close();
                    try { await Hardware.ShutdownAsync(() => { cpu.Dispose(); driverSession?.Dispose(); }); }
                    finally { shutdownComplete = true; }
                    environment.LocalHandleOpen = false;
                    SetStatusText("Hardware session ended. Close the application after maintenance.");
                });
                maintenance.Show(this);
                maintenance.FormClosed += (s, e) => { if (Hardware.IsClosing) Close(); };
            });
            tools.DropDownItems.Add("Core mapping", null, (sender, args) => ShowResultForm("Core mapping",
                "Source: " + hardware.Topology.Source + "\r\nSignature: " + hardware.Topology.Signature + "\r\n" + hardware.Topology.Error + "\r\n" +
                string.Join("\r\n", hardware.Topology.Targets.Select(t => $"Core {t.Slot}: {t.Identity}; logical {t.LogicalIndex}; mask 0x{t.Mask:X8}"))));
            menuStrip.Items.Add(tools);
            var languageMenu = new ToolStripMenuItem("Language");
            foreach (var language in new[] { Tuple.Create("English", "en"), Tuple.Create("简体中文", "zh-CN") })
                languageMenu.DropDownItems.Add(language.Item1, null, (sender, args) =>
                {
                    UiText.SetLanguage(language.Item2);
                    var manager = new UiSettingsManager(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json"));
                    var preferences = manager.Load(); preferences.Language = language.Item2;
                    try { manager.Save(preferences); } catch (Exception ex) { SetStatusText(ex.Message); }
                    foreach (Form form in Application.OpenForms) UiText.Apply(form);
                    editControls?.Refresh();
                    applyResultsPanel?.RefreshLanguage();
                });
            menuStrip.Items.Add(languageMenu);

            var settingsManager = new UiSettingsManager(
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.json"));
            tabController = new TabVisibilityController(tabControl1, settingsManager)
            {
                OnStatus = SetStatusText
            };
            tabController.RegisterTabsFromControl();
            tabController.BuildViewMenu(viewMenu);

            // Added after splitContainer1/statusStrip1 (per the designer convention of adding
            // the Fill control first), so the menu docks at the top and the rest reflows below.
            Controls.Add(menuStrip);
            MainMenuStrip = menuStrip;

            tabController.ApplyInitial();
        }

        // The window is shown before the deferred SMU refreshes; once it's visible (handle
        // created, so Invoke works) we kick off a one-time background load that gathers all
        // the slow hardware/WMI values off the UI thread and applies them in one marshal.
        private bool _hardwareLoaded;
        // True once the background load has applied values. The CO/CS Apply handlers check
        // this so a click during the brief load can't write default (0) margins to the CPU.
        private volatile bool _hardwareReady;
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_hardwareLoaded || cpu == null || shutdownComplete || shuttingDown) return;
            _hardwareLoaded = true;
            StartHardwareLoad();
        }

        private void StartHardwareLoad()
        {
            var loader = new Thread(() =>
            {
                try
                {
                    bool manualOc = Hardware.Locked(() => cpu.smu.Rsmu.SMU_MSG_IsOverclockable == 0
                        || cpu.GetOverclockingCaps()[OcCapabilities.OverclockingEnabled]);
                    var pstateItems = GatherPStates();
                    UiInvoke(() => ApplyPStateList(pstateItems));

                    // Reads only (cpu/WMI), no control access — safe off the UI thread.
                    // The SMU/CPU reads are serialized against other threads via
                    // Hardware.Sync; the UI apply happens later (UiInvoke) outside the lock.
                    Dictionary<int, int> coMargins;
                    uint fmax;
                    uint[] csValues;
                    double? bclk;
                    bool? prochot;
                    lock (Hardware.Sync)
                    {
                        Hardware.ThrowIfClosing();
                        coMargins = GatherCoMargins();
                        // 0 on unsupported CPUs (skipped below); GetFMax has no working
                        // read-back there anyway.
                        fmax = _fmaxSupported ? cpu.GetFMax() : 0;
                        // null on unsupported CPUs; ApplyCurveShaperValues ignores null.
                        csValues = _curveShaperSupported ? cpu.GetAllCurveShaperMargins() : null;
                        bclk = cpu.GetBclk();
                        prochot = cpu.IsProchotEnabled();
                    }
                    List<object> wmiItems = GatherWmiCommands();

                    // Apply everything to the controls in a single UI-thread marshal.
                    UiInvoke(() =>
                    {
                        try
                        {
                            _manualOcSupported = manualOc;
                            var ocTab = tabControl1.TabPages.Cast<TabPage>().FirstOrDefault(t => t.Name == "tabPageFreqOC");
                            if (!manualOc && ocTab != null)
                            {
                                ocTab.Controls.Clear();
                                BuildManualOcLockedTab(ocTab);
                            }
                            ApplyCoMargins(coMargins);
                            if (_fmaxSupported)
                                ApplyFMax(fmax);
                            ApplyCurveShaperValues(CurveShaperCodec.ResolveDisplay(csValues, _lastAppliedCurveShaper), false, !CurveShaperCodec.IsAllZero(csValues));
                            ApplyBclk(bclk);
                            ApplyProchot(prochot);
                            ApplyWmiCommands(wmiItems);
                            _hardwareReady = true;
                            editControls?.Refresh();
                            SetStatusText(hardware.Topology.Error == null ? $"{cpu.info.codeName}. Ready." : $"Ready. Per-core controls unavailable: {hardware.Topology.Error}");
                        }
                        catch (Exception ex)
                        {
                            _hardwareReady = true; // unblock the gated controls regardless
                            editControls?.Refresh();
                            SetStatusText($"Hardware load applied with errors: {ex.Message}");
                        }
                    });
                }
                catch (Exception ex)
                {
                    UiInvoke(() => SetStatusText($"Hardware load error: {ex.Message}"));
                }
            })
            {
                IsBackground = true,
                Name = "HardwareLoad"
            };
            loader.Start();
        }

        // Marshals an action onto the UI thread, tolerating a form that's closing.
        private void UiInvoke(MethodInvoker action)
        {
            if (!IsHandleCreated || IsDisposed || shuttingDown) return;
            try { BeginInvoke(action); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private void ApplyFMax(uint fmax)
        {
            if (editControls != null) {
                if (fmax != 0) editControls.Observe(new TuningRequest { Kind = TuningKind.Fmax, Value = fmax }, "CPU report; unverified");
                return;
            }
            numericUpDownFmax.Value =
                Math.Max(numericUpDownFmax.Minimum, Math.Min(numericUpDownFmax.Maximum, fmax));
        }

        // Writes a read-back FMax into the box only when it's usable. GetFMax() ignores the
        // SMU status and returns 0 on CPUs without a working read-back, so writing it blindly
        // would wipe the user's entry (e.g. when applying/refreshing CO, which also rereads
        // FMax). The clamp keeps an out-of-range read from throwing on the control.
        private void SetFmaxFromReadback(uint hwFmax)
        {
            if (hwFmax == 0) return;
            if (editControls != null) { editControls.Observe(new TuningRequest { Kind = TuningKind.Fmax, Value = hwFmax }, "CPU report; unverified"); return; }
            numericUpDownFmax.Value =
                Math.Max(numericUpDownFmax.Minimum, Math.Min(numericUpDownFmax.Maximum, hwFmax));
        }

        private void ApplyBclk(double? bclk)
        {
            labelBCLK.Text = bclk + " MHz";
            numericUpDownBclk.Text = $"{bclk}";
        }

        private void ApplyProchot(bool? prochotEnabled)
        {
            checkBoxPROCHOT.Checked = prochotEnabled ?? false;
        }

        private void PopulateMailboxesList(ComboBox.ObjectCollection l)
        {
            l.Clear();
            l.Add(new MailboxListItem("RSMU", cpu.smu.Rsmu));
            l.Add(new MailboxListItem("MP1", cpu.smu.Mp1Smu));
            l.Add(new MailboxListItem("HSMP", cpu.smu.Hsmp));
        }

        private void AddMailboxToList(string label, SmuAddressSet addressSet)
        {
            comboBoxMailboxSelect.Items.Add(new MailboxListItem(label, addressSet));
        }

        private void InitCoreControl()
        {
            uint cores = (uint)GetPhysicalCoreCount();
            //var performanceOfCores = cpu.info.topology.performanceOfCore;
            uint coresPerGroup = 8;

            for (uint i = 0; i < cores; i++)
            {
                uint mapIndex = i / coresPerGroup;
                uint coreInGroup = i % coresPerGroup;
                //bool isDisabled = ((~cpu.info.topology.coreDisableMap[mapIndex] >> (int)coreInGroup) & 1) == 0;

                if (IsCoreEnabled((int)i))
                {
                    try
                    {
                        CheckBox control = GetCoreCheckBox((int)i);
                        if (control != null)
                        {
                            control.Enabled = true;
                            control.Checked = true;

                            var target = hardware.Topology.Find((int)i);
                            control.Tag = hardware.Topology.Targets.Where(t => t.Ccd == target.Ccd)
                                .OrderBy(t => t.LogicalIndex).TakeWhile(t => t.Slot != target.Slot).Count().ToString(CultureInfo.InvariantCulture);
                        }
                    }
                    catch (Exception e)
                    {
                        Debug.WriteLine($"Error initializing core {i}: {e}");
                    }
                }
            }

            checkBoxSMT.Checked = cpu.systemInfo.SMT;
        }

        // The 5 Curve Shaper tiers (min, low, med, high, max), each [0]=low [1]=med [2]=high
        // designer control. Built once so the read/apply/save/load paths can loop over the
        // grid instead of repeating 15 hand-written lines (which were easy to get out of sync).
        private NumericUpDown[][] _csGrid;
        private NumericUpDown[][] CsGrid => _csGrid ?? (_csGrid = new[]
        {
            new[] { cs_min_low,  cs_min_med,  cs_min_high  },
            new[] { cs_low_low,  cs_low_med,  cs_low_high  },
            new[] { cs_med_low,  cs_med_med,  cs_med_high  },
            new[] { cs_high_low, cs_high_med, cs_high_high },
            new[] { cs_max_low,  cs_max_med,  cs_max_high  },
        });
        private static readonly string[] CsTierNames = { "min", "low", "medium", "high", "max" };

        // Curve Shaper margins applied in this session, packed like GetAllCurveShaperMargins
        // (low/med/high in bits 8/16/24 per tier). GetAllCurveShaperMargins returns all
        // zeros on CPUs that don't report Curve Shaper back, so Refresh falls back to these
        // instead of wiping the grid to 0.
        private uint[] _lastAppliedCurveShaper;

        // Whether this CPU's RSMU mailbox defines the Curve Shaper messages (Zen 4/5).
        // When false the Curve Shaper tab is removed and every read/write is skipped.
        private bool _curveShaperSupported;

        // Whether SetFMax works on this CPU (Zen 4+). When false the FMax input/Apply
        // button are disabled and the read-back is skipped.
        private bool _fmaxSupported;

        // Apply-only half (UI thread); the read is done by the caller so the startup path
        // can gather it on a background thread. Each tier word packs low/med/high in bits
        // 8/16/24.
        private void ApplyCurveShaperValues(uint[] csValues, bool showStatus = false, bool cpuReported = false)
        {
            if (csValues == null || csValues.Length < 5)
                return;

            for (int tier = 0; tier < 5; tier++)
            {
                CurveShaperCodec.Unpack(csValues[tier], out int low, out int med, out int high);
                if (editControls != null) {
                    if (cpuReported || !CurveShaperCodec.IsAllZero(csValues))
                        editControls.Observe(new TuningRequest { Kind = TuningKind.CurveShaper, Index = tier, Value = low, Medium = med, High = high },
                            cpuReported ? "CPU report; unverified" : "Session value; unverified", cpuReported);
                    continue;
                }
                CsGrid[tier][0].Value = low;
                CsGrid[tier][1].Value = med;
                CsGrid[tier][2].Value = high;
            }

            if (showStatus)
                SetStatusText("Curve Shaper margins refreshed.");
        }

        // Background-safe: reads CO margins for enabled cores into a dictionary, no control
        // access. Returns an empty dictionary when CO isn't supported.
        private Dictionary<int, int> GatherCoMargins()
        {
            var result = new Dictionary<int, int>();
            if ((cpu.smu.Rsmu.SMU_MSG_SetDldoPsmMargin == 0 && cpu.smu.Mp1Smu.SMU_MSG_SetDldoPsmMargin == 0))
                return result;

            int cores = GetPhysicalCoreCount();
            var reader = new ZenStatesTuningTransport(cpu);
            for (int i = 0; i < cores; i++)
            {
                Hardware.ThrowIfClosing();
                if (!IsCoreEnabled(i))
                    continue;
                var reply = reader.Read(new TuningRequest { Kind = TuningKind.CurveOptimizer, Index = i });
                if (reply?.Status == 1 && reply.Direct && reply.Value.HasValue) result[i] = (int)reply.Value.Value;
            }
            return result;
        }

        // UI thread: enable the CO spinners for enabled cores and apply the gathered values.
        private void ApplyCoMargins(Dictionary<int, int> margins)
        {
            if ((cpu.smu.Rsmu.SMU_MSG_SetDldoPsmMargin == 0 && cpu.smu.Mp1Smu.SMU_MSG_SetDldoPsmMargin == 0))
                return;

            int cores = GetPhysicalCoreCount();
            for (int i = 0; i < cores; i++)
            {
                if (!IsCoreEnabled(i))
                    continue;
                NumericUpDown control = GetCOControl(i);
                if (control == null)
                    continue;
                control.Enabled = true;
                if (margins.TryGetValue(i, out int value))
                {
                    if (editControls != null) editControls.Observe(new TuningRequest { Kind = TuningKind.CurveOptimizer, Index = i, Value = value }, "CPU readback");
                    else control.Value = Math.Max(control.Minimum, Math.Min(control.Maximum, value));
                }
            }
        }

        // Reads the startup-profile state (Task Scheduler / file IO; no SMU reads) into the
        // checkbox + dropdown. The controls themselves live in the right-hand profile panel
        // (built by BuildProfilePanel), so this only populates them.
        // While true, the checkbox/dropdown handlers do NOT touch the scheduled task. Set
        // during programmatic population (startup and the "Re-read from CPU" refresh) so the
        // registered startup profile only changes in response to real user interaction.
        private bool _suppressStartupTaskUpdates;

        private void InitStartupProfileUi()
        {
            _suppressStartupTaskUpdates = true;
            try
            {
                checkBoxApplyCOStartup.Checked = StartupTaskService.Exists();

                if (comboBoxStartupProfile == null) return;

                comboBoxStartupProfile.Items.Clear();
                foreach (var n in profileManager.List())
                    comboBoxStartupProfile.Items.Add(n);
                string startupName = StartupTaskService.GetProfileName();
                if (startupName != null && comboBoxStartupProfile.Items.Contains(startupName))
                    comboBoxStartupProfile.SelectedItem = startupName;
                else if (comboBoxStartupProfile.Items.Count > 0)
                    comboBoxStartupProfile.SelectedIndex = 0;
            }
            finally
            {
                _suppressStartupTaskUpdates = false;
            }
        }

        private void InitPboLayout()
        {
            BuildCoActionBar();
            BuildCcdBlocks();
            BuildProfilePanel();
        }

        private void BuildCoActionBar()
        {
            // The per-column +/- buttons and the Apply/Refresh stack (both built in
            // BuildCcdBlocks) replace the old horizontal action bar, so hide this row.
            flowLayoutPanelCcdActions.Controls.Clear();
            flowLayoutPanelCcdActions.Visible = false;
        }

        private Button MakeBarButton(string text, EventHandler onClick)
        {
            var btn = new Button
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 0, 4, 0),
                Padding = new Padding(6, 0, 6, 0),
                Text = text,
                UseVisualStyleBackColor = true
            };
            btn.Click += onClick;
            return btn;
        }

        // Profile management + PBO limits live in the right-hand panel (above a shrunk log),
        // so the narrow PBO tab keeps its clean core-grid layout and nothing clips.
        private void BuildProfilePanel()
        {
            var grid = new TableLayoutPanel
            {
                ColumnCount = 2,
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0),
                Padding = new Padding(3, 3, 3, 3)
            };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));

            comboBoxProfiles = new ComboBox
            {
                // Editable: type a new name to save, or pick an existing profile to load.
                DropDownStyle = ComboBoxStyle.DropDown,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 2, 0, 4)
            };
            comboBoxProfiles.SelectedIndexChanged += ComboBoxProfiles_SelectedIndexChanged;

            var buttonRow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 0, 8)
            };
            buttonRow.Controls.Add(MakeBarButton("Save", ButtonSaveProfile_Click));
            buttonRow.Controls.Add(MakeBarButton("Load", ButtonLoadProfile_Click));
            applyChangesButton = MakeBarButton("Apply changes", ApplyChanges_Click);
            discardEditsButton = MakeBarButton("Discard edits", DiscardEdits_Click);
            buttonRow.Controls.Add(applyChangesButton);
            buttonRow.Controls.Add(MakeBarButton("Apply all settings", ButtonApplyProfile_Click));
            buttonRow.Controls.Add(discardEditsButton);
            buttonRow.Controls.Add(MakeBarButton("Delete", ButtonDeleteProfile_Click));

            numericUpDownPpt = MakeLimitBox(0, 1000);
            numericUpDownTdc = MakeLimitBox(0, 1000);
            numericUpDownEdc = MakeLimitBox(0, 1000);
            numericUpDownPboScalar = MakeLimitBox(0, 10);

            int r = 0;
            grid.Controls.Add(MakeFieldLabel("Profile"), 0, r);
            grid.Controls.Add(comboBoxProfiles, 1, r); r++;
            grid.Controls.Add(buttonRow, 0, r); grid.SetColumnSpan(buttonRow, 2); r++;
            pendingEditsLabel = MakeFieldLabel("0 pending settings. Yellow fields are local edits.");
            grid.Controls.Add(pendingEditsLabel, 0, r); grid.SetColumnSpan(pendingEditsLabel, 2); r++;
            grid.Controls.Add(MakeFieldLabel("PPT (W)"), 0, r);
            grid.Controls.Add(new PowerLimitEditor(TuningKind.Ppt, numericUpDownPpt, ApplyTuningAsync), 1, r); r++;
            grid.Controls.Add(MakeFieldLabel("TDC (A)"), 0, r);
            grid.Controls.Add(new PowerLimitEditor(TuningKind.Tdc, numericUpDownTdc, ApplyTuningAsync), 1, r); r++;
            grid.Controls.Add(MakeFieldLabel("EDC (A)"), 0, r);
            grid.Controls.Add(new PowerLimitEditor(TuningKind.Edc, numericUpDownEdc, ApplyTuningAsync), 1, r); r++;
            grid.Controls.Add(MakeFieldLabel("PBO Scalar"), 0, r);
            grid.Controls.Add(new PowerLimitEditor(TuningKind.Scalar, numericUpDownPboScalar, ApplyTuningAsync), 1, r); r++;
            grid.Controls.Add(MakeFieldLabel("0 leaves a power limit unchanged."), 1, r); r++;

            // Startup-profile setting, moved here from the PBO tab so it sits with the other
            // profile controls (and is visible on every tab, not just PBO). It goes in its own
            // single-column sub-panel that spans both grid columns, so the checkbox, label and
            // full-width dropdown all stack flush-left instead of fighting the label/field
            // column split used by the rows above.
            comboBoxStartupProfile = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 2, 0, 4)
            };
            comboBoxStartupProfile.SelectedIndexChanged += ComboBoxStartupProfile_SelectedIndexChanged;

            tableLayoutPanel12.Controls.Remove(checkBoxApplyCOStartup);
            checkBoxApplyCOStartup.Dock = DockStyle.None;
            checkBoxApplyCOStartup.Anchor = AnchorStyles.Left;
            checkBoxApplyCOStartup.Margin = new Padding(0, 0, 0, 4);

            var startupPanel = new TableLayoutPanel
            {
                ColumnCount = 1,
                Dock = DockStyle.Fill,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 8, 0, 0)
            };
            startupPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            startupPanel.Controls.Add(checkBoxApplyCOStartup, 0, 0);
            startupPanel.Controls.Add(MakeFieldLabel("Startup profile"), 0, 1);
            startupPanel.Controls.Add(comboBoxStartupProfile, 0, 2);
            startupPanel.RowCount = 3;
            for (int i = 0; i < 3; i++)
                startupPanel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            grid.Controls.Add(startupPanel, 0, r);
            grid.SetColumnSpan(startupPanel, 2); r++;

            grid.RowCount = r;
            for (int i = 0; i < r; i++)
                grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            // Re-home the right panel: profile/PBO grid sized to content on top, log fills below.
            tableLayoutPanel11.SuspendLayout();
            tableLayoutPanel11.Controls.Remove(textBoxResult);
            while (tableLayoutPanel11.RowStyles.Count < 2)
                tableLayoutPanel11.RowStyles.Add(new RowStyle());
            tableLayoutPanel11.RowCount = 2;
            tableLayoutPanel11.RowStyles[0] = new RowStyle(SizeType.AutoSize);
            tableLayoutPanel11.RowStyles[1] = new RowStyle(SizeType.Percent, 100F);
            tableLayoutPanel11.Controls.Add(grid, 0, 0);
            textBoxResult.Dock = DockStyle.Fill;
            tableLayoutPanel11.Controls.Add(textBoxResult, 0, 1);
            tableLayoutPanel11.ResumeLayout();

            // The named-profile UI here supersedes the legacy single-profile Save/Load buttons.
            btnSaveCOProfile.Visible = false;
            btnLoadCOProfile.Visible = false;

            RefreshProfileList(null);
        }

        private static Label MakeFieldLabel(string text)
        {
            return new Label
            {
                Text = text,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 6, 8, 4)
            };
        }

        private NumericUpDown MakeLimitBox(int min, int max)
        {
            return new NumericUpDown
            {
                Minimum = min,
                Maximum = max,
                Width = 70,
                Anchor = AnchorStyles.Left,
                Margin = new Padding(0, 3, 0, 3)
            };
        }

        private void RefreshProfileList(string select)
        {
            if (comboBoxProfiles == null) return;
            comboBoxProfiles.SelectedIndexChanged -= ComboBoxProfiles_SelectedIndexChanged;
            try
            {
                comboBoxProfiles.Items.Clear();
                foreach (var n in profileManager.List())
                    comboBoxProfiles.Items.Add(n);
                if (!string.IsNullOrEmpty(select))
                    comboBoxProfiles.Text = select;
                else if (comboBoxProfiles.Items.Count > 0)
                    comboBoxProfiles.SelectedIndex = 0;
            }
            catch (Exception ex)
            {
                HandleError(ex.Message);
            }
            finally
            {
                comboBoxProfiles.SelectedIndexChanged += ComboBoxProfiles_SelectedIndexChanged;
            }

            // Keep the startup-profile dropdown in sync so a newly created (or deleted)
            // profile shows up without restarting the app.
            RefreshStartupProfileList();
        }

        // Repopulates the startup-profile dropdown from the current profile set, preserving
        // the current selection. The suppress flag keeps the SelectedIndexChanged handler
        // from touching the scheduled task during repopulation, so this only refreshes the
        // list - it does not re-register or change the startup task.
        private void RefreshStartupProfileList()
        {
            if (comboBoxStartupProfile == null) return;
            _suppressStartupTaskUpdates = true;
            try
            {
                string previous = comboBoxStartupProfile.SelectedItem as string;
                comboBoxStartupProfile.Items.Clear();
                foreach (var n in profileManager.List())
                    comboBoxStartupProfile.Items.Add(n);

                if (previous != null && comboBoxStartupProfile.Items.Contains(previous))
                    comboBoxStartupProfile.SelectedItem = previous;
                else if (comboBoxStartupProfile.Items.Count > 0)
                    comboBoxStartupProfile.SelectedIndex = 0;
            }
            finally
            {
                _suppressStartupTaskUpdates = false;
            }
        }

        // The profile name typed into, or selected from, the combo box.
        private string CurrentProfileName => (comboBoxProfiles?.Text ?? string.Empty).Trim();

        private void ComboBoxProfiles_SelectedIndexChanged(object sender, EventArgs e)
        {
            // Picking an existing profile from the list loads it into the form.
            var name = comboBoxProfiles.SelectedItem as string;
            if (!string.IsNullOrEmpty(name))
                LoadProfileIntoForm(name);
        }

        private void LoadProfileIntoForm(string name)
        {
            try
            {
                var profile = profileManager.Load(name);
                if (profile == null) { HandleError($"Profile '{name}' not found."); return; }
                string inputError = TuningService.ValidateProfileInput(profile);
                if (inputError != null) throw new InvalidOperationException(inputError);
                if (hardware.Topology.Validate(profile) != null)
                {
                    using (var review = new ProfileTargetsForm(profile, hardware.Topology))
                    {
                        if (review.ShowDialog(this) != DialogResult.OK) return;
                        profile = review.ReviewedProfile;
                    }
                }
                ApplyProfileToUi(profile);
                editControls?.RememberSaved(profile);
                SetStatusText($"Profile '{name}' loaded as local edits. Review with Apply changes or Apply all settings.");
            }
            catch (Exception ex)
            {
                HandleError(ex.Message);
            }
        }

        private void ButtonLoadProfile_Click(object sender, EventArgs e)
        {
            var name = CurrentProfileName;
            if (string.IsNullOrEmpty(name)) { HandleError("Select or type a profile name to load."); return; }
            LoadProfileIntoForm(name);
        }

        private async void ButtonApplyProfile_Click(object sender, EventArgs e)
        {
            await ApplyTuningAsync(GatherProfileFromUi(CurrentProfileName), "Apply all settings");
        }

        private void ButtonSaveProfile_Click(object sender, EventArgs e)
        {
            var name = CurrentProfileName;
            if (string.IsNullOrEmpty(name)) { HandleError("Type a profile name to save."); return; }
            if (!ProfileManager.IsValidName(name)) { HandleError("Invalid profile name (avoid \\ / : * ? \" < > |)."); return; }
            try
            {
                var snapshot = GatherProfileFromUi(name);
                profileManager.Save(snapshot);
                editControls?.RememberSaved(snapshot);
                RefreshProfileList(name);
                SetStatusText($"Profile '{name}' saved.");
            }
            catch (Exception ex)
            {
                HandleError(ex.Message);
            }
        }

        private void ButtonDeleteProfile_Click(object sender, EventArgs e)
        {
            var name = CurrentProfileName;
            if (string.IsNullOrEmpty(name)) return;
            if (MessageBox.Show($"Delete profile '{name}'?", "Confirm",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try
            {
                profileManager.Delete(name);
                RefreshProfileList(null);
                SetStatusText($"Profile '{name}' deleted.");
            }
            catch (Exception ex)
            {
                HandleError(ex.Message);
            }
        }

        private void AllCcdDecrement_Click(object sender, EventArgs e)
        {
            int ccdCount = GetCcdCount();
            for (int ccd = 0; ccd < ccdCount; ccd++)
                BulkMarginChangeHandler(ccd, -1);
        }

        private void AllCcdIncrement_Click(object sender, EventArgs e)
        {
            int ccdCount = GetCcdCount();
            for (int ccd = 0; ccd < ccdCount; ccd++)
                BulkMarginChangeHandler(ccd, 1);
        }

        private void BuildCcdBlocks()
        {
            coControls.Clear();

            flowLayoutPanelCOList.SuspendLayout();
            flowLayoutPanelCOList.Controls.Clear();
            flowLayoutPanelCOList.WrapContents = true;
            flowLayoutPanelCOList.Padding = new Padding(0);

            int ccdCount = GetCcdCount();
            const int coresPerCcd = 8;

            // One tall column per CCD: a full-width "+" on top, a "Core N" + spinner per
            // core, and a full-width "-" at the bottom (bulk adjust the whole CCD).
            for (int ccd = 0; ccd < ccdCount; ccd++)
            {
                int startCore = ccd * coresPerCcd;
                int endCore = Math.Min(startCore + coresPerCcd, GetPhysicalCoreCount());
                int coresInCcd = endCore - startCore;
                if (coresInCcd <= 0) break;

                var col = new TableLayoutPanel
                {
                    ColumnCount = 2,
                    AutoSize = true,
                    AutoSizeMode = AutoSizeMode.GrowAndShrink,
                    Margin = new Padding(3, 0, 6, 0)
                };
                col.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                col.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

                int r = 0;

                var incBtn = new Button
                {
                    Text = "+",
                    Dock = DockStyle.Fill,
                    Height = 22,
                    Margin = new Padding(0, 0, 0, 3),
                    Tag = Tuple.Create(ccd, 1),
                    UseVisualStyleBackColor = true
                };
                incBtn.Click += CcdBulkButton_Click;
                col.Controls.Add(incBtn, 0, r);
                col.SetColumnSpan(incBtn, 2);
                r++;

                for (int i = 0; i < coresInCcd; i++)
                {
                    int coreIndex = startCore + i;

                    var lbl = new Label
                    {
                        Text = $"Core {coreIndex}",
                        AutoSize = true,
                        Anchor = AnchorStyles.Left,
                        Margin = new Padding(0, 4, 8, 0),
                        Name = $"labelCO_{coreIndex}"
                    };

                    var nud = new NumericUpDown
                    {
                        Enabled = false,
                        Maximum = 999,
                        Minimum = -999,
                        Width = 52,
                        Margin = new Padding(0, 1, 0, 1),
                        Name = $"numericUpDownCO_{coreIndex}",
                        Tag = coreIndex
                    };

                    col.Controls.Add(lbl, 0, r);
                    col.Controls.Add(nud, 1, r);
                    coControls[coreIndex] = nud;
                    r++;
                }

                var decBtn = new Button
                {
                    Text = "\u2212",
                    Dock = DockStyle.Fill,
                    Height = 22,
                    Margin = new Padding(0, 3, 0, 0),
                    Tag = Tuple.Create(ccd, -1),
                    UseVisualStyleBackColor = true
                };
                decBtn.Click += CcdBulkButton_Click;
                col.Controls.Add(decBtn, 0, r);
                col.SetColumnSpan(decBtn, 2);

                flowLayoutPanelCOList.Controls.Add(col);
            }

            // Apply / Re-read stacked to the right of the CCD columns.
            var actions = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.TopDown,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false,
                Margin = new Padding(6, 22, 0, 0)
            };
            var applyBtn = new Button { Text = "Apply", AutoSize = true, Margin = new Padding(0, 0, 0, 4), UseVisualStyleBackColor = true };
            applyBtn.Click += ButtonApplyCO_Click;
            var refreshBtn = new Button { Text = "Re-read from CPU", AutoSize = true, Margin = new Padding(0, 0, 0, 4), UseVisualStyleBackColor = true };
            refreshBtn.Click += buttonGetCO_Click;
            actions.Controls.Add(applyBtn);
            actions.Controls.Add(refreshBtn);
            flowLayoutPanelCOList.Controls.Add(actions);

            flowLayoutPanelCOList.ResumeLayout();
        }

        private void CcdBulkButton_Click(object sender, EventArgs e)
        {
            Button button = sender as Button;
            Tuple<int, int> action = button?.Tag as Tuple<int, int>;
            if (action != null)
            {
                BulkMarginChangeHandler(action.Item1, action.Item2);
            }
        }

        private NumericUpDown GetCOControl(int coreIndex)
        {
            NumericUpDown control;
            return coControls.TryGetValue(coreIndex, out control) ? control : null;
        }

        // Returns the designer's "checkBox{index}" core control (or null), caching the
        // recursive lookup so it runs at most once per core.
        private CheckBox GetCoreCheckBox(int index)
        {
            if (!coreCheckBoxes.TryGetValue(index, out CheckBox cb))
            {
                Control[] found = Controls.Find($"checkBox{index}", true);
                cb = found.Length > 0 ? found[0] as CheckBox : null;
                coreCheckBoxes[index] = cb;
            }
            return cb;
        }

        private int GetCcdCount()
        {
            if (hardware.Topology.Targets.Count > 0) return hardware.Topology.Targets.Max(t => t.Ccd) + 1;
            if (cpu.info.topology.ccds > 0)
            {
                return (int)cpu.info.topology.ccds;
            }

            return Math.Max(1, (int)Math.Ceiling(GetPhysicalCoreCount() / 8.0));
        }

        private int GetPhysicalCoreCount()
        {
            return hardware.Topology.SlotCount;
        }

        private bool IsCoreEnabled(int coreIndex)
        {
            return hardware.Topology.Find(coreIndex) != null;
        }

        // True for APU SMU types, which address CO margins by a flat core index.
        private bool IsApu()
        {
            return cpu.smu.SMU_TYPE >= SMU.SmuType.TYPE_APU0 && cpu.smu.SMU_TYPE <= SMU.SmuType.TYPE_APU2;
        }

        private bool EnableOCMode(bool prochotEnabled = true)
        {
            uint[] args = new uint[6];
            args[0] = prochotEnabled ? 0U : 0x1000000;
            return cpu.smu.SendSmuCommand(cpu.smu.Rsmu, cpu.smu.Rsmu.SMU_MSG_EnableOcMode, ref args) == SMU.Status.OK;
        }

        private async Task DisableOCMode()
        {
            try
            {
                var status = await RunHardwareAsync(() => Hardware.Locked(() => cpu.DisableOcMode()));
                if (!shuttingDown) SetStatusText("Disable OC Mode: " + status);
            }
            catch (Exception ex) { if (!shuttingDown) HandleError(ex.Message); }
        }

        // Manual-OC frequency tab. Every Apply writes ALL cores at once (from the per-core
        // fields) so no core is left undefined - setting one core in OC Mode otherwise drops
        // the rest to the SMU default (~2500 MHz). OC Mode overrides PBO / Curve Optimizer.
        private readonly Dictionary<int, NumericUpDown> freqControls = new Dictionary<int, NumericUpDown>();

        // Single all-core manual-OC voltage input (SetOverclockCpuVid is package-wide;
        // there is no per-core voltage). Null until BuildFrequencyTab runs.
        private NumericUpDown _ocVoltageNud;

        // Whether this CPU allows manual OC (fixed frequency / fixed voltage via OC Mode).
        // AMD fuses this off on X3D parts (e.g. 5800X3D), where the SMU rejects EnableOcMode
        // and every OC write. Set in BuildFrequencyTab from the SMU's IsOverclockable answer.
        private bool _manualOcSupported = true;

        private void BuildFrequencyTab()
        {
            freqControls.Clear();
            var tab = new TabPage("Freq (OC)") { Name = "tabPageFreqOC" };

            // Only treat OC as locked on a DEFINITIVE "not overclockable" answer: the SMU must
            // define the IsOverclockable query and report OC disabled. If the query isn't defined
            // we can't tell, so leave the tab enabled (an Apply would surface its own error).

            if (!_manualOcSupported)
            {
                BuildManualOcLockedTab(tab);
                tabControl1.TabPages.Add(tab);
                return;
            }

            var root = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(8)
            };

            root.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new Size(340, 0),
                ForeColor = Color.Firebrick,
                Margin = new Padding(0, 0, 0, 8),
                Text = "Manual OC overrides PBO / Curve Optimizer. Every core runs at the fixed clock " +
                       "you set below - no boost, no idle downclock. Core voltage is a SINGLE value " +
                       "applied to ALL cores on every CCD. Recommended order: set voltage FIRST, then " +
                       "frequency (applying voltage engages OC Mode and holds cores at ~2500 MHz until " +
                       "you apply frequencies). Too high a voltage or clock can hang, reboot, or damage the CPU."
            });

            // Bulk fillers only populate the per-core fields below; nothing is sent until Apply.
            var allNud = MakeFreqNud();
            root.Controls.Add(MakeBulkRow("All cores", allNud, () =>
            {
                foreach (var n in freqControls.Values) n.Value = allNud.Value;
            }));

            // Single all-core voltage row with its own Apply button. Voltage is applied
            // separately from frequency; recommended order is voltage first (see warning).
            bool x3d = VoltageCodec.IsX3D(cpu.systemInfo.CpuName);
            _ocVoltageNud = new NumericUpDown
            {
                Minimum = 0.700m,
                // X3D parts share the rail with the voltage-sensitive V-Cache CCD, so cap lower.
                Maximum = x3d ? 1.400m : 1.550m,
                DecimalPlaces = 3,
                Increment = 0.005m,
                Value = 1.200m,
                Width = 70,
                Margin = new Padding(0, 2, 0, 0)
            };
            var voltRow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                WrapContents = false,
                Margin = new Padding(0, 0, 0, 4)
            };
            voltRow.Controls.Add(new Label { Text = "Core voltage (all cores)", AutoSize = true, Width = 130, Margin = new Padding(0, 6, 8, 0) });
            voltRow.Controls.Add(_ocVoltageNud);
            var applyVoltBtn = new Button { Text = "Apply voltage", AutoSize = true, UseVisualStyleBackColor = true, Margin = new Padding(6, 0, 0, 0) };
            applyVoltBtn.Click += async (s, e) => await ApplyOverclockVoltage();
            voltRow.Controls.Add(applyVoltBtn);

            // Capability guard: disable if the SMU defines no SetOverclockCpuVid message in
            // either mailbox (not the case for any current target, but future-proof).
            if (cpu.smu.Rsmu.SMU_MSG_SetOverclockCpuVid == 0
                && cpu.smu.Mp1Smu.SMU_MSG_SetOverclockCpuVid == 0)
            {
                _ocVoltageNud.Enabled = false;
                applyVoltBtn.Enabled = false;
            }
            else if (x3d)
            {
                voltRow.Controls.Add(new Label
                {
                    Text = "(X3D: shared with V-Cache CCD; capped 1.40 V)",
                    AutoSize = true,
                    ForeColor = Color.Firebrick,
                    Margin = new Padding(8, 6, 0, 0)
                });
            }

            root.Controls.Add(voltRow);

            int ccdCount = GetCcdCount();
            const int coresPerCcd = 8;
            for (int ccd = 0; ccd < ccdCount; ccd++)
            {
                int start = ccd * coresPerCcd;
                int end = Math.Min(start + coresPerCcd, GetPhysicalCoreCount());
                if (end <= start) break;
                var ccdNud = MakeFreqNud();
                int s2 = start, e2 = end;
                root.Controls.Add(MakeBulkRow($"CCD {ccd} (cores {start}-{end - 1})", ccdNud, () =>
                {
                    for (int c = s2; c < e2; c++)
                        if (freqControls.TryGetValue(c, out var n)) n.Value = ccdNud.Value;
                }));
            }

            // Per-core fields in two columns (column-major like the PBO grid).
            int coreCount = GetPhysicalCoreCount();
            int rowsPerCol = (coreCount + 1) / 2;
            var grid = new TableLayoutPanel
            {
                ColumnCount = 4,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 8, 0, 8)
            };
            for (int i = 0; i < coreCount; i++)
            {
                var nud = MakeFreqNud();
                nud.Enabled = IsCoreEnabled(i);
                nud.Width = 64;
                freqControls[i] = nud;
                int gcol = i < rowsPerCol ? 0 : 2;
                int grow = i < rowsPerCol ? i : i - rowsPerCol;
                grid.Controls.Add(new Label { Text = $"Core {i}", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 5, 6, 0) }, gcol, grow);
                grid.Controls.Add(nud, gcol + 1, grow);
            }
            root.Controls.Add(grid);

            // Action bar docked to the bottom so it stays visible while the list above scrolls.
            var actionBar = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(8, 4, 8, 6)
            };
            var applyBtn = new Button { Text = "Apply all cores", AutoSize = false, Size = new Size(115, 26), UseVisualStyleBackColor = true, Margin = new Padding(0, 0, 8, 0) };
            applyBtn.Click += async (s, e) => await ApplyAllCoreFrequencies();
            var offBtn = new Button { Text = "Disable OC Mode", AutoSize = false, Size = new Size(115, 26), UseVisualStyleBackColor = true, Margin = new Padding(0, 0, 0, 0) };
            offBtn.Click += async (s, e) => await DisableOCMode();
            actionBar.Controls.Add(applyBtn);
            actionBar.Controls.Add(offBtn);

            // Add the fill panel first, then the bottom bar: the bottom bar reserves its
            // strip and the scrollable content fills the rest above it.
            tab.Controls.Add(root);
            tab.Controls.Add(actionBar);
            tabControl1.TabPages.Add(tab);
        }

        // Content for the Freq (OC) tab on CPUs where manual OC is fused off (e.g. X3D parts):
        // explain why the controls aren't here and point at what does work, instead of offering
        // frequency/voltage inputs whose Apply always fails at the SMU. CO / PBO are unaffected.
        private void BuildManualOcLockedTab(TabPage tab)
        {
            var root = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Padding = new Padding(12)
            };
            root.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new Size(340, 0),
                ForeColor = Color.Firebrick,
                Font = new Font(Font, FontStyle.Bold),
                Text = "Manual OC is locked on this CPU."
            });
            root.Controls.Add(new Label
            {
                AutoSize = true,
                MaximumSize = new Size(340, 0),
                Margin = new Padding(0, 8, 0, 0),
                Text = "AMD fuses off manual overclocking on X3D processors (e.g. the 5800X3D) to " +
                       "protect the 3D V-Cache, so a fixed all-core frequency and a fixed core " +
                       "voltage can't be applied here - the SMU rejects OC Mode. Use Curve Optimizer " +
                       "(per-core undervolt) and PBO limits on the CPU / PBO tabs to tune this CPU instead."
            });
            tab.Controls.Add(root);
        }

        private FlowLayoutPanel MakeBulkRow(string label, NumericUpDown nud, System.Action onSet)
        {
            var row = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                WrapContents = false,
                Margin = new Padding(0, 0, 0, 4)
            };
            row.Controls.Add(new Label { Text = label, AutoSize = true, Width = 130, Margin = new Padding(0, 6, 8, 0) });
            row.Controls.Add(nud);
            var btn = new Button { Text = "Set", AutoSize = true, UseVisualStyleBackColor = true, Margin = new Padding(6, 0, 0, 0) };
            btn.Click += (s, e) => onSet();
            row.Controls.Add(btn);
            return row;
        }

        // Enables OC Mode and writes EVERY core's frequency, so none are left at the default.
        private async Task ApplyAllCoreFrequencies()
        {
            if (!_hardwareReady) { SetStatusText("Hardware is still loading."); return; }
            if (!_manualOcSupported || cpu == null || freqControls.Count == 0) return;
            var values = freqControls.Where(x => x.Value.Enabled && IsCoreEnabled(x.Key)).ToDictionary(x => x.Key, x => (int)x.Value.Value);
            if (values.Count == 0) { SetStatusText("No validated core targets are available for manual OC."); return; }
            try
            {
                bool ok = await RunHardwareAsync(() => Hardware.Locked(() =>
                {
                    if (!EnableOCMode(true)) return false;
                    foreach (var kv in values)
                    {
                        Hardware.ThrowIfClosing();
                        if (!SetCoreFrequency(kv.Key, kv.Value)) return false;
                    }
                    return true;
                }));
                if (!shuttingDown) SetStatusText(ok ? $"Frequencies accepted for {values.Count} cores (OC Mode on; unverified)." : "OC frequency apply stopped after a rejected command.");
            }
            catch (Exception ex) { if (!shuttingDown) HandleError(ex.Message); }
        }

        // Applies the single all-core manual-OC voltage. Enables OC Mode first so the
        // fixed VID actually holds (outside OC Mode the SMU governs voltage). Recommended
        // to run this BEFORE applying frequencies: this pins the voltage while cores idle
        // at the OC-mode default (~2500 MHz), so frequency then ramps into an established
        // voltage floor rather than a high clock on an undefined voltage.
        private async Task ApplyOverclockVoltage()
        {
            if (!_hardwareReady) { SetStatusText("Hardware is still loading."); return; }
            try
            {
                if (!_manualOcSupported || cpu == null || _ocVoltageNud == null || !_ocVoltageNud.Enabled) return;

                double volts = (double)_ocVoltageNud.Value;
                bool svi3 = SmuDecodeAdapter.IsSvi3(cpu.info.codeName);
                uint vid = VoltageCodec.VoltageToVid(volts, svi3);

                bool ok = await RunHardwareAsync(() => Hardware.Locked(() => EnableOCMode(true) && cpu.SetOverclockCpuVid(vid) == SMU.Status.OK));
                if (ok)
                {
                    // Report the voltage actually applied (derived from the VID), which can
                    // differ from the typed value when it rounds/clamps to an encodable VID.
                    double appliedVolts = VoltageCodec.VidToVoltage(vid, svi3);
                    SetStatusText($"{appliedVolts:0.000} V (VID {vid}) applied - OC Mode on. Apply frequencies next.");
                }
                else
                    HandleError("Failed to set core voltage.");

            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!shuttingDown) HandleError(ex.Message); }
        }

        private NumericUpDown MakeFreqNud()
        {
            return new NumericUpDown
            {
                Minimum = 400,
                Maximum = 7000,
                Increment = 25,
                Value = 4000,
                Width = 70,
                Margin = new Padding(0, 2, 0, 0)
            };
        }

        // Builds the per-core frequency mask (CCD/CCX/core) the SMU expects. frequency is in MHz.
        private bool SetCoreFrequency(int coreIndex, int mhz)
        {
            // Let the library build the SMU core mask so the per-family layout is correct.
            // MakeCoreMask (via this overload) uses ccd<<28 | (core%8)<<20 for Zen 3/4/5, and
            // the 2x4-CCX split for older families. The previous hand-rolled mask assumed the
            // old CCX layout, so cores 4-7 of each CCD got a wrong mask on Zen 3+ and failed to
            // set (observed on the 9950X3D). coreIndex is a flat 0..N index over 8-core CCDs;
            // core/ccd/ccx below feed MakeCoreMask correctly for both modern and legacy parts.
            uint core = (uint)(coreIndex % 8);
            uint ccd = (uint)(coreIndex / 8);
            uint ccx = (uint)((coreIndex % 8) / 4);
            var target = hardware.Topology.Find(coreIndex);
            return target != null && Hardware.Locked(() => cpu.SetFrequencySingleCore(target.Mask, (uint)mhz));
        }

        private void SetStatusText(string status)
        {
            labelStatus.Text = status;
#if DEBUG
            Debug.WriteLine($"CMD Status: {status}");
#endif
        }

        // Newest-first result log. Prepends and caps total length, so the control can't
        // grow without bound and each write stays O(cap) instead of O(history) — the old
        // `textBoxResult.Text = x + textBoxResult.Text` pattern got slower with every entry.
        private const int MaxResultLength = 100_000;
        private void PrependResult(string text)
        {
            string combined = text + textBoxResult.Text;
            if (combined.Length > MaxResultLength)
                combined = combined.Substring(0, MaxResultLength);
            textBoxResult.Text = combined;
        }

        private void SetButtonsState(bool enabled = true)
        {
            buttonApply.Enabled = enabled;
            buttonDefaults.Enabled = enabled;
            buttonProbe.Enabled = enabled;
            buttonPciRead.Enabled = enabled;
            buttonPciScan.Enabled = enabled;
            buttonExport.Enabled = enabled;
            buttonMsrRead.Enabled = enabled;
            buttonMsrScan.Enabled = enabled;
            buttonMsrWrite.Enabled = enabled;
            buttonPMTable.Enabled = enabled;
            buttonSmuLog.Enabled = enabled;

            textBoxCMDAddress.Enabled = enabled;
            textBoxRSPAddress.Enabled = enabled;
            textBoxARGAddress.Enabled = enabled;
            textBoxCMD.Enabled = enabled;
            textBoxARG0.Enabled = enabled;
            textBoxPciAddress.Enabled = enabled;
            textBoxPciValue.Enabled = enabled;
            textBoxPciStartReg.Enabled = enabled;
            textBoxPciEndReg.Enabled = enabled;
            textBoxMsrAddress.Enabled = enabled;
            textBoxMsrEdx.Enabled = enabled;
            textBoxMsrEax.Enabled = enabled;
            textBoxMsrStart.Enabled = enabled;
            textBoxMsrEnd.Enabled = enabled;
            comboBoxMailboxSelect.Enabled = enabled;
            // textBoxResult.Enabled = enabled;
        }

        private void TryConvertToUint(string text, out uint address)
        {
            try
            {
                address = Convert.ToUInt32(text.Trim().ToLowerInvariant(), 16);
            }
            catch
            {
                throw new ApplicationException("Invalid hexadecimal value.");
            }
        }

        private void HandleError(string message, string title = "Error")
        {
            SetStatusText(Resources.Error);
            MessageBox.Show(message, title);
        }

        private void ShowResultMessageBox(uint data)
        {
            uint[] d = { data };
            ShowResultMessageBox(d);
        }

        private void ShowResultMessageBox(uint[] data)
        {
            string responseString = "";
            string[] hexArray = new string[data.Length];
            string[] decArray = new string[data.Length];
            string[] binArray = new string[data.Length];

            for (var i = 0; i < data.Length; i++)
            {
                hexArray[i] = $"0x{Convert.ToString(data[i], 16).ToUpper()}";
                decArray[i] = $"{Convert.ToString(data[i], 10).ToUpper()}";
                binArray[i] = $"{Convert.ToString(data[i], 2).ToUpper()}";
            }

            responseString += "HEX: " + string.Join(", ", hexArray);
            responseString += Environment.NewLine;
            responseString += "DEC: " + string.Join(", ", decArray);
            responseString += Environment.NewLine;
            responseString += "BIN: " + string.Join(", ", binArray);
            responseString += Environment.NewLine;
            responseString += Environment.NewLine;

            Debug.WriteLine($"Response: {responseString}");
            PrependResult(responseString);
        }

        private void ShowResult(uint data)
        {
            string responseString =
                $"REG: {textBoxPciAddress.Text.Trim()}" +
                Environment.NewLine +
                $"HEX: 0x{Convert.ToString(data, 16).ToUpper()}" +
                Environment.NewLine +
                $"INT: {Convert.ToString(data, 10).ToUpper()}" +
                Environment.NewLine +
                $"BIN: {Convert.ToString(data, 2).PadLeft(32, '0')}" +
                Environment.NewLine +
                Environment.NewLine;
            Debug.WriteLine($"Response: {responseString}");
            PrependResult(responseString);
            if (TryConvertToUintNoThrow(textBoxPciAddress.Text, out uint pciAddr))
            {
                string decoded = RegisterDecoder.Decode(RegisterKind.Pci, pciAddr, data, decodeContext);
                if (!string.IsNullOrEmpty(decoded))
                    PrependResult(decoded + Environment.NewLine);
            }
        }

        private static bool TryConvertToUintNoThrow(string text, out uint address)
        {
            address = 0;
            try { address = Convert.ToUInt32(text.Trim().ToLowerInvariant(), 16); return true; }
            catch { return false; }
        }

        private void ShowResultForm(string title = "Result", string result = "No result")
        {
            Invoke(new MethodInvoker(delegate
            {
                var resultForm = new ResultForm();
                resultForm.textBoxFormResult.Text = result;
                resultForm.Text = title;
                // Owned by the main form: it stays above the owner and is closed
                // automatically when the app exits, so scans can't leave orphan windows.
                resultForm.Show(this);
            }));
        }

        // TODO: Show all args
        private async Task ApplySettings()
        {
            try
            {
                try
                {
                    uint[] args = ZenStates.Core.Utils.MakeCmdArgs();
                    string[] userArgs = textBoxARG0.Text.Trim().Split(',');

                    TryConvertToUint(textBoxCMDAddress.Text, out uint addrMsg);
                    TryConvertToUint(textBoxRSPAddress.Text, out uint addrRsp);
                    TryConvertToUint(textBoxARGAddress.Text, out uint addrArg);
                    TryConvertToUint(textBoxCMD.Text, out uint command);

                    testMailbox.SMU_ADDR_MSG = addrMsg;
                    testMailbox.SMU_ADDR_RSP = addrRsp;
                    testMailbox.SMU_ADDR_ARG = addrArg;

                    for (var i = 0; i < userArgs.Length; i++)
                    {
                        if (i == args.Length)
                            break;

                        TryConvertToUint(userArgs[i], out uint temp);
                        args[i] = temp;
                    }


                    Debug.WriteLine("MSG Address:  0x" + Convert.ToString(testMailbox.SMU_ADDR_MSG, 16).ToUpper());
                    Debug.WriteLine("RSP Address:  0x" + Convert.ToString(testMailbox.SMU_ADDR_RSP, 16).ToUpper());
                    Debug.WriteLine("ARG0 Address: 0x" + Convert.ToString(testMailbox.SMU_ADDR_ARG, 16).ToUpper());
                    Debug.WriteLine("ARG0        : 0x" + Convert.ToString(args[0], 16).ToUpper());

                    uint[] argsLocal = args;
                    SMU.Status status = await RunHardwareAsync(() => Hardware.Locked(() => cpu.smu.SendSmuCommand(testMailbox, command, ref argsLocal)));
                    args = argsLocal;

                    if (status == SMU.Status.OK)
                    {
                        string cmdName = SmuCommandNames.Resolve(smuNameMap, command);
                        if (cmdName != null)
                            PrependResult($"CMD: 0x{command:X} ({cmdName}){Environment.NewLine}");
                        ShowResultMessageBox(args);
                    }

                    SetStatusText(GetSMUStatus.GetByType(status));
                }
                catch (ApplicationException ex)
                {
                    HandleError(ex.Message);
                }

            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!shuttingDown) HandleError(ex.Message); }
        }

        private void ButtonDefaults_Click(object sender, EventArgs e)
        {
            InitTestMailbox(cpu.smu.Rsmu);
            comboBoxMailboxSelect.SelectedIndex = 0;
            textBoxCMD.Value = 1;
            textBoxARG0.Text = "0";
        }

        private async void ButtonApply_Click(object sender, EventArgs e)
        {
            try
            {
                await ApplySettings();
            }
            catch (ApplicationException ex)
            {
                HandleError(ex.Message, "Error reading response");
            }
        }

        private async Task HandlePciReadBtnClick()
        {
            try
            {
                try
                {
                    SetStatusText("Reading, please wait...");
                    SetButtonsState(false);

                    TryConvertToUint(textBoxPciAddress.Text, out uint address);
                    uint data = await RunHardwareAsync(() => Hardware.Locked(() => cpu.ReadDword(address)));

                    textBoxPciValue.Text = $"0x{data:X8}";

                    SetButtonsState();
                    SetStatusText(GetSMUStatus.GetByType(SMU.Status.OK));
                    ShowResult(data);
                }
                catch (ApplicationException ex)
                {
                    SetButtonsState();
                    HandleError(ex.Message);
                }

            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!shuttingDown) HandleError(ex.Message); }
        }

        private async Task HandlePciWriteBtnClick()
        {
            try
            {
                try
                {
                    SetStatusText("Writing, please wait...");
                    SetButtonsState(false);

                    TryConvertToUint(textBoxPciAddress.Text, out uint address);
                    TryConvertToUint(textBoxPciValue.Text, out uint data);

                    bool res = await RunHardwareAsync(() => Hardware.Locked(() =>
                        cpu.WriteDwordEx(cpu.smu.SMU_OFFSET_ADDR, address) &&
                        cpu.WriteDwordEx(cpu.smu.SMU_OFFSET_DATA, data)));

                    if (res)
                        SetStatusText("Write OK.");
                    else
                        SetStatusText(Resources.Error);

                    SetButtonsState();
                }
                catch (ApplicationException ex)
                {
                    SetButtonsState();
                    HandleError(ex.Message);
                }

            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!shuttingDown) HandleError(ex.Message); }
        }

        private async void ButtonPciRead_Click(object sender, EventArgs e)
        {
            await HandlePciReadBtnClick();
        }

        private async void TextBoxPciAddress_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
                await HandlePciReadBtnClick();
        }

        private async void ButtonPciWrite_Click(object sender, EventArgs e)
        {
            await HandlePciWriteBtnClick();
        }

        private async void TextBoxPciValue_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
                await HandlePciWriteBtnClick();
        }

        private SMU.Status TrySettings(uint msgAddr, uint rspAddr, uint argAddr, uint cmd, uint value)
        {
            uint[] args = new uint[6];
            args[0] = value;

            testMailbox.SMU_ADDR_MSG = msgAddr;
            testMailbox.SMU_ADDR_RSP = rspAddr;
            testMailbox.SMU_ADDR_ARG = argAddr;

            return Hardware.Locked(() => cpu.smu.SendSmuCommand(testMailbox, cmd, ref args));
        }

        private void ScanSmuRange(uint start, uint end, uint step, uint offset)
        {
            matches = new List<SmuAddressSet>();

            List<KeyValuePair<uint, uint>> temp = new List<KeyValuePair<uint, uint>>();

            if (step == 0 || end < start || (ulong)end - start > 65536) throw new ApplicationException("Invalid or excessively large SMU scan range.");
            for (ulong address = start; address <= end; address += step)
            {
                Hardware.ThrowIfClosing();
                start = (uint)address;
                ulong smuRspAddress = (ulong)start + offset;

                // Each cpu access is locked individually (not the whole loop) because the
                // match-found branch below marshals to the UI thread with a synchronous
                // Invoke; holding Hardware.Sync across that would risk a cross-thread deadlock.
                if (Hardware.Locked(() => cpu.ReadDword(start)) != 0xFFFFFFFF)
                {
                    // Send unknown command 0xFF to each pair of this start and possible response addresses
                    if (Hardware.Locked(() => cpu.WriteDwordEx(start, 0xFF)))
                    {
                        Thread.Sleep(10);

                        while (smuRspAddress <= end)
                        {
                            uint rspAddr = (uint)smuRspAddress;
                            // Expect UNKNOWN_CMD status to be returned if the mailbox works
                            if (Hardware.Locked(() => cpu.ReadDword(rspAddr)) == 0xFE)
                            {
                                // Send Get_SMU_Version command
                                if (Hardware.Locked(() => cpu.WriteDwordEx(start, 0x2)))
                                {
                                    Thread.Sleep(10);
                                    if (Hardware.Locked(() => cpu.ReadDword(rspAddr)) == 0x1)
                                        temp.Add(new KeyValuePair<uint, uint>(start, (uint)smuRspAddress));
                                }
                            }
                            smuRspAddress += step;
                        }
                    }
                }


            }

            if (temp.Count > 0)
            {
                for (var i = 0; i < temp.Count; i++)
                {
                    Debug.WriteLine($"{temp[i].Key:X8}: {temp[i].Value:X8}");
                }

                Debug.WriteLine("");
            }

            List<uint> possibleArgAddresses = new List<uint>();

            foreach (var pair in temp)
            {
                Debug.WriteLine($"Testing {pair.Key:X8}: {pair.Value:X8}");

                if (TrySettings(pair.Key, pair.Value, 0xFFFFFFFF, 0x2, 0xFF) == SMU.Status.OK)
                {
                    ulong smuArgAddress = (ulong)pair.Value + 4;
                    while (smuArgAddress <= end)
                    {
                        uint argAddr = (uint)smuArgAddress;
                        if (Hardware.Locked(() => cpu.ReadDword(argAddr)) == cpu.smu.Version)
                        {
                            possibleArgAddresses.Add((uint)smuArgAddress);
                        }
                        smuArgAddress += step;
                    }
                }

                // Verify the arg address returns correct value (should be test argument + 1)
                foreach (var address in possibleArgAddresses)
                {
                    uint testArg = 0xFAFAFAFA;
                    var retries = 3;

                    while (retries > 0)
                    {
                        testArg++;
                        retries--;

                        // Send test command
                        if (TrySettings(pair.Key, pair.Value, address, 0x1, testArg) == SMU.Status.OK)
                            if (Hardware.Locked(() => cpu.ReadDword(address)) != testArg + 1)
                                retries = -1;
                    }

                    if (retries == 0)
                    {
                        matches.Add(new SmuAddressSet(pair.Key, pair.Value, address));

                        string responseString =
                                $"CMD:  0x{pair.Key:X8}" +
                                Environment.NewLine +
                                $"RSP:  0x{pair.Value:X8}" +
                                Environment.NewLine +
                                $"ARG:  0x{address:X8}" +
                                Environment.NewLine +
                                Environment.NewLine;

                        Invoke(new MethodInvoker(delegate
                        {
                            PrependResult(responseString);
                        }));

                        break;
                    }
                }
            }
        }


        private void RunBackgroundTask(DoWorkEventHandler task, RunWorkerCompletedEventHandler completedHandler, object argument = null)
        {
            try
            {
                if (hardwareOperation || (backgroundWorker1?.IsBusy ?? false) || shuttingDown) return;
                tabControl1.Enabled = false;
                SetButtonsState(false);
                textBoxResult.Clear();

                // Dispose the previous worker before replacing it, so repeated scans don't
                // leak a BackgroundWorker (and its event subscriptions) each time.
                backgroundWorker1?.Dispose();
                backgroundWorker1 = new BackgroundWorker();
                backgroundWorker1.DoWork += task;
                backgroundWorker1.RunWorkerCompleted += (sender, args) =>
                {
                    if (shuttingDown || IsDisposed) return;
                    tabControl1.Enabled = true;
                    if (args.Error != null) { SetButtonsState(); SetStatusText("Operation failed: " + args.Error.Message); }
                    else completedHandler(sender, args);
                };
                backgroundWorker1.RunWorkerAsync(argument);
            }
            catch (ApplicationException ex)
            {
                SetStatusText(Resources.Error);
                SetButtonsState();
                HandleError(ex.Message);
            }
        }

        private void BackgroundWorkerTrySettings_DoWork(object sender, DoWorkEventArgs e)
        {
            try
            {
                Invoke(new MethodInvoker(delegate
                {
                    SetStatusText("Scanning SMU addresses, please wait...");
                }));

                switch (cpu.info.codeName)
                {
                    case Cpu.CodeName.BristolRidge:
                        //ScanSmuRange(0x13000000, 0x13000F00, 4, 0x10);
                        break;
                    case Cpu.CodeName.RavenRidge:
                    case Cpu.CodeName.Picasso:
                    case Cpu.CodeName.FireFlight:
                    case Cpu.CodeName.Dali:
                    case Cpu.CodeName.Renoir:
                        ScanSmuRange(0x03B10500, 0x03B10998, 8, 0x3C);
                        ScanSmuRange(0x03B10A00, 0x03B10AFF, 4, 0x60);
                        break;
                    case Cpu.CodeName.PinnacleRidge:
                    case Cpu.CodeName.SummitRidge:
                    case Cpu.CodeName.Matisse:
                    case Cpu.CodeName.Whitehaven:
                    case Cpu.CodeName.Naples:
                    case Cpu.CodeName.Colfax:
                    case Cpu.CodeName.Vermeer:
                        //case Cpu.CodeName.Raphael:
                        ScanSmuRange(0x03B10500, 0x03B10998, 8, 0x3C);
                        ScanSmuRange(0x03B10500, 0x03B10AFF, 4, 0x4C);
                        break;
                    case Cpu.CodeName.Raphael:
                    case Cpu.CodeName.GraniteRidge:
                        ScanSmuRange(0x03B10500, 0x03B10998, 8, 0x3C);
                        // ScanSmuRange(0x03B10500, 0x03B10AFF, 4, 0x4C);
                        break;
                    case Cpu.CodeName.Rome:
                        ScanSmuRange(0x03B10500, 0x03B10AFF, 4, 0x4C);
                        break;
                    default:
                        break;
                }
            }
            catch (ApplicationException)
            {
                Invoke(new MethodInvoker(delegate
                {
                    SetButtonsState();
                    SetStatusText(Resources.Error);
                }));
            }
        }

        private void ButtonScan_Click(object sender, EventArgs e)
        {
            var confirmResult = MessageBox.Show(
                "The scan process might crash your system or have other unexpected results. " +
                Environment.NewLine +
                "It could take up to 1 minute, depending on the system and current workload." +
                Environment.NewLine +
                "Do you want to continue?",
                "Confirm Scan",
                MessageBoxButtons.OKCancel
            );

            if (confirmResult == DialogResult.OK)
                RunBackgroundTask(BackgroundWorkerTrySettings_DoWork, SmuScan_WorkerCompleted);
        }

        private void TabControl1_Selected(object sender, TabControlEventArgs e)
        {
            if (e.TabPage == tabPageInfo)
                splitContainer1.Panel2Collapsed = true;
            else if (splitContainer1.Panel2Collapsed)
                splitContainer1.Panel2Collapsed = false;
        }

        public string GenerateReportJson()
        {
            StringWriter sw = new StringWriter();
            JsonTextWriter writer = new JsonTextWriter(sw)
            {
                Formatting = Formatting.Indented
            };

            // {
            writer.WriteStartObject();

            writer.WritePropertyName("AppVersion");
            writer.WriteValue(Application.ProductVersion);

            writer.WritePropertyName("OSVersion");
            writer.WriteValue(new ComputerInfo().OSFullName);

            Type type = cpu.systemInfo.GetType();
            PropertyInfo[] properties = type.GetProperties();

            foreach (PropertyInfo property in properties)
            {
                writer.WritePropertyName(property.Name);
                if (property.Name == "CpuId" || property.Name == "PatchLevel")
                    writer.WriteValue($"{property.GetValue(cpu.systemInfo, null):X8}");
                else if (property.Name == "SmuVersion")
                    writer.WriteValue(cpu.systemInfo.SmuVersion.ToString());
                else
                    writer.WriteValue(property.GetValue(cpu.systemInfo, null));
            }

            // "SmuAddresses:"
            writer.WritePropertyName("Mailboxes");
            writer.WriteStartArray();
            foreach (SmuAddressSet set in matches)
            {
                writer.WriteStartObject();
                writer.WritePropertyName("MsgAddress");
                writer.WriteValue($"0x{set.MsgAddress:X8}");
                writer.WritePropertyName("RspAddress");
                writer.WriteValue($"0x{set.RspAddress:X8}");
                writer.WritePropertyName("ArgAddress");
                writer.WriteValue($"0x{set.ArgAddress:X8}");
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            // }
            writer.WriteEndObject();

            sw.Close();

            return sw.ToString();
        }

        private void BackgroundWorkerReport_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            string unixTimestamp = Convert.ToString((DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1))).TotalMinutes);
            // Write next to the exe, not the (unpredictable) current working directory, so
            // the report always lands in a known place.
            string fileName = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, $"SMUDebug_{unixTimestamp}.json");

            if (File.Exists(fileName))
                File.Delete(fileName);

            using (var sw = new StreamWriter(fileName, true))
            {
                sw.WriteLine(GenerateReportJson());
            }

            //ResetSmuAddresses();
            SetButtonsState();
            SetStatusText("Report Complete.");
            MessageBox.Show($"Report saved as {fileName}");
        }

        private void ButtonExport_Click(object sender, EventArgs e)
        {
            RunBackgroundTask(BackgroundWorkerTrySettings_DoWork, BackgroundWorkerReport_RunWorkerCompleted);
        }

        private int? pstateReadId;

        private sealed class PStateItem
        {
            public int Id { get; }
            public ulong? Raw { get; }
            public PStateItem(int id, ulong? raw) { Id = id; Raw = raw; }
            public override string ToString() => $"P{Id}" + (!Raw.HasValue ? " (unreadable)" :
                (Raw.Value & (1UL << 63)) == 0 ? " (disabled)" : "");
        }

        private void InitPStateEditor()
        {
            InitPStateVidEditor();
            pstateIdBox.Items.Clear();
            pstateIdBox.DisplayMember = "";
            pstateIdBox.Enabled = btnPstateRead.Enabled = false;
            pstateIdBox.SelectedIndexChanged += (s, e) => ResetPStateFields();
            pstateFid.TextChanged += (s, e) => UpdatePStatePreview();
            pstateDid.TextChanged += (s, e) => UpdatePStatePreview();
            if (decodeContext.PStateLayout == PStateLayout.Zen5)
                label9.Text = "DID (n/a)";
            ResetPStateFields();
        }

        private List<PStateItem> GatherPStates()
        {
            var items = new List<PStateItem>();
            for (int id = 0; id < PStateMath.Count; id++)
            {
                ulong? raw = null;
                try
                {
                    var read = hardware.ReadPState(id);
                    if (read.Ok) raw = ((ulong)read.Edx << 32) | read.Eax;
                }
                catch { /* Keep failed probes visible and read-only; manual Read can retry. */ }
                items.Add(new PStateItem(id, raw));
            }
            return items;
        }

        private void ApplyPStateList(List<PStateItem> items)
        {
            pstateIdBox.Items.Clear();
            foreach (var item in items) pstateIdBox.Items.Add(item);
            pstateIdBox.Enabled = btnPstateRead.Enabled = true;
            if (items.Count > 0) pstateIdBox.SelectedIndex = 0;
        }

        private void ResetPStateFields()
        {
            pstateReadId = null;
            pstateFid.ReadOnly = pstateDid.ReadOnly = true;
            pstateFid.Text = "";
            pstateDid.Text = decodeContext.PStateLayout == PStateLayout.Zen5 ? "N/A" : "";
            pstateDid.Enabled = decodeContext.PStateLayout != PStateLayout.Zen5;
            pstateFrequency.Text = "";
            btnPstateWrite.Enabled = false;
            if (pstateVid != null) { pstateVid.ReadOnly = true; pstateVid.Text = ""; pstateVidVoltage.Text = ""; pstateVidWrite.Enabled = false; }
        }

        private void UpdatePStatePreview()
        {
            bool valid = PStateMath.TryParse(decodeContext.PStateLayout, pstateFid.Text, pstateDid.Text,
                out uint fid, out uint did, out _);
            double? mhz = valid ? PStateMath.FrequencyMhz(decodeContext.PStateLayout, fid, did) : null;
            pstateFrequency.Text = mhz.HasValue ? $"{mhz.Value:0.##} MHz" : "N/A";
            btnPstateWrite.Enabled = valid && hardware.CanWritePStates
                && pstateIdBox.SelectedItem is PStateItem item && pstateReadId == item.Id;
        }

        private async void BtnPstateRead_Click(object sender, EventArgs e)
        {
            ResetPStateFields();
            if (!(pstateIdBox.SelectedItem is PStateItem item)) return;
            try
            {
                var read = await RunHardwareAsync(() => hardware.ReadPState(item.Id));
                if (shuttingDown) return;
                if (!read.Ok)
                {
                    SetStatusText($"Error reading P-state {item.Id}.");
                    return;
                }
                ulong raw = ((ulong)read.Edx << 32) | read.Eax;
                PrependResult(RegisterDecoder.Decode(RegisterKind.Msr, PStateMath.BaseMsr + (uint)item.Id, raw, decodeContext));
                if (decodeContext.PStateLayout == PStateLayout.Unknown)
                {
                    SetStatusText("Unknown P-state layout; raw data shown in the log.");
                    return;
                }
                var value = PStateMath.Decode(raw, decodeContext.PStateLayout);
                pstateFid.Text = value.CpuFid.ToString(CultureInfo.InvariantCulture);
                if (decodeContext.PStateLayout == PStateLayout.FidDid)
                    pstateDid.Text = value.CpuDfsId.ToString(CultureInfo.InvariantCulture);
                pstateFid.ReadOnly = !value.Enabled;
                pstateDid.ReadOnly = !value.Enabled || decodeContext.PStateLayout != PStateLayout.FidDid;
                pstateReadId = value.Enabled ? (int?)item.Id : null;
                pstateVid.Text = PStateVidMath.Decode(raw, decodeContext.PStateVidEncoding).ToString(CultureInfo.InvariantCulture);
                pstateVid.ReadOnly = !value.Enabled || decodeContext.PStateVidEncoding == PStateVidEncoding.Unknown;
                UpdatePStateVidPreview();
                UpdatePStatePreview();
                SetStatusText(!value.Enabled ? $"P-state {item.Id} is disabled (PstateEn=0); read only." :
                    !hardware.CanWritePStates ? "P-state read; per-processor writes are unavailable with this core library/process." :
                    $"P-state {item.Id} successfully read.");
            }
            catch (Exception ex) { SetStatusText($"P-state read failed: {ex.Message}"); }
        }

        private async void BtnPstateWrite_Click(object sender, EventArgs e)
        {
            if (!(pstateIdBox.SelectedItem is PStateItem item) || pstateReadId != item.Id
                || !hardware.CanWritePStates)
            {
                SetStatusText("Read an enabled P-state before editing its frequency.");
                return;
            }
            if (!PStateMath.TryParse(decodeContext.PStateLayout, pstateFid.Text, pstateDid.Text,
                out uint fid, out uint did, out string error))
            {
                SetStatusText(error);
                return;
            }
            double? mhz = PStateMath.FrequencyMhz(decodeContext.PStateLayout, fid, did);
            var confirm = MessageBox.Show(
                $"This will change P-state {item.Id} to {mhz.Value:0.##} MHz on all active processors."
                + Environment.NewLine + "Setting a high frequency could crash/damage your system."
                + Environment.NewLine + "Do you want to continue?",
                "Confirm P-state change", MessageBoxButtons.OKCancel);
            if (confirm != DialogResult.OK) return;

            pstateReadId = null;
            btnPstateWrite.Enabled = false;
            PStateWriteResult result;
            try { result = await RunHardwareAsync(() => hardware.WritePStateFrequency(item.Id, decodeContext.PStateLayout, fid, did)); }
            catch (Exception ex) { if (!shuttingDown) SetStatusText(ex.Message); return; }
            if (shuttingDown) return;
            if (result.Success)
            {
                BtnPstateRead_Click(sender, e);
                SetStatusText($"P-state {item.Id}: {mhz.Value:0.##} MHz verified on {result.Completed} processors.");
            }
            else
            {
                string target = result.Target != null ? $" at {result.Target}" : "";
                SetStatusText($"P-state {item.Id}: {result.Status}{target}; "
                    + $"{result.Completed} processors verified. {result.Error}");
                if (result.Requested.HasValue)
                    PrependResult($"Requested MSR: 0x{result.Requested.Value:X16}; readback: "
                        + (result.Observed.HasValue ? $"0x{result.Observed.Value:X16}" : "unavailable")
                        + Environment.NewLine);
            }
        }

        private void PciScan_DoWork(object sender, DoWorkEventArgs e)
        {
            try
            {
                var range = (Tuple<uint, uint>)e.Argument;
                uint startReg = range.Item1, endReg = range.Item2;

                Invoke(new MethodInvoker(delegate
                {
                    SetStatusText("Scanning PCI addresses, please wait...");
                }));

                var result = new StringBuilder("REG         Value(HEX) Value(BIN)" + Environment.NewLine);

                // Lock the whole read loop (no UI Invoke happens inside it); the status and
                // result-form marshals are outside this region.
                lock (Hardware.Sync)
                {
                    Hardware.ThrowIfClosing();
                    if ((ulong)endReg - startReg > 262144) throw new ApplicationException("Scan at most 256 KiB at a time.");
                    for (ulong address = startReg; address <= endReg; address += 4)
                    {
                        Hardware.ThrowIfClosing();
                        startReg = (uint)address;
                        var data = cpu.ReadDword(startReg);
                        result.AppendLine($"0x{startReg:X8}: 0x{data:X8} {Convert.ToString(data, 2).PadLeft(32, '0')}");
                        string decoded = RegisterDecoder.Decode(RegisterKind.Pci, startReg, data, decodeContext);
                        if (!string.IsNullOrEmpty(decoded))
                            result.Append(decoded);

                    }
                }

                ShowResultForm("PCI Scan result", result.ToString());
            }
            catch (ApplicationException ex)
            {
                Invoke(new MethodInvoker(delegate
                {
                    SetButtonsState();
                    HandleError(ex.Message);
                }));
            }
        }

        private void Scan_WorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            SetButtonsState();
            SetStatusText("Scan Complete.");
        }

        private void SmuScan_WorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            int index = comboBoxMailboxSelect.SelectedIndex;
            PopulateMailboxesList(comboBoxMailboxSelect.Items);

            for (var i = 0; i < matches.Count; i++)
            {
                AddMailboxToList($"Mailbox {i + 1}", matches[i]);
            }

            if (index > comboBoxMailboxSelect.Items.Count)
                index = 0;

            comboBoxMailboxSelect.SelectedIndex = index;
            SetButtonsState();
            //ResetSmuAddresses();
            SetStatusText("Scan Complete.");
        }

        private void ButtonPciScan_Click(object sender, EventArgs e)
        {
            try
            {
                TryConvertToUint(textBoxPciStartReg.Text, out uint start);
                TryConvertToUint(textBoxPciEndReg.Text, out uint end);
                if (end <= start || (ulong)end - start > 262144) { HandleError("Select an increasing PCI range of at most 256 KiB."); return; }
                RunBackgroundTask(PciScan_DoWork, Scan_WorkerCompleted, Tuple.Create(start, end));
            }
            catch (Exception ex) { HandleError(ex.Message); }
        }

        private async void ButtonApplyPROCHOT_Click(object sender, EventArgs e)
        {
            try
            {
                if (checkBoxPROCHOT.Checked)
                {
                    // Enabling PROCHOT = normal operation. Exiting OC mode restores PBO /
                    // auto-boost and re-asserts PROCHOT; we must NOT enter OC mode here
                    // (doing so would pin all cores to the SMU's default OC frequency).
                    await DisableOCMode();
                }
                else
                {
                    // Disabling PROCHOT requires manual OC mode on Ryzen, which pins the
                    // all-core clock and disables PBO / auto-boost until PROCHOT is
                    // re-enabled. Make the trade-off explicit before applying it.
                    DialogResult choice = MessageBox.Show(
                        "Disabling PROCHOT requires the CPU to enter manual OC mode.\n\n" +
                        "While PROCHOT is disabled, all cores are pinned to a fixed frequency " +
                        "and PBO / auto-boost are turned off. Re-enable PROCHOT to restore " +
                        "normal operation.\n\nContinue?",
                        "Disable PROCHOT?",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Warning);

                    if (choice != DialogResult.Yes)
                    {
                        // User backed out: restore the checkbox to the enabled state.
                        checkBoxPROCHOT.Checked = true;
                        return;
                    }

                    bool accepted = await RunHardwareAsync(() => Hardware.Locked(() => EnableOCMode(false)));
                    if (!accepted) { SetStatusText("OC mode command rejected."); return; }
                }

                if (!checkBoxPROCHOT.Checked && await RunHardwareAsync(() => Hardware.Locked(() => cpu.IsProchotEnabled())) == true)
                {
                    checkBoxPROCHOT.Checked = true;
                    HandleError($@"Error, PROCHOT could not be disabled!");
                }

            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!shuttingDown) HandleError(ex.Message); }
        }

        private uint _msrScanStart;
        private uint _msrScanEnd;

        private void ReadMsr_Task(object sender, DoWorkEventArgs e)
        {
            Invoke(new MethodInvoker(delegate
            {
                SetStatusText("Scanning MSR range, please wait...");
            }));

            var result = new StringBuilder("MSR         EDX(63-32) EAX(31-0)" + Environment.NewLine);

            foreach (MsrReading reading in hardware.ScanMsrRange(_msrScanStart, _msrScanEnd))
            {
                result.AppendLine($"0x{reading.Address:X8}: 0x{reading.Edx:X8} 0x{reading.Eax:X8}");
                string decoded = RegisterDecoder.Decode(
                    RegisterKind.Msr, reading.Address, ((ulong)reading.Edx << 32) | reading.Eax, decodeContext);
                if (!string.IsNullOrEmpty(decoded))
                    result.Append(decoded);
            }

            ShowResultForm("MSR Scan result", result.ToString());
        }

        private async void ButtonMsrRead_Click(object sender, EventArgs e)
        {
            try
            {
                TryConvertToUint(textBoxMsrAddress.Text, out uint msr);
                MsrReadResult r = await RunHardwareAsync(() => hardware.ReadMsr(msr));
                if (r.Ok)
                {
                    textBoxMsrEdx.Text = $"0x{r.Edx:X8}";
                    textBoxMsrEax.Text = $"0x{r.Eax:X8}";

                    ulong value = ((ulong)r.Edx << 32) | r.Eax;
                    string decoded = RegisterDecoder.Decode(RegisterKind.Msr, msr, value, decodeContext);
                    if (!string.IsNullOrEmpty(decoded))
                        PrependResult(decoded + Environment.NewLine);
                }

            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!shuttingDown) HandleError(ex.Message); }
        }

        private async void ButtonMsrWrite_Click(object sender, EventArgs e)
        {
            try
            {
                TryConvertToUint(textBoxMsrEdx.Text, out uint edx);
                TryConvertToUint(textBoxMsrEax.Text, out uint eax);
                TryConvertToUint(textBoxMsrAddress.Text, out uint msr);

                if (!await RunHardwareAsync(() => hardware.WriteMsr(msr, eax, edx)))
                {
                    HandleError($@"Error writing MSR {textBoxMsrAddress.Text}!");
                    return;
                }

                SetStatusText("Write OK.");

            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!shuttingDown) HandleError(ex.Message); }
        }

        private void ButtonMsrScan_Click(object sender, EventArgs e)
        {
            try
            {
                TryConvertToUint(textBoxMsrStart.Text, out _msrScanStart);
                TryConvertToUint(textBoxMsrEnd.Text, out _msrScanEnd);
            }
            catch (ApplicationException ex)
            {
                HandleError(ex.Message);
                return;
            }
            RunBackgroundTask(ReadMsr_Task, Scan_WorkerCompleted);
        }

        private void ReadCPUID_Task(object sender, DoWorkEventArgs e)
        {
            try
            {
                Invoke(new MethodInvoker(delegate
                {
                    SetStatusText("Scanning CPUID range, please wait...");
                }));

                var result = new StringBuilder("CPUID       EAX        EBX        ECX        EDX" + Environment.NewLine);
                uint LFuncStd = 0, LFuncExt = 0;
                uint eax = 0, ebx = 0, ecx = 0, edx = 0;

                lock (Hardware.Sync)
                {
                    Hardware.ThrowIfClosing();
                    if (cpu.Cpuid(0x00000000, ref eax, ref ebx, ref ecx, ref edx))
                        LFuncStd = eax;

                    if (cpu.Cpuid(0x80000000, ref eax, ref ebx, ref ecx, ref edx))
                        LFuncExt = eax - 0x80000000;

                    LFuncStd = Math.Min(LFuncStd, 65535);
                    LFuncExt = Math.Min(LFuncExt, 65535);
                    for (uint i = 0; i <= LFuncStd; ++i)
                    {
                        Hardware.ThrowIfClosing();
                        var index = 0x00000000 + i;
                        cpu.Cpuid(index, ref eax, ref ebx, ref ecx, ref edx);
                        result.AppendLine($"0x{index:X8}: 0x{eax:X8} 0x{ebx:X8} 0x{ecx:X8} 0x{edx:X8}");
                        string decoded = RegisterDecoder.Decode(RegisterKind.Cpuid, index, eax, decodeContext);
                        if (!string.IsNullOrEmpty(decoded))
                            result.Append(decoded);
                    }

                    for (uint i = 0; i <= LFuncExt; ++i)
                    {
                        Hardware.ThrowIfClosing();
                        var index = 0x80000000 + i;
                        cpu.Cpuid(index, ref eax, ref ebx, ref ecx, ref edx);
                        result.AppendLine($"0x{index:X8}: 0x{eax:X8} 0x{ebx:X8} 0x{ecx:X8} 0x{edx:X8}");
                        string decoded = RegisterDecoder.Decode(RegisterKind.Cpuid, index, eax, decodeContext);
                        if (!string.IsNullOrEmpty(decoded))
                            result.Append(decoded);
                    }
                }

                ShowResultForm("CPUID Scan result", result.ToString());
            }
            catch (ApplicationException ex)
            {
                Invoke(new MethodInvoker(delegate
                {
                    SetButtonsState();
                    HandleError(ex.Message);
                }));
            }
        }

        private async void ButtonCPUIDRead_Click(object sender, EventArgs e)
        {
            try
            {
                TryConvertToUint(textBoxCPUIDAddress.Text, out uint index);
                uint eax = 0, ebx = 0, ecx = 0, edx = 0;
                uint cEax = 0, cEbx = 0, cEcx = 0, cEdx = 0;
                bool ok = await RunHardwareAsync(() => Hardware.Locked(() => cpu.Cpuid(index, ref cEax, ref cEbx, ref cEcx, ref cEdx)));
                eax = cEax; ebx = cEbx; ecx = cEcx; edx = cEdx;
                if (ok)
                {
                    textBoxCPUIDeax.Text = $"0x{eax:X8}";
                    textBoxCPUIDebx.Text = $"0x{ebx:X8}";
                    textBoxCPUIDecx.Text = $"0x{ecx:X8}";
                    textBoxCPUIDedx.Text = $"0x{edx:X8}";

                    string decoded = RegisterDecoder.Decode(RegisterKind.Cpuid, index, eax, decodeContext);
                    if (!string.IsNullOrEmpty(decoded))
                        PrependResult(decoded + Environment.NewLine);
                }

            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!shuttingDown) HandleError(ex.Message); }
        }

        private void ButtonCPUIDScan_Click(object sender, EventArgs e)
        {
            RunBackgroundTask(ReadCPUID_Task, Scan_WorkerCompleted);
        }

        private void ButtonPMTable_Click(object sender, EventArgs e)
        {
            if (cpu.Status == IODriver.LibStatus.OK)
                new PowerTableMonitor(cpu).Show(this);
            else
                HandleError("IO driver is not responding or not loaded.");
        }

        private void ButtonSMUMonitor_Click(object sender, EventArgs e)
        {
            TryConvertToUint(textBoxCMDAddress.Text, out uint addrMsg);
            TryConvertToUint(textBoxRSPAddress.Text, out uint addrRsp);
            TryConvertToUint(textBoxARGAddress.Text, out uint addrArg);

            new SMUMonitor(cpu, addrMsg, addrArg, addrRsp).Show(this);
        }

        private void ComboBoxMailboxSelect_SelectedIndexChanged(object sender, EventArgs e)
        {
            MailboxListItem item = comboBoxMailboxSelect.SelectedItem as MailboxListItem;
            InitTestMailbox(item.msgAddr, item.rspAddr, item.argAddr);
        }

        private void SettingsForm_FormClosed(object sender, FormClosedEventArgs e) { }

        private async void buttonGetCO_Click(object sender, EventArgs e)
        {
            try
            {
                var values = await RunHardwareAsync(() => Hardware.Locked(() => GatherCoMargins()));
                if (!shuttingDown) { ApplyCoMargins(values); SetStatusText("CO read complete."); }
            }
            catch (Exception ex) { if (!shuttingDown) SetStatusText(ex.Message); }
        }

        private uint EncodeCoreMarginBitmask(int coreIndex, int coresPerCCD = 8)
        {
            return hardware.Topology.Find(coreIndex)?.Mask ?? throw new InvalidOperationException("Core target unavailable.");
        }

        private async Task ApplyCO()
        {
            var profile = new Profile();
            for (int i = 0; i < GetPhysicalCoreCount(); i++)
            {
                var control = GetCOControl(i);
                if (control != null && control.Enabled && IsCoreEnabled(i)) profile.CoMargins[i] = (int)control.Value;
            }
            await ApplyTuningAsync(profile);
        }

        private async void ButtonApplyCO_Click(object sender, EventArgs e) { await ApplyCO(); }

        private string GetWmiInstanceName()
        {
            try
            {
                instanceName = WMI.GetInstanceName(wmiScope, wmiAMDACPI);
            }
            catch
            {
                // ignored
            }

            return instanceName;
        }

        // Background-safe: runs the WMI enumeration (the slow part) and returns the combo
        // items to add. No control access. Sets instanceName/classInstance/pack fields, which
        // are only read by handlers that fire after the load completes.
        private List<object> GatherWmiCommands()
        {
            var items = new List<object>();
            try
            {
                instanceName = GetWmiInstanceName();
                classInstance = new ManagementObject(wmiScope,
                    $"{wmiAMDACPI}.InstanceName='{instanceName}'",
                    null);

                // Get function names with their IDs
                string[] functionObjects = { "GetObjectID", "GetObjectID2" };

                foreach (var functionObject in functionObjects)
                {
                    try
                    {
                        pack = WMI.InvokeMethodAndGetValue(classInstance, functionObject, "pack", null, 0);

                        if (pack != null)
                        {
                            var ID = (uint[])pack.GetPropertyValue("ID");
                            var IDString = (string[])pack.GetPropertyValue("IDString");
                            var Length = (byte)pack.GetPropertyValue("Length");

                            for (var i = 0; i < Length; ++i)
                            {
                                if (IDString[i] == "")
                                    break;

                                items.Add(new WmiCmdListItem($"{IDString[i] + ": "}{ID[i]:X8}", ID[i], !IDString[i].StartsWith("Get")));
                            }
                        }
                        else
                        {
                            items.Add("<FAILED>");
                        }
                    }
                    catch
                    {
                        // ignored
                    }
                }
            }
            catch
            {
                // ignored
            }
            return items;
        }

        // UI thread: add the gathered WMI commands to the combo. Selecting index 0 triggers
        // one Getdvalues lookup for the first command (still on the UI thread, but a single
        // call rather than the whole enumeration).
        private void ApplyWmiCommands(List<object> items)
        {
            if (items == null)
                return;
            foreach (var item in items)
                comboBoxAvailableCommands.Items.Add(item);
            if (comboBoxAvailableCommands.Items.Count > 0)
                comboBoxAvailableCommands.SelectedIndex = 0;
        }

        private async void ComboBoxAvailableCommands_SelectedIndexChanged(object sender, EventArgs e)
        {
            var command = comboBoxAvailableCommands.SelectedItem as WmiCmdListItem;
            comboBoxAvailableValues.Items.Clear();
            comboBoxAvailableValues.Enabled = textBoxWmiArgument.Enabled = false;
            textBoxWmiArgument.Text = "";
            if (command == null) return;
            if (!command.isSet) { comboBoxAvailableValues.Items.Add("Get commands don't support values"); comboBoxAvailableValues.SelectedIndex = 0; return; }
            try
            {
                uint[] values = await RunHardwareAsync(() => Hardware.Locked(() =>
                {
                    var data = WMI.InvokeMethodAndGetValue(classInstance, "Getdvalues", "pack", "ID", command.value);
                    return data == null ? null : (uint[])data.GetPropertyValue("DValuesBuffer");
                }));
                if (shuttingDown || comboBoxAvailableCommands.SelectedItem != command) return;
                foreach (uint value in values ?? new uint[0])
                    if (value != 0) comboBoxAvailableValues.Items.Add(new WmiCmdListItem(value.ToString(), value));
                comboBoxAvailableValues.Enabled = comboBoxAvailableValues.Items.Count > 0;
                textBoxWmiArgument.Enabled = true;
                if (comboBoxAvailableValues.Items.Count > 0) comboBoxAvailableValues.SelectedIndex = 0;
            }
            catch (Exception ex) { if (!shuttingDown) SetStatusText("ACPI values: " + ex.Message); }
        }

        private void ComboBoxAvailableValues_SelectedIndexChanged(object sender, EventArgs e)
        {
            WmiCmdListItem command = comboBoxAvailableCommands.SelectedItem as WmiCmdListItem;
            if (command != null && command.isSet && comboBoxAvailableValues.Enabled)
                textBoxWmiArgument.Text = comboBoxAvailableValues.Text;
            else
                textBoxWmiArgument.Text = "";
        }

        private async void ButtonWmiCmdSend_Click(object sender, EventArgs e)
        {
            try
            {
                WmiCmdListItem command = comboBoxAvailableCommands.SelectedItem as WmiCmdListItem;
                if (command == null) return;

                uint value = 0;
                if (command.isSet)
                {
                    if (!uint.TryParse(textBoxWmiArgument.Text.Trim(), out value))
                    {
                        HandleError("Enter a valid numeric argument (0 - 65535).");
                        return;
                    }
                }

                if (value < 0x10000)
                {
                    var response = await RunHardwareAsync(() => Hardware.Locked(() => WMI.RunCommand(classInstance, command.value, value)));
                    var text = command.text + Environment.NewLine + "------------------------" + Environment.NewLine;
                    foreach (byte b in response)
                    {
                        text += "0x" + b.ToString("X2") + Environment.NewLine;
                    }
                    text += "------------------------" + Environment.NewLine;
                    PrependResult(text + Environment.NewLine);
                }

            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!shuttingDown) HandleError(ex.Message); }
        }

        private async void ButtonBCLKApply_Click(object sender, EventArgs e)
        {
            try
            {
                if (!double.TryParse(numericUpDownBclk.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double targetBclk)
                    && !double.TryParse(numericUpDownBclk.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out targetBclk))
                {
                    HandleError("Invalid BCLK value.");
                    return;
                }
                double? currentBclk = await RunHardwareAsync(() => Hardware.Locked(() =>
                {
                    cpu.SetBclk(targetBclk);
                    return cpu.GetBclk();
                }));
                labelBCLK.Text = currentBclk + " MHz";
                numericUpDownBclk.Text = $"{currentBclk}";

            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { if (!shuttingDown) HandleError(ex.Message); }
        }

        private void BulkMarginChangeHandler(int ccd, int step = 1)
        {
            int startCore = ccd * 8;
            int endCore = Math.Min(startCore + 8, GetPhysicalCoreCount());

            for (var i = startCore; i < endCore; ++i)
            {
                NumericUpDown control = GetCOControl(i);
                if (control != null && control.Enabled && IsCoreEnabled(i))
                {
                    decimal newValue = control.Value + step;
                    newValue = Math.Max(control.Minimum, Math.Min(control.Maximum, newValue));
                    control.Value = newValue;
                }
            }
        }

        private void ButtonCpuidDecode_Click(object sender, EventArgs e)
        {
            TryConvertToUint(textBoxCpuid.Text.Trim(), out uint eax);

            Cpu.CPUInfo info = new Cpu.CPUInfo
            {
                cpuid = eax
            };
            info.family = (Family)(((info.cpuid & 0xf00) >> 8) + ((info.cpuid & 0xff00000) >> 20));
            info.baseModel = (info.cpuid & 0xf0) >> 4;
            info.extModel = (info.cpuid & 0xf0000) >> 16;
            info.model = info.baseModel + info.extModel * 0x10;
            info.stepping = eax & 0xf;

            string responseString =
                Environment.NewLine +
                $"cpuid: 0x{info.cpuid:X8}" +
                Environment.NewLine +
                $"family: {info.family} ({(uint)info.family:X2}h)" +
                Environment.NewLine +
                $"base model: 0x{info.baseModel:X1}" +
                Environment.NewLine +
                $"ext. model: 0x{info.extModel:X1}" +
                Environment.NewLine +
                $"model: 0x{info.model:X2}" +
                Environment.NewLine +
                $"stepping: {info.stepping}" +
                Environment.NewLine +
                Environment.NewLine;

            Invoke(new MethodInvoker(delegate
            {
                PrependResult(responseString);
            }));
        }

        private void BtnSaveCOProfile_Click(object sender, EventArgs e)
        {
            List<Tuple<int, int>> margins = new List<Tuple<int, int>>();

            if ((cpu.smu.Rsmu.SMU_MSG_SetDldoPsmMargin != 0 || cpu.smu.Mp1Smu.SMU_MSG_SetDldoPsmMargin != 0))
            {
                for (var i = 0; i < GetPhysicalCoreCount(); i++)
                {
                    NumericUpDown control = GetCOControl(i);
                    if (control != null && control.Enabled)
                    {
                        margins.Add(new Tuple<int, int>(i, Convert.ToInt32(control.Value)));
                    }
                }
            }

            if (margins.Count > 0)
            {
                try
                {
                    using (StreamWriter file = new StreamWriter(defaultsPath))
                    {
                        foreach (var entry in margins)
                            file.WriteLine("[{0},{1}]", entry.Item1, entry.Item2);

                        file.WriteLine("fmax={0}", numericUpDownFmax.Value);

                        PrependResult($"Profile saved in {defaultsPath}" + Environment.NewLine);
                    }
                }
                catch (Exception)
                {
                    HandleError("Could not save profile to file!");
                }
            }
        }

        private Profile GatherProfileFromUi(string name)
        {
            if (editControls != null) {
                var snapshot = editControls.Capture(); snapshot.Name = name;
                hardware.Topology.Bind(snapshot); return snapshot;
            }
            var profile = new Profile { Name = name };

            if ((cpu.smu.Rsmu.SMU_MSG_SetDldoPsmMargin != 0 || cpu.smu.Mp1Smu.SMU_MSG_SetDldoPsmMargin != 0))
            {
                for (var i = 0; i < GetPhysicalCoreCount(); i++)
                {
                    NumericUpDown control = GetCOControl(i);
                    if (control != null && control.Enabled)
                        profile.CoMargins[i] = Convert.ToInt32(control.Value);
                }
            }

            profile.CurveShaperTiers = new List<CurveShaperTier>();
            for (int tier = 0; tier < 5; tier++)
                profile.CurveShaperTiers.Add(new CurveShaperTier
                {
                    Low = (int)CsGrid[tier][0].Value,
                    Medium = (int)CsGrid[tier][1].Value,
                    High = (int)CsGrid[tier][2].Value
                });

            profile.Fmax = numericUpDownFmax.Value;

            profile.PptWatts = (int)numericUpDownPpt.Value;
            profile.TdcAmps = (int)numericUpDownTdc.Value;
            profile.EdcAmps = (int)numericUpDownEdc.Value;
            profile.PboScalar = (int)numericUpDownPboScalar.Value;

            hardware.Topology.Bind(profile);
            return profile;
        }

        private void ApplyProfileToUi(Profile profile)
        {
            if (profile == null) return;
            string topologyError = hardware.Topology.Validate(profile);
            if (topologyError != null) throw new InvalidOperationException(topologyError);
            foreach (var entry in coControls)
                entry.Value.Enabled = IsCoreEnabled(entry.Key) && profile.CoMargins != null && profile.CoMargins.ContainsKey(entry.Key);

            if (profile.CoMargins != null && (cpu.smu.Rsmu.SMU_MSG_SetDldoPsmMargin != 0 || cpu.smu.Mp1Smu.SMU_MSG_SetDldoPsmMargin != 0))
            {
                foreach (var kv in profile.CoMargins)
                {
                    NumericUpDown control = GetCOControl(kv.Key);
                    if (control != null && control.Enabled)
                        control.Value = Math.Max(control.Minimum, Math.Min(control.Maximum, kv.Value));
                }
            }

            if (profile.CurveShaperTiers != null && profile.CurveShaperTiers.Count >= 5)
            {
                for (int tier = 0; tier < 5; tier++)
                    SetCsTier(CsGrid[tier][0], CsGrid[tier][1], CsGrid[tier][2], profile.CurveShaperTiers[tier]);
            }

            if (profile.Fmax.HasValue)
                numericUpDownFmax.Value =
                    Math.Max(numericUpDownFmax.Minimum, Math.Min(numericUpDownFmax.Maximum, profile.Fmax.Value));

            if (profile.PptWatts.HasValue)
                numericUpDownPpt.Value = Math.Max(numericUpDownPpt.Minimum, Math.Min(numericUpDownPpt.Maximum, profile.PptWatts.Value));
            if (profile.TdcAmps.HasValue)
                numericUpDownTdc.Value = Math.Max(numericUpDownTdc.Minimum, Math.Min(numericUpDownTdc.Maximum, profile.TdcAmps.Value));
            if (profile.EdcAmps.HasValue)
                numericUpDownEdc.Value = Math.Max(numericUpDownEdc.Minimum, Math.Min(numericUpDownEdc.Maximum, profile.EdcAmps.Value));
            if (profile.PboScalar.HasValue)
                numericUpDownPboScalar.Value = Math.Max(numericUpDownPboScalar.Minimum, Math.Min(numericUpDownPboScalar.Maximum, profile.PboScalar.Value));
        }

        private static void SetCsTier(NumericUpDown low, NumericUpDown med, NumericUpDown high, CurveShaperTier tier)
        {
            if (tier == null) return;
            low.Value = Math.Max(low.Minimum, Math.Min(low.Maximum, tier.Low));
            med.Value = Math.Max(med.Minimum, Math.Min(med.Maximum, tier.Medium));
            high.Value = Math.Max(high.Minimum, Math.Min(high.Maximum, tier.High));
        }

        private List<Tuple<int, int>> LoadCOProfile()
        {
            List<Tuple<int, int>> margins = new List<Tuple<int, int>>();
            try
            {
                if (!Directory.Exists(profilesPath))
                {
                    MessageBox.Show("Profiles directory does not exist, created one for you.");
                    Directory.CreateDirectory(profilesPath);
                }

                // load from file if it exists
                if (File.Exists(defaultsPath))
                {
                    var lines = File.ReadAllLines(defaultsPath);
                    foreach (var line in lines)
                    {
                        if (line.StartsWith("["))
                        {
                            var values = line.Replace("[", "").Replace("]", "").Replace(" ", "").Split(',');
                            Int32.TryParse(values[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int index);
                            Int32.TryParse(values[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int margin);
                            margins.Add(new Tuple<int, int>(index, margin));
                        }
                        else if (line.StartsWith("fmax="))
                        {
                            var fmaxStr = line.Substring(5);
                            if (decimal.TryParse(fmaxStr, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal fmaxVal))
                                fmaxVal = Math.Max(numericUpDownFmax.Minimum, Math.Min(numericUpDownFmax.Maximum, fmaxVal));
                            else
                                fmaxVal = numericUpDownFmax.Value;
                            // store temporarily in Tag for retrieval in BtnLoadCOProfile_Click
                            numericUpDownFmax.Tag = fmaxVal;
                        }
                    }
                }
                else
                {
                    HandleError("No CO profile saved.");
                }
            }
            catch (Exception)
            {
                HandleError("Could not load saved profile!");
            }

            return margins;
        }

        private void BtnLoadCOProfile_Click(object sender, EventArgs e)
        {
            numericUpDownFmax.Tag = null;
            List<Tuple<int, int>> margins = LoadCOProfile();

            if (margins.Count > 0 && (cpu.smu.Rsmu.SMU_MSG_SetDldoPsmMargin != 0 || cpu.smu.Mp1Smu.SMU_MSG_SetDldoPsmMargin != 0))
            {
                for (var i = 0; i < margins.Count; i++)
                {
                    NumericUpDown control = GetCOControl(margins[i].Item1);
                    if (control != null && control.Enabled)
                    {
                        control.Value = margins[i].Item2;
                    }
                }

                if (numericUpDownFmax.Tag is decimal savedFmax)
                    numericUpDownFmax.Value = savedFmax;

                PrependResult($"Saved CO profile loaded from {defaultsPath}" + Environment.NewLine);
            }
        }

        private void ComboBoxStartupProfile_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_suppressStartupTaskUpdates) return;
            if (checkBoxApplyCOStartup.Checked) RegisterOrRemoveStartupTask();
        }

        private void RegisterOrRemoveStartupTask()
        {
            string name = comboBoxStartupProfile?.SelectedItem as string;
            if (checkBoxApplyCOStartup.Checked && !string.IsNullOrEmpty(name))
            {
                if (StartupTaskService.Exists()) StartupTaskService.Remove();
                StartupTaskService.Register(Application.ExecutablePath, name, 5);
            }
            else if (!checkBoxApplyCOStartup.Checked && StartupTaskService.Exists())
            {
                StartupTaskService.Remove();
            }
        }

        private void CheckBoxApplyCOStartup_CheckedChanged(object sender, EventArgs e)
        {
            if (_suppressStartupTaskUpdates) return;

            string name = comboBoxStartupProfile?.SelectedItem as string;
            if (checkBoxApplyCOStartup.Checked)
            {
                if (string.IsNullOrEmpty(name))
                {
                    HandleError("Select a startup profile first.");
                    checkBoxApplyCOStartup.Checked = false;
                    return;
                }
                // The logon task bakes in this profile NAME and applies it headlessly (no UI)
                // from this exe's profiles folder. If the profile isn't there, the startup run
                // silently fails with "Profile not found" — so refuse to enable a doomed task.
                if (profileManager.Load(name) == null)
                {
                    HandleError($"Profile '{name}' was not found in this build's profiles folder, " +
                        "so it can't be applied at startup. Save it first, then enable this.");
                    checkBoxApplyCOStartup.Checked = false;
                    return;
                }
            }

            try
            {
                RegisterOrRemoveStartupTask();
            }
            catch (Exception ex)
            {
                // Surface a real error and snap the checkbox back to the actual task state,
                // instead of letting it bubble to the generic thread-exception popup.
                HandleError($"Could not update the startup task: {ex.Message}");
                _suppressStartupTaskUpdates = true;
                checkBoxApplyCOStartup.Checked = StartupTaskService.Exists();
                _suppressStartupTaskUpdates = false;
                return;
            }

            PrependResult(checkBoxApplyCOStartup.Checked
                ? $"Startup apply enabled for '{name}'." + Environment.NewLine
                : "Startup apply disabled." + Environment.NewLine);
        }

        // Surfaces the most recent headless startup-apply outcome (from profiles\apply.log) in
        // the results pane, so a silent logon-task failure (e.g. a missing profile) is visible.
        private void ShowLastStartupApplyResult()
        {
            try
            {
                string logPath = Path.Combine(profilesPath, "apply.log");
                if (!File.Exists(logPath)) return;

                string[] lines;
                using (var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    stream.Seek(Math.Max(0, stream.Length - 65536), SeekOrigin.Begin);
                    using (var reader = new StreamReader(stream)) lines = reader.ReadToEnd().Split('\n');
                }
                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    if (!string.IsNullOrWhiteSpace(lines[i]))
                    {
                        PrependResult($"Last startup apply: {lines[i].Trim()}" + Environment.NewLine);
                        return;
                    }
                }
            }
            catch { /* never block startup on a log read */ }
        }

        private async void ButtonApplyCoreMap_Click(object sender, EventArgs e)
        {
            try
            {
                if (hardware.Topology.Error != null || hardware.Topology.Targets.Any(t => t.Ccd > 1)) { SetStatusText("Downcore controls support only a validated one/two-CCD map."); return; }
                uint smt = checkBoxSMT.Checked ? 1U : 0U;
                uint ccd0 = 0x8000;
                uint ccd1 = 0x8100;

                for (int i = 0; i < 8; i++)
                {
                    CheckBox control = GetCoreCheckBox(i);
                    if (control != null && control.Enabled)
                    {
                        if (!control.Checked)
                        {
                            int logicalIndex = Convert.ToInt32(control.Tag as string);
                            ccd0 = Utils.SetBits(ccd0, logicalIndex, 1, 1);
                        }
                    }
                }

                for (int i = 0; i < 8; i++)
                {
                    CheckBox control = GetCoreCheckBox(i + 8);
                    if (control != null && control.Enabled)
                    {
                        if (!control.Checked)
                        {
                            int logicalIndex = Convert.ToInt32(control.Tag as string);
                            ccd1 = Utils.SetBits(ccd1, logicalIndex, 1, 1);
                        }
                    }
                }

                var cmdItem = comboBoxAvailableCommands.Items
                         .OfType<WmiCmdListItem>()
                         .FirstOrDefault(item => item.text.Contains("Software Downcore Config"));

                if (cmdItem != null)
                {
                    await RunHardwareAsync(() => Hardware.Locked(() => WMI.RunCommand(classInstance, cmdItem.value, ccd0)));
                    await RunHardwareAsync(() => Hardware.Locked(() => WMI.RunCommand(classInstance, cmdItem.value, ccd1)));
                }

                cmdItem = comboBoxAvailableCommands.Items
                         .OfType<WmiCmdListItem>()
                         .FirstOrDefault(item => item.text.Contains("Set SMTEn"));

                if (cmdItem != null)
                {
                    await RunHardwareAsync(() => Hardware.Locked(() => WMI.RunCommand(classInstance, cmdItem.value, smt)));
                }

                ConfirmWindowsRestart();

            }
            catch (Exception ex) { if (!shuttingDown) HandleError(ex.Message); }
        }

        private async void Button5_Click(object sender, EventArgs e)
        {
            try
            {
                if (hardware.Topology.Error != null || !hardware.Topology.Targets.Any(t => t.Ccd == 0) || !hardware.Topology.Targets.Any(t => t.Ccd == 1) || hardware.Topology.Targets.Any(t => t.Ccd > 1)) { SetStatusText("This preset requires a validated two-CCD map."); return; }
                var cmdItem = comboBoxAvailableCommands.Items
                         .OfType<WmiCmdListItem>()
                         .FirstOrDefault(item => item.text.Contains("Software Downcore Config"));

                if (cmdItem != null)
                {
                    await RunHardwareAsync(() => Hardware.Locked(() => WMI.RunCommand(classInstance, cmdItem.value, 0x8000)));
                    await RunHardwareAsync(() => Hardware.Locked(() => WMI.RunCommand(classInstance, cmdItem.value, 0x81FF)));
                }

                cmdItem = comboBoxAvailableCommands.Items
                         .OfType<WmiCmdListItem>()
                         .FirstOrDefault(item => item.text.Contains("Set SMTEn"));

                if (cmdItem != null)
                {
                    await RunHardwareAsync(() => Hardware.Locked(() => WMI.RunCommand(classInstance, cmdItem.value, 0)));
                }

                ConfirmWindowsRestart();

            }
            catch (Exception ex) { if (!shuttingDown) HandleError(ex.Message); }
        }

        private async void Button6_Click(object sender, EventArgs e)
        {
            try
            {
                if (hardware.Topology.Error != null || !hardware.Topology.Targets.Any(t => t.Ccd == 0) || !hardware.Topology.Targets.Any(t => t.Ccd == 1) || hardware.Topology.Targets.Any(t => t.Ccd > 1)) { SetStatusText("This preset requires a validated two-CCD map."); return; }
                var cmdItem = comboBoxAvailableCommands.Items
                         .OfType<WmiCmdListItem>()
                         .FirstOrDefault(item => item.text.Contains("Software Downcore Config"));

                if (cmdItem != null)
                {
                    await RunHardwareAsync(() => Hardware.Locked(() => WMI.RunCommand(classInstance, cmdItem.value, 0x8000)));
                    await RunHardwareAsync(() => Hardware.Locked(() => WMI.RunCommand(classInstance, cmdItem.value, 0x8100)));
                }

                cmdItem = comboBoxAvailableCommands.Items
                         .OfType<WmiCmdListItem>()
                         .FirstOrDefault(item => item.text.Contains("Set SMTEn"));

                if (cmdItem != null)
                {
                    await RunHardwareAsync(() => Hardware.Locked(() => WMI.RunCommand(classInstance, cmdItem.value, 1)));
                }

                ConfirmWindowsRestart();

            }
            catch (Exception ex) { if (!shuttingDown) HandleError(ex.Message); }
        }

        private void ConfirmWindowsRestart()
        {
            var result = MessageBox.Show(
                "A restart is required to apply the changes. Would you like to restart now?",
                "Confirm Restart",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);

            if (result == DialogResult.Yes)
            {
                try
                {
                    // Restart Windows
                    Process.Start(new ProcessStartInfo("shutdown", "/r /t 0")
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false
                    });
                }
                catch (Exception ex)
                {
                    HandleError($"Failed to restart: {ex.Message}");
                }
            }
        }

        private void RadioButtonManualCoreControl_CheckedChanged(object sender, EventArgs e)
        {
            bool manual = radioButtonManualCoreControl.Checked == true;
            panelManualCoreControl.Enabled = manual;
            panelX3D.Enabled = !manual;
        }

        private async void ButtonApplyFMax_Click(object sender, EventArgs e)
        {
            await ApplyTuningAsync(new Profile { Fmax = numericUpDownFmax.Value });
        }

        private void ButtonPCIRangeMonitor_Click(object sender, EventArgs e)
        {
            TryConvertToUint(textBoxPciStartReg.Text, out uint startAddress);
            TryConvertToUint(textBoxPciEndReg.Text, out uint endAddress);

            try { new PCIRangeMonitor(cpu, startAddress, endAddress).Show(this); }
            catch (Exception ex) { HandleError(ex.Message); }
        }

        private async void ButtonDump_Click(object sender, EventArgs e)
        {
            string name = textBoxDumpName.Text.Trim();

            if (string.IsNullOrWhiteSpace(name))
            {
                HandleError("Please specify a valid file name!");
                return;
            }

            if (File.Exists(name))
            {
                var result = MessageBox.Show(
                    $"File {name} already exists. Overwrite?",
                    "Confirm Overwrite",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (result != DialogResult.Yes)
                {
                    return;
                }
            }

            uint startAddress, endAddress;
            try
            {
                TryConvertToUint(textBoxDumpStartAddress.Text.Trim(), out startAddress);
                TryConvertToUint(textBoxDumpEndAddress.Text.Trim(), out endAddress);
            }
            catch (Exception)
            {
                HandleError("Invalid address format!");
                return;
            }

            // Run the dump off the UI thread so the window stays responsive; disable the
            // button to prevent a second dump to the same file while one is in progress.
            var button = sender as Control;
            if (button != null) button.Enabled = false;
            try
            {
                SetStatusText(name + ": Dumping memory, please wait...");

                var stopwatch = Stopwatch.StartNew();
                await RunHardwareAsync(() => { MemoryDumper.Dump32BitAddressSpaceAsBytes(cpu, name, startAddress, endAddress); return true; });
                if (shuttingDown) return;
                stopwatch.Stop();

                string elapsedTime = $"{stopwatch.Elapsed.TotalSeconds:F2}";
                SetStatusText(name + $": Dump complete. ({elapsedTime}s)");
                MessageBox.Show($"Memory dump completed successfully to file: {name}\n\nTime elapsed: {elapsedTime}s", "Dump Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                if (!shuttingDown) HandleError($"Memory dump failed: {ex.Message}");
            }
            finally
            {
                if (button != null) button.Enabled = true;
            }
        }

        private async void ButtonRefreshCS_Click(object sender, EventArgs e)
        {
            try
            {
                var values = await RunHardwareAsync(() => Hardware.Locked(() => cpu.GetAllCurveShaperMargins()));
                if (!shuttingDown) { ApplyCurveShaperValues(CurveShaperCodec.ResolveDisplay(values, _lastAppliedCurveShaper), false, !CurveShaperCodec.IsAllZero(values)); SetStatusText("Curve Shaper read complete; session fallback used if hardware reports no values."); }
            }
            catch (Exception ex) { if (!shuttingDown) SetStatusText(ex.Message); }
        }

        private async void ButtonApplyCS_Click(object sender, EventArgs e)
        {
            var profile = new Profile { CurveShaperTiers = new List<CurveShaperTier>() };
            for (int tier = 0; tier < 5; tier++)
                profile.CurveShaperTiers.Add(new CurveShaperTier
                {
                    Low = (int)CsGrid[tier][0].Value,
                    Medium = (int)CsGrid[tier][1].Value,
                    High = (int)CsGrid[tier][2].Value
                });
            await ApplyTuningAsync(profile);
        }
    }
}
