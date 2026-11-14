using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ZenStatesDebugTool
{
    public partial class SettingsForm
    {
        private readonly NativeCoSession nativeSession = new NativeCoSession();
        private NativeCoWorkbenchForm nativeWorkbench;
        private void ShowNativeWorkbench()
        {
            if (nativeWorkbench != null && !nativeWorkbench.IsDisposed) { nativeWorkbench.Activate(); return; }
            nativeWorkbench = new NativeCoWorkbenchForm(nativeSession, CreateNativeSourceAsync, CaptureNativeContextAsync, ApplyNativeStepAsync);
            nativeWorkbench.Show(this);
        }
        private Task<Func<CancellationToken, IReadOnlyList<CoreVidReading>>> CreateNativeSourceAsync()
        {
            RequireOverviewReady();
            return RunHardwareAsync(() => Hardware.Locked(() => {
                var reader = NativeVidReader.Create(cpu, hardware.Topology);
                return new Func<CancellationToken, IReadOnlyList<CoreVidReading>>(cancellation => {
                    cancellation.ThrowIfCancellationRequested(); Hardware.ThrowIfClosing();
                    return Hardware.TryRead(() => reader.Read(cancellation), out IReadOnlyList<CoreVidReading> rows) ? rows : new CoreVidReading[0];
                });
            }));
        }
        private Task<NativeCoContext> CaptureNativeContextAsync()
        {
            RequireOverviewReady();
            var requests = TuningSnapshots.Profile(GatherProfileFromUi("Session editor requests"));
            return RunHardwareAsync(() => Hardware.Locked(() => {
                var transport = new ZenStatesTuningTransport(cpu); var observed = new Dictionary<int, int>();
                foreach (var target in hardware.Topology.Targets) {
                    Hardware.ThrowIfClosing();
                    var request = new TuningRequest { Kind = TuningKind.CurveOptimizer, Index = target.Slot };
                    if (!transport.IsSupported(request, out _)) continue;
                    var reply = transport.Read(request);
                    if (TuningService.IsFatal(reply?.Status)) break;
                    if (reply?.Status == 1 && reply.Direct && reply.ReadIssue == null && reply.Value >= -999 && reply.Value <= 999 && reply.Value == decimal.Truncate(reply.Value.Value)) observed[target.Slot] = (int)reply.Value.Value;
                }
                return new NativeCoContext(cpu.info.cpuName + "; BIOS " + cpu.systemInfo?.BiosVersion + "; SMU " + cpu.smu.Version,
                    hardware.Topology.Signature, requests, observed, hardware.Topology.Targets.Count);
            }));
        }
        private async Task<TuningResult> ApplyNativeStepAsync(CoStepProposal proposal)
        {
            RequireOverviewReady();
            var result = await RunHardwareWriteAsync(() => Hardware.Locked(() => {
                var transport = new ZenStatesTuningTransport(cpu);
                if (hardware.Topology.Find(proposal.Slot) == null) throw new InvalidOperationException("Physical CO target is no longer available. No command sent.");
                return NativeCoStepTransaction.Apply(transport, proposal, Hardware.ClosingToken);
            }));
            result.Label = "Native VID one-step CO trial";
            if (!shuttingDown && !IsDisposed) {
                foreach (var row in result.Rows)
                    if (row.Outcome == TuningOutcome.Verified) editControls.Observe(new TuningRequest { Kind = TuningKind.CurveOptimizer, Index = row.Request.Index, Value = row.Read.Value.Value }, "CPU readback");
                editControls.RecordResult(result); applyResultsPanel.ShowResult(result);
                nativeWorkbench?.NoteChange(result); SetStatusText(TuningResultFormatter.FriendlySummary(result));
                PrependResult(TuningResultFormatter.Report(result) + Environment.NewLine);
            }
            return result;
        }
    }
}
