# v1.52.0 — Guided CO optimizer

The Tools menu now opens a guided CO session: Prepare → Reference → Balance under load → Validate fixed offsets → Decide.

- Check the remembered Cinebench 2024.1.0 executable and native physical-core mapping automatically. Opening the window sends no tuning setters or workload launch.
- Start from verified all-core CO 0 (default), or explicitly improve the current offsets. Preserve original offsets before the first change.
- Warm up and collect repeated fixed-offset reference scores. Balance with one continuous owned all-processor load and verified one-core -1 trials, bounded by time, offset and step limits. No scores from changing-offset load enter the comparison.
- Show each core's reference/current CO, native requested VID and eligibility reason. Require fresh busy samples, comparable same-CCD peers and clocks; exclude transitions. Stop after three trials without reproducible comparable VID-gap reduction on a core.
- Pause on missing/stale native evidence, temperature limits, WHEA observation failure/incidents, unexpected workload exit, context changes or failed verification. Guard the final setter boundary and persist trial intent before writing.
- Validate the fixed candidate with repeated scored runs. Report medians, observed ranges and overlap without claiming statistical significance or stability.
- Explicitly restore original CO, keep current offsets temporarily, or save a new experimental CO-only profile. Startup profile selection is unchanged. Interrupted sessions require a fresh current-state review before keep/save; no journal replay occurs on restart.
- Keep per-core rendering coverage separate from all-core balancing. Export the session, latest evidence and bounded trial journal.
- Improve startup by skipping unused DIMM/SPD/AOD/SuperIO inventory, narrowing the normal driver probe, loading P-state/AMD ACPI data on demand and reading startup-task state in the background. **Tools → Startup timings** reports window/tuning milestones; see [startup performance](startup-performance.md).

Release build, 520 hardware-free app tests, 9 upstream-bot tests and package success/failure checks passed. Synthetic normal/doubled-font layouts are checked separately from actual DPI and hardware acceptance. No live CPU setters or real Cinebench run were executed during development.

This is an experimental supervised tuner. VID is a voltage request, not measured per-core supply voltage. Temperature and Windows WHEA monitoring are stop signals, not a correctness engine. Core attribution, idle/transition testing, computational correctness and long-duration validation remain incomplete. Native telemetry agreement, overhead, firmware behavior and the user's real Cinebench installation require hardware acceptance. See [the guide](guided-co-optimizer.md).
