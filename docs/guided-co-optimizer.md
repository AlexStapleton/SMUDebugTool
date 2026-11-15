# Guided CO optimizer

## User flow

1. Open **Tools → CO optimizer**. The program checks native physical-core mapping and your remembered Cinebench executable without changing CO or starting a load. Choose a local **Cinebench 2024.1.0** executable once. HWiNFO and CSV imports are not used.
2. Choose **Start from CO 0** (default) or **Improve current offsets**. Set a negative lower bound and balancing time budget. Test settings expose the software CPU-temperature stop, scored duration and repeated-run count. The budget covers balancing; reference and validation take additional time. The negative bound is a limit on the experiment, not a recommendation.
3. Select **Start reference**. Before any setter, temperature/WHEA monitoring must be available. Save the original direct CO readback and session context. If selected, set all physical cores to zero with checked preflight and direct verification. Warm up, then collect repeated all-processor scored runs with fixed offsets.
4. Select **Start balancing**. One owned all-processor load stays running across trials. Collect a fresh steady window, select one eligible higher-VID core, persist intent, decrement its CO by one and verify direct readback. Discard transition samples and collect a new settled window before the next decision. Watch the per-core eligibility reasons rather than interpreting unavailable data as zero.
5. Select **Finish balancing & validate**, or let the configured limit stop balancing. The next primary action is **Validate fixed candidate**. Validation warms up and repeats the same scored recipe with offsets fixed. The comparison shows medians and observed ranges. Scores produced while offsets change are excluded.
6. Decide: **Stop & restore original**, **Keep temporarily**, or **Save experimental profile** under a new name. Saving includes observed CO only and topology binding. No startup profile is selected or changed. Additional core/SMT renders are available separately; they do not establish stability.

**Pause** terminates the owned load and blocks further setters. Resume checks context and starts a new load/window. Close leaves original recovery information pending. On reopening an interrupted session, read current offsets before explicitly keeping them, or restore the saved original. The program never replays a tuning journal on startup.

## Trial and comparison contract

- The native source is the reviewed current-processor MSR path, with physical/APIC/Windows mapping and family/model gates described in [the native capture contract](native-co-workbench.md). Ambiguous mapping blocks tuning.
- A proposal requires at least 20 valid busy samples spanning two seconds inside a five-second window, latest data at most 0.5 seconds old, stable reported clocks within 3%, and at least two similar-clock peers in the same CCD. The peer statistic is a median, including the average middle pair for an even peer count.
- A requested-VID gap above 12.5 mV can propose one negative step. Positive starting offsets, bound-reached cores, missing peers, stale readings and transitions have explicit reasons. No eligible evidence means no setter.
- After each trial, compare the settled gap at comparable reported clocks. Three trials on a core without at least a 6.25 mV reduction each trigger a stop for review. This is evidence of response, not an estimate of voltage-per-CO-step or correctness. Resuming does not reset this counter or the cumulative balancing budget.
- Every trial uses fresh full CO/context checks, scalar/CO preflight, cancellation checks and a final live write guard. Application tuning writes are excluded while the session owns hardware operations. External software can still change hardware between reads; the tool does not control other processes.
- The reference and candidate each use 2–5 scored runs (default 3), the same executable/recipe and unchanged other application tuning requests. Pausing validation preserves completed runs. Overlapping observed score ranges yield “no demonstrated performance improvement”; nonoverlap still asks for independent confirmation. This is not a confidence interval or a causal proof.
- Native run summaries retain their existing sample semantics, including observations outside busy windows. The busy-window means used for trial eligibility are separate from whole-run reported/effective-clock summaries. Do not treat whole-run means as load-only measurements.

## Stops, recovery and responsiveness

Temperature is sampled on a dedicated worker at approximately 1 Hz. A fresh plausible temperature and a stable window are required; the chosen threshold is a software stop, not a replacement for hardware protection. New Windows System WHEA-Logger events conservatively stop the experiment, without claiming core attribution. Subscription/read failures also block continuation. Existing historical events are not replayed as new incidents.

Missing/stale native capture, unavailable safety evidence, excess temperature, a WHEA incident, workload failure/exit, changed hardware/topology/editor context, failed persistence or failed verification stops further trials. Automatic rollback is not assumed safe after a failure. Original CO remains available for an explicit checked restore. A partial batch retains per-core evidence and original recovery information. A crash/restart never automatically reapplies a candidate.

The bounded journal atomically records frozen configuration, full-before/target intent, selection evidence, result/readback and measured response. It retains one previous journal and has entry/size limits. Persistence failure before intent prevents the setter. The original recovery snapshot is independent. Report export is bounded and serialized off the UI thread.

Acquisition requests 100 ms; display renders every 200 ms. Cadence is observed, not guaranteed. Native reads and writes share the application's hardware lifetime/serialization. UI rendering does no driver I/O; safety initialization and exports run outside the UI thread. Cinebench processes are created into an owned job and only that process tree is terminated on cancellation. Existing benchmark instances are not killed.

## Acceptance and remaining limits

Automated tests use fake clocks, native readings, safety signals and owned-process fixtures. They cover phase ordering, bounds, trial effectiveness, mid-preflight failures, cancellation, recovery, profile isolation and score exclusion. Synthetic WinForms layouts are inspected with normal and doubled fonts. These checks do not validate actual monitor DPI or firmware behavior.

Hardware acceptance still requires the user's native VID agreement and mapping, temperature/WHEA availability, actual Cinebench console/version behavior, sustained load, useful CO response and UI/acquisition overhead. No live tuning or real benchmark was performed during development.

Requested VID is not measured per-core supply voltage; making core VIDs equal does not establish an optimal or stable CO curve. The experimental heuristic can stop inconclusively. Individual rendering coverage is not a computational correctness adapter. Idle/light-load transitions, long-duration validation, WHEA core attribution and cross-workload correctness remain future work. Do not promote a candidate to startup solely because this flow completed.
