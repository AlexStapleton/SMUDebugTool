# Remaining updates: implementation and validation

Implemented on `feature/remaining-updates`, based on v1.47.5. Includes the optional
P-state VID editor and English/Simplified Chinese localization requested in this chat.

## Build and dependency choices

The application now uses an SDK-style net48 project with explicit item lists, existing
resource-runtime dependencies and output folder layout. The unmodified ZenStates-Core
submodule is pinned at `3bb1dad5b6cccc14f063d6eacbcf891a309944f3`. A sibling net48 project
compiles its NET20 compatibility branches, four embedded PawnIO modules and fixed version
metadata. It avoids upstream's missing native-copy inputs and Git-depth-dependent version.
Preparation verifies the pin and clean tracked core sources before generating an
intermediate partial Cpu declaration. The small partial-class addition exposes
current-processor MSR operations on the same driver instance. The application no longer
reflects into `_pawnAmd` or uses a broadcast write for a per-processor P-state update.

The existing InpOut x64 binary is retained and hash-locked. Source core, InpOut and PawnIO
licenses are packaged. No additional native driver is introduced. The old unused core DLL
is removed from Prebuilt. Upstream source remains separately attributable and unchanged.

## Targeting and profiles

CoreLibraryAdapter produces immutable snapshots from APOB, or a validated contiguous-CCD
legacy fuse map. Counts, coordinate ranges, logical indices, physical identities and masks
are checked. Ambiguous layouts, unknown families, per-core APUs and unvalidated multi-instance
maps cannot acquire writable per-core targets. UI CO/manual-OC paths and headless profiles
use the same physical-slot/mask contract. Windows processor-group affinity remains separate.
Downcore checkbox positions use each CCD's logical order and those controls refuse layouts
outside their supported one/two-CCD contract.

Schema 1 CO dictionary keys keep their physical-slot interpretation. Newly saved profiles
add SchemaVersion 2, a topology signature and physical CoreBindings. A mixed-feature profile
fails before every write if its binding preflight fails. No file is rewritten on load.
An explicit review dialog can convert only when every requested target has an unambiguous
physical match. It changes controls only; Apply and Save remain separate. Unrequested CO
controls remain disabled after profile load until an explicit hardware Refresh. Invalid
numeric input is rejected before loading rather than clamped into a different setting.

Direct CO/FMax packets retain the established routing and six-argument contract. The newer
core's derived CO convenience getter is not used for verification. Reports retain raw
statuses, target masks and physical identities; acceptance cannot silently become verification.

## P-state VID

The VID editor is separate from the frequency writer. Known SVI2 uses bits 21:14; known
SVI3 adds bit 32. Unknown encodings/layouts and disabled/unreadable states are not writable.
Both paths use the shared checked transaction engine: fresh per-target read, enabled-state
check, masked update, checked affinity, direct readback, and partial-progress reporting.
VID writes preserve frequency, IDD, enabled and reserved bits. The displayed voltage is
derived from the requested VID and is labeled accordingly. Opening the editor performs
no write; an explicit confirmation precedes a VID operation. No VDDG control is added.

## Driver and UI lifecycle

Startup uses a worker/progress window and a bounded machine-wide maintenance gate. GUI and
headless processes hold per-process participation markers until after Cpu disposal. The
shutdown barrier still cancels pending work before waiting for current I/O.

Diagnostics read service/path/hash/participation state without removal. Existing installations
are not adopted as owned. If this app observes an absent service before initialization and
its expected installation afterward, it records path/hash provenance in an administrator-only
registry location. Removal is an explicit maintenance action after ending the hardware
session; normal exit always retains the shared driver. Ownership, path/hash and participating
instances are rechecked under the gate. The UI explicitly acknowledges that unrelated tools
cannot be enumerated reliably. Stop/delete failures retain their partial outcomes and never
trigger direct registry-tree deletion or forced closure of other applications. This is not
a guarantee of universal external-consumer detection.

Per-Monitor V2 configuration and Windows compatibility metadata are retained in the actual
packaged executable/config. Forms resize and scroll; the compact result row grows with its
font. Resource-backed English/Simplified Chinese captions preserve control IDs, profile
formats and technical values. Language changes preserve live summaries and existing logs.
Messages without a translation use their English diagnostic fallback.

## Verification

Automated coverage includes the previous P-state, transport and responsiveness regressions;
synthetic APOB/legacy mapping, reordered and duplicate identities, binding conversion and
whole-profile preflight; VID bit preservation, zero writes on invalid/failed reads, disabled
states, affinity failure, partial writes and readback failure; fake driver eligibility and
stop/delete failure cases; localized controls and font-size layout checks.

Package tests inspect executable resources as data (no entry-point execution), validate UAC,
Windows compatibility and DPI configuration, compare repeated archive hashes, reject missing
inputs/version mismatches/unsafe entries/corruption, and check dependency provenance. CI runs
with read-only build permissions; only the tag publication job receives release write access.

No automated test constructs Cpu, issues a hardware tuning command or removes an installed
driver. Real BIOS/firmware behavior, multi-group VID writes, driver maintenance and actual
mixed-monitor DPI behavior remain hardware-validation items. These limits are included in
the release notes rather than represented as passing hardware tests.

## Upstream references

- [SMUDebugTool 2c55d18](https://github.com/irusanov/SMUDebugTool/commit/2c55d18d930338f0f32b7b8afbc7c5df2216f248): source dependency, APOB and lifecycle integration reference.
- [ZenStates-Core 3bb1dad](https://github.com/irusanov/ZenStates-Core/tree/3bb1dad5b6cccc14f063d6eacbcf891a309944f3): pinned implementation and command contracts.
- [SMUDebugTool fdac4ff](https://github.com/irusanov/SMUDebugTool/commit/fdac4ff33784a07e816bea78f2026508a9eeacc4): P-state editor/VID ideas, adapted to per-target preservation.
- [acetaffy workflow](https://github.com/acetaffy/SMUDebugTool/commit/5c71ce2b04e0417d14d65b93371584605fa619b6): downloadable build artifact idea, adapted to the tested net48 package.
