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
using System.Windows.Forms;
using ZenStates.Core;
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

        public SettingsForm()
        {
            InitializeComponent();
            _numaUtil = new NUMAUtil();
            textBoxResult.Text = $@"Detected NUMA nodes. ({_numaUtil.HighestNumaNode + 1})" + textBoxResult.Text;

            try
            {
                profilesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, profilesFolderName);
                defaultsPath =  Path.Combine(profilesPath, filename);

                profileManager = new ProfileManager(profilesPath);
                profileManager.EnsureDirectory();
                profileManager.MigrateLegacyIfNeeded();

                cpu = new Cpu();

                InitForm();
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, Resources.Error);
                Dispose();
                ExitApplication();
            }
        }

        private void ExitApplication()
        {
            cpu?.Dispose();

            if (Application.MessageLoop)
                Application.Exit();
            else
                Environment.Exit(1);
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
                smuInfoLabel.Text = cpu.systemInfo.SmuVersionString;
                firmwareInfoLabel.Text = $"{cpu.systemInfo.PatchLevel:X8}";
                cpuIdLabel.Text = $"{cpu.systemInfo.CpuIdString} ({cpu.info.codeName})";
                decodeContext = new DecodeContext
                {
                    VidToVoltage = SmuDecodeAdapter.GetVidToVoltage(cpu.info.codeName)
                };
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
            DisplaySystemInfo();

            pstateIdBox.SelectedIndex = 0;

            pstateDid.KeyDown += PstateFidDid_KeyDown;
            pstateDid.KeyPress += PstateFidDid_KeyPress;
            pstateDid.KeyUp += PstateFidDid_KeyUp;
            pstateFid.KeyDown += PstateFidDid_KeyDown;
            pstateFid.KeyPress += PstateFidDid_KeyPress;
            pstateFid.KeyUp += PstateFidDid_KeyUp;

            PopulateMailboxesList(comboBoxMailboxSelect.Items);

            // UI layout is built synchronously here (it only touches in-memory topology),
            // but the slow SMU command round-trips and WMI enumeration are deferred to a
            // background load (see OnShown/StartHardwareLoad) so the window appears promptly
            // instead of freezing while every per-core margin etc. is read on the UI thread.
            InitCoreControl();
            InitPboLayout();
            BuildFrequencyTab();
            InitStartupProfileUi();

            comboBoxMailboxSelect.SelectedIndex = 0;

            ToolTip toolTip = new ToolTip();
            // Checked state mirrors IsProchotEnabled(): checked = PROCHOT (thermal
            // throttling) ON. Uncheck + Apply to DISABLE throttling. The old tooltip read
            // as "checking disables throttling", which is backwards.
            toolTip.SetToolTip(checkBoxPROCHOT,
                "Checked = PROCHOT (thermal throttling) enabled. Uncheck and click Apply to " +
                "disable temperature throttling - useful on extreme cooling, but risks overheating.");

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

        // The window is shown before any SMU reads happen; once it's visible (handle
        // created, so Invoke works) we kick off a one-time background load that gathers all
        // the slow hardware/WMI values off the UI thread and applies them in one marshal.
        private bool _hardwareLoaded;
        // True once the background load has applied values. The CO/CS Apply handlers check
        // this so a click during the brief load can't write default (0) margins to the CPU.
        private volatile bool _hardwareReady;
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_hardwareLoaded || cpu == null) return;
            _hardwareLoaded = true;
            StartHardwareLoad();
        }

        private void StartHardwareLoad()
        {
            var loader = new Thread(() =>
            {
                try
                {
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
                        coMargins = GatherCoMargins();
                        fmax = cpu.GetFMax();
                        csValues = cpu.GetAllCurveShaperMargins();
                        bclk = cpu.GetBclk();
                        prochot = cpu.IsProchotEnabled();
                    }
                    List<object> wmiItems = GatherWmiCommands();

                    // Apply everything to the controls in a single UI-thread marshal.
                    UiInvoke(() =>
                    {
                        try
                        {
                            ApplyCoMargins(coMargins);
                            ApplyFMax(fmax);
                            ApplyCurveShaperValues(csValues);
                            ApplyBclk(bclk);
                            ApplyProchot(prochot);
                            ApplyWmiCommands(wmiItems);
                            _hardwareReady = true;
                            SetStatusText($"{cpu.info.codeName}. Ready.");
                        }
                        catch (Exception ex)
                        {
                            _hardwareReady = true; // unblock the gated controls regardless
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
            if (!IsHandleCreated || IsDisposed) return;
            try { BeginInvoke(action); }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }

        private void ApplyFMax(uint fmax)
        {
            numericUpDownFmax.Value =
                Math.Max(numericUpDownFmax.Minimum, Math.Min(numericUpDownFmax.Maximum, fmax));
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
            uint logicalIndexGroup1 = 0;
            uint logicalIndexGroup2 = 0;

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

                            if (mapIndex == 0) // Group 1
                            {
                                control.Tag = $"{logicalIndexGroup1}";
                                //var performanceOfCore = performanceOfCores[logicalIndexGroup1];
                                logicalIndexGroup1++;
                            }
                            else // Group 2
                            {
                                control.Tag = $"{logicalIndexGroup2}";
                                logicalIndexGroup2++;
                            }
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

        private static int ConvertMarginToInt(uint value)
        {
            return (sbyte)(unchecked(value));
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

        // Synchronous read+apply, used by the Refresh button (UI thread).
        private void InitCS(bool showStatus = false)
        {
            ApplyCurveShaperValues(Hardware.Locked(() => cpu.GetAllCurveShaperMargins()), showStatus);
        }

        // Apply-only half (UI thread); the read is done by the caller so the startup path
        // can gather it on a background thread. Each tier word packs low/med/high in bits
        // 8/16/24.
        private void ApplyCurveShaperValues(uint[] csValues, bool showStatus = false)
        {
            if (csValues == null || csValues.Length < 5)
                return;

            for (int tier = 0; tier < 5; tier++)
            {
                CsGrid[tier][0].Value = ConvertMarginToInt(csValues[tier] >> 8 & 0xFF);
                CsGrid[tier][1].Value = ConvertMarginToInt(csValues[tier] >> 16 & 0xFF);
                CsGrid[tier][2].Value = ConvertMarginToInt(csValues[tier] >> 24 & 0xFF);
            }

            if (showStatus)
                SetStatusText("Curve Shaper margins refreshed.");
        }

        // Full synchronous refresh, used by the CO Refresh/Apply buttons (UI thread).
        private void InitPBO()
        {
            RefreshCoMarginsFromHardware();
            InitStartupProfileUi();
            numericUpDownFmax.Value = Hardware.Locked(() => cpu.GetFMax());
        }

        // Per-core CO margin read + apply, on the UI thread (button path).
        private void RefreshCoMarginsFromHardware()
        {
            if (cpu.smu.Rsmu.SMU_MSG_SetDldoPsmMargin == 0)
                return;

            uint cores = (uint)GetPhysicalCoreCount();
            for (var i = 0; i < cores; i++)
            {
                if (!IsCoreEnabled((int)i))
                    continue;
                NumericUpDown control = GetCOControl((int)i);
                if (control == null)
                    continue;
                control.Enabled = true;
                uint? margin = Hardware.Locked(() => cpu.GetPsmMarginSingleCore(EncodeCoreMarginBitmask((int)i)));
                if (margin != null)
                    control.Value = Convert.ToDecimal((int)margin);
            }
        }

        // Background-safe: reads CO margins for enabled cores into a dictionary, no control
        // access. Returns an empty dictionary when CO isn't supported.
        private Dictionary<int, int> GatherCoMargins()
        {
            var result = new Dictionary<int, int>();
            if (cpu.smu.Rsmu.SMU_MSG_SetDldoPsmMargin == 0)
                return result;

            int cores = GetPhysicalCoreCount();
            for (int i = 0; i < cores; i++)
            {
                if (!IsCoreEnabled(i))
                    continue;
                uint? margin = cpu.GetPsmMarginSingleCore(EncodeCoreMarginBitmask(i));
                if (margin != null)
                    result[i] = (int)margin;
            }
            return result;
        }

        // UI thread: enable the CO spinners for enabled cores and apply the gathered values.
        private void ApplyCoMargins(Dictionary<int, int> margins)
        {
            if (cpu.smu.Rsmu.SMU_MSG_SetDldoPsmMargin == 0)
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
                    control.Value = Math.Max(control.Minimum, Math.Min(control.Maximum, value));
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
            buttonRow.Controls.Add(MakeBarButton("Apply", ButtonApplyProfile_Click));
            buttonRow.Controls.Add(MakeBarButton("Delete", ButtonDeleteProfile_Click));

            numericUpDownPpt       = MakeLimitBox(0, 1000);
            numericUpDownTdc       = MakeLimitBox(0, 1000);
            numericUpDownEdc       = MakeLimitBox(0, 1000);
            numericUpDownPboScalar = MakeLimitBox(0, 10);

            int r = 0;
            grid.Controls.Add(MakeFieldLabel("Profile"), 0, r);
            grid.Controls.Add(comboBoxProfiles, 1, r); r++;
            grid.Controls.Add(buttonRow, 0, r); grid.SetColumnSpan(buttonRow, 2); r++;
            grid.Controls.Add(MakeFieldLabel("PPT (W)"), 0, r);
            grid.Controls.Add(numericUpDownPpt, 1, r); r++;
            grid.Controls.Add(MakeFieldLabel("TDC (A)"), 0, r);
            grid.Controls.Add(numericUpDownTdc, 1, r); r++;
            grid.Controls.Add(MakeFieldLabel("EDC (A)"), 0, r);
            grid.Controls.Add(numericUpDownEdc, 1, r); r++;
            grid.Controls.Add(MakeFieldLabel("PBO Scalar"), 0, r);
            grid.Controls.Add(numericUpDownPboScalar, 1, r); r++;

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
                ApplyProfileToUi(profile);
                SetStatusText($"Profile '{name}' loaded into the form. Use Apply to send it to the CPU.");
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

        private void ButtonApplyProfile_Click(object sender, EventArgs e)
        {
            try
            {
                // Apply exactly what's on screen, not the last-saved file, so edits to
                // PPT/TDC/EDC/CO/etc. take effect without needing a Save first. Selecting a
                // profile in the dropdown loads it into these fields (LoadProfileIntoForm),
                // so saved profiles still apply via the same path.
                var name = CurrentProfileName;
                var profile = GatherProfileFromUi(name);

                // One lock covers every hardware write the applier performs (it does no UI
                // Invoke), serializing it against monitors and other readers.
                var result = Hardware.Locked(() => profileApplier.Apply(profile, cpu));

                string label = string.IsNullOrEmpty(name) ? "Current settings" : $"Profile '{name}'";
                SetStatusText(result.Success ? $"{label} applied." : $"{label} applied with errors.");

                // Per-limit / per-step outcomes so an accepted-but-ineffective limit is
                // visible (a Set returning OK that the board's PBO/AGESA later overrides).
                if (result.Messages.Count > 0)
                    PrependResult(string.Join(Environment.NewLine, result.Messages) + Environment.NewLine);
            }
            catch (Exception ex)
            {
                HandleError(ex.Message);
            }
        }

        private void ButtonSaveProfile_Click(object sender, EventArgs e)
        {
            var name = CurrentProfileName;
            if (string.IsNullOrEmpty(name)) { HandleError("Type a profile name to save."); return; }
            if (!ProfileManager.IsValidName(name)) { HandleError("Invalid profile name (avoid \\ / : * ? \" < > |)."); return; }
            try
            {
                profileManager.Save(GatherProfileFromUi(name));
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
            if (cpu.info.topology.ccds > 0)
            {
                return (int)cpu.info.topology.ccds;
            }

            return Math.Max(1, (int)Math.Ceiling(GetPhysicalCoreCount() / 8.0));
        }

        private int GetPhysicalCoreCount()
        {
            return (int)cpu.info.topology.physicalCores;
        }

        private bool IsCoreEnabled(int coreIndex)
        {
            return CoreTopology.IsCoreEnabled(cpu.info.topology.coreDisableMap, coreIndex);
        }

        // True for APU SMU types, which address CO margins by a flat core index.
        private bool IsApu()
        {
            return cpu.smu.SMU_TYPE >= SMU.SmuType.TYPE_APU0 && cpu.smu.SMU_TYPE <= SMU.SmuType.TYPE_APU2;
        }

        private void EnableOCMode(bool prochotEnabled = true)
        {
            bool ok = Hardware.Locked(() =>
                cpu.smu.SendSmuCommand(cpu.smu.Rsmu, cpu.smu.Rsmu.SMU_MSG_EnableOcMode, prochotEnabled ? 0U : 0x1000000));
            if (ok)
                SetStatusText(prochotEnabled ? "PROCHOT enabled." : "PROCHOT disabled.");
            else
                HandleError("Error setting OC Mode!");
        }

        private void DisableOCMode()
        {
            if (Hardware.Locked(() => cpu.DisableOcMode()) == SMU.Status.OK)
                SetStatusText(string.Format("Set OK!"));
            else
                HandleError("Error disabling OC Mode!");
        }

        // Manual-OC frequency tab. Every Apply writes ALL cores at once (from the per-core
        // fields) so no core is left undefined - setting one core in OC Mode otherwise drops
        // the rest to the SMU default (~2500 MHz). OC Mode overrides PBO / Curve Optimizer.
        private readonly Dictionary<int, NumericUpDown> freqControls = new Dictionary<int, NumericUpDown>();

        private void BuildFrequencyTab()
        {
            freqControls.Clear();
            var tab = new TabPage("Freq (OC)") { Name = "tabPageFreqOC" };

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
                       "you set below - no boost, no idle downclock. 'Apply all cores' writes ALL cores " +
                       "at once. Too high can hang or reboot the PC."
            });

            // Bulk fillers only populate the per-core fields below; nothing is sent until Apply.
            var allNud = MakeFreqNud();
            root.Controls.Add(MakeBulkRow("All cores", allNud, () =>
            {
                foreach (var n in freqControls.Values) n.Value = allNud.Value;
            }));

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
            applyBtn.Click += (s, e) => ApplyAllCoreFrequencies();
            var offBtn = new Button { Text = "Disable OC Mode", AutoSize = false, Size = new Size(115, 26), UseVisualStyleBackColor = true, Margin = new Padding(0, 0, 0, 0) };
            offBtn.Click += (s, e) => DisableOCMode();
            actionBar.Controls.Add(applyBtn);
            actionBar.Controls.Add(offBtn);

            // Add the fill panel first, then the bottom bar: the bottom bar reserves its
            // strip and the scrollable content fills the rest above it.
            tab.Controls.Add(root);
            tab.Controls.Add(actionBar);
            tabControl1.TabPages.Add(tab);
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
        private void ApplyAllCoreFrequencies()
        {
            if (cpu == null || freqControls.Count == 0) return;
            EnableOCMode(true);
            bool ok = true;
            foreach (var kv in freqControls)
                ok &= SetCoreFrequency(kv.Key, (int)kv.Value.Value);
            if (ok)
                SetStatusText($"Applied per-core frequencies to {freqControls.Count} cores (OC Mode on).");
            else
                HandleError("Some cores failed to set. Check the values are in range and OC Mode is active.");
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
            int ccxInCcd = cpu.info.family == Cpu.Family.FAMILY_19H ? 1 : 2;
            int coresInCcx = 8 / ccxInCcd;
            int ccd = coreIndex / 8;
            int ccx = coreIndex / coresInCcx;
            uint mask = (uint)(((ccd << 4 | ccx % 2 & 15) << 4 | coreIndex % 4 & 15) << 20);
            return Hardware.Locked(() => cpu.SetFrequencySingleCore(mask, (uint)mhz));
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

        private void ShowResultForm(string title="Result", string result="No result")
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
        private void ApplySettings()
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
                SMU.Status status = Hardware.Locked(() => cpu.smu.SendSmuCommand(testMailbox, command, ref argsLocal));
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

        private void ButtonDefaults_Click(object sender, EventArgs e)
        {
            InitTestMailbox(cpu.smu.Rsmu);
            comboBoxMailboxSelect.SelectedIndex = 0;
            textBoxCMD.Value = 1;
            textBoxARG0.Text = "0";
        }

        private void ButtonApply_Click(object sender, EventArgs e)
        {
            try
            {
                ApplySettings();
            }
            catch (ApplicationException ex)
            {
                HandleError(ex.Message, "Error reading response");
            }
        }

        private void HandlePciReadBtnClick()
        {
            try
            {
                SetStatusText("Reading, please wait...");
                SetButtonsState(false);

                TryConvertToUint(textBoxPciAddress.Text, out uint address);
                uint data = Hardware.Locked(() => cpu.ReadDword(address));

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

        private void HandlePciWriteBtnClick()
        {
            try
            {
                SetStatusText("Writing, please wait...");
                SetButtonsState(false);

                TryConvertToUint(textBoxPciAddress.Text, out uint address);
                TryConvertToUint(textBoxPciValue.Text, out uint data);

                bool res = Hardware.Locked(() =>
                    cpu.WriteDwordEx(cpu.smu.SMU_OFFSET_ADDR, address) &&
                    cpu.WriteDwordEx(cpu.smu.SMU_OFFSET_DATA, data));

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

        private void ButtonPciRead_Click(object sender, EventArgs e)
        {
            HandlePciReadBtnClick();
        }

        private void TextBoxPciAddress_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
                HandlePciReadBtnClick();
        }

        private void ButtonPciWrite_Click(object sender, EventArgs e)
        {
            HandlePciWriteBtnClick();
        }

        private void TextBoxPciValue_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
                HandlePciWriteBtnClick();
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

            while (start <= end)
            {
                uint smuRspAddress = start + offset;
 
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
                            uint rspAddr = smuRspAddress;
                            // Expect UNKNOWN_CMD status to be returned if the mailbox works
                            if (Hardware.Locked(() => cpu.ReadDword(rspAddr)) == 0xFE)
                            {
                                // Send Get_SMU_Version command
                                if (Hardware.Locked(() => cpu.WriteDwordEx(start, 0x2)))
                                {
                                    Thread.Sleep(10);
                                    if (Hardware.Locked(() => cpu.ReadDword(rspAddr)) == 0x1)
                                        temp.Add(new KeyValuePair<uint, uint>(start, smuRspAddress));
                                }
                            }
                            smuRspAddress += step;
                        }
                    }
                }

                start += step;
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
                    var smuArgAddress = pair.Value + 4;
                    while (smuArgAddress <= end)
                    {
                        uint argAddr = smuArgAddress;
                        if (Hardware.Locked(() => cpu.ReadDword(argAddr)) == cpu.smu.Version)
                        {
                            possibleArgAddresses.Add(smuArgAddress);
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


        private void RunBackgroundTask(DoWorkEventHandler task, RunWorkerCompletedEventHandler completedHandler)
        {
            try
            {
                SetButtonsState(false);
                textBoxResult.Clear();

                // Dispose the previous worker before replacing it, so repeated scans don't
                // leak a BackgroundWorker (and its event subscriptions) each time.
                backgroundWorker1?.Dispose();
                backgroundWorker1 = new BackgroundWorker();
                backgroundWorker1.DoWork += task;
                backgroundWorker1.RunWorkerCompleted += completedHandler;
                backgroundWorker1.RunWorkerAsync();
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
                    writer.WriteValue(cpu.systemInfo.SmuVersionString);
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

        public static void CalculatePstateDetails(uint eax, ref uint IddDiv, ref uint IddVal, ref uint CpuVid, ref uint CpuDfsId, ref uint CpuFid)
        {
            IddDiv = eax >> 30;
            IddVal = eax >> 22 & 0xFF;
            CpuVid = eax >> 14 & 0xFF;
            CpuDfsId = eax >> 8 & 0x3F;
            CpuFid = eax & 0xFF;
        }

        private void ButtonExport_Click(object sender, EventArgs e)
        {
            RunBackgroundTask(BackgroundWorkerTrySettings_DoWork, BackgroundWorkerReport_RunWorkerCompleted);
        }

        private bool nonNumberEntered;

        private void PstateFidDid_KeyDown(object sender, KeyEventArgs e)
        {
            nonNumberEntered = false;

            if (e.KeyCode < Keys.D0 || e.KeyCode > Keys.D9)
            {
                if (e.KeyCode < Keys.NumPad0 || e.KeyCode > Keys.NumPad9)
                {
                    if (e.KeyCode != Keys.Back)
                    {
                        nonNumberEntered = true;
                    }
                }
            }

            if (ModifierKeys == Keys.Shift)
            {
                nonNumberEntered = true;
            }
        }

        private void PstateFidDid_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (nonNumberEntered)
            {
                e.Handled = true;
            }
        }

        private void PstateFidDid_KeyUp(object sender, KeyEventArgs e)
        {
            // TryParse, not Parse: this runs on every keystroke, and a partial/overlong entry
            // would otherwise throw straight to the global exception handler.
            int fid = int.TryParse(pstateFid.Text, out int f) ? f : 0;
            int did = int.TryParse(pstateDid.Text, out int d) ? d : 1;
            if (did == 0) did = 1; // avoid divide-by-zero
            pstateFrequency.Text = (fid * 25 / (did * 12.5)) * 100 + "MHz";
        }

        private void BtnPstateRead_Click(object sender, EventArgs e)
        {
            uint eax = default, edx = default;
            var pstateId = pstateIdBox.SelectedIndex;
            uint pstateEax = 0, pstateEdx = 0;
            bool readOk = Hardware.Locked(() => cpu.ReadMsr(Convert.ToUInt32(Convert.ToInt64(0xC0010064) + pstateId), ref pstateEax, ref pstateEdx));
            eax = pstateEax; edx = pstateEdx;
            if (!readOk)
            {
                SetStatusText($@"Error reading PState {pstateId}!");
                return;
            }

            uint IddDiv = 0x0;
            uint IddVal = 0x0;
            uint CpuVid = 0x0;
            uint CpuDfsId = 0x0;
            uint CpuFid = 0x0;

            CalculatePstateDetails(eax, ref IddDiv, ref IddVal, ref CpuVid, ref CpuDfsId, ref CpuFid);

            pstateDid.Text = Convert.ToString(CpuDfsId, 10);
            pstateFid.Text = Convert.ToString(CpuFid, 10);
            pstateFrequency.Text = (CpuFid * 25 / (CpuDfsId * 12.5)) * 100 + "MHz";

            SetStatusText($@"PState {pstateId} successfully read.");

            pstateDid.ReadOnly = false;
            pstateFid.ReadOnly = false;
            btnPstateWrite.Enabled = true;
        }

        private void BtnPstateWrite_Click(object sender, EventArgs e)
        {
            var confirmResult = MessageBox.Show(
                @"This will change the selected PState and your CPU frequency." +
                Environment.NewLine +
                @"Setting a high frequency could crash/damage your system." +
                Environment.NewLine +
                @"Do you want to continue?",
                @"Confirm PState change",
                MessageBoxButtons.OKCancel
            );

            if (confirmResult != DialogResult.OK) return;

            if (string.IsNullOrEmpty(pstateDid.Text) || string.IsNullOrEmpty(pstateFid.Text))
            {
                MessageBox.Show("Can't write because DID/FID is empty!", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            var pstateId = pstateIdBox.SelectedIndex;
            uint eax = default, edx = default;
            uint IddDiv = 0x0;
            uint IddVal = 0x0;
            uint CpuVid = 0x0;
            uint CpuDfsId = 0x0;
            uint CpuFid = 0x0;

            uint readEax = 0, readEdx = 0;
            bool pstateReadOk = Hardware.Locked(() => cpu.ReadMsr(Convert.ToUInt32(Convert.ToInt64(0xC0010064) + pstateId), ref readEax, ref readEdx));
            eax = readEax; edx = readEdx;
            if (!pstateReadOk)
            {
                SetStatusText($@"Error reading PState {pstateId}!");
                return;
            }

            CalculatePstateDetails(eax, ref IddDiv, ref IddVal, ref CpuVid, ref CpuDfsId, ref CpuFid);

            eax = (IddDiv & 0xFF) << 30 | (IddVal & 0xFF) << 22 | (CpuVid & 0xFF) << 14 | (uint.Parse(pstateDid.Text) & 0xFF) << 8 | uint.Parse(pstateFid.Text) & 0xFF;

            if (_numaUtil.HighestNumaNode > 0)
            {
                for (var i = 0; i < (int)_numaUtil.HighestNumaNode; i++)
                {
                    if (!WritePstateClick(pstateId, eax, edx, i)) return;
                }
            }
            else
            {
                if (!WritePstateClick(pstateId, eax, edx)) return;
            }

            SetStatusText($@"Successfully written PState {pstateId}.");
        }

        // P0 fix C001_0015 HWCR[21]=1
        // Fixes timer issues when not using HPET
        public bool ApplyTscWorkaround()
        {
            uint eax = 0, edx = 0;

            return Hardware.Locked(() =>
            {
                if (cpu.ReadMsr(0xC0010015, ref eax, ref edx))
                {
                    eax |= 0x200000;
                    return cpu.WriteMsr(0xC0010015, eax, edx);
                }

                SetStatusText($@"Error applying TSC fix!");
                return false;
            });
        }

        private bool WritePstateClick(int pstateId, uint eax, uint edx, int numanode = 0)
        {
            if (_numaUtil.HighestNumaNode > 0) _numaUtil.SetThreadProcessorAffinity((ushort)(numanode + 1), Enumerable.Range(0, Environment.ProcessorCount).ToArray());

            if (!ApplyTscWorkaround()) return false;

            if (!Hardware.Locked(() => cpu.WriteMsr(Convert.ToUInt32(Convert.ToInt64(0xC0010064) + pstateId), eax, edx)))
            {
                SetStatusText($@"Error writing PState {pstateId}!");
                return false;
            }

            return true;
        }

        private void PciScan_DoWork(object sender, DoWorkEventArgs e)
        {
            try
            {
                TryConvertToUint(textBoxPciStartReg.Text, out uint startReg);
                TryConvertToUint(textBoxPciEndReg.Text, out uint endReg);

                if (endReg <= startReg)
                {
                    HandleError("End register is not greater than start register");
                    return;
                }

                Invoke(new MethodInvoker(delegate
                {
                    SetStatusText("Scanning PCI addresses, please wait...");
                }));

                var result = new StringBuilder("REG         Value(HEX) Value(BIN)" + Environment.NewLine);

                // Lock the whole read loop (no UI Invoke happens inside it); the status and
                // result-form marshals are outside this region.
                lock (Hardware.Sync)
                {
                    while (startReg <= endReg)
                    {
                        var data = cpu.ReadDword(startReg);
                        result.AppendLine($"0x{startReg:X8}: 0x{data:X8} {Convert.ToString(data, 2).PadLeft(32, '0')}");
                        string decoded = RegisterDecoder.Decode(RegisterKind.Pci, startReg, data, decodeContext);
                        if (!string.IsNullOrEmpty(decoded))
                            result.Append(decoded);
                        startReg += 4;
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
            RunBackgroundTask(PciScan_DoWork, Scan_WorkerCompleted);
        }

        private void ButtonApplyPROCHOT_Click(object sender, EventArgs e)
        {
            if (checkBoxPROCHOT.Checked)
            {
                // Enabling PROCHOT = normal operation. Exiting OC mode restores PBO /
                // auto-boost and re-asserts PROCHOT; we must NOT enter OC mode here
                // (doing so would pin all cores to the SMU's default OC frequency).
                DisableOCMode();
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

                EnableOCMode(false);
            }

            if (!checkBoxPROCHOT.Checked && Hardware.Locked(() => cpu.IsProchotEnabled()) == true)
            {
                checkBoxPROCHOT.Checked = true;
                HandleError($@"Error, PROCHOT could not be disabled!");
            }
        }

        private void ReadMsr_Task(object sender, DoWorkEventArgs e)
        {
            try
            {
                Invoke(new MethodInvoker(delegate
                {
                    SetStatusText("Scanning MSR range, please wait...");
                }));

                var result = new StringBuilder("MSR         EDX(63-32) EAX(31-0)" + Environment.NewLine);

                TryConvertToUint(textBoxMsrStart.Text, out uint startReg);
                TryConvertToUint(textBoxMsrEnd.Text, out uint endReg);

                lock (Hardware.Sync)
                {
                    while (startReg <= endReg)
                    {
                        uint eax = default, edx = default;
                        if (cpu.ReadMsr(startReg, ref eax, ref edx))
                        {
                            result.AppendLine($"0x{startReg:X8}: 0x{edx:X8} 0x{eax:X8}");
                            string decoded = RegisterDecoder.Decode(
                                RegisterKind.Msr, startReg, ((ulong)edx << 32) | eax, decodeContext);
                            if (!string.IsNullOrEmpty(decoded))
                                result.Append(decoded);
                        }

                        startReg += 1;
                    }
                }

                ShowResultForm("MSR Scan result", result.ToString());
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

        private void ButtonMsrRead_Click(object sender, EventArgs e)
        {
            TryConvertToUint(textBoxMsrAddress.Text, out uint msr);
            uint eax = default, edx = default;
            uint rEax = 0, rEdx = 0;
            bool ok = Hardware.Locked(() => cpu.ReadMsr(msr, ref rEax, ref rEdx));
            eax = rEax; edx = rEdx;
            if (ok)
            {
                textBoxMsrEdx.Text = $"0x{edx:X8}";
                textBoxMsrEax.Text = $"0x{eax:X8}";

                ulong value = ((ulong)edx << 32) | eax;
                string decoded = RegisterDecoder.Decode(RegisterKind.Msr, msr, value, decodeContext);
                if (!string.IsNullOrEmpty(decoded))
                    PrependResult(decoded + Environment.NewLine);
            }
        }

        private void ButtonMsrWrite_Click(object sender, EventArgs e)
        {
            TryConvertToUint(textBoxMsrEdx.Text, out uint edx);
            TryConvertToUint(textBoxMsrEax.Text, out uint eax);
            TryConvertToUint(textBoxMsrAddress.Text, out uint msr);

            if (!Hardware.Locked(() => cpu.WriteMsr(msr, eax, edx)))
            {
                HandleError($@"Error writing MSR {textBoxMsrAddress.Text}!");
                return;
            }

            SetStatusText("Write OK.");
        }

        private void ButtonMsrScan_Click(object sender, EventArgs e)
        {
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
                    if (cpu.Cpuid(0x00000000, ref eax, ref ebx, ref ecx, ref edx))
                        LFuncStd = eax;

                    if (cpu.Cpuid(0x80000000, ref eax, ref ebx, ref ecx, ref edx))
                        LFuncExt = eax - 0x80000000;

                    for (uint i = 0; i <= LFuncStd; ++i)
                    {
                        var index = 0x00000000 + i;
                        cpu.Cpuid(index, ref eax, ref ebx, ref ecx, ref edx);
                        result.AppendLine($"0x{index:X8}: 0x{eax:X8} 0x{ebx:X8} 0x{ecx:X8} 0x{edx:X8}");
                        string decoded = RegisterDecoder.Decode(RegisterKind.Cpuid, index, eax, decodeContext);
                        if (!string.IsNullOrEmpty(decoded))
                            result.Append(decoded);
                    }

                    for (uint i = 0; i <= LFuncExt; ++i)
                    {
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

        private void ButtonCPUIDRead_Click(object sender, EventArgs e)
        {
            TryConvertToUint(textBoxCPUIDAddress.Text, out uint index);
            uint eax = 0, ebx = 0, ecx = 0, edx = 0;
            uint cEax = 0, cEbx = 0, cEcx = 0, cEdx = 0;
            bool ok = Hardware.Locked(() => cpu.Cpuid(index, ref cEax, ref cEbx, ref cEcx, ref cEdx));
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

        private void ButtonCPUIDScan_Click(object sender, EventArgs e)
        {
            RunBackgroundTask(ReadCPUID_Task, Scan_WorkerCompleted);
        }

        private void ButtonPMTable_Click(object sender, EventArgs e)
        {
            if (cpu.Status == IODriver.LibStatus.OK)
                new Thread(() => new PowerTableMonitor(cpu).ShowDialog()).Start();
            else
                HandleError("IO driver is not responding or not loaded.");
        }

        private void ButtonSMUMonitor_Click(object sender, EventArgs e)
        {
            TryConvertToUint(textBoxCMDAddress.Text, out uint addrMsg);
            TryConvertToUint(textBoxRSPAddress.Text, out uint addrRsp);
            TryConvertToUint(textBoxARGAddress.Text, out uint addrArg);

            new Thread(() => new SMUMonitor(cpu, addrMsg, addrArg, addrRsp).ShowDialog()).Start();
        }

        private void ComboBoxMailboxSelect_SelectedIndexChanged(object sender, EventArgs e)
        {
            MailboxListItem item = comboBoxMailboxSelect.SelectedItem as MailboxListItem;
            InitTestMailbox(item.msgAddr, item.rspAddr, item.argAddr);
        }

        private void SettingsForm_FormClosed(object sender, FormClosedEventArgs e)
        {
            ExitApplication();
        }

        private void buttonGetCO_Click(object sender, EventArgs e)
        {
            InitPBO();
        }

        private uint EncodeCoreMarginBitmask(int coreIndex, int coresPerCCD = 8)
        {
            return CoreTopology.EncodeCoreMarginBitmask(IsApu(), coreIndex, coresPerCCD);
        }

        private void ApplyCO()
        {
            //if (cpu.info.family == Cpu.Family.FAMILY_19H)
            //if (cpu.smu.Rsmu.SMU_MSG_SetDldoPsmMargin != 0)
            {
                lock (Hardware.Sync)
                {
                    for (var i = 0; i < GetPhysicalCoreCount(); i++)
                    {
                        if (IsCoreEnabled(i))
                        {
                            NumericUpDown control = GetCOControl(i);
                            if (control != null)
                            {
                                cpu.SetPsmMarginSingleCore(EncodeCoreMarginBitmask(i), Convert.ToInt32(control.Value));
                            }
                        }
                    }
                }
            }
            //else
            //{
            //    HandleError("Not supported");
            //}
        }

        private void ButtonApplyCO_Click(object sender, EventArgs e)
        {
            if (!_hardwareReady)
            {
                SetStatusText("Still loading hardware values, please wait...");
                return;
            }
            ApplyCO();
            InitPBO();
        }

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

        private void ComboBoxAvailableCommands_SelectedIndexChanged(object sender, EventArgs e)
        {
            WmiCmdListItem command = comboBoxAvailableCommands.SelectedItem as WmiCmdListItem;

            comboBoxAvailableValues.Items.Clear();
            comboBoxAvailableValues.Enabled = false;
            textBoxWmiArgument.Text = "";
            textBoxWmiArgument.Enabled = false;

            if (command.isSet) {
                // Get possible values (index) of a memory option in BIOS
                var dvaluesPack = WMI.InvokeMethodAndGetValue(classInstance, "Getdvalues", "pack", "ID", command.value);
                if (dvaluesPack != null)
                {
                    uint[] DValuesBuffer = (uint[])dvaluesPack.GetPropertyValue("DValuesBuffer");
                    Debug.WriteLine(command.text);
                    foreach (uint value in DValuesBuffer)
                    {
                        if (value != 0)
                        {
                            WmiCmdListItem item = new WmiCmdListItem(value.ToString(), value);
                            Debug.WriteLine(value);
                            comboBoxAvailableValues.Items.Add(item);
                        }
                    }
                    Debug.WriteLine("------------------------");

                    if (comboBoxAvailableValues.Items.Count > 0)
                        comboBoxAvailableValues.Enabled = true;
                    else
                        comboBoxAvailableValues.Items.Add("No values available for this command");
                }
                textBoxWmiArgument.Enabled = true;
            }
            else
            {
                comboBoxAvailableValues.Items.Add("Get commands don't support values");
            }

            comboBoxAvailableValues.SelectedIndex = 0;
        }

        private void ComboBoxAvailableValues_SelectedIndexChanged(object sender, EventArgs e)
        {
            WmiCmdListItem command = comboBoxAvailableCommands.SelectedItem as WmiCmdListItem;
            if (command.isSet && comboBoxAvailableValues.Enabled)
                textBoxWmiArgument.Text = comboBoxAvailableValues.Text;
            else
                textBoxWmiArgument.Text = "";
        }

        private void ButtonWmiCmdSend_Click(object sender, EventArgs e)
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
                var response = WMI.RunCommand(classInstance, command.value, value);
                var text = command.text + Environment.NewLine + "------------------------" + Environment.NewLine;
                foreach (byte b in response)
                {
                    text += "0x" + b.ToString("X2") + Environment.NewLine;
                }
                text += "------------------------" + Environment.NewLine;
                PrependResult(text + Environment.NewLine);
            }
        }

        private void ButtonBCLKApply_Click(object sender, EventArgs e)
        {
            if (!double.TryParse(numericUpDownBclk.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double targetBclk)
                && !double.TryParse(numericUpDownBclk.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out targetBclk))
            {
                HandleError("Invalid BCLK value.");
                return;
            }
            double? currentBclk = Hardware.Locked(() =>
            {
                cpu.SetBclk(targetBclk);
                return cpu.GetBclk();
            });
            labelBCLK.Text = currentBclk + " MHz";
            numericUpDownBclk.Text = $"{currentBclk}";
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

            if (cpu.smu.Rsmu.SMU_MSG_SetDldoPsmMargin != 0)
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
            var profile = new Profile { Name = name };

            if (cpu.smu.Rsmu.SMU_MSG_SetDldoPsmMargin != 0)
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

            return profile;
        }

        private void ApplyProfileToUi(Profile profile)
        {
            if (profile == null) return;

            if (profile.CoMargins != null && cpu.smu.Rsmu.SMU_MSG_SetDldoPsmMargin != 0)
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

            if (margins.Count > 0 && cpu.smu.Rsmu.SMU_MSG_SetDldoPsmMargin != 0)
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
            if (checkBoxApplyCOStartup.Checked
                && string.IsNullOrEmpty(comboBoxStartupProfile?.SelectedItem as string))
            {
                HandleError("Select a startup profile first.");
                checkBoxApplyCOStartup.Checked = false;
                return;
            }
            RegisterOrRemoveStartupTask();
            PrependResult($"Startup settings saved." + Environment.NewLine);
        }

        private void ButtonApplyCoreMap_Click(object sender, EventArgs e)
        {
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

            if (cmdItem != null) {
                WMI.RunCommand(classInstance, cmdItem.value, ccd0);
                WMI.RunCommand(classInstance, cmdItem.value, ccd1);
            }

            cmdItem = comboBoxAvailableCommands.Items
                     .OfType<WmiCmdListItem>()
                     .FirstOrDefault(item => item.text.Contains("Set SMTEn"));

            if (cmdItem != null)
            {
                WMI.RunCommand(classInstance, cmdItem.value, checkBoxSMT.Checked ? 1u : 0);
            }

            ConfirmWindowsRestart();
        }

        private void Button5_Click(object sender, EventArgs e)
        {
            var cmdItem = comboBoxAvailableCommands.Items
                     .OfType<WmiCmdListItem>()
                     .FirstOrDefault(item => item.text.Contains("Software Downcore Config"));

            if (cmdItem != null)
            {
                WMI.RunCommand(classInstance, cmdItem.value, 0x8000);
                WMI.RunCommand(classInstance, cmdItem.value, 0x81FF);
            }

            cmdItem = comboBoxAvailableCommands.Items
                     .OfType<WmiCmdListItem>()
                     .FirstOrDefault(item => item.text.Contains("Set SMTEn"));

            if (cmdItem != null)
            {
                WMI.RunCommand(classInstance, cmdItem.value, 0);
            }

            ConfirmWindowsRestart();
        }

        private void Button6_Click(object sender, EventArgs e)
        {
            var cmdItem = comboBoxAvailableCommands.Items
                     .OfType<WmiCmdListItem>()
                     .FirstOrDefault(item => item.text.Contains("Software Downcore Config"));

            if (cmdItem != null)
            {
                WMI.RunCommand(classInstance, cmdItem.value, 0x8000);
                WMI.RunCommand(classInstance, cmdItem.value, 0x8100);
            }

            cmdItem = comboBoxAvailableCommands.Items
                     .OfType<WmiCmdListItem>()
                     .FirstOrDefault(item => item.text.Contains("Set SMTEn"));

            if (cmdItem != null)
            {
                WMI.RunCommand(classInstance, cmdItem.value, 1);
            }

            ConfirmWindowsRestart();
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

        private void ButtonApplyFMax_Click(object sender, EventArgs e)
        {
            uint targetFmax = (uint)numericUpDownFmax.Value;
            uint? newFmax = Hardware.Locked(() => cpu.SetFMax(targetFmax) ? (uint?)cpu.GetFMax() : null);
            if (newFmax.HasValue) {
                numericUpDownFmax.Value = newFmax.Value;
            }
        }

        private void ButtonPCIRangeMonitor_Click(object sender, EventArgs e)
        {
            TryConvertToUint(textBoxPciStartReg.Text, out uint startAddress);
            TryConvertToUint(textBoxPciEndReg.Text, out uint endAddress);

            new Thread(() => new PCIRangeMonitor(cpu, startAddress, endAddress).ShowDialog()).Start();
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
                await System.Threading.Tasks.Task.Run(() => MemoryDumper.Dump32BitAddressSpaceAsBytes(cpu, name, startAddress, endAddress));
                stopwatch.Stop();

                string elapsedTime = $"{stopwatch.Elapsed.TotalSeconds:F2}";
                SetStatusText(name + $": Dump complete. ({elapsedTime}s)");
                MessageBox.Show($"Memory dump completed successfully to file: {name}\n\nTime elapsed: {elapsedTime}s", "Dump Complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                HandleError($"Memory dump failed: {ex.Message}");
            }
            finally
            {
                if (button != null) button.Enabled = true;
            }
        }

        private void ButtonRefreshCS_Click(object sender, EventArgs e)
        {
            InitCS(showStatus: true);
        }

        private void ButtonApplyCS_Click(object sender, EventArgs e)
        {
            if (!_hardwareReady)
            {
                SetStatusText("Still loading hardware values, please wait...");
                return;
            }

            var errorMessages = new List<string>();

            lock (Hardware.Sync)
            {
                for (int tier = 0; tier < 5; tier++)
                {
                    if (cpu.SetCurveShaperMargin(
                            marginHigh: (int)CsGrid[tier][2].Value,
                            marginMedium: (int)CsGrid[tier][1].Value,
                            marginLow: (int)CsGrid[tier][0].Value, tier) != SMU.Status.OK)
                    {
                        errorMessages.Add($"Failed to set Curve Shaper margins for frequency tier {tier} ({CsTierNames[tier]}).");
                    }
                }
            }

            if (errorMessages.Count == 0)
            {
                SetStatusText("Curve Shaper margins applied successfully.");
            }
            else
            {
                PrependResult(string.Join(Environment.NewLine, errorMessages) + Environment.NewLine);
                SetStatusText("One or more errors occurred while applying Curve Shaper margins.");
            }

            // Do NOT re-read here: GetAllCurveShaperMargins returns 0 on this hardware, which
            // would wipe the values just entered. Keep what was applied (use Refresh to re-read).
        }
    }
}
