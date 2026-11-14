# SMUDebugTool capability roadmap and Curve Optimizer workbench

Prepared 14 November 2026. Baseline: released v1.50.1, source `4d8d5d39cc5007fbd0106ac729bfc2e37f23e62d`; bundled ZenStates-Core `3bb1dad5b6cccc14f063d6eacbcf891a309944f3`.

This is an implementation plan, not an implementation or a hardware test. No tuning commands, benchmark processes, driver maintenance, startup-task changes, or profile changes were performed to prepare it. Scope covers the capabilities identified in our source/interface audit, plus a workbench for the user's HWiNFO64 and Cinebench 2024 workflow. It does not claim access to all private Ryzen Master driver functions.

## Living implementation tracker

Updated 14 November 2026. This document is now both the design and the change tracker. The preparation note above describes the original plan; implementation begins below. **Implemented** means the code and automated checks are delivered, not that a real BIOS has been tested. Update this table and the dated change log in each roadmap PR. Canonical repository path: `docs/capability-and-CO-roadmap.md`; the chat's output document mirrors it.

| Area | Current status | Delivered / remaining |
| --- | --- | --- |
| C01 Capability/provenance | Partial implementation | CPU/board/BIOS/SMU/topology identity, RSMU/MP1 metadata and checked read evidence. Unified WMI/driver evidence still planned. |
| C02 Unified overview | Partial implementation | On-demand observed CPU snapshot. Full requested/saved/observed consolidation still planned. |
| C03 Scalar verification | Implemented; firmware validation pending | Fresh float-decoded direct getter in shared apply path; invalid/missing replies cannot confirm. |
| C04 Thermal ceiling | Implemented; firmware validation pending | Separate guarded one-packet desktop RSMU action; accepted only, not falsely verified; no profile/startup changes. |
| C05 Limit diagnostics | Partial implementation | Individual fused command statuses/raw words and configured snapshots. Fused scaling and active-limit verification remain open. |
| C06 CPU/CCD/rail telemetry | Partial implementation | Manual snapshots of CPU/CCD temperatures and mapped SVI3 fields; no capture stream or graphs yet. |
| C07 Memory overview | Planned | Existing PMTable labels retained; consolidated view not started. |
| C08 Channel timings | Planned | No new implementation. |
| C09 DIMM/SPD/EXPO | Planned | No new implementation. |
| C10 Guided firmware editor | Planned | Existing v1.50 metadata guidance retained; grouped editing not started. |
| C11 Session recording | Next main milestone | Baseline/candidate records and Cinebench 2024 markers, then CSV import. |
| C12 Assisted CO | Planned after C11 | Core VIDs confirmed as user's source; mapping/native feasibility still open. |
| C13 Validation/recovery | Planned before automatic search | No experiment journal, WHEA integration or automatic search is active. |
| C14 Manual-OC reporting | Planned | Existing dedicated controls retained. |
| C15 GPU CO | Conditional/planned | Firmware acceptance untested. |
| C16 Profile expansion | Deferred per feature | Thermal action remains standalone; no new profile fields in this milestone. |
| C17 Mobile/APU expansion | Conditional/planned | Not included in desktop thermal contract. |
| C18 WMI/HSMP extensions | Research | No speculative commands added. |

### Change log — 14 November 2026, first CPU-overview milestone

- Added `CpuOverviewForm`, `OverviewReadPlan`, immutable snapshot/readings and serialized native capture. Opening/refreshing starts no timer and sends no tuning setters. View closes without waiting for pending reads; parent shutdown still waits safely before driver disposal.
- Added scalar readback after accepted scalar writes, keeping GUI/headless results consistent. The getter encodes an IEEE float, distinct from the existing setter's scaled integer. Missing, failed, short, nonfinite or out-of-range responses remain unverified.
- Added standalone temperature action only for reviewed Raphael/Granite Ridge maps, with a clearly labeled software guard. It uses the shared validation/result path, preserves raw route/status and does not retry, disable protections, save profiles or update startup tasks.
- Added per-command fused/configured read evidence, snapshot times and rail/request labels. Unavailable data never becomes a measured zero. Fused scaling is deliberately not guessed.
- Added driver-free packet, failure/cancellation, snapshot-isolation and UI tests. Build/test/package evidence and PR link are recorded below when complete.
- Hardware validation remains pending. No live Cpu instance, tuning write, benchmark, driver maintenance or startup task was invoked during implementation/testing.

Validation record: Release build passed with zero warnings/errors; **414 app tests** (32 new cases) and **9 upstream-bot tests** passed; package success/failure checks passed. Synthetic overview renders inspected at normal and doubled font sizes; corrected clipped headers/value cells. This does not establish live firmware behavior or mixed-monitor DPI correctness. PR: [#22 — first CPU-overview milestone](https://github.com/AlexStapleton/SMUDebugTool/pull/22). Release publication: not started.

## Product goal

Help a user answer three questions: what can this machine do, what actually changed, and did that change improve the workload they care about?

Retain the existing low-level debug tabs. Add a readable overview, memory views, and a tuning-session workbench. New supported controls use the existing checked apply/result path. A visible control must not imply that a command works on every AMD CPU.

## 1. Complete capability inventory and user benefit

Availability classes: **existing** means we already have an app path; **integrate** means the bundled library contains relevant code; **conditional** requires platform/firmware discovery; **research** means a usable, correctly interpreted implementation is not yet established. Library presence is evidence of an implementation opportunity, not proof of hardware support.

| ID | Deliverable | Starting point / boundary | Recommended use and user payoff | Order |
| --- | --- | --- | --- | --- |
| C01 | Capability and provenance report | Integrate command maps, firmware command lists, topology and successful read results | Before tuning, see supported, rejected, unavailable and untested operations; understand why a control is disabled | 0 |
| C02 | Unified observed/requested overview | Existing edit tracking and detached results | Check which values came from the CPU, a saved profile, firmware setup, or the current session; avoid mistaking acceptance for readback | 1 |
| C03 | Scalar readback and verification | Integrate `TryGetPBOScalar`/checked raw getter | Confirm the scalar sent by the PBO editor; see a mismatch instead of assuming the change worked | 1 |
| C04 | CPU temperature ceiling | Integrate `SetTctlMax`; command exists in inherited Zen 5 maps | Request a lower thermal ceiling for quieter/cooler operation and measure the performance tradeoff | 1 |
| C05 | Fused and configured power/thermal diagnostics | Integrate fused-limit and configured-limit commands; validate each returned field separately | Understand factory limits versus configuration; troubleshoot a limit that appears to be ignored | 1 |
| C06 | CPU/CCD temperatures and rail telemetry | Integrate CPU/CCD readers and mapped PM-table/SVI3 fields | See temperatures, requested VID, measured rail voltage and current while testing a change | 1 |
| C07 | Memory clocks, ratios and voltage overview | Integrate existing FCLK/UCLK/MCLK and mapped voltage data | Diagnose memory clock ratios and check what the firmware reports without searching raw PM-table rows | 2 |
| C08 | Timing snapshot by channel | Integrate `MemoryConfig` and supported timing layouts | Compare actual decoded timings against the intended memory setup; copy a readable support report | 2 |
| C09 | DIMM/SPD/EXPO and telemetry page | Integrate supported DDR4/DDR5 readers; conditional sensor access | Identify installed memory and profile data, and watch supported DIMM temperatures during memory testing | 2 |
| C10 | Structured firmware memory/SMT editor | Existing generic WMI command enumeration, input metadata and status parsing; conditional commands | Edit related supported fields together with names, units, choices and explicit activation requirements | 2 |
| C11 | Tuning-session recording and comparison | New work built on C01/C02/C06 | Replace manually watching fast sensor rows with reproducible baseline/candidate summaries and an exportable timeline | 3 |
| C12 | Assisted per-core CO workbench | New orchestration using existing per-core CO and verification | Find worthwhile candidate offsets with fewer manual steps; retain evidence of what was tested on each core | 3 then 4 |
| C13 | Broader validation and crash recovery | New session journal, workload adapters, WHEA/event correlation and recovery handling | Distinguish a completed benchmark from a broader validation campaign; recover an interrupted experiment without startup reapplication | 4 |
| C14 | Manual-OC observation and consistent result reporting | Existing setters; additional reads conditional | Separate command acceptance, target register readback, observed clocks and measured voltage in one result model | 5 |
| C15 | GPU Curve Optimizer | Integrate setter/getter; commands appear in inherited desktop maps, firmware acceptance untested | For supported integrated graphics, test a GPU curve offset against graphics performance, power and errors | 5 |
| C16 | Reviewed profile expansion | Existing profiles cover CO, CS, FMax, PPT/TDC/EDC and scalar | Save additional validated settings, initially the temperature ceiling; keep experiments and firmware/setup changes distinct from startup profiles | incremental |
| C17 | APU/mobile power and clock controls | Conditional library APIs: STAPM, slow/fast limits and timing, skin-temperature limits, SoC TDC/EDC, clock bounds | On supported laptops/APUs, tune sustained behavior or a power envelope using controls relevant to that device | 6 |
| C18 | Additional firmware and HSMP investigation | Research: SPD/profile/OPP WMI methods and platform-specific HSMP contracts | Potentially obtain richer supported data; present findings as diagnostics until the platform contract is proven | research |

C01-C16 are the desktop roadmap. C17 is a separate platform expansion, not a list of promised 9950X3D features. C18 is an investigation track, not a release commitment.

## 2. Implementation sequence and release gates

### Stage 0 — Know what the machine exposes

Create `CapabilitySnapshot` and `SensorDescriptor` models. Record CPU family/model, BIOS/SMU version, physical topology, driver/backend identity, firmware command metadata, and source revision. Keep command-defined, read-successful, write-accepted and write-verified as separate facts. Reports include units, freshness, scope (core/CCD/package/DIMM), limitations and rejection reasons.

Discover metadata and use allowlisted, status-checked reads where appropriate. Do not discover support by sending setters, scanning unknown command IDs, enabling OC mode, or changing a value and changing it back. Refresh capability evidence after a hardware/firmware session changes. Export a compact support bundle with serial numbers and unrelated Windows event contents excluded by default.

**Gate:** opening discovery sends no tuning write. Failed, stale, truncated, all-zero ambiguous or unmapped data cannot become a supported measurement. Fake fixtures cover missing commands, partial initialization and topology changes.

### Stage 1 — Finish desktop readback and telemetry

Implement scalar verification first, then the temperature ceiling and limit diagnostics. Extend result formatting so every new setting retains the raw route/status and distinguishes accepted, verified, mismatched and inconclusive outcomes.

Audit the scalar representation and response status rather than assuming its wire value equals the UI multiplier. For temperature writes, select one reviewed platform route; do not inherit convenience-method retries blindly. No disabling PROCHOT or other protections to achieve a target. A temperature observation is not readback of the requested temperature ceiling.

Fused-limit readers must preserve each subcommand's status. One successful final command cannot validate preceding failed fields. Fused defaults, configured setup limits, active limits and instantaneous consumption have separate labels. Add CPU/CCD temperature and mapped rail fields to a readable view. Unknown fields stay unavailable, not zero.

**Gate:** existing CO/CS/FMax results and profiles remain compatible; packet/unit tests cover success, mismatch, failure and partial data. On hardware, verify fresh read-only values before enabling each new write control. Active PPT/TDC/EDC verification remains unavailable until a valid active-limit source is demonstrated for that platform.

### Stage 2 — Make memory information useful

Start read-only: display MCLK/UCLK/FCLK, ratios, channel timings, SPD/profile data, and mapped voltage/temperature fields. Differentiate firmware setup snapshots, decoded registers and live telemetry. A memory-clock field uses MHz; DDR transfer rate uses MT/s. Identify supported channels and DIMMs without assuming slots or channel numbering.

Then build a structured editor over firmware-listed commands. Reuse existing min/max/step/list validation. Add encodings only when documented or validated for the exact command/platform. Preserve raw access for advanced users. Keep memory/SMT firmware actions outside runtime CPU tuning profiles initially.

For multiple firmware edits, validate the whole request before sending; record each result. Firmware operations are not atomic. On a partial failure, show what was accepted and what was not attempted; do not promise rollback or an automatic reboot. After restart, re-read supported actual state to compare with the intended settings.

**Gate:** no writes on view/refresh; no guessed voltage conversions; no unvalidated live-apply claims. Cache SPD reads and avoid high-rate SMBus/embedded-controller traffic. Layout tests cover long names, scaling, missing channels and partially readable DIMMs.

### Stage 3 — Ship the manual CO workbench first

Build session recording and comparison before automatic search. Support an independently launched Cinebench 2024 run, manual start/end markers, score entry and HWiNFO CSV import first. Add optional live HWiNFO shared-memory reading after format, identity mapping, freshness and licensing constraints are reviewed. Native telemetry remains usable without HWiNFO where supported.

Expose one per-core table: physical core/CCD, current and candidate CO, readback outcome, active-sample VID if available, effective and reported clock, utilization/residency when available, test coverage and errors. Put measured rail voltage, package power and temperature in separate package/rail panels, not duplicated as alleged per-core physical voltages.

Allow one candidate or one explicitly selected batch to be applied while recording. Mark the transition, wait for a suitable comparable measurement window, and retain the previous run. A finite benchmark scored across a mid-run offset change is a mixed run and cannot serve as the score for either configuration. Collect clean scored runs after the candidate settles.

**Gate:** mapping must be validated before per-core conclusions or writes. Missing per-core VID does not block recording clocks and benchmark results, but disables VID-based guidance. No automatic offset changes in this release. Export/import round-trips preserve units, provenance, settings and missing-data flags.

### Stage 4 — Add assistance, then optional bounded search

Add repeatable core-focused tests, workload/error integration and a reviewable candidate planner. First offer suggestions with a reason, e.g. a core has higher requested VID under comparable activity, or a candidate lost performance outside observed run variation. Do not infer a universal voltage-per-CO-step conversion or rank a core's quality from VID alone.

Only after manual sessions and validation work correctly, add optional automatic testing inside user-selected offset bounds, time budgets and test recipes. Start with the current verified offsets or another explicitly chosen baseline. Change one core or one declared batch at a time. Exclude Curve Shaper and other tuning dimensions from the initial CO search. Freeze their recorded settings, detect external changes where readable, and invalidate comparisons when the context changes.

Candidate outcomes: accepted, readback-confirmed, screen passed, validation incomplete, failed, interrupted and inconclusive. Search should retain a tested candidate with margin from a failure boundary rather than simply selecting the most negative attempted offset. Neither a short run nor an error-free Windows event log earns a universal stability guarantee.

Re-test the selected candidate and baseline using fresh runs not used to select the candidate, so searching many offsets does not turn ordinary noise into an apparent improvement. Keep a tested-candidate frontier for performance, power and temperature rather than claiming that one offset is best for every workload. Curve Shaper experiments can follow later as a separate mode: change one declared frequency/temperature-band request at a time, keep CO fixed, and report coverage without guessing undocumented band thresholds or claiming the active band was observed.

**Gate:** stop on stale essential telemetry, unknown target mapping, unsuccessful verification, workload failure, configured thermal stop, or hardware errors. Cancellation sends no further candidate writes. After current I/O completes, restoration can be attempted only from a known snapshot under a still-valid session; verify restoration and report failure. A hung kernel call or system reset cannot be reliably undone by an in-process watchdog.

### Stage 5 — Extend supported desktop tuning

Unify manual-OC reporting and add GPU CO only on validated command maps. Keep manual OC mutually exclusive with CO search. Readback of a request/register is distinct from effective clocks and measured rail voltage. A frequency cap is not disproved just because an idle core runs below it.

Expand profiles through versioned optional fields and whole-request validation, starting with temperature. Keep new experimental candidates separate from the selected startup profile. BCLK, raw ACPI, P-state VID and manual-OC startup automation require separate dependency/ordering and recovery reviews; being editable does not automatically make a setting appropriate to apply unattended.

**Gate:** old profiles load unchanged; missing capabilities fail before mixed writes where dependencies require it. Existing startup behavior remains covered by regression tests. GPU CO needs a graphics workload and GPU-specific evidence, not Cinebench CPU success.

### Stage 6 — Support other AMD platforms deliberately

Add mobile/APU controls in device-specific groups after confirming units, routes and applicability. Include power time windows, STAPM, SoC current and supported frequency bounds. Explain overlapping limits and dependencies. Provide independent acceptance/readback evidence for each control. Do not copy mobile controls onto the 9950X3D because their APIs exist.

Keep additional WMI methods and HSMP development in a separate investigation ledger. Establish schema/version and client-versus-server semantics before exposing operations. Do not reuse EPYC message IDs merely because a desktop mailbox address exists.

## 3. The user's voltage-balancing workflow

The user confirmed the sensor is **Core VIDs**. The current workflow is HWiNFO64 at a requested 20 ms polling interval, Cinebench 2024 CPU load, then negative per-core CO adjustments until the displayed Core VIDs converge. Preserve Core VIDs as the initial user-selected sensor family; do not silently substitute Core VID (Effective), a package VID, or measured rail voltage. Exact external sensor IDs, physical-core mapping, unit conversion and update timestamps still require validation.

HWiNFO's separately named **CPU Core VID (Effective)**, where available, denotes the final requested core-rail VID; it is not a time-averaged per-core VID alternative and cannot replace the individual Core VIDs in a core comparison. Its absence does not block this workflow. Do not reconstruct it as the maximum or average of separately sampled core rows: source methods and sample times can differ. Keep **Core Effective Clocks** as a distinct frequency metric.

**Useful part:** a repeatable workload makes it easier to observe how curve changes affect requests, clocks, temperature and performance. Recording this in our program can remove substantial manual comparison work.

**Adjustment to the objective:** identify the sensor first. Per-core VID is a request, not an independently measured voltage delivered to that core. Shared rail behavior means lowering a non-dominant core's request may not lower supplied rail voltage. A high VID can also accompany a different frequency or operating condition. Identical VIDs are therefore not proof of efficiency, equal silicon requirements or stability. A lower voltage request can be accompanied by higher boost rather than lower measured voltage.

Retain a **VID spread** visualization as an optional diagnostic, not the success criterion. Show median, distribution, valid sample count and comparable activity/clock/temperature windows. Exclude sleeping, parked, stale or insufficiently loaded samples; do not replace them with zero. Use population summaries over a window rather than equality of individual 20 ms samples. Unknown sensor semantics disable matching-based suggestions.

For the 9950X3D, group by validated physical CCD and measured capability. Detect/cache-label differing CCD roles when supported; do not assume equal targets or a fixed cache CCD number. Start comparisons within a CCD and within similar operating windows, then evaluate the overall workload. Native per-core VID is a feasibility gate: the bundled `HwPstateStatus.CurCpuVid` is a legacy-width field, and its logical-index calculation is not our validated physical-core mapping. Do not reuse it blindly for Zen 5 per-core guidance.

Offer three user objectives:

| Objective | How the program evaluates a candidate | User payoff |
| --- | --- | --- |
| Performance | Repeatable score/throughput improvement with error-free tested coverage; effective clocks and limits explain the result | More useful work, not merely a higher reported clock |
| Efficiency | Equivalent performance within measured variation with lower measured package/rail energy for the same work | Less heat/power for similar output; CPU energy is not wall-system energy |
| Quiet/cool operation | Stay within user-selected power/temperature preferences with an acceptable measured performance tradeoff | A predictable thermal envelope and a documented cost in performance |

VID balancing can help generate a candidate in any mode; it cannot override a regression or failure. Do not chase identical requests across all sixteen cores.

### Recommended user journey

1. Open the workbench, inspect source availability and verify core mapping. For this user's workflow, select HWiNFO Core VIDs and confirm their individual physical-core bindings; preserve each sensor's name, ID, unit and scope in the session. Other sensor families remain explicitly selectable alternatives, not automatic substitutions.
2. Choose the goal and capture the current CO/CS/FMax/PBO context. Record BIOS/SMU version, power plan, workload version/settings, affinity and cooling context. An unreadable value is recorded as unknown, not silently assumed stock.
3. Record an unchanged baseline. Warm up consistently, exclude startup/settling intervals, and repeat clean scored runs to estimate ordinary variation. Retain a separate idle/light-load segment.
4. Display per-core/CCD summaries. Identify a candidate using comparable active samples. The user can edit manually or accept a later recommendation; the program does not call a high-VID core defective.
5. Apply the candidate once through existing CO verification. Keep other tuning dimensions fixed. Record exact timestamps, affected cores and any intermediate/failed write. A confirmed setting still has validation pending.
6. Record a separate candidate run. Compare score, effective clocks, requested VID, measured rail voltage, package energy/power, temperatures and test results. Mark throttling, source gaps or a changing thermal context.
7. Validate beyond the all-core render: selected core single-thread tests, SMT-sibling/paired tests, heavier and lighter workloads, transitions, idle and the user's real applications. Where practical, use computational correctness tests with explicit pass/fail output. A Cinebench completion is performance evidence for that workload, not comprehensive error checking.
8. Keep, discard or continue. Save a named experimental result with its test coverage. Only explicitly promote a chosen validated profile to startup. Interrupted/failed experiments cannot become the startup candidate automatically.

Example presentation, without invented hardware results: “Core 4: CO readback confirmed; Cinebench comparison available; single-core validation pending.” After tests: “Selected tests passed for the recorded durations.” Avoid “CPU is stable” as an absolute claim.

## 4. Capture, performance and integration design

### Sampling model

Separate acquisition, analysis and rendering. Each value has a sensor ID, source, unit, physical scope, source/sample timestamp, quality and freshness. A capture frame records start/end and per-source skew; sequential core reads are not a simultaneous hardware snapshot. Re-reading the same shared-memory update does not create a new independent measurement.

Proposed starting policies, to be measured rather than advertised as achieved:

| Work | Initial policy |
| --- | --- |
| Selected fast native CPU metrics | 100 ms default; optional requested 20 ms mode only after reader and overhead validation |
| HWiNFO shared-memory consumption | Follow provider updates; report provider age and actual update cadence, including 20 ms when available |
| PM-table/thermal metrics | Source-specific measured rate; initially 250-1000 ms, with freshness shown |
| Slow SPD/SMBus/board reads | On demand or 1-2 s when validated; never blanket 20 ms polling |
| UI table/chart refresh | Approximately 5 Hz; acquisition remains independent |
| History | Bounded in-memory window, e.g. 120 s plus a 64 MiB cap; optional streamed recording with a storage quota |

Use a persistent worker and monotonic clock, not a new task for every sensor tick. Skip missed deadlines and busy hardware windows; never queue a catch-up burst. Hardware transactions remain serialized through the existing lifetime/lock contract. Tuning writes take precedence over optional monitoring; scans and dumps are paused during a controlled experiment. Cancellation is checked between operations; no disposal until active I/O finishes.

Keep slow I/O outside fast batches. One source's delay must not cause stale sensor values to look fresh. Display requested versus achieved sampling rate and dropped/missed samples. Do not promise 50 independent hardware measurements per second just because the timer is set to 20 ms. Counter-based effective-clock readers require family-supported semantics, wrap/reset handling, pinned validated targets and tests; ratio x BCLK remains a reported clock, not an effective clock substitute.

Integrate power over actual elapsed timestamps only when source quality and coverage support it; identify missing intervals and the measured domain. Do not treat requested PPT as consumed power, multiply a benchmark score by a power sample and call it efficiency, or present CPU/package energy as whole-system wall energy. If power coverage is inadequate, retain performance/temperature comparisons and mark energy unavailable.

Benchmark capture overhead with capture off, normal capture and fast capture under otherwise identical repeated runs. Initial engineering targets: UI input acknowledgments within 100 ms under synthetic I/O delay, no overlapping native I/O, bounded memory, and capture-induced score changes within the established measurement noise. If fast mode misses its budget or shifts results, report it and offer a slower mode. These are release criteria, not present measurements.

### External applications

Support manual workload launch and CSV import first; these do not require taking over an existing process. Later adapters can launch a user-selected local executable, capture exit/output and manage only the child they own. Never terminate unrelated benchmark instances. Store workload version and recipe; scores from different Cinebench releases are not directly comparable. Verify Cinebench 2024 automation against that release rather than assuming current Cinebench 2026 flags apply unchanged.

Optional HWiNFO live integration reads a user-run installation; do not bundle HWiNFO or claim an SDK license. Respect its shared-memory availability and limits. If the provider stops, marks data stale or disables sharing, show disconnected and suspend dependent recommendations/search. Never manufacture samples or automatically restart it to circumvent a limit. Map stable sensor identity and topology, not display-row position alone. A CSV import with uncertain core mapping can support package analysis but not per-core writes.

### Errors, recovery and startup

Journal the baseline, candidate, verified write outcomes, source context and phase to disk before workload/candidate advancement. Subscribe to new relevant WHEA records from the session boundary. Preserve attribution confidence: an APIC ID is not automatically the UI core index, and a WHEA event can originate outside CO. Deduplicate events and distinguish historical, corrected, fatal and unrelated reports. A process crash, timeout or unexpected reboot can indicate an interrupted/failed experiment without proving which core caused it.

Normal stop ends owned workloads and stops new writes promptly; it may offer or perform the session-authorized restore transaction when the driver remains responsive. Report partial restoration. After a system crash, next launch opens recovery without silently replaying or continuing the experiment. To make that effective, GUI and `--applyprofile` must share a persistent experiment interlock: a pending/interrupted trial blocks automatic experimental reapplication. If the current selected startup profile has not been independently validated, automated search must not arm until the user chooses a recovery/startup policy. An existing stable startup profile may remain eligible only when explicitly identified and not overwritten by experiments.

Recovery records are machine/topology-bound and written atomically; corrupt/missing metadata fails closed for experiment resumption. Never automatically resume a search after a crash. The program cannot guarantee recovery from hardware hangs, firmware persistence or a configuration applied independently in BIOS.

## 5. Architecture and verification plan

Suggested new components:

- `CapabilityService`: platform metadata and checked capability evidence.
- `TelemetryCoordinator` / provider adapters: native, HWiNFO live and CSV; immutable source-tagged snapshots.
- `CoreIdentityMap`: physical SMU core, Windows group/logical processor, SMT siblings and external sensor identities, with validation status.
- `ExperimentSession` / journal: baseline, configuration generations, trials, segments and recovery interlock.
- `WorkloadAdapter`: manual markers first; versioned child-process adapters later.
- `ComparisonAnalyzer`: valid-window filtering, repeated-run variation, score/energy comparison and explicit inconclusive outcomes.
- `CoCandidatePlanner`: bounded proposals/search; independent from telemetry rendering and existing tuning transport.
- `ValidationRecorder`: correctness-test output, Windows error evidence and coverage; no absolute stability certificate.

Reuse `TuningService`, validated topology, detached results, `HardwareOperationRunner`, `HardwareLifetime` and current package checks. Keep the source pin unchanged until a separate dependency review is justified. Extend profile schema only when a new field actually ships.

Automated verification uses fake transports/providers/workloads; it must not construct a live CPU or write real settings. Cover signed offsets, sparse/disabled cores, reordered sensor rows, processor groups, SMT, mixed CCD roles, source skew, duplicates, stale/missing values, counter resets, slow I/O, cancellation, partial writes/restores, external edits, event attribution, experiment crashes and startup interlock. Test statistical comparison against known synthetic noise, not merely the implementation's chosen formula. Include long-running bounded-memory and UI responsiveness checks.

Hardware acceptance is staged: read-only captures and mapping checks first; then user-initiated known CO changes; then manual baseline/candidate comparison; then validation adapters; finally optional bounded search. Compare native and HWiNFO sources at equivalent windows while preserving their different meanings. Verify actual driver serialization and scheduling overhead before enabling fast native capture. No claimed success on one BIOS establishes support for every Zen 5/X3D system.

## 6. Recommended delivery decisions

1. Build Stage 0 and Stage 1 as the first release: capability report, scalar verification, thermal ceiling and a compact read-only overview. This gives useful improvements before the larger workbench.
2. Prioritize Stage 3 next for this user: a manual session recorder with Cinebench 2024 markers, CSV import and per-core/CCD comparisons. Native per-core VID feasibility runs in parallel as an engineering investigation, not as a prerequisite for all session recording.
3. Ship memory reads/editor work as an independent track so it does not block CO work. It targets a different user task and has different activation semantics.
4. Add assisted proposals and broader validation after recorded comparisons are trustworthy. Automatic search is the last CO milestone, not the first demo.
5. Add manual-OC/GPU refinements and mobile/APU support independently, with their own hardware acceptance evidence. Keep live VDDG/DRAM voltage setters and unknown HSMP/WMI formats in research until proven.

## 7. Plan review

Reviewed against all ten desktop gaps in the capability audit plus mobile/APU expansion, extra WMI methods, HSMP investigation and profile limitations. Each has a roadmap entry. No research item is represented as confirmed hardware support.

The design keeps distinct: requested VID versus measured rail voltage; physical core versus Windows logical index; register confirmation versus test outcome; fused/setup limits versus active limits; timer cadence versus source update rate; Cinebench score versus computational correctness; a passed workload versus universal stability. Sampling and startup recovery are dependencies of automation rather than optional cleanup work.

Resolved user preference: the HWiNFO sensor family is Core VIDs. Unresolved implementation gates: exact external sensor IDs and units; reliable native Zen 5 per-core VID source; validated physical-to-OS-to-sensor mapping; effective-clock counter contract; available PM-table layout and active-limit reads on the user's BIOS; Cinebench 2024 automation behavior; external workload result formats; live integration terms/format; actual 20 ms overhead. None prevents capability reporting, scalar work, read-only memory views or the manual/CSV session prototype.

## Primary sources and local evidence

- [Bundled CPU APIs](https://github.com/irusanov/ZenStates-Core/blob/3bb1dad5b6cccc14f063d6eacbcf891a309944f3/Cpu.cs), including the legacy-width status VID field and the current logical-index calculation. A public getter name alone is not a validated Zen 5 sensor contract.
- [Zen 4 command map](https://github.com/irusanov/ZenStates-Core/blob/3bb1dad5b6cccc14f063d6eacbcf891a309944f3/Hardware/Smu/Settings/Zen4Settings.cs) and [Zen 5 inheritance](https://github.com/irusanov/ZenStates-Core/blob/3bb1dad5b6cccc14f063d6eacbcf891a309944f3/Hardware/Smu/Settings/Zen5Settings.cs).
- [Power-table telemetry](https://github.com/irusanov/ZenStates-Core/blob/3bb1dad5b6cccc14f063d6eacbcf891a309944f3/Hardware/PowerTable.cs) and [memory readers](https://github.com/irusanov/ZenStates-Core/blob/3bb1dad5b6cccc14f063d6eacbcf891a309944f3/Hardware/DRAM/MemoryConfig.cs).
- [AMD Ryzen Master 3.1.2 CPU guide, 2 November 2026](https://docs.amd.com/r/en-US/68886-ryzen-master-user-guide/CPU): curve tuning, operating modes and test-based optimization. The guide describes Ryzen Master, not an assurance that our interfaces expose the same functions.
- [AMD Curve Optimizer FAQ](https://www.amd.com/content/dam/amd/en/documents/products/software-tools/faq-curve-optimizer.pdf): derived offsets depend on the test and duration; users should validate their workloads. Its historical support list is not used as the current platform matrix.
- [HWiNFO author on VID versus supplied voltage](https://www.hwinfo.com/forum/threads/can-i-view-actual-cpu-core-voltages-not-vids.8586/): supports the measurement distinction, not a model-specific complete rail topology for every Ryzen CPU.
- [HWiNFO author on final effective VID](https://www.hwinfo.com/forum/threads/hwinfo-v6-40-released.6894/) and [different VID sampling methods](https://www.hwinfo.com/forum/threads/cpu-core-vid-effective-vs-core-vids.7749/): the final request is distinct from individual core readings and supplied voltage.
- [HWiNFO author on effective clocks](https://www.hwinfo.com/forum/threads/effective-clock-vs-instant-discrete-clock.5958/): effective clock includes behavior across the measurement interval, including sleep.
- [HWiNFO shared-memory availability](https://www.hwinfo.com/licenses/) and [SDK information](https://www.hwinfo.com/sdk/): verify applicable integration terms during implementation.
- [Maxon Cinebench technical information](https://www.maxon.net/en/tech-info-cinebench): workload/environment repeatability and version-specific automation. The currently published page documents Cinebench 2026; the user uses 2024, which needs its own adapter validation.
- [AMD-authored WMI interface proposal](https://lore-kernel.gnuweeb.org/platform-driver-x86/20261010190543.563161-4-Shyam-sundar.S-k@amd.com/): firmware metadata, extra methods and setup-versus-active limitations. This is a proposed Linux driver/interface document, not proof of uniform Windows BIOS behavior.
- App baseline inspected locally: `Utils/ZenStatesTuningTransport.cs`, `Utils/TuningService.cs`, `Utils/TopologySnapshot.cs`, `Utils/Hardware.cs`, `Utils/HardwareLifetime.cs`, `Utils/HardwareOperationRunner.cs`, `Utils/SmuDecodeAdapter.cs`, `Utils/AcpiCommandHelp.cs`, `Profiles/Profile.cs`, `Program.cs`, `Utils/StartupTaskService.cs` and monitor forms.


