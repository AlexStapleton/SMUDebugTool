using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace ZenStatesDebugTool
{
    public partial class SettingsForm
    {
        private DeferredStartupWork<List<object>> wmiDiscovery;
        private DeferredStartupWork<List<PStateItem>> pstateDiscovery;
        private bool wmiApplied, pstatesApplied;

        private async Task<bool> RequireWmiReadyAsync()
        {
            if (!_hardwareReady || shuttingDown || nativeManagedOperation) { SetStatusText("Wait for hardware loading or stop the optimizer before firmware changes."); return false; }
            await EnsureWmiReadyAsync();
            if (shuttingDown || !wmiApplied || !comboBoxAvailableCommands.Items.OfType<WmiCmdListItem>().Any()) { SetStatusText("Firmware commands are unavailable; no change sent."); return false; }
            return true;
        }

        private async Task EnsureWmiReadyAsync()
        {
            if (!_hardwareReady || shuttingDown || wmiApplied) return;
            if (nativeManagedOperation) { acpiUi.Result.Text = "Stop the optimizer workload before loading firmware commands."; return; }
            if (wmiDiscovery == null) wmiDiscovery = new DeferredStartupWork<List<object>>(() => {
                using (StartupTimings.Current.Measure("AMD ACPI discovery (on demand)")) return GatherWmiCommands();
            });
            comboBoxAvailableCommands.Enabled = false; acpiUi.Result.Text = "Loading firmware commands…";
            try {
                var items = await wmiDiscovery.GetAsync(Hardware.ClosingToken);
                if (shuttingDown || IsDisposed || wmiApplied) return;
                wmiApplied = true; comboBoxAvailableCommands.Enabled = true;
                acpiUi.Result.Text = items.OfType<WmiCmdListItem>().Any() ? "" : "Firmware commands are unavailable on this system.";
                ApplyWmiCommands(items);
            } catch (OperationCanceledException) { }
            catch (Exception ex) { if (!shuttingDown) acpiUi.Result.Text = "Firmware discovery failed. Reopen this tab to retry: " + ex.Message; }
            finally { await StartupTimings.Current.PersistAsync(); }
        }
        private async Task EnsurePStatesReadyAsync()
        {
            if (!_hardwareReady || shuttingDown || pstatesApplied) return;
            if (pstateDiscovery == null) pstateDiscovery = new DeferredStartupWork<List<PStateItem>>(() => {
                using (StartupTimings.Current.Measure("P-state discovery (on demand)")) return GatherPStates();
            });
            try {
                var items = await pstateDiscovery.GetAsync(Hardware.ClosingToken);
                if (shuttingDown || IsDisposed || pstatesApplied) return;
                pstatesApplied = true; ApplyPStateList(items);
            } catch (OperationCanceledException) { }
            catch (Exception ex) { if (!shuttingDown) SetStatusText("P-state discovery failed. Reopen this tab to retry: " + ex.Message); }
            finally { await StartupTimings.Current.PersistAsync(); }
        }
        private async void LoadSelectedHardwareTab()
        {
            if (tabControl1.SelectedTab == tabPageWmi) await EnsureWmiReadyAsync();
            else if (tabControl1.SelectedTab == tabPagePstates) await EnsurePStatesReadyAsync();
        }
        private async void LoadStartupTaskState()
        {
            try {
                StartupTaskState state;
                using (StartupTimings.Current.Measure("Startup task read")) state = await Task.Run(StartupTaskService.ReadState);
                if (shuttingDown || IsDisposed) return;
                _suppressStartupTaskUpdates = true;
                try {
                    checkBoxApplyCOStartup.Checked = state.Exists;
                    if (state.ProfileName != null && comboBoxStartupProfile.Items.Contains(state.ProfileName)) comboBoxStartupProfile.SelectedItem = state.ProfileName;
                    checkBoxApplyCOStartup.Enabled = comboBoxStartupProfile.Enabled = true;
                } finally { _suppressStartupTaskUpdates = false; }
            } catch (Exception ex) {
                if (!shuttingDown && !IsDisposed) PrependResult("Startup task state unavailable; startup controls remain disabled: " + ex.Message + Environment.NewLine);
            } finally { await StartupTimings.Current.PersistAsync(); }
        }
    }
}
