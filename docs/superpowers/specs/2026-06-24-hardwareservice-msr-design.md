# HardwareService facade (Phase 2, MSR slice) — design

Date: 2026-06-24

## Problem

`SettingsForm` (~3,050 lines) does its own raw hardware IO inline: every button
handler reaches into `cpu` and wraps the call in `Hardware.Locked(...)` / `lock
(Hardware.Sync)` by hand, and reads inputs straight from textboxes (sometimes on
a background thread). This scatters the hardware-lock convention across dozens of
sites (each an opportunity to slip it) and keeps the form untestable and large.

Phase 2 of the A-06 maintainability work introduces a `HardwareService` facade
that owns hardware access + locking, so the form gathers inputs and renders
output but no longer touches `cpu`/`Hardware` directly. To keep risk low on this
live-hardware tool, Phase 2 is delivered in **slices, one register group per PR**.
**This spec covers the first slice: MSR (read / write / scan).** PCI, CPUID, and
the OC/PBO/CO/CS paths are later slices, each its own spec+plan.

## Decisions (from brainstorming)

- **Approach A** — a `HardwareService` that wraps `cpu` + `Hardware.Sync` and
  exposes intent-revealing methods returning **data, not formatted strings**.
- **Scope** — MSR only this slice; everything else stays exactly as-is.
- **Verification** — `HardwareService` wraps `cpu` (ZenStates), so it is **not
  unit-testable** (like `SmuDecodeAdapter`); this slice is verified by **clean
  build + on-hardware smoke test**. Its value is centralized locking, a threading
  fix, and a clearer form — it is the pattern-setter for the later slices, not a
  test-coverage win.

## Architecture

### New: `Utils/HardwareService.cs` (app-only — references `ZenStates.Core`; NOT linked into the test project)

```
public struct MsrReadResult { bool Ok; uint Eax; uint Edx; }   // single read
public struct MsrReading    { uint Address; uint Eax; uint Edx; } // one scan row

public sealed class HardwareService
{
    HardwareService(Cpu cpu)                       // wraps the form's cpu
    MsrReadResult ReadMsr(uint msr)                // Hardware.Locked internally
    bool          WriteMsr(uint msr, uint eax, uint edx) // Hardware.Locked internally
    List<MsrReading> ScanMsrRange(uint start, uint end)  // whole loop under one lock
}
```

- `ReadMsr` wraps `cpu.ReadMsr(msr, ref eax, ref edx)` in `Hardware.Locked` and
  returns `(ok, eax, edx)`.
- `WriteMsr` wraps `cpu.WriteMsr(msr, eax, edx)` in `Hardware.Locked`.
- `ScanMsrRange` holds `lock (Hardware.Sync)` across the whole `start..end` loop
  (matching the current scan), collecting only successful reads into the list.
  The loop preserves the current semantics exactly (`while (msr <= end) { read;
  msr++; }`), including its existing behaviour at the `uint` range boundary — this
  slice does not change scan iteration behaviour.

The two structs are plain data carriers; they live in `HardwareService.cs` (the
file already depends on `ZenStates.Core`, and the structs carry no logic worth
isolating).

### Modified: `SettingsForm.cs`

- Add a `private readonly HardwareService hardware;` field, constructed next to
  `cpu` (once `cpu` exists).
- **`ButtonMsrRead_Click`**: gather `msr` from the textbox → `hardware.ReadMsr(msr)`
  → on `Ok`, set the eax/edx textboxes and append the decode (decode stays in the
  form — it is presentation). Behaviour identical.
- **`ButtonMsrWrite_Click`**: gather msr/eax/edx → `hardware.WriteMsr(...)` →
  same error / "Write OK." handling.
- **MSR scan**: `ButtonMsrScan_Click` gathers `start`/`end` from the textboxes
  **on the UI thread** and passes them into the background task; the task calls
  `hardware.ScanMsrRange(start, end)` and renders the `StringBuilder` (header +
  per-row line + `RegisterDecoder` decode) exactly as today. This removes the
  current cross-thread textbox read inside `ReadMsr_Task`.

### Data flow (unchanged from the user's perspective)

`gather inputs (UI thread) → HardwareService (owns lock, returns data) → form
formats + decodes + renders`. No behaviour change; only the seam moves.

## Error handling

- `ReadMsr`/`WriteMsr` return status as data (`Ok` / bool); the form keeps the
  existing UI error messages and status text.
- `ScanMsrRange` collects only successful reads (matching today's `if
  (cpu.ReadMsr(...))` guard). Input parsing (`TryConvertToUint`) stays in the form
  and keeps its existing `ApplicationException` handling.

## Testing

- `HardwareService` is hardware-coupled → **build-verified + on-hardware smoke**,
  not unit-tested (consistent with `SmuDecodeAdapter`).
- Smoke: MSR Read of a known register shows the same eax/edx + decode as v1.44.4;
  MSR Write reports OK/error correctly; MSR Scan over a small range produces the
  same output (rows + decode) as before.
- `dotnet test` must still pass (72) — unchanged, since no pure logic moved.

## Scope / out of scope

- **In:** MSR read/write/scan only.
- **Out (later slices):** PCI, CPUID, OC/PROCHOT, PBO/CO/CS, P-state, FMax, the
  SMU mailbox (`ApplySettings`/`ScanSmuRange`), and report/WMI. Each later slice
  adds methods to the same `HardwareService` following this pattern.
- No version bump (pure refactor; rides the next functional release).
