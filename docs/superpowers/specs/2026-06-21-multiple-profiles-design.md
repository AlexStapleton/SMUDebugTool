# Multiple Profiles + External Activation — Design

**Date:** 2026-06-21
**Component:** ZenStates Ryzen SMU Debug Tool (SMUDebugTool / RyzenSDT)

## Summary

Replace the tool's single Curve Optimizer profile with multiple **named profiles**, and
let other programs choose which profile is activated by launching the executable with a
command-line argument. As part of this work, add real **PBO power-limit** tuning (PPT /
TDC / EDC / scalar), which the tool does not currently support, and include those values
in each profile.

## Goals

- Save, load, and delete multiple named profiles instead of one `co_profile.txt`.
- Each profile captures: per-core CO margins, Curve Shaper margins, fmax, frequency,
  and PBO PPT / TDC / EDC limits + PBO scalar.
- An external program activates a profile with `SMUDebugTool.exe --applyprofile <name>`,
  which applies silently and exits.
- Keep the existing "apply on startup" (logon) feature working, now bound to an
  explicitly chosen profile.

## Non-Goals

- No live IPC / background service / named pipe. Activation is launch-with-argument only.
- No "active profile" pointer concept. A missing/unknown profile name does nothing.
- No web/HTTP control surface.

## Current State (for context)

- A single profile is stored in `profiles/co_profile.txt` in a custom text format
  (`[core,margin]` lines plus `fmax=`).
- `BtnSaveCOProfile_Click` / `BtnLoadCOProfile_Click` / `LoadCOProfile()` /
  `ApplyCOProfile()` live inline in `SettingsForm.cs` (~2,400 lines).
- `--applyprofile` (no name) applies the single profile, then **shows** the window on the
  PBO tab.
- A Task Scheduler task `RyzenSDT` (and commented-out registry Run key) launches the exe
  with `--applyprofile` at logon; toggled by the `checkBoxApplyCOStartup` checkbox.
- The PBO tab currently exposes only CO margins, Curve Shaper margins, fmax, and
  frequency. **There are no PPT/TDC/EDC controls anywhere.**
- `ZenStates-Core.dll` is a prebuilt binary that exposes `SetPPTLimit`,
  `SetTDCSOCLimit`, `SetEDCSOCLimit`, `SetPBOScalar`, `SetFMax`/`GetFMax`,
  `SetPsmMarginSingleCore`/`GetPsmMarginSingleCore`, `SetCurveShaperMargin`,
  `SetOverclockFrequency*`, plus `PBO_SCALAR_MIN/MAX` constants.

## Architecture

Approach: **extract a WinForms-free profile core**, so the new complexity is isolated,
testable without a CPU, and reused by both the GUI and the headless activation path.

```
Program.Main
  ├─ args contain --applyprofile <name>?  → headless path (no window)
  │     Cpu → ProfileManager.Load(name) → ProfileApplier.Apply(profile, cpu) → exit
  └─ otherwise → SettingsForm (GUI)
        dropdowns/buttons → ProfileManager (disk) + ProfileApplier (hardware)
```

### New types (no WinForms dependency)

- **`Profile`** — plain data model, JSON-serializable (Newtonsoft.Json, already
  referenced). Fields:
  - `Name` (string)
  - `CoMargins` (map core-index → int)
  - `CurveShaperMargins` (the existing curve-shaper value set)
  - `Fmax` (decimal)
  - `Frequency` (the existing frequency override representation)
  - `PptWatts`, `TdcAmps`, `EdcAmps` (int/decimal)
  - `PboScalar` (int)
  - Fields are optional/nullable; absent fields are not applied (forward/backward
    compatible as the schema grows).

- **`ProfileManager`** — disk layer over `profiles/`:
  - `IEnumerable<string> List()` — profile names from `profiles/*.json`.
  - `Profile Load(string name)`
  - `void Save(Profile profile)` — writes `profiles/<name>.json`.
  - `void Delete(string name)`
  - `void MigrateLegacyIfNeeded()` — if `profiles/co_profile.txt` exists and no JSON
    profiles do, import it as profile `Default`.
  - Knows nothing about `Cpu` or WinForms.

- **`ProfileApplier`** — applies a `Profile` to hardware via a `Cpu`:
  - `ApplyResult Apply(Profile profile, Cpu cpu)` — applies each present field, guarded
    by the same capability checks the form uses today (e.g.
    `cpu.smu.Rsmu.SMU_MSG_SetDldoPsmMargin != 0`, core-enabled checks).
  - Returns success/failure with a message (no UI, no `MessageBox`).
  - This is the single apply path shared by the GUI "Apply" button and the headless
    `--applyprofile` flow.

### `SettingsForm` changes

- Delegates all profile load/save/delete/apply to the new classes.
- Replaces today's single Save/Load pair with a **profile dropdown + Save / Save As… /
  Delete** in the existing programmatically-built PBO action bar
  (`flowLayoutPanelCcdActions`, see `BuildCoActionBar`).
- Adds PBO-limit numeric controls (PPT / TDC / EDC / scalar) on the PBO tab near
  `numericUpDownFmax`.
- Startup section: the existing `checkBoxApplyCOStartup` gains a **separate dropdown**
  for the profile to apply at logon (see Startup Auto-Apply below).

## Profile Storage

- One JSON file per profile: `profiles/<name>.json`.
- Profile names restricted to filesystem-safe characters (validated on Save As…; reject
  or sanitize invalid names).
- **Migration:** `ProfileManager.MigrateLegacyIfNeeded()` runs once; converts a legacy
  `profiles/co_profile.txt` into `Default.json`. The legacy file is left in place
  (non-destructive).

## PBO Limits (new hardware plumbing)

New controls + apply for **PPT (W), TDC (A), EDC (A), PBO scalar**, using the DLL's
`SetPPTLimit` / `SetTDCSOCLimit` / `SetEDCSOCLimit` / `SetPBOScalar`.

**Verification required during implementation (known unknowns):**

- Exact method signatures and **units** (watts vs milliwatts, amps vs centi-amps) are not
  yet confirmed; verify each against the DLL before wiring.
- Whether the **SOC**-rail variants (`SetTDCSOCLimit` / `SetEDCSOCLimit`) are the correct
  user-facing limits vs the VDD-rail SMU messages (`SMU_MSG_SetTDCVDDLimit` /
  `SMU_MSG_SetEDCVDDLimit`) must be confirmed.
- **Read-back:** no live getter for PPT/TDC/EDC has been found (only `GetPBOScalar` and
  fused/system power-limit reads). If no clean read-back exists, the PPT/TDC/EDC controls
  display the **profile's stored value** rather than a live hardware read. PBO scalar can
  use `GetPBOScalar`.
- Scalar range clamped to `PBO_SCALAR_MIN`/`PBO_SCALAR_MAX`.

Because wrong power limits carry hardware risk, the hardware setters stay thin and
isolated in `ProfileApplier`, applied only when the corresponding capability/message is
available, and each is manually verified on real hardware.

## Command-Line Activation

`Program.Main` branches **before constructing any window**:

- `--applyprofile <name>`:
  - Headless. Construct `Cpu`, `ProfileManager.Load(name)`, `ProfileApplier.Apply(...)`,
    then exit. No window is ever shown.
  - On success: exit code `0`.
  - On failure (unknown/missing name, load error, apply error): append a line to
    `profiles/apply.log`, exit with a **non-zero** code. **No `MessageBox`** (it would
    hang automation).
- `--applyprofile` with **no name**: do nothing, log, exit non-zero.
- No `--applyprofile`: launch the GUI as today.

This removes the old behavior of showing the window after a CLI apply.

## Startup Auto-Apply (logon)

- The PBO tab gets a **separate startup dropdown** next to `checkBoxApplyCOStartup`.
- Ticking the checkbox registers the `RyzenSDT` Task Scheduler task with
  `--applyprofile <startup-dropdown selection>` (an explicit profile name baked into the
  task arguments).
- Changing the startup dropdown while the checkbox is enabled re-registers the task.
- Unticking removes the task (unchanged behavior).
- **Persistence with no pointer file:** on launch, read the profile name back out of the
  existing `RyzenSDT` task's action arguments to populate the startup dropdown. The task's
  own arguments are the single source of truth — no `active.txt`.

## Error Handling

- **GUI:** existing `HandleError` / `MessageBox` patterns for interactive failures
  (invalid profile name, save/delete errors, missing capability).
- **Headless:** never shows UI. All outcomes go to `profiles/apply.log`; process exit code
  signals success (`0`) or failure (non-zero) to the calling program.
- Apply operations are guarded by the same SMU-capability checks already used in the form,
  so an unsupported field is skipped rather than throwing.

## Testing

- **Unit (no CPU):** `Profile` JSON round-trip; `ProfileManager` list/save/load/delete;
  `MigrateLegacyIfNeeded` converts legacy text → `Default.json`; load of missing/invalid
  file handled gracefully; profile-name validation.
- **Manual (hardware):** GUI Save/Save As/Delete and dropdown switching; "Apply" applies
  CO + PBO fields; `--applyprofile <name>` applies silently and exits 0; unknown name
  exits non-zero and logs; startup checkbox registers/re-registers/removes the `RyzenSDT`
  task with the correct profile argument; PBO PPT/TDC/EDC/scalar setters verified against
  expected hardware behavior with the unit/signature checks resolved.

## Open Items to Resolve in Implementation

1. Confirm `SetPPTLimit` / `SetTDCSOCLimit` / `SetEDCSOCLimit` / `SetPBOScalar`
   signatures, units, and SOC-vs-VDD rail choice against `ZenStates-Core.dll`.
2. Confirm representation of the existing "frequency" and "Curve Shaper margins" fields so
   they serialize cleanly into `Profile`.
3. Decide read-back behavior per field once getters are confirmed (live read vs stored).
