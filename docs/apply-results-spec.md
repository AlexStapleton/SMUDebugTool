# SMUDebugTool: CO and FMax apply results

Design baseline: [v1.47.3, commit 1aa497b](https://github.com/AlexStapleton/SMUDebugTool/tree/1aa497bcf71ab874fcd46b4c258c99e7be3271fe). Prepared 13 November 2026.

The next improvement is to make each apply operation explain what the firmware accepted and what the application could actually verify. Start with Curve Optimizer and FMax, shared between manual buttons, profile Apply and headless startup.

This should precede the larger ZenStates-Core/APOB upgrade: the result reporting will make that upgrade easier to validate. The current bundled DLL exposes public SMU send methods returning raw status codes, so this first change can retain it.

## Problem and outcome

The [profile CO path](https://github.com/AlexStapleton/SMUDebugTool/blob/1aa497bcf71ab874fcd46b4c258c99e7be3271fe/Profiles/ProfileApplier.cs#L29) and manual ApplyCO path discard the boolean result of each core write. Headless apply can consequently return success after rejected CO commands. The [manual FMax handler](https://github.com/AlexStapleton/SMUDebugTool/blob/1aa497bcf71ab874fcd46b4c258c99e7be3271fe/SettingsForm.cs#L3154) does nothing visible when SetFMax fails. Existing FMax and Curve Shaper readback fallbacks must remain available and must be identified accurately.

After this change, every requested CO core and FMax write gets an outcome. A firmware acceptance is distinct from a matching readback. Unknown readback is explicit; it is never represented by an invented hardware value.

## User experience

Keep the existing controls and profile workflows. Add a compact last-apply summary above the existing log, with a Details button. Details opens a resizable, read-only results grid. It does not open automatically after every Apply.

Example only, not hardware results or recommended tuning values:

**Apply completed with errors — 1 verified, 1 unverified, 1 rejected, 1 mismatch**

| Setting | Requested | Readback | Result |
| --- | --- | --- | --- |
| CO core 3 | -20 steps | -20 steps | Verified |
| CO core 7 | -25 steps | — | Rejected: prerequisites not met |
| CO core 11 | -15 steps | -10 steps | Readback differs |
| FMax | 5600 MHz | Unavailable | Accepted; not verified |

The details column includes the raw status code when available, mailbox, command ID, target/core mask and observation time. Human explanations appear first. Text labels carry meaning independently of color. A Copy report button exports the same plain-text format used by startup logs.

Results describe one dated operation, not continuously monitored current state. Show the profile name or Current settings, timestamp and application version. Existing startup results are labeled as a previous operation.

A failed write must preserve the requested input. Observed values belong in the result/readback column. Remove the unconditional post-CO refresh that currently overwrites the grid immediately after Apply. Explicit Refresh remains a read-only action and cannot turn a previously failed command into an apparent success.

Where FMax readback is unavailable or zero is the library's failure sentinel, retain the user's entry and show Accepted; not verified after an accepted write. Never replace it with zero or label a cached/requested value as a fresh hardware reading.

## Outcome contract

Use a hardware-independent result object for one setting/target, plus an aggregate report.

| Outcome | Meaning | Hard failure |
| --- | --- | --- |
| Verified | Firmware accepted the command and a supported, direct getter returned the requested setting | No |
| AcceptedUnverified | Firmware accepted it; direct verification is unavailable or could not complete | No, but explicitly unverified |
| Mismatch | Accepted write followed by valid direct readback of a different setting | Yes |
| Rejected | Firmware returned a non-OK status | Yes |
| Skipped | Setting omitted, known unsupported, or target disabled; reason recorded | No |
| Invalid | Requested input or target cannot be represented/validated | Yes |
| NotAttempted | A batch was stopped before this request was sent | Report incomplete |
| Error | Driver/I/O exception or otherwise indeterminate execution | Yes |

Keep write status and verification status separate internally. For example, a successful write followed by a getter timeout remains an accepted write with failed verification; it must not become WriteFailed. If that timeout aborts the batch, the aggregate is also incomplete.

Fields: setting kind; physical core/CCD label when applicable; requested value and unit; optional observed value; write status byte; separate optional getter status byte; readback source (direct, derived, unavailable); timestamp; reason; mailbox and command ID. Raw values remain numeric in the model. A formatter owns display text.

A derived estimate or startup-profile fallback may be displayed as such, but never earns Verified. Verification concerns the CO parameter or boost ceiling; it does not prove CPU stability, guarantee achieved clock frequency, or prove the setting persists after reboot.

Aggregate report: all rows; counts by outcome; complete/incomplete; and HasErrors. Retain a compatibility Success property meaning no hard errors and a complete operation. Do not use Success as shorthand for all settings verified.

Unsupported/omitted-only operations get No applicable settings, not Profile applied. Accepted-unverified results remain a valid outcome and are called out in both GUI and logs.

## Hardware access and command behavior

Put the shared workflow behind TuningService and an injectable ITuningTransport. The production transport uses the existing Cpu/SMU object and public SendRsmuCommand/SendMp1Command APIs. Do not reflect into the DLL's internal command classes.

Before implementation, audit the bundled DLL's packet construction and mailbox preference against the public upstream command implementations. Upstream master has different namespaces and is reference material, not a drop-in dependency. Add packet/routing fixtures for the contract actually shipped.

Rules:

- Resolve Set and Get capability separately. A defined setter does not imply a working getter.
- Check both RSMU and MP1 candidates for CO/FMax. Select the same route/payload as the bundled implementation; do not introduce speculative retries or fallback after a failed write.
- Use the existing core-mask helper and library margin-argument encoding. Preserve physical core IDs and log the encoded target. APOB mapping changes remain in the later core upgrade.
- Validate numeric representability and the application's supported input ranges before sending anything. Reject fractional FMax from edited JSON rather than silently truncating it. Do not silently clamp values or describe field/UI bounds as safe silicon limits.
- Preflight the complete requested batch. If any input/target is invalid, send no writes; report invalid rows and mark the rest NotAttempted.
- Snapshot the requested values and CPU capabilities before the operation; mutable WinForms controls are not read by a hardware worker.
- Allocate fresh argument arrays for each command. A response payload is usable only when its own status is OK and its format is known.
- Obtain and validate a fresh direct readback at most once per accepted write. CO zero is a legitimate value; all-zero CO readings are not automatically proof that a getter is broken. Handle signed margins correctly.
- Use the existing capability/readback knowledge to distinguish unavailable or derived getters. Do not infer direct verification from a convenience getter that hides failures.
- Preserve Hardware.Sync serialization across the batch and its immediate readbacks. The service never invokes UI while holding that lock.
- Make no automatic retries and no automatic rollback. Previously accepted writes remain accepted when a later operation fails.
- Ordinary firmware rejections such as prerequisites/unknown command can be recorded while remaining independent requests proceed. Busy, mutex/transport timeouts, PCI failure or an I/O exception stop the batch; remaining requests are NotAttempted.
- Missing support discovered before sending is Skipped. A selected, advertised command rejected by firmware is a visible Rejected result, including UNKNOWN_CMD.

Raw statuses confirmed in the bundled DLL: OK 0x01; mutex timeout 0x30; mailbox-ready timeout 0x31; message-write timeout 0x32; PCI failure 0x33; busy 0xFC; prerequisites 0xFD; unsupported command 0xFE; failed 0xFF. Map every value to a short explanation and preserve unfamiliar codes verbatim.

The first target requiring live hardware validation is the 9950X3D/AM5 path. Any additional CPU/mailbox layout enabled by the new capability checks needs a known packet/target contract; an unrecognized mapping must not be guessed.

## Shared GUI, profiles and startup behavior

All three entry points call the same CO/FMax service:

1. Manual CO Apply snapshots enabled core inputs, applies CO only, and displays the report.
2. Manual FMax Apply sends FMax only through that same service.
3. Profile Apply snapshots current on-screen values and invokes ProfileApplier. Headless --applyprofile loads the saved JSON and invokes the same applier.

Profile JSON retains its existing schema. No automatic saving, task registration or profile conversion is added.

PPT/TDC/EDC/scalar and Curve Shaper keep their current command behavior. Adapt their existing per-command return statuses into the report as accepted-unverified/rejected rows, so the profile summary accounts for all settings. This does not add new telemetry or readback implementations for those controls.

Only update last-accepted caches for commands actually accepted. Existing Curve Shaper profile application currently seeds its cache from the whole request even after failures; account for accepted tiers rather than claiming a failed request was applied. Keep fallback values visibly distinct from direct readback.

Headless exit behavior remains 0 for complete operations with no hard failure, including accepted-unverified and expected unsupported skips; exit 1 for invalid input, rejection, mismatch, transport error or incomplete execution. A no-op is explicitly logged. No dialogs appear in headless mode.

Keep profiles/apply.log readable and append one complete summary line, preserving the current ShowLastStartupApplyResult reader. It includes counts, verification state and failed targets. Detailed per-setting rows can be included in the same line in stable target order, avoiding an unrelated log-format migration in this PR.

## Implementation boundaries

| File | Responsibility |
| --- | --- |
| Utils/TuningResult.cs (new) | Outcome/readback enums, per-setting result and aggregate report |
| Utils/TuningService.cs (new) | Injected transport contract, request validation, CO/FMax sequencing and verification |
| Utils/ZenStatesTuningTransport.cs (new) | Bundled-library capability, packet, mailbox and signed-value adaptation |
| Utils/TuningResultFormatter.cs (new) | Consistent summary/detail text for UI and headless logs |
| ApplyResultsForm.cs (new) | Read-only grid and Copy report action; code-built WinForms dialog |
| Utils/HardwareService.cs | Own the shared production tuning service alongside current MSR/P-state operations |
| Profiles/ProfileApplier.cs | Shared report aggregation; injectable hardware boundary so profile outcomes can be tested without constructing Cpu |
| SettingsForm.cs | Wire manual/profile buttons, summary/details UI, and preserve edit inputs |
| Program.cs | Format the same report and map its aggregate result to the existing exit-code convention |
| App/test csproj and tests | Include new files and hardware-free regression coverage |

Make the profile hardware boundary cover the existing PBO/Curve Shaper calls too, allowing whole-profile tests with a fake transport. This is a bounded extraction of existing calls; it does not redesign the profile format or add a general plugin/command framework.

No new voltage write commands, VDDG controls, AMD-driver integration, ACPI reboot claims, core-library replacement, driver cleanup or P-state changes are part of this feature.

## Tests and acceptance

Use fake transports; no test initializes Cpu, a driver, or a real SMU command.

Required scenarios:

- One failed CO core makes GUI and startup reports fail with the same target/status.
- Requested, disabled, invalid and omitted targets are accounted for accurately.
- RSMU-only, MP1-only, both and neither cases use the validated routing policy.
- Accepted plus matching direct readback is Verified; derived/absent/failed readback is not.
- CO zero and negative values decode correctly; FMax zero/failure does not erase the request.
- A readback mismatch preserves both values and fails the aggregate.
- Each known status and an unknown code gets useful text without losing the raw byte.
- Busy/timeouts/PCI failure stop the batch and mark remaining requests NotAttempted.
- A failed setter is never retried or reported as accepted; partial completion is preserved.
- Invalid JSON values cause zero writes during preflight.
- GUI profile apply uses the current snapshot, not the last saved profile.
- Curve Shaper caches update only for accepted tiers.
- Headless exit codes and summary lines distinguish errors, unverified acceptance and no-op skips.
- Regression coverage retains the P-state codec, bit-preservation and affinity tests from v1.47.3.

Manual checks: result grid at normal/high DPI; keyboard and Copy report access; input values surviving failed Apply; repeated Apply producing a new timestamped report; startup summary matching the same saved-profile request. On supported hardware, first exercise already-established settings and capture the real status/getter behavior without claiming that an accepted limit guarantees a target clock.

Delivery order: (1) result contract, formatter and fake-transport tests; (2) bundled-library transport and shared service; (3) GUI/profile/startup integration and cache corrections; (4) full build/test, manual UI checks, hardware results and reviewable PR. Existing .NET 8 Windows CI and the net48 suite remain the build gate.

The feature is complete when no CO/FMax rejection can disappear, every report distinguishes acceptance from direct verification, all entry points share the same semantics, and existing input/readback fallbacks remain accurate.


## Implementation and review addendum — 13 November 2026

The implementation on `feature/tuning-apply-results` follows the outcome contract above. It retains the bundled core DLL and existing profile JSON. The formatter lives alongside the result model in `Utils/TuningResult.cs`; the summary bar is isolated in `TuningResultsPanel.cs`. `ProfileApplier` accepts an injectable transport for whole-profile tests and holds the process hardware lock for production batches.

### Bundled DLL contract verified during review

Metadata and IL inspection of the shipped DLL, without constructing Cpu or loading a driver, confirmed:

- CO Set prefers MP1 when defined, otherwise RSMU. Argument 0 is `(mask & 0xFFF00000) | Utils.MakePsmMarginArg(margin)`.
- CO Get uses RSMU and the original target mask; its successful raw response is decoded as a signed 32-bit margin. Zero is valid.
- FMax Set prefers RSMU, otherwise MP1, with the bundled 20-bit payload mask. FMax Get uses RSMU; TYPE_APU0 uses argument 3.
- A missing getter sends no command. Each transaction gets a fresh six-element argument array. Rejection does not trigger another route or a retry.
- Desktop CO masks can represent core indices 0–127. Larger indices fail preflight. Per-core APU writes are skipped with an explicit explanation because the bundled setter masks off flat APU indices; this implementation does not invent a mapping.
- PBO and Curve Shaper retain their Cpu API calls and raw statuses. Their reports say accepted/unverified; route/command details unavailable through those convenience APIs remain explicitly unavailable.

The application's numeric input bounds are validation bounds, not assertions about safe silicon settings.

### Responsiveness requirements implemented

1. Manual CO, FMax, Curve Shaper and profile batches use the same asynchronous operation gate. Raw SMU, PCI, MSR, CPUID, BCLK, ACPI, manual OC, P-state actions and memory dumps also perform driver work away from the message loop. UI values are captured before workers run.
2. Existing background scans share the UI gate, report worker errors, and cannot overlap a tuning apply. The log, details window and message loop remain available while input tabs are disabled.
3. Power Table, PCI Range and SMU monitors allow only one poll in flight through UI rendering. Polls skip a busy hardware lock. No poll creates an unbounded queue of UI callbacks. Restart/clear generations discard stale SMU samples.
4. Monitors copy hardware values before rendering. Power Table rebuilds if its length changes. Monitor forms are owned by the main UI and close with it.
5. Shutdown cancels pending work before waiting asynchronously for the shared hardware lock. The active command finishes before Cpu disposal; new locked operations fail the closing check. Disposal runs once. Native driver calls cannot be forcibly interrupted.
6. Range arithmetic uses 64-bit counters to avoid wraparound. PCI monitoring is limited to 16,384 registers; PCI scans to 256 KiB; MSR scans to 65,536 addresses. Startup reads only the last 64 KiB of the apply log rather than the entire file. The existing visible result-log cap remains 100,000 characters.

### Review findings resolved

| Finding | Resolution | Evidence |
| --- | --- | --- |
| CO failures disappeared; FMax could fail silently | Shared per-setting outcomes and separate raw write/read status | Fake-transport rejection, mismatch and partial-batch tests |
| Fractional JSON FMax was silently truncated | Validate the complete request before hardware access | Invalid JSON tests assert zero writes |
| Failed profile Curve Shaper tiers populated the fallback | Merge only accepted tiers; remove saved-profile-as-accepted startup seeding | Partial-failure cache test |
| UI profile applies and Power Table ticks blocked the message loop | Worker execution plus one-operation/one-poll gates | Real WinForms timer heartbeat during a slow fake operation |
| Results bar conflicted with the profile panel's log re-parenting | Attach after profile layout and preserve the table cell | STA layout tests at normal and doubled container sizes |
| Driver could be disposed while a worker used it | Cancellation and a shared-lock shutdown barrier | Shutdown waits, queued work cancels, disposal-once tests |
| Partial-class static initialization could bind the wrong shutdown lock | Initialize lifetime next to Sync in the same class part | Static-lifetime lock-identity regression test |
| Range loops could wrap at the top of the uint address space | Bounded ranges and widened counters | Near-uint-max range test plus source review |

### Validation and remaining acceptance

Local Release build: **passed, 0 warnings, 0 errors**. Full suite: **205 tests passed** (141 baseline plus 64 new cases). Tests use fake transports and metadata; none constructs Cpu, initializes a driver, or changes hardware settings. The suite retains the previous P-state encoding, affinity and bit-preservation tests. UI tests create controls on STA threads and render the details form without driver initialization. `git diff --check` passes.

The review and tests establish software behavior and the command contract, not motherboard firmware behavior. Before release, exercise established settings on the 9950X3D/MSI system and capture the actual setter/getter codes. Check manual and profile applies, a saved-profile startup result, keyboard/Copy report use, and actual normal/high-DPI display. Doubled-container tests do not substitute for high-DPI inspection. No claim is made that an accepted value guarantees stability, voltage telemetry, sustained clock frequency or persistence across reboot.

No VDDG control, new AMD driver integration, core-library upgrade, or P-state register algorithm change is included. The P-state buttons now call their existing affinity-aware routines asynchronously.
