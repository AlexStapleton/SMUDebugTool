using System;
using System.Linq;
using System.Threading.Tasks;

namespace ZenStatesDebugTool
{
    public partial class SettingsForm
    {
        private CpuOverviewForm cpuOverview;
        private void ShowCpuOverview()
        {
            if (cpuOverview != null && !cpuOverview.IsDisposed) { cpuOverview.Activate(); return; }
            cpuOverview = new CpuOverviewForm(
                () => { RequireOverviewReady(); return RunHardwareAsync(() => Hardware.Locked(() => CpuOverviewProvider.Capture(cpu, hardware.Topology, Hardware.ClosingToken))); },
                ApplyTemperatureCeilingAsync);
            cpuOverview.Show(this);
        }
        private void RequireOverviewReady()
        {
            if (!_hardwareReady || shuttingDown || Hardware.IsClosing) throw new InvalidOperationException("Hardware is still loading or closing.");
        }
        private async Task<TuningResult> ApplyTemperatureCeilingAsync(int value)
        {
            RequireOverviewReady();
            var result = await RunHardwareWriteAsync(() => Hardware.Locked(() => new TuningService(new ZenStatesTuningTransport(cpu)).Apply(
                new[] { new TuningRequest { Kind = TuningKind.Temperature, Value = value } }, Hardware.ClosingToken)));
            result.Label = "Apply temperature ceiling";
            nativeWorkbench?.NoteChange(result);
            if (!shuttingDown && !IsDisposed) {
                applyResultsPanel.ShowResult(result); SetStatusText(TuningResultFormatter.FriendlySummary(result));
                PrependResult(TuningResultFormatter.Report(result) + Environment.NewLine);
            }
            return result;
        }
    }
}
