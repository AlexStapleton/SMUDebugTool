# Startup performance

The v1.52.0 startup follow-up removes work from both time-to-window and time-to-tuning. It does not claim a measured speedup on hardware that has not been tested.

## Changes

- **Skip unused inventory:** the app does not currently display the core library's DIMM/SPD, AOD-table or SuperIO inventory. It now disables those construction paths and their unused SMBus/LPC modules, instead of scanning every launch. The pinned net48 compatibility build honors the existing module options; original upstream files and revision stay unchanged. Explicit full-inventory options remain available for future consumers.
- **Keep necessary identity and telemetry:** CPU/SMU initialization, fuse/APOB core mapping, SMBIOS board/BIOS identity, native CPU temperature and power-table/SVI3 access remain on the initialized path. AMD ACPI controls use their independent WMI discovery and do not require the unused AOD memory table.
- **Narrow the driver startup probe:** normal startup reads service presence under the existing maintenance gate. It does not hash an existing driver or enumerate all processes. Unreadable presence never establishes ownership. A provably new installation still requires checked canonical path/hash before recording ownership; full maintenance inspection remains unchanged and still checks consumers before removal.
- **Read tuning first:** CO/FMax/Curve Shaper/BCLK/PROCHOT and manual-OC capabilities publish readiness without waiting for P-state or AMD ACPI discovery. Failed initial UI application leaves writes gated.
- **Load optional tabs on demand:** P-state registers load when PStates is opened; AMD ACPI commands load when AMD ACPI is opened, or when a CPU downcore/SMT action needs them. A saved default tab triggers its own discovery after essential values load. Repeated selections share one in-flight discovery and closing discards late results. Discovery faults can retry on reopening the tab.
- **Read Task Scheduler after showing the window:** one background connection reads task presence and profile together. Startup controls remain disabled until the observed state is applied with change handlers suppressed. Initialization does not register, delete or modify startup tasks.

## Measuring your machine

Open **Tools → Startup timings** after launch. The report separates **Main window shown** and **Tuning controls ready** milestones from CPU initialization, main-window construction, essential reads, startup-task discovery and optional-tab durations.

A bounded atomic JSON report is saved off the UI thread to `%LOCALAPPDATA%\SMUDebugTool\startup-timings.json`. It contains stage labels and monotonic timings, without hardware identifiers, profile values or driver paths. Timing failures do not fail startup. Reports represent the latest GUI launch and subsequent optional-tab discovery; UAC approval and time before managed program entry are excluded. Stages can overlap and must not be summed. Core-stage labels follow the library's progress boundaries, rather than individually instrumenting every native call.

Compare several launches from the same install and environment, separating the first/cold launch from subsequent launches. Check both milestones; a faster splash alone is not proof that tuning becomes ready sooner. Do not infer latency from the synthetic unit tests.

## Validation and remaining acceptance

The Release build and 520 hardware-free app tests pass. Tests cover deferred execution/coalescing/retry, cancellation and late-result exclusion, unknown/existing/new driver ownership, failure cleanup, fast/full module options and atomic timing reports. Nine upstream-bot tests and package success/failure checks pass. The build verifies pinned, clean core source before applying exact construction-site guards.

Hardware acceptance remains pending: startup times, APOB/native mapping agreement, VID/temperature/rail availability and on-demand ACPI/P-state behavior on the user's system. No live CPU initialization, tuning write, real benchmark or Task Scheduler mutation was performed during development. The guided optimizer's firmware and stability acceptance gates remain unchanged.
