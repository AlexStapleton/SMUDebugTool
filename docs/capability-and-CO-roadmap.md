# SMUDebugTool capability roadmap and Curve Optimizer workbench

Prepared 14 November 2026. Baseline: released v1.50.1, source `4d8d5d39cc5007fbd0106ac729bfc2e37f23e62d`; bundled ZenStates-Core `3bb1dad5b6cccc14f063d6eacbcf891a309944f3`.

This began as an implementation plan; the tracker below records delivered code separately from hardware testing. No live tuning commands, benchmark processes, driver maintenance, startup-task changes, or profile changes were performed to prepare or implement it. Scope covers the capabilities identified in our source/interface audit and a native workbench for the user's Cinebench 2024 workflow. It does not claim access to all private Ryzen Master driver functions.

**User direction, 14 November:** capture per-core VID through this program's existing driver access and use those measurements for assisted CO trials. No HWiNFO CSV imports or HWiNFO runtime dependency. This replaces the original external-import proposal throughout this roadmap.

## Living implementation tracker

Updated 14 November 2026. This document is now both the design and the change tracker. The preparation note above describes the original plan; implementation begins below. **Implemented** means the code and automated checks are delivered, not that a real BIOS has been tested. Update this table and the dated change log in each roadmap PR. Canonical repository path: `docs/capability-and-CO-roadmap.md`; the chat's output document mirrors it.

| Area | Current status | Delivered / remaining |
| --- | --- | --- |
| C01 Capability/provenance | Partial implementation | CPU/board/BIOS/SMU/topology identity, RSMU/MP1 metadata and checked read evidence. Unified WMI/driver evidence still planned. |
| C02 Unified overview | Partial implementation | On-demand observed CPU snapshot. Full requested/saved/observed consolidation still planned. |
| C03 Scalar verification | Implemented; firmware validation pending | Fresh float-decoded direct getter in shared apply path; invalid/missing replies cannot confirm. |
| C04 Thermal ceiling | Implemented; firmware validation pending | Separate guarded one-packet desktop RSMU action; accepted only, not falsely verified; no profile/startup changes. |
| C05 Limit diagnostics | Partial implementation | Individual fused command statuses/raw words and configured snapshots. Fused scaling and active-limit verification remain open. |
| C06 CPU/CCD/rail telemetry | Partial implementation | Manual CPU/CCD/SVI3 snapshots plus bounded native per-core VID/reported/effective-clock capture. Continuous package/thermal capture and graphs remain planned. |
| C07 Memory overview | Planned | Existing PMTable labels retained; consolidated view not started. |
| C08 Channel timings | Planned | No new implementation. |
| C09 DIMM/SPD/EXPO | Planned | No new implementation. |
| C10 Guided firmware editor | Planned | Existing v1.50 metadata guidance retained; grouped editing not started. |
| C11 Session recording | Managed/manual implementation; hardware validation pending | Cinebench 2024.1.0 process ownership, duration/timeout, parsed scores, baseline/candidate records and export. Repeated-run statistics and richer telemetry remain planned. |
| C12 Assisted CO | Explicit live/recorded one-step trials implemented | Zero/current-CO baseline choice; qualified proposal retained from a successful managed run; unchanged-context and fresh CO checks before applying. Automatic search remains deferred. |
| C13 Validation/recovery | Partial implementation | Owned-process cancellation, core/SMT affinity, failure gates, persistent original CO recovery and startup-apply interlock. Correctness adapters, WHEA attribution and automatic search remain planned. |
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

### Change log — 14 November 2026, native CO workbench

- Added **Tools → Native CO workbench** using the existing current-processor MSR backend. Reviewed desktop gates: Raphael family 19h/model 61h and Granite Ridge family 1Ah/model 44h. Physical/APIC/SMT identity must agree before capture; list positions are never CO targets.
- Implemented read-only per-core status VID and reported clock plus counter-based APERF effective clock. Missing/reset/failed/stale intervals remain unavailable. Native read evidence includes APIC/Windows identity and raw status for hardware acceptance checks.
- Added a persistent cancellable worker, skipped busy hardware windows, a five-second/16,000-reading ring, 200 ms UI rendering and requested/achieved cadence reporting. Twenty milliseconds is a request, not a measured performance promise.
- Added baseline/candidate markers, manual benchmark score, bounded online summaries, wrapped run table, JSON report export and explicit new-session action. Endpoint CO reads and editor requests are separate evidence; app write attempts mark a recording mixed. No CSV/import/live HWiNFO path remains.
- Added one-action assisted trials: at least 20 valid busy samples over two seconds, similar reported clocks, two comparable peers in the same CCD, a 12.5 mV requested-VID gap, user lower bound, usable scalar evidence and unchanged fresh CO. One action sends at most one CO decrement and verifies it through the existing result path. It does not loop, launch a workload, select startup profiles or declare stability.
- Added 40 driver-free cases covering native decoding, identity ambiguity, counter intervals, failed reads, affinity restoration, stale/unloaded data, bounded proposals, context comparisons, cancellation and capture/write races. Release build, **454 app tests**, **9 bot tests** and package success/failure checks passed. Synthetic native views were inspected at normal/doubled fonts; live readings and run comparisons use separate tabs to retain readable rows. PR [#22](https://github.com/AlexStapleton/SMUDebugTool/pull/22) carries the combined milestones and CI status; release publication has not started.
- No live CPU access was used during development. Exact firmware VID behavior, physical binding on the user's machine, capture overhead and useful tuning outcomes remain hardware acceptance gates. See [native workbench contract](native-co-workbench.md).

### Change log — 14 November 2026, managed workload milestone

- Added a **Managed tests** tab: choose and remember a local Cinebench 2024.1.0 executable, map all-processor/core/SMT targets, select duration/timeout and run a baseline or candidate without switching applications to launch it.
- Baselines explicitly choose verified all-core CO 0 or current offsets. Save original direct CO readback atomically before a reset or native assisted write. Preflight the whole physical batch; stop on failed verification and retain partial per-core results.
- Added suspended owned-process creation, job assignment before execution, restricted handle inheritance and bounded output parsing. Stop, timeout, stale essential capture, reported errors and close terminate only the owned job tree. Existing benchmark processes are untouched.
- Retain a qualified suggestion from busy native samples acquired inside a successful run. The user can apply one recorded -1 step after the workload exits, with matching context, fresh scalar/CO readback and the current lower bound. Failed/inconclusive runs discard proposals and pause assistance. No unattended search was added.
- Persist original CO across restarts, block headless automatic apply while recovery is pending/unreadable, and provide explicit verified restore or keep-current/clear actions. Profiles are not automatically promoted. The recovery record is an interlock/original snapshot, not a full WHEA/crash-attribution journal.
- Keep native rendering active during managed work. Serialize exports outside the acquisition lock and UI thread. Collapse setup during a test and retain scrollable larger-font layouts.
- Added 25 driver-free cases: Release build passed with zero warnings/errors; **479 app tests**, **9 upstream-bot tests** and package success/failure checks passed. Coverage includes process descendants, cancellation, unrelated-process isolation, output quotas, version/score validation, zero reset, partial writes, changed context, startup exclusion and UI close. Synthetic setup/result views were inspected at normal/doubled fonts. PR [#22](https://github.com/AlexStapleton/SMUDebugTool/pull/22) records CI for the exact commit. No real benchmark or live hardware operation was executed.
- Runtime acceptance of the user's Cinebench console output, actual CPU load/affinity, native VID agreement and overhead remains pending. Full protocol and user flow: [managed workload guide](managed-co-workloads.md).

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

Build native session recording and comparison before automatic search. Manual workload markers remain available, and managed Cinebench 2024.1.0 runs now coordinate process lifetime, native recording and parsed scores. Capture per-core VID and clocks through the existing driver; there is no external CSV or shared-memory dependency. Package/thermal streams and repeated-run statistics follow later.

Expose one per-core table: physical core/CCD, current and candidate CO, readback outcome, active-sample VID if available, effective and reported clock, utilization/residency when available, test coverage and errors. Put measured rail voltage, package power and temperature in separate package/rail panels, not duplicated as alleged per-core physical voltages.

Allow one candidate or one explicitly selected batch to be applied while recording. Mark the transition, wait for a suitable comparable measurement window, and retain the previous run. A finite benchmark scored across a mid-run offset change is a mixed run and cannot serve as the score for either configuration. Collect clean scored runs after the candidate settles.

**Gate:** mapping must be validated before per-core conclusions or writes. Missing per-core VID disables VID-based guidance. No unattended offset changes in this release. Exported reports preserve units, context and missing-data evidence and cannot be imported to apply or resume tuning.

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

The user originally watched **Core VIDs** in HWiNFO64 at a requested 20 ms interval while running Cinebench 2024. The requested product now replaces that capture with native per-core reads and uses them for assisted negative CO trials. Do not substitute a package VID or measured rail voltage. The native encoding and identity contract are documented in `native-co-workbench.md`; comparison on the actual machine remains pending.

HWiNFO's separately named **CPU Core VID (Effective)**, where available, denotes the final requested core-rail VID; it is not a time-averaged per-core VID alternative and cannot replace the individual Core VIDs in a core comparison. Its absence does not block this workflow. Do not reconstruct it as the maximum or average of separately sampled core rows: source methods and sample times can differ. Keep **Core Effective Clocks** as a distinct frequency metric.

**Useful part:** a repeatable workload makes it easier to observe how curve changes affect requests, clocks, temperature and performance. Recording this in our program can remove substantial manual comparison work.

**Adjustment to the objective:** identify the sensor first. Per-core VID is a request, not an independently measured voltage delivered to that core. Shared rail behavior means lowering a non-dominant core's request may not lower supplied rail voltage. A high VID can also accompany a different frequency or operating condition. Identical VIDs are therefore not proof of efficiency, equal silicon requirements or stability. A lower voltage request can be accompanied by higher boost rather than lower measured voltage.

Retain a **VID spread** visualization as an optional diagnostic, not the success criterion. Show median, distribution, valid sample count and comparable activity/clock/temperature windows. Exclude sleeping, parked, stale or insufficiently loaded samples; do not replace them with zero. Use population summaries over a window rather than equality of individual 20 ms samples. Unknown sensor semantics disable matching-based suggestions.

For the 9950X3D, group by validated physical CCD and measured capability. Detect/cache-label differing CCD roles when supported; do not assume equal targets or a fixed cache CCD number. Start comparisons within a CCD and within similar operating windows, then evaluate the overall workload. The new reader uses physical APIC/SMT identities instead of the bundled getter's list-index calculation. It keeps the status register's eight-bit VID request encoding separate from SVI3 rail and split nine-bit P-state-definition encodings; independent implementations support that interpretation, but real firmware agreement remains to be tested.

Offer three user objectives:

| Objective | How the program evaluates a candidate | User payoff |
| --- | --- | --- |
| Performance | Repeatable score/throughput improvement with error-free tested coverage; effective clocks and limits explain the result | More useful work, not merely a higher reported clock |
| Efficiency | Equivalent performance within measured variation with lower measured package/rail energy for the same work | Less heat/power for similar output; CPU energy is not wall-system energy |
| Quiet/cool operation | Stay within user-selected power/temperature preferences with an acceptable measured performance tradeoff | A predictable thermal envelope and a documented cost in performance |

VID balancing can help generate a candidate in any mode; it cannot override a regression or failure. Do not chase identical requests across all sixteen cores.

### Recommended user journey

1. Open **Tools → Native CO workbench**, start monitoring and inspect native read evidence. In **Managed tests**, choose Cinebench 2024.1.0 and read load targets. The program validates physical/APIC/SMT identities; reviewed one-group affinity exposes individual core/thread/SMT targets. No external sensor import is needed.
2. Choose the goal and capture the current CO/CS/FMax/PBO context. Record BIOS/SMU version, power plan, workload version/settings, affinity and cooling context. An unreadable value is recorded as unknown, not silently assumed stock.
3. Choose **Baseline: set every core to CO 0** for a zero-CO reference, or explicitly keep current offsets. Run the baseline; original CO is preserved before reset and every reset must verify before launch. Use consistent warm-up and repeat runs to estimate ordinary variation.
4. Inspect per-core/CCD summaries. The successful managed run can retain a qualified same-CCD native suggestion; choose **Apply recorded -1 CO trial** to use it after the workload exits. The live trial button remains for independently generated steady load. A higher-VID core is not defective.
5. The action re-reads current CO, applies one negative step and verifies it through the existing result path. Keep other tuning dimensions fixed. A write during a scored recording marks that recording mixed; end it and collect a clean candidate run. A confirmed setting still has validation pending.
6. Choose **Run candidate** with the same executable, target and duration. Compare native VID/effective clocks and parsed score. Rail, energy, temperatures, repeated-run statistics and richer coverage remain future integration; unavailable metrics are not claimed measured. Failure, timeout, missing/ambiguous score and changed context pause assistance.
7. Validate beyond the all-core render: selected core single-thread tests, SMT-sibling/paired tests, heavier and lighter workloads, transitions, idle and the user's real applications. Where practical, use computational correctness tests with explicit pass/fail output. A Cinebench completion is performance evidence for that workload, not comprehensive error checking.
8. Keep, discard or continue. Save a named experimental result with its test coverage. Only explicitly promote a chosen validated profile to startup. Interrupted/failed experiments cannot become the startup candidate automatically.

Example presentation, without invented hardware results: “Core 4: CO readback confirmed; Cinebench comparison available; single-core validation pending.” After tests: “Selected tests passed for the recorded durations.” Avoid “CPU is stable” as an absolute claim.

## 4. Capture, performance and integration design

### Sampling model

Separate acquisition, analysis and rendering. Each value has a sensor ID, source, unit, physical scope, source/sample timestamp, quality and freshness. A capture frame records start/end and per-source skew; sequential core reads are not a simultaneous hardware snapshot. Re-reading the same shared-memory update does not create a new independent measurement.

Proposed starting policies, to be measured rather than advertised as achieved:

| Work | Initial policy |
| --- | --- |
| Native VID/reported/effective-clock metrics | Implemented: 100 ms default; optional requested 20 ms, achieved cadence shown; hardware overhead pending |
| PM-table/thermal metrics | Source-specific measured rate; initially 250-1000 ms, with freshness shown |
| Slow SPD/SMBus/board reads | On demand or 1-2 s when validated; never blanket 20 ms polling |
| UI table/chart refresh | Approximately 5 Hz; acquisition remains independent |
| History | Implemented: 5 s/16,000 readings; online summaries for at most 100 runs; report capped at 8 MiB. Longer streamed capture remains future work. |

Use a persistent worker and monotonic clock, not a new task for every sensor tick. Skip missed deadlines and busy hardware windows; never queue a catch-up burst. Hardware transactions remain serialized through the existing lifetime/lock contract. Tuning writes take precedence over optional monitoring; scans and dumps are paused during a controlled experiment. Cancellation is checked between operations; no disposal until active I/O finishes.

Keep slow I/O outside fast batches. One source's delay must not cause stale sensor values to look fresh. Display requested versus achieved sampling rate and dropped/missed samples. Do not promise 50 independent hardware measurements per second just because the timer is set to 20 ms. Counter-based effective-clock readers require family-supported semantics, wrap/reset handling, pinned validated targets and tests; ratio x BCLK remains a reported clock, not an effective clock substitute.

Integrate power over actual elapsed timestamps only when source quality and coverage support it; identify missing intervals and the measured domain. Do not treat requested PPT as consumed power, multiply a benchmark score by a power sample and call it efficiency, or present CPU/package energy as whole-system wall energy. If power coverage is inadequate, retain performance/temperature comparisons and mark energy unavailable.

Benchmark capture overhead with capture off, normal capture and fast capture under otherwise identical repeated runs. Initial engineering targets: UI input acknowledgments within 100 ms under synthetic I/O delay, no overlapping native I/O, bounded memory, and capture-induced score changes within the established measurement noise. If fast mode misses its budget or shifts results, report it and offer a slower mode. These are release criteria, not present measurements.

### External applications

Managed Cinebench 2024.1.0 now launches a selected local executable, records its version/hash/target/duration, captures bounded output and owns its job tree. No 2026-only SMT command is used; core/SMT restrictions use reviewed Windows affinity. Missing or unrecognized results are inconclusive. Actual 2024 console behavior remains a runtime acceptance gate. Never terminate unrelated benchmark instances or compare different versions/recipes as equivalent.

HWiNFO is not an implementation dependency or a planned import requirement. A user may independently compare its display during hardware acceptance, but native capture and assisted writes must function without it. If the native source becomes unavailable, suspend dependent assistance and show the reason; never substitute cached values as new samples.

### Errors, recovery and startup

Journal the baseline, candidate, verified write outcomes, source context and phase to disk before workload/candidate advancement. Subscribe to new relevant WHEA records from the session boundary. Preserve attribution confidence: an APIC ID is not automatically the UI core index, and a WHEA event can originate outside CO. Deduplicate events and distinguish historical, corrected, fatal and unrelated reports. A process crash, timeout or unexpected reboot can indicate an interrupted/failed experiment without proving which core caused it.

Normal stop ends owned workloads and stops new writes promptly; it may offer or perform the session-authorized restore transaction when the driver remains responsive. Report partial restoration. After a system crash, next launch opens recovery without silently replaying or continuing the experiment. To make that effective, GUI and `--applyprofile` must share a persistent experiment interlock: a pending/interrupted trial blocks automatic experimental reapplication. If the current selected startup profile has not been independently validated, automated search must not arm until the user chooses a recovery/startup policy. An existing stable startup profile may remain eligible only when explicitly identified and not overwritten by experiments.

Implemented original-CO recovery records are machine/topology-bound and written atomically. Pending/unreadable records block automatic startup apply before CPU initialization; a file lease excludes concurrent automatic apply and experiment snapshot creation. Restore is explicit and retains the record on partial failure. Keep-current clears the interlock without saving a profile. Full phase journals, workload-error attribution and automatic-search recovery remain future work. The program cannot guarantee recovery from hardware hangs, firmware persistence or independently applied BIOS configuration.

## 5. Architecture and verification plan

Suggested new components:

- `CapabilityService`: platform metadata and checked capability evidence.
- Native provider/coordinator: implemented `NativeVidReader` and `NativeVidMonitor`; immutable source-tagged readings. Extend to package/thermal streams later.
- `CoreIdentityMap`: physical SMU core, Windows group/logical processor, SMT siblings and external sensor identities, with validation status.
- `ExperimentSession` / journal: baseline, configuration generations, trials, segments and recovery interlock.
- `WorkloadRecipe` / `OwnedWorkloadProcess` / `ManagedCoRun`: managed Cinebench 2024.1.0 and manual markers delivered; correctness-test adapters remain future work.
- `ComparisonAnalyzer`: valid-window filtering, repeated-run variation, score/energy comparison and explicit inconclusive outcomes.
- `CoCandidatePlanner`: bounded proposals/search; independent from telemetry rendering and existing tuning transport.
- `ValidationRecorder`: correctness-test output, Windows error evidence and coverage; no absolute stability certificate.

Reuse `TuningService`, validated topology, detached results, `HardwareOperationRunner`, `HardwareLifetime` and current package checks. Keep the source pin unchanged until a separate dependency review is justified. Extend profile schema only when a new field actually ships.

Automated verification uses fake transports/providers/workloads; it must not construct a live CPU or write real settings. Cover signed offsets, sparse/disabled cores, reordered sensor rows, processor groups, SMT, mixed CCD roles, source skew, duplicates, stale/missing values, counter resets, slow I/O, cancellation, partial writes/restores, external edits, event attribution, experiment crashes and startup interlock. Test statistical comparison against known synthetic noise, not merely the implementation's chosen formula. Include long-running bounded-memory and UI responsiveness checks.

Hardware acceptance is staged: read-only captures and mapping checks first; then user-initiated known CO changes; then manual baseline/candidate comparison; then validation adapters; finally optional bounded search. Independently displayed monitoring tools can help cross-check equivalent windows, preserving source differences. Measure actual driver serialization and scheduling overhead before relying on fast capture. No claimed success on one BIOS establishes support for every Zen 5/X3D system.

## 6. Recommended delivery decisions

1. Build Stage 0 and Stage 1 as the first release: capability report, scalar verification, thermal ceiling and a compact read-only overview. This gives useful improvements before the larger workbench.
2. Deliver the native Stage 3 recorder and explicit one-step Stage 4 assistance for this user. This is implemented in PR #22; finish hardware acceptance before expanding into automatic search. Native per-core capture is the product path, with no CSV requirement.
3. Ship memory reads/editor work as an independent track so it does not block CO work. It targets a different user task and has different activation semantics.
4. Add assisted proposals and broader validation after recorded comparisons are trustworthy. Automatic search is the last CO milestone, not the first demo.
5. Add manual-OC/GPU refinements and mobile/APU support independently, with their own hardware acceptance evidence. Keep live VDDG/DRAM voltage setters and unknown HSMP/WMI formats in research until proven.

## 7. Plan review

Reviewed against all ten desktop gaps in the capability audit plus mobile/APU expansion, extra WMI methods, HSMP investigation and profile limitations. Each has a roadmap entry. No research item is represented as confirmed hardware support.

The design keeps distinct: requested VID versus measured rail voltage; physical core versus Windows logical index; register confirmation versus test outcome; fused/setup limits versus active limits; timer cadence versus source update rate; Cinebench score versus computational correctness; a passed workload versus universal stability. Sampling and startup recovery are dependencies of automation rather than optional cleanup work.

Resolved user preference: native per-core VID capture and tuning assistance; no CSV import. Implemented source contracts: restricted desktop status-VID decoder, CPUID/APIC/SMT bindings and APERF elapsed-counter effective clocks. Remaining gates: real-machine source/mapping agreement and overhead; PM-table layout and active-limit reads on the user's BIOS; repeated-run comparison; Cinebench 2024 automation behavior; workload/error formats and recovery interlock. Memory views and other independent roadmap areas can proceed without those future automation gates.

## Primary sources and local evidence

- [Bundled CPU APIs](https://github.com/irusanov/ZenStates-Core/blob/3bb1dad5b6cccc14f063d6eacbcf891a309944f3/Cpu.cs), including the legacy-width status VID field and the current logical-index calculation. A public getter name alone is not a validated Zen 5 sensor contract.
- [Zen 4 command map](https://github.com/irusanov/ZenStates-Core/blob/3bb1dad5b6cccc14f063d6eacbcf891a309944f3/Hardware/Smu/Settings/Zen4Settings.cs) and [Zen 5 inheritance](https://github.com/irusanov/ZenStates-Core/blob/3bb1dad5b6cccc14f063d6eacbcf891a309944f3/Hardware/Smu/Settings/Zen5Settings.cs).
- [Power-table telemetry](https://github.com/irusanov/ZenStates-Core/blob/3bb1dad5b6cccc14f063d6eacbcf891a309944f3/Hardware/PowerTable.cs) and [memory readers](https://github.com/irusanov/ZenStates-Core/blob/3bb1dad5b6cccc14f063d6eacbcf891a309944f3/Hardware/DRAM/MemoryConfig.cs).
- [AMD Ryzen Master 3.1.2 CPU guide, 2 November 2026](https://docs.amd.com/r/en-US/68886-ryzen-master-user-guide/CPU): curve tuning, operating modes and test-based optimization. The guide describes Ryzen Master, not an assurance that our interfaces expose the same functions.
- [AMD Curve Optimizer FAQ](https://www.amd.com/content/dam/amd/en/documents/products/software-tools/faq-curve-optimizer.pdf): derived offsets depend on the test and duration; users should validate their workloads. Its historical support list is not used as the current platform matrix.
- [HWiNFO author on VID versus supplied voltage](https://www.hwinfo.com/forum/threads/can-i-view-actual-cpu-core-voltages-not-vids.8586/): supports the measurement distinction, not a model-specific complete rail topology for every Ryzen CPU.
- [HWiNFO author on final effective VID](https://www.hwinfo.com/forum/threads/hwinfo-v6-40-released.6894/) and [different VID sampling methods](https://www.hwinfo.com/forum/threads/cpu-core-vid-effective-vs-core-vids.7749/): the final request is distinct from individual core readings and supplied voltage.
- [HWiNFO author on effective clocks](https://www.hwinfo.com/forum/threads/effective-clock-vs-instant-discrete-clock.5958/): effective clock includes behavior across the measurement interval, including sleep.
- [Native workbench source contract](native-co-workbench.md): pinned independent references for status-VID, frequency and APERF interpretation. No HWiNFO API or SDK is used.
- [Maxon Cinebench technical information](https://www.maxon.net/en/tech-info-cinebench): workload/environment repeatability and version-specific automation. The currently published page documents Cinebench 2026; the user uses 2024, which needs its own adapter validation.
- [AMD-authored WMI interface proposal](https://lore-kernel.gnuweeb.org/platform-driver-x86/20261010190543.563161-4-Shyam-sundar.S-k@amd.com/): firmware metadata, extra methods and setup-versus-active limitations. This is a proposed Linux driver/interface document, not proof of uniform Windows BIOS behavior.
- App baseline inspected locally: `Utils/ZenStatesTuningTransport.cs`, `Utils/TuningService.cs`, `Utils/TopologySnapshot.cs`, `Utils/Hardware.cs`, `Utils/HardwareLifetime.cs`, `Utils/HardwareOperationRunner.cs`, `Utils/SmuDecodeAdapter.cs`, `Utils/AcpiCommandHelp.cs`, `Profiles/Profile.cs`, `Program.cs`, `Utils/StartupTaskService.cs` and monitor forms.


