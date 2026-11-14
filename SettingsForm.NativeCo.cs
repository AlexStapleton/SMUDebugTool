using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ZenStatesDebugTool
{
    public partial class SettingsForm
    {
        private readonly NativeCoSession nativeSession = new NativeCoSession();
        private NativeCoWorkbenchForm nativeWorkbench;
        private readonly CoRecoveryStore nativeRecovery = new CoRecoveryStore(CoRecoveryStore.DefaultPath);
        private IReadOnlyList<NativeCoreBinding> nativeBindings;
        private bool nativeManagedOperation;
        private void ShowNativeWorkbench()
        {
            if (nativeWorkbench != null && !nativeWorkbench.IsDisposed) { nativeWorkbench.Activate(); return; }
            nativeWorkbench = new NativeCoWorkbenchForm(nativeSession, CreateNativeSourceAsync, CaptureNativeContextAsync, ApplyNativeStepAsync,
                new ManagedCoServices { Recovery = nativeRecovery, Apply = ApplyNativeBatchAsync, Targets = NativeLoadTargetsAsync, SetHardwareOwner = value => nativeManagedOperation = value,
                    InitialExecutable = WorkloadPreferences.Load(), RememberExecutable = WorkloadPreferences.Save });
            nativeWorkbench.Show(this);
        }
        private Task<Func<CancellationToken, IReadOnlyList<CoreVidReading>>> CreateNativeSourceAsync()
        {
            RequireOverviewReady();
            return RunHardwareAsync(() => Hardware.Locked(() => {
                var reader = NativeVidReader.Create(cpu, hardware.Topology);
                nativeBindings = reader.Bindings;
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
            var view = nativeWorkbench;
            if (view == null || view.IsDisposed) throw new OperationCanceledException("Native workbench is closed.");
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(Hardware.ClosingToken, view.ClosingToken)) {
                var original = await CaptureNativeContextAsync();
                linked.Token.ThrowIfCancellationRequested();
                await Task.Run(() => nativeRecovery.Preserve(original));
                linked.Token.ThrowIfCancellationRequested();
                var result = await RunHardwareWriteAsync(() => Hardware.Locked(() => {
                    var transport = new ZenStatesTuningTransport(cpu);
                    if (hardware.Topology.Find(proposal.Slot) == null) throw new InvalidOperationException("Physical CO target is no longer available. No command sent.");
                    return NativeCoStepTransaction.Apply(transport, proposal, linked.Token);
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
        private Task<IReadOnlyList<WorkloadTarget>> NativeLoadTargetsAsync()
        {
            if (nativeBindings == null) throw new InvalidOperationException("Start native monitoring to validate load targets.");
            var targets = new List<WorkloadTarget> { new WorkloadTarget("All processors", 0) };
            // A job affinity mask is reviewed for one Windows processor group only.
            if (nativeBindings.SelectMany(b => b.Siblings).All(p => p.Group == 0))
                foreach (var binding in nativeBindings) {
                    targets.Add(new WorkloadTarget("Core " + binding.Core.Slot + " / one thread", 1UL << binding.Processor.Processor, true));
                    if (binding.Siblings.Count > 1) targets.Add(new WorkloadTarget("Core " + binding.Core.Slot + " / SMT siblings", binding.Siblings.Aggregate(0UL, (mask, p) => mask | 1UL << p.Processor)));
                }
            return Task.FromResult<IReadOnlyList<WorkloadTarget>>(targets.AsReadOnly());
        }
        private async Task<TuningResult> ApplyNativeBatchAsync(CoBatchPlan plan, CancellationToken cancellation)
        {
            RequireOverviewReady();
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, Hardware.ClosingToken)) {
                var result = await RunHardwareWriteAsync(() => Hardware.Locked(() => {
                    if (plan.Requested.Count != hardware.Topology.Targets.Count || plan.Requested.Keys.Any(k => hardware.Topology.Find(k) == null)) throw new InvalidOperationException("Complete physical CO targets are required.");
                    return CoBatchTransaction.Apply(new ZenStatesTuningTransport(cpu), plan.Expected, plan.Requested, linked.Token);
                }), true);
                result.Label = "Managed CO baseline / recovery";
                if (!shuttingDown && !IsDisposed) {
                    foreach (var row in result.Rows.Where(r => r.Outcome == TuningOutcome.Verified))
                        editControls.Observe(new TuningRequest { Kind = TuningKind.CurveOptimizer, Index = row.Request.Index, Value = row.Read.Value.Value }, "CPU readback");
                    editControls.RecordResult(result); applyResultsPanel.ShowResult(result);
                    PrependResult(TuningResultFormatter.Report(result) + Environment.NewLine);
                }
                return result;
            }
        }
    }
}
