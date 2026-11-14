# Managed CO workload milestone

User flow: select a local Cinebench 2024 executable, select all processors or a
validated physical core (one thread / both SMT siblings), choose test duration and
timeout, then run a baseline or candidate. A baseline explicitly chooses CO 0 or
current offsets. Original physical-core CO readback is saved before any reset;
all requested writes must verify before a workload starts. Candidate runs keep CO
fixed. Assisted steps remain explicit user actions between scored runs.

The app launches its own Cinebench process suspended, assigns a kill-on-close
Windows job and any validated affinity restriction, then resumes. Redirected
output is bounded and drained independently. Stop/timeout/close kills only that
job. It never attaches to or kills an existing benchmark. No benchmark is bundled,
downloaded or run during development. No live tuning is used for verification.

Choose **All processors** for the VID-balancing baseline. After a successful run,
the last qualified proposal from samples acquired inside that run is retained for
at most a 30-second gap at finalization. It is displayed for explicit review/apply;
there is no unattended search. Idle CPU state after the process exits does not
erase that recorded evidence. Fresh complete CO/editor context must still match
before the transaction; any app hardware-write attempt clears pending suggestions.
Changing target/duration/version makes a different recipe and suppresses direct
score comparison. Core-focused tests use CPU1 or CPUX plus a job mask for a validated
Windows group-0 physical core/sibling set, never a guessed logical index. Other
processor-group arrangements retain all-processor tests only.

Native VID capture stays independent of process supervision. UI rendering continues
during tests. Essential stale/missing capture, process errors, cancellation,
timeout, ambiguous/missing score or changed context block further assisted writes
until a successful managed test or explicit recovery. Completion means benchmark
completion, not a computational-correctness or stability certificate. Core-focused
Cinebench runs are rendering coverage, not a substitute for a later correctness
test adapter/WHEA milestone.

Validation: Release build with zero warnings/errors; 479 app tests (25 new cases),
9 upstream-bot tests and package success/failure checks passed. Synthetic setup and
collapsed result views were inspected at normal/doubled fonts. These checks do not
establish real Cinebench console behavior, load/affinity on the user's machine or
firmware tuning correctness.

Recovery is manual: persist original CO plus hardware/topology identity atomically;
block automatic startup apply while pending/corrupt recovery exists. Restore only
after matching fresh hardware/topology/CO evidence, verify each result, and retain
the record on partial failure. An explicit keep-current action clears recovery
without saving/promoting a profile. No automatic restore or search after a crash.
Original readback is preserved once across subsequent steps/resets until recovery
is explicitly cleared. The headless startup path holds the same file lease while
applying a profile, so an experiment cannot race past its interlock check. Old
program versions do not understand this new interlock.

Acquisition, process supervision, output draining and rendering are separate. A
single process supervisor checks cancellation/health every 50 ms. Output retains
64 KiB, rejects over 4 MiB of text and parses chunked CPU-result lines; duplicate,
missing or unsupported formats are inconclusive. Export serialization runs outside
the native session lock and the UI thread. No guessed benchmark score, automatic
startup promotion, thermal-protection override or background offset loop is added.

Sources: [Maxon command-line interface](https://www.maxon.net/en/tech-info-cinebench)
documents CPU1/CPUX flags, minimum duration and console results. Its current page
describes 2026; this adapter is restricted to the user's locally inspected
2024.1.0 executable metadata and retains runtime acceptance as a gate. It does not
use the newer CPU-SMT flag. Missing/unrecognized output is inconclusive.
[Windows jobs](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects)
provide owned-tree lifetime and affinity limits;
[CreateProcessW](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessw)
supports suspended creation before assignment. Automated acceptance uses only fake
transports and a tiny fixture executable, including cancellation/descendant tests.
