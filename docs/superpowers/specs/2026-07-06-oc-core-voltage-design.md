# Manual OC core voltage — design

## Goal

Add the ability to set a fixed all-core CPU voltage on the **Freq (OC)** tab,
alongside the existing manual per-core frequency override, so a user can run a
classic manual overclock (fixed clock + fixed vcore). Frequency and voltage are
applied by separate buttons.

## Background / current state

The **Freq (OC)** tab (`BuildFrequencyTab` in `SettingsForm.cs`) already does
manual all-core / per-core frequency:

- `EnableOCMode(true)` enables manual OC.
- Per-core `SetFrequencySingleCore(mask, mhz)` writes each core's clock.
- "Apply all cores" writes every core; "Disable OC Mode" reverts.

The missing piece is voltage. ZenStates-Core exposes `Cpu.SetOverclockCpuVid(uint vid)`,
which sets a **single all-core** override voltage (there is no per-core voltage
command). It routes through `SMU_MSG_SetOverclockCpuVid` (RSMU, MP1 fallback),
which is defined from Zen 2 onward — so both target CPUs (9950X3D, 5800X3D) support it.

A fixed override voltage is only honored while **OC mode is enabled**; outside OC
mode the SMU governs voltage itself.

## Dual-CCD and X3D behavior

Desktop Ryzen (AM4 and AM5) has a **single shared VDDCR_CPU (VCORE) rail** that
feeds every core across all CCDs. There is no per-CCD/CCX/core voltage command —
`SetOverclockCpuVid` takes a single VID with no mask, and the library exposes no
V-Cache/CCD voltage handling. So the manual OC voltage is **package-wide**: one
value applied to all cores on both CCDs at once. Frequency remains granular (the
existing tab already sets per-core / per-CCD clocks), but all cores share the one
voltage.

- **Uniform dual-CCD (e.g. 5950X)**: both CCDs are ordinary CCDs on the shared
  rail, so a single all-core voltage is the normal manual-OC behavior — nothing
  special.
- **Asymmetric X3D (e.g. 9950X3D)**: CCD0 carries the 3D V-Cache and is
  voltage-sensitive (the SMU normally caps its vcore to protect the stacked cache
  die); CCD1 is a regular CCD. Because both CCDs share the one rail, a fixed
  manual voltage **removes the SMU's protective cap on the cache CCD** — the value
  set must be safe for the lower-tolerance V-Cache CCD.

X3D handling: detect an X3D part from `systemInfo.CpuName` (contains "X3D" —
the library gives no cleaner signal) and, on those parts, lower the voltage box
maximum to **1.400 V** (default still 1.200 V) plus a stronger warning. Non-X3D
parts keep the full 0.700–1.550 V range.

## Voltage → VID conversion

The SMU takes a VID byte, not raw volts. Conversion is generation-aware:

| Generation | CPUs (examples)     | Volts → VID                  | Check: 1.200 V |
|------------|---------------------|------------------------------|----------------|
| SVI3       | Zen 4/5 (9950X3D)   | `VID = (V − 0.245) / 0.005`  | 191            |
| SVI2       | Zen 2/3 (5800X3D)   | `VID = (1.55 − V) × 160`     | 56             |

The result is rounded to the nearest integer and clamped to `0..255`. On SVI3,
1.550 V exceeds the encodable range and clamps to VID 255 (~1.52 V) — the apply
status line notes the resolved VID so this is visible.

Which CPUs are SVI3 is already decided by `SmuDecodeAdapter.IsSvi3(CodeName)`.
That method is currently `private`; it will be made `public` and reused as the
single source of truth, so the voltage path and the existing VID→voltage decode
never disagree.

## Components

### 1. `Utils/VoltageCodec.cs` (new, pure)

WinForms-free and hardware-free, like `CurveShaperCodec`, so it can be unit
tested in `ProfileCore.Tests`. Implements the formulas directly (does not call
`ZenStates.Core.Utils`, which the test project does not reference):

```csharp
public static class VoltageCodec
{
    // svi3 == true for Zen 4/5, false for Zen 2/3.
    public static uint VoltageToVid(double volts, bool svi3);   // clamped 0..255
    public static double VidToVoltage(uint vid, bool svi3);     // for labels / round-trip tests
}
```

### 2. `SmuDecodeAdapter.IsSvi3` → public

No behavior change; visibility change only, so `SettingsForm` can pass the right
mode into `VoltageCodec`.

### 3. UI — voltage row on the Freq (OC) tab

Added in `BuildFrequencyTab`:

- A labeled row: "Core voltage (all cores)", a `NumericUpDown`
  (`Minimum = 0.700`, `DecimalPlaces = 3`, `Increment = 0.005`, `Value = 1.200`),
  and an **"Apply voltage"** button. The `Maximum` is CPU-dependent: **1.400 V**
  on X3D parts, **1.550 V** otherwise.
- The existing red warning label is extended to state that (a) the voltage is a
  single package-wide value applied to all cores on both CCDs, and (b) applying
  voltage also engages OC mode (which fixes all-core clocks), so frequency should
  normally be set first.
- On X3D parts, an additional caution notes the shared rail also feeds the
  voltage-sensitive V-Cache CCD, which is why the max is limited to 1.400 V.

X3D detection is a pure string helper — `VoltageCodec.IsX3D(string cpuName)` —
that checks for "X3D" (case-insensitive). Keeping it a string predicate (rather
than taking the `cpu` object) lets it live in the pure codec and be unit tested;
`SettingsForm` calls it with `cpu.systemInfo.CpuName`.

### 4. Apply logic — `ApplyOverclockVoltage()`

```
volts  = voltageNud.Value
svi3   = SmuDecodeAdapter.IsSvi3(cpu.info.codeName)
vid    = VoltageCodec.VoltageToVid(volts, svi3)
EnableOCMode(true)                                  // so the fixed vcore holds
ok = Hardware.Locked(() => cpu.SetOverclockCpuVid(vid) == SMU.Status.OK)
ok ? SetStatusText($"{volts:0.000} V (VID {vid}) applied - OC Mode on.")
   : HandleError("Failed to set core voltage.")
```

All hardware access goes through `Hardware.Locked` / the `Hardware.Sync` lock,
per the project convention; the lock is not held across a UI Invoke.

### 5. Capability guard

If `SMU_MSG_SetOverclockCpuVid` is 0 in both RSMU and MP1 (not the case for any
current target, but future-proofing consistent with the FMax / Curve Shaper
gates), the voltage box and "Apply voltage" button are disabled with an
explanatory tooltip.

## Out of scope (YAGNI)

- **Profiles**: voltage is not saved to / loaded from profiles, matching the
  per-core OC frequencies, which are also not part of profiles.
- **Live read-back**: no reading of the current VID; the box defaults to 1.200 V,
  matching how the frequency fields default rather than read hardware.
- **Per-core / per-CCD voltage**: unsupported by the hardware (single shared
  VCORE rail) and the SMU; voltage is package-wide only.

## Error handling

- Conversion clamps rather than throwing; out-of-encodable-range values (e.g.
  1.550 V on SVI3) resolve to the nearest valid VID and the resolved VID is shown.
- A failed `SetOverclockCpuVid` surfaces via `HandleError`, consistent with the
  frequency apply path.
- OC-mode side effect (clocks drop to SMU default until frequencies are applied)
  is documented in the tab warning and implied by the "OC Mode on" status.

## Testing

`ProfileCore.Tests` gains `VoltageCodecTests`:

- Known values: SVI3 1.200 V → 191; SVI2 1.200 V → 56.
- Round-trip: `VidToVoltage(VoltageToVid(v)) ≈ v` within one VID step, per mode.
- Clamping: 1.550 V on SVI3 → 255; values below the floor → 0; nothing exceeds
  the 0..255 byte range.
- `IsX3D` detection: names containing "X3D" (e.g. "AMD Ryzen 9 9950X3D") return
  true; regular names (e.g. "AMD Ryzen 9 5950X") return false; case-insensitive.

Manual verification on hardware (both CPUs): apply a frequency, then a voltage,
confirm the status line shows the expected VID and the machine remains stable;
"Disable OC Mode" reverts.
