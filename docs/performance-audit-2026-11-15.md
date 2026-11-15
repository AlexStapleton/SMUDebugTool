# Application performance audit — 15 November 2026

The largest verified recurring cost was the optimizer's presentation layer. Rebinding its complete grid every 200 ms forced repeated row creation, measurement, binding and repaint work. Repeated window sorting, history copying and synchronous recovery-file probes added avoidable overhead. These costs can interfere with the workload being observed, even though hardware acquisition itself runs on a worker.

This pass implements the verified software improvements and records hardware-dependent follow-up separately. It builds on the guided optimizer in PR #23 and startup improvements in PR #24. No live CPU initialization, tuning setter, real Cinebench run, driver maintenance or startup-task mutation was executed during this audit.

## Measured results

Baseline: `6ad5bbabff8854ca96680edb5c48c01095ce276d`, including the startup improvements. Production binaries are Release/x64 on .NET Framework 4.8. The standalone fixture uses synthetic native readings, a fixed logical clock and hidden WinForms controls; driver/workload factories cannot initialize hardware or launch a benchmark.

CPU/UI numbers below are medians of three independent, sequential process runs per build. Each case warms up eight times. The UI cases measure 160 updates, eligibility 500, and frame recording/window reads 1,000. Export averages five measured writes after eight warm-ups, in one process per build. Measurements include fixture/reflection/managed/UI overhead and are not whole-program CPU usage, resident memory or hardware latency.

| Synthetic operation | Before | After | Interpretation |
| --- | ---: | ---: | --- |
| Optimizer refresh, 16 cores, unchanged frame | 142.23 ms | 0.47 ms | Stable rows and changed-cell updates remove repeated binding/layout work. |
| Optimizer refresh, 64 cores, unchanged frame | 274.06 ms | 1.16 ms | The display no longer scales through a full grid rebuild each tick. |
| Optimizer refresh, 16 cores, changing frame | 113.84 ms | 6.89 ms | Includes rebuilding synthetic input in both versions; not a pure rendering measurement. |
| Eligibility, 16 cores, five-second 20 ms window | 0.57 ms | 0.30 ms | One-pass aggregation replaces per-core sorting and repeated enumeration. |
| Eligibility, 64 cores, five-second 20 ms window | 2.74 ms | 1.28 ms | Same qualification thresholds and peer-median semantics. |
| Record a preconstructed 64-core frame | 0.019 ms | 0.008 ms | Dictionary lookup replaces repeated linear core searches. |
| Export 100 runs × 64 cores with 64 KiB transcripts | 93.63 ms | 23.44 ms | Stream detached snapshots once instead of serializing, parsing and serializing again. |

Allocation is approximate cumulative managed allocation per operation, not live/peak memory:

| Operation | Before | After |
| --- | ---: | ---: |
| 16-core unchanged optimizer refresh | 787 KiB | 23 KiB |
| 64-core unchanged optimizer refresh | 2,590 KiB | 75 KiB |
| 16-core eligibility | 281 KiB | 7 KiB |
| 64-core eligibility | 1,120 KiB | 25 KiB |
| Re-read unchanged 64-core telemetry window | 125 KiB | Approximately zero, amortized |
| Read history count at 100 runs/64 cores | 762 KiB | Approximately zero |
| Large report export | 118.4 MB | 6.7 MB |

The original UI timings varied substantially: 16-core steady updates ranged from **59.8–148.8 ms** across process runs; the optimized runs ranged from **0.450–0.465 ms**. The 64-core ranges were **129.7–286.6 ms** and **1.052–1.161 ms**. Do not infer a precise confidence interval or a universal speedup factor. The source fixture and [raw medians/ranges with binary hashes](performance-measurements-2026-11-15.json) are retained. Window-cache-hit timings are near the fixture's resolution and should be interpreted through eliminated copies, not nanosecond claims.

The UI fixture uses a three-second, 20 ms synthetic window; the guided product normally requests 100 ms acquisition and 200 ms rendering. The eligibility stress fixture uses five seconds at 20 ms. These deliberately exercise denser windows than the normal guided setup. Sampling frequency and qualification thresholds were not reduced to obtain the improvements.

## Audit coverage and changes

| Path | Finding and disposition |
| --- | --- |
| Startup / initial discovery | PR #24 already skips unused DIMM/SPD/AOD/SuperIO inventory, defers optional tabs, narrows driver probing and moves startup-task reads off the UI thread. Preserve those changes; real latency still needs measurement with Tools → Startup timings. |
| Guided optimizer UI | **Fixed:** retain rows by physical-core key, batch initial population and update only changed cells. Size displayed rows rather than all rows. Preserve selection/scroll. Do not reset unchanged report text. Skip presentation work while minimized; acquisition, phase control, temperature/WHEA checks and owned workloads continue. |
| Recovery status in the timer | **Fixed:** display-only background probe, throttled to one second, with conservative error handling. Rendering performs no recovery-file I/O. Explicit start/keep/restore and automatic-apply interlocks retain their real recovery checks; a display cache never approves a hardware action. Initial session recovery discovery remains authoritative. |
| Native telemetry snapshots | **Fixed:** freeze one incoming frame for publication/recording and share read-only window snapshots until content changes. Retained snapshots cannot be modified by the producer or a caller. Freshness is still evaluated against current time; cached samples do not become fresh again. Acquisition still skips a busy driver rather than queuing work. |
| Eligibility analysis | **Fixed:** one pass records timestamp extrema, means, activity/invalidity and clock spread. Peer grouping is by CCD. Preserve shuffled-input handling, last equal-timestamp semantics, true median, clock/activity/freshness requirements, bounds and incomplete-mapping exclusion. Differential tests compare 160 varied windows with the frozen previous algorithm. |
| Recording / history | **Fixed:** indexed accumulation for active core summaries, constant-time trial count and active-change queries. Full detached history copies remain available when actually needed for reports/UI selection; metadata checks no longer clone every run. Statistical definitions are unchanged. |
| PCI range monitor | **Fixed:** at most 64 reads per hardware-lock batch, yield between batches and discard an incomplete pass if the driver is busy. Unchanged frames emit no binding reset; small changes update individual items. Large changes use one reset and restore highlights. Avoid all-row address-column sizing. |
| Foreground PCI/MSR/CPUID scans | **Fixed:** release the hardware lock between independent register/leaf reads, keep formatting/decoding outside that lock and yield periodically. Shutdown is checked before each native access. A diagnostic scan is a series of observations, not an atomic hardware snapshot. |
| Power-table monitor | **Fixed:** reuse label metadata until table version/length changes. Format and invalidate only changed rows; retain maxima. Rebuild the model when layout identity changes. Actual firmware layout/monitor behavior needs hardware acceptance. |
| Reports / exports | **Fixed:** capture detached records/journal data, serialize once to a bounded UTF-8 stream off the UI thread and atomically replace the destination. The 16 MiB guided-report limit is enforced while writing. Oversize/serialization failure leaves an existing report intact and cleans the temporary file. Schema versions and evidence are preserved. |
| Tuning / safety journal | **Retained deliberately:** fresh scalar/CO/context/readback checks, serialized SMU transactions and persisted trial intent before every setter. Do not batch, cache or defer these to make tuning appear faster. Journal writes stay atomic and awaited. |
| General logging | The main diagnostic text is already capped at 100,000 characters and action/run histories are bounded. Prepending remains O(cap), but it is action-driven rather than the recurring UI hot path. Further changes need measured evidence. |
| Workload process / output | Already uses owned jobs, bounded transcripts/lines, dedicated process/output workers and cancellation/health checks. Preserve output/error parsing and stop behavior. No real-workload throughput measurement was made. |
| Lifetime / shutdown | Keep the shared driver shutdown barrier, skipped busy reads and cancellation between native calls. No thread aborts, overlapping mailbox transactions or cancellation through an in-flight driver call were introduced. |

## Follow-up priorities

1. **Measure observer overhead on the actual machine.** Check startup milestones separately, achieved native cadence, process CPU/allocation behavior and UI interaction during a sustained workload. Confirm native mapping, VID, temperature and rail availability. Software-only fixtures cannot establish driver latency or a Cinebench improvement.
2. **Review the high-frequency SMU monitor.** Its 15 ms timer still schedules per-poll work through the single driver. Consider a shared/coordinated acquisition service only after measuring contention. Keep capture/logging semantics when minimized; silently reducing or pausing observation could hide events.
3. **Improve large diagnostic result presentation and saving.** ResultForm can still assign/save a large completed scan synchronously, and legacy probing can marshal frequent progress/log updates. A bounded visible preview with complete asynchronous export and coalesced progress is a separate, reviewable improvement.
4. **Keep storage durability ahead of throughput.** If real traces show journal latency matters, evaluate a versioned append/recovery format. Do not remove the intent-before-write boundary or silently accept a failed persistence operation.
5. **Retire unused legacy UI carefully.** The old manual workbench remains compiled and retains its older rebinding renderer, but the Tools menu uses the guided form. Removing/migrating it is maintenance work; it is not an ongoing cost on the normal guided path.

## Validation and reproducibility

Release build: zero warnings/errors. **530 hardware-free app tests**, **nine upstream-bot tests** and package success/failure checks pass. Added coverage includes immutable/cached snapshots, time-based staleness despite caching, differential eligibility, stable row/selection/scroll retention, display-probe coalescing/close/error handling, cooperative scan cancellation/contention and detached/atomic UTF-8 export limits. Synthetic normal/doubled-font views were inspected. This does not establish monitor-DPI or live firmware behavior.

An existing P-state layout assertion failed once during the initial parallel suite. It passed in isolation and subsequent complete suites. Its production layout code was not modified in this pass; exact-commit CI is recorded with the PR.

To reproduce on Windows x64, build the solution in Release and place each comparison build plus its dependencies in separate directories. The script compiles the fixture with the installed Framework C# compiler and invokes production code without running Program.Main:

```powershell
./scripts/Measure-Performance.ps1 -BuildDirectory C:\path\to\baseline -Output before.json
./scripts/Measure-Performance.ps1 -BuildDirectory C:\path\to\candidate -Output after.json
# Repeat into before-2/3.json and after-2/3.json, sequentially, with no competing test/build runs.
./scripts/Measure-Performance.ps1 -BuildDirectory C:\path\to\baseline -Output export-before.json -ExportOnly
./scripts/Measure-Performance.ps1 -BuildDirectory C:\path\to\candidate -Output export-after.json -ExportOnly
python Tests/Performance/compare.py --before before.json before-2.json before-3.json --after after.json after-2.json after-3.json --export-before export-before.json --export-after export-after.json --output comparison.json
```

No hard timing threshold is added to CI: functional regression tests gate behavior, while wall-clock probes retain their variance for review. Hardware acceptance, actual process utilization and long-session stability remain pending.
