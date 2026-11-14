# Native Curve Optimizer workbench

Open **Tools → Native CO workbench**. This feature reads per-core VID through the
program's existing MSR backend, records manual benchmark runs and offers a bounded
negative CO trial. It does not require HWiNFO, CSV files, another driver or another
monitoring application. Hardware acceptance is pending; automated verification uses
fake transports and synthetic data only.

## Use

1. Start monitoring. Default acquisition is 100 ms; 20 ms is an optional request.
   Check achieved batch interval, read-pass duration and sample age. **Show native
   read evidence** exposes the physical/APIC/Windows bindings and raw status words.
2. Warm up consistently, start a baseline, then run the same benchmark recipe.
   Enter its score if wanted and end the run. Launch Cinebench independently;
   the program does not discover or score an existing benchmark process.
3. Under steady load, set your own CO lower bound and press **Trial next eligible
   core (-1 CO)**. The -40 default is an editable software bound, not a recommended
   stable value. A qualifying core gets at most one negative step per click.
   The result names the core and shows readback confirmation or failure.
4. End any recording spanning that change: its context is mixed. Collect a separate
   candidate run using the same workload and other settings. Select it in the run
   table to see score and native per-core comparisons with the preceding baseline.
   Repeat runs to establish ordinary variation; a single score is descriptive.
5. Validate the chosen offsets with core-focused, light-load, transition and real
   application tests. Lower/equal VID does not prove better performance or stability.
   Save the JSON session report before explicitly clearing runs for a new session.

Closing stops capture and marks an active run interrupted. It leaves explicitly
applied offsets in place, like the existing tuning controls. Reports cannot be
imported to apply/resume tuning. No experiment is automatically saved to a startup
profile. Automatic search, workload control, WHEA correlation and recovery journals
are not implemented in this milestone.

## Source and identity contract

The reader allows only Raphael family 19h/model 61h and Granite Ridge family
1Ah/model 44h with the existing current-processor MSR bridge available. Other CPUs
remain unavailable until reviewed. Each Windows group/logical target is pinned to
read CPUID 8000001Eh. APIC IDs, CoreId and one/two-way SMT identities must form a
complete, unique map matching the existing physical SMU slots. The current restricted
contract requires CoreId = APIC / threads-per-core; unexpected, compressed or missing
identities fail closed. The lowest APIC sibling supplies one reading per physical
core; Windows enumeration position is never used as the CO target. CCD IDs come from
validated topology, without guessing which CCD carries extra cache.

| Read | Interpretation |
| --- | --- |
| MSR C0010293h, bits 21:14 | Eight-bit status VID request: 1.55 - code × 0.00625 V. This is neither measured voltage nor the SVI3 rail encoding. |
| Same register, Zen 5 bits 11:0 | Reported frequency = field × 5 MHz. |
| Same register, Zen 4 fields | Reported frequency = FID × 200 / DID MHz; zero DID is invalid. |
| APERF read-only MSR C00000E8h | Delta cycles / monotonic elapsed seconds / 1,000,000 = effective MHz. |

Zero/all-ones status, unsupported/implausible encoding, read failure and missing
counter intervals cannot become measured zero. First samples, APERF resets,
nonpositive intervals, gaps over two seconds or implausible effective clocks have
no effective-clock value. Invalid reads reset that core's counter baseline. An
affinity/driver exception invalidates the current reading and stops the reader;
affinity is restored on success, failure and cancellation. Raw status is retained
for diagnostics. Core reads are sequential, with per-core timestamps, not a
simultaneous package snapshot.

This interpretation was independently reviewed against pinned primary project
implementations, not established as an AMD guarantee for every BIOS:

- [LibreHardwareMonitor Amd17Cpu.cs, 9e2d677](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/blob/9e2d677cefff5c614ecc38da88fbc447eb478e2a/LibreHardwareMonitorLib/Hardware/Cpu/Amd17Cpu.cs): hardware status fields, Zen 5 clock, legacy status VID conversion and APERF elapsed-counter reading.
- [CoreFreq AMD register definitions, 20de51c](https://github.com/cyring/CoreFreq/blob/20de51cbff0c028cb3ad52a0c61c68355e417d64/x86_64/amd_reg.h): hardware-status VID and the distinct split nine-bit P-state-definition VID.
- [CoreFreq kernel reader, 20de51c](https://github.com/cyring/CoreFreq/blob/20de51cbff0c028cb3ad52a0c61c68355e417d64/x86_64/corefreqk.c): per-processor hardware-status acquisition.
- Bundled ZenStates-Core is pinned at `3bb1dad5b6cccc14f063d6eacbcf891a309944f3`.
  This implementation uses the reviewed current-processor bridge instead of the
  library getter's logical-list-index calculation. No code from those independent
  projects was copied into this feature.

## Assistance and comparison boundaries

The planner requires at least 20 valid samples spanning two seconds inside its
five-second window; the latest sample must be at most 500 ms old. Every included
sample must have a valid VID, reported clock and effective clock at least 80% of
reported clock. This is a **busy clock heuristic**, not measured utilization or a
stability test. Reported-clock spread must stay within 3%. At least two eligible
peers must be in the same CCD within 3% reported frequency. A requested VID at least
12.5 mV above the peer median qualifies for one decrement, provided current CO is
nonpositive and the new value respects the user's lower bound.

Before applying, the program requires complete fresh native readings and direct
CO context. Inside one serialized transaction it checks a usable direct scalar
readback and re-reads the target CO, refusing if it changed. Scalar is mode evidence,
not proof that all firmware state is known. The existing tuning service sends at
most one CO setter and reads back the outcome. No retry, fallback target, automatic
loop or other tuning dimension is added. Sampling history is invalidated around app
write attempts, including rejected attempts; in-flight old frames are discarded.

Run summaries retain valid/requested VID means, busy-sample counts and effective
clock means. They are sample averages, not energy integrals. Start/end snapshots
separate actual CO reads from editor requests. Non-CO editor/workload/topology
differences or mixed/incomplete endpoint evidence suppress score-delta attribution.
Unreadable settings and unobserved external changes are not asserted unchanged.
An identical CO endpoint does not prove no external change happened mid-run.

## Responsiveness and acceptance

One persistent worker performs serialized, cancellable native batches through the
existing hardware lifetime. Busy windows are skipped; missed deadlines never form
a catch-up queue. Driver operations stay off the UI thread. Rendering runs at
200 ms, independently of acquisition. Memory is bounded by a five-second/16,000-row
window and online summaries for at most 100 runs. JSON export is capped at 8 MiB and
replaces the destination only after writing a temporary file successfully.

Automated cases cover decoding, reordered processor groups, missing/ambiguous SMT,
counter resets/gaps, affinity restoration, cancellation, in-flight invalidation,
stale/unloaded/unstable windows, one-write verification, changed preconditions and
mixed/comparison context. The fixture never constructs a live Cpu.

Hardware acceptance order:

1. Read-only: confirm CPU/BIOS identity, complete mapping, plausible native readings
   under idle/load and unavailable handling. Independently displayed tools can help
   compare equivalent windows; exact sample equality is not expected.
2. Measure capture off/100 ms/20 ms under repeated identical workloads. Record actual
   cadence, UI response and score variation; slow the request if overhead matters.
3. Try one user-authorized step, confirm the physical target and CO readback, then
   collect separate baseline/candidate runs and broader validation.

No automated test or synthetic preview establishes the user's 9950X3D firmware
behavior, sustained 20 ms overhead, actual monitor DPI behavior or stable offsets.
