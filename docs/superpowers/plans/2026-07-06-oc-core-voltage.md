# Manual OC Core Voltage Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a fixed all-core CPU voltage control to the Freq (OC) tab so a user can run a classic manual overclock (fixed clock + fixed vcore), applied by its own button, with an X3D-aware voltage cap.

**Architecture:** A new pure `VoltageCodec` converts volts↔VID (generation-aware SVI2/SVI3) and detects X3D by CPU name; it is unit-tested with no hardware dependency, mirroring the existing `CurveShaperCodec`. `SettingsForm` adds a voltage row to the existing `BuildFrequencyTab` and an `ApplyOverclockVoltage()` handler that enables OC mode and calls `Cpu.SetOverclockCpuVid` under the `Hardware.Sync` lock. `SmuDecodeAdapter.IsSvi3` is promoted to `public` so the UI picks the conversion mode from one source of truth.

**Tech Stack:** C# / .NET Framework 4.8 WinForms (main app), ZenStates-Core (prebuilt SMU library), xUnit (`ProfileCore.Tests`, net48). Build with `dotnet build`; test with `dotnet test`.

**Spec:** `docs/superpowers/specs/2026-07-06-oc-core-voltage-design.md`

**Reference facts (from the spec / library):**
- VID→volts: SVI3 `V = 0.245 + vid*0.005`; SVI2 `V = 1.55 - vid/160`.
- Volts→VID: SVI3 `vid = round((V-0.245)/0.005)`; SVI2 `vid = round((1.55-V)*160)`; both clamped `0..255`.
- Known values: SVI3 1.200 V → 191; SVI2 1.200 V → 56.
- Voltage is a single package-wide value: `Cpu.SetOverclockCpuVid(uint)` (no core/CCD mask). Supported Zen 2+ (both target CPUs).
- A fixed vcore only holds while OC mode is enabled. Recommended apply order: **voltage first, then frequency.**
- X3D parts share the one rail with the voltage-sensitive V-Cache CCD → cap the box max at 1.400 V; non-X3D 1.550 V. Default 1.200 V, min 0.700 V.

---

## File Structure

- **Create** `Utils/VoltageCodec.cs` — pure volts↔VID conversion (SVI2/SVI3) + `IsX3D(string)`. One responsibility: voltage/VID encoding and X3D name detection. No WinForms, no ZenStates dependency.
- **Create** `Tests/ProfileCore.Tests/VoltageCodecTests.cs` — unit tests for `VoltageCodec`.
- **Modify** `ZenStatesDebugTool.csproj` — add `Compile Include` for `Utils/VoltageCodec.cs`.
- **Modify** `Tests/ProfileCore.Tests/ProfileCore.Tests.csproj` — add linked `Compile Include` for `Utils/VoltageCodec.cs`.
- **Modify** `Utils/SmuDecodeAdapter.cs` — change `IsSvi3` from `private` to `public`.
- **Modify** `SettingsForm.cs` — add voltage row + "Apply voltage" button in `BuildFrequencyTab`; add `ApplyOverclockVoltage()`; extend the tab warning text; capability guard.

---

## Task 1: VoltageCodec — VID → volts (both generations)

**Files:**
- Create: `Utils/VoltageCodec.cs`
- Modify: `ZenStatesDebugTool.csproj`
- Modify: `Tests/ProfileCore.Tests/ProfileCore.Tests.csproj`
- Test: `Tests/ProfileCore.Tests/VoltageCodecTests.cs`

- [ ] **Step 1: Write the failing test**

Create `Tests/ProfileCore.Tests/VoltageCodecTests.cs`:

```csharp
using ZenStatesDebugTool;
using Xunit;

namespace ProfileCore.Tests
{
    public class VoltageCodecTests
    {
        [Theory]
        [InlineData(191u, true, 1.200)]   // SVI3: 0.245 + 191*0.005 = 1.200
        [InlineData(0u, true, 0.245)]     // SVI3 floor
        [InlineData(56u, false, 1.200)]   // SVI2: 1.55 - 56/160 = 1.200
        [InlineData(0u, false, 1.550)]    // SVI2: vid 0 = 1.55 V
        public void VidToVoltage_matches_generation_formula(uint vid, bool svi3, double expected)
        {
            Assert.Equal(expected, VoltageCodec.VidToVoltage(vid, svi3), 3);
        }
    }
}
```

- [ ] **Step 2: Add the codec file to both projects so the test compiles**

In `ZenStatesDebugTool.csproj`, find the line `<Compile Include="Utils\CoreTopology.cs" />` and add directly after it:

```xml
    <Compile Include="Utils\VoltageCodec.cs" />
```

In `Tests/ProfileCore.Tests/ProfileCore.Tests.csproj`, find the line
`<Compile Include="..\..\Utils\CurveShaperCodec.cs" Link="CurveShaperCodec.cs" />`
and add directly after it:

```xml
    <Compile Include="..\..\Utils\VoltageCodec.cs" Link="VoltageCodec.cs" />
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test Tests/ProfileCore.Tests/ProfileCore.Tests.csproj`
Expected: FAIL to build — `VoltageCodec` does not exist (`CS0103`/`CS0246`).

- [ ] **Step 4: Write minimal implementation**

Create `Utils/VoltageCodec.cs`:

```csharp
namespace ZenStatesDebugTool
{
    // Pure volts<->VID conversion for manual OC core voltage. WinForms- and
    // hardware-free (no ZenStates dependency) so it unit tests like CurveShaperCodec.
    // The SMU takes a VID byte; the mapping differs by voltage interface generation:
    //   SVI3 (Zen 4/5): V = 0.245 + vid * 0.005
    //   SVI2 (Zen 2/3): V = 1.55  - vid / 160
    public static class VoltageCodec
    {
        public static double VidToVoltage(uint vid, bool svi3)
        {
            if (svi3)
                return 0.245 + vid * 0.005;
            return 1.55 - vid / 160.0;
        }
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test Tests/ProfileCore.Tests/ProfileCore.Tests.csproj`
Expected: PASS (4 cases).

- [ ] **Step 6: Commit**

```bash
git add Utils/VoltageCodec.cs Tests/ProfileCore.Tests/VoltageCodecTests.cs ZenStatesDebugTool.csproj Tests/ProfileCore.Tests/ProfileCore.Tests.csproj
git commit -m "feat: add VoltageCodec VID->voltage conversion"
```

---

## Task 2: VoltageCodec — volts → VID with clamping

**Files:**
- Modify: `Utils/VoltageCodec.cs`
- Test: `Tests/ProfileCore.Tests/VoltageCodecTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `VoltageCodecTests.cs` inside the class:

```csharp
        [Theory]
        [InlineData(1.200, true, 191u)]    // SVI3 known value
        [InlineData(1.200, false, 56u)]    // SVI2 known value
        [InlineData(1.550, true, 255u)]    // SVI3: (1.55-0.245)/0.005 = 261 -> clamp 255
        [InlineData(0.100, true, 0u)]      // below SVI3 floor -> 0
        [InlineData(2.000, false, 0u)]     // SVI2: (1.55-2.0)*160 < 0 -> clamp 0
        public void VoltageToVid_converts_and_clamps(double volts, bool svi3, uint expected)
        {
            Assert.Equal(expected, VoltageCodec.VoltageToVid(volts, svi3));
        }

        [Theory]
        [InlineData(0.900, true)]
        [InlineData(1.350, true)]
        [InlineData(0.900, false)]
        [InlineData(1.350, false)]
        public void VoltageToVid_then_VidToVoltage_round_trips_within_one_step(double volts, bool svi3)
        {
            uint vid = VoltageCodec.VoltageToVid(volts, svi3);
            double back = VoltageCodec.VidToVoltage(vid, svi3);
            Assert.True(System.Math.Abs(back - volts) <= 0.00625, $"got {back} for {volts}");
        }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/ProfileCore.Tests/ProfileCore.Tests.csproj`
Expected: FAIL to build — `VoltageToVid` not defined.

- [ ] **Step 3: Write minimal implementation**

In `Utils/VoltageCodec.cs`, add a `using System;` at the top and the method inside the class:

```csharp
        // Inverse of VidToVoltage, rounded to the nearest VID and clamped to the
        // byte range the SMU accepts. Out-of-range voltages resolve to the nearest
        // encodable VID (e.g. >~1.52 V on SVI3 -> 255) rather than throwing.
        public static uint VoltageToVid(double volts, bool svi3)
        {
            double vid = svi3
                ? (volts - 0.245) / 0.005
                : (1.55 - volts) * 160.0;
            long rounded = (long)Math.Round(vid, MidpointRounding.AwayFromZero);
            if (rounded < 0) rounded = 0;
            if (rounded > 255) rounded = 255;
            return (uint)rounded;
        }
```

Add the `using System;` as the first line of the file (above the namespace).

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/ProfileCore.Tests/ProfileCore.Tests.csproj`
Expected: PASS (all VoltageCodec cases).

- [ ] **Step 5: Commit**

```bash
git add Utils/VoltageCodec.cs Tests/ProfileCore.Tests/VoltageCodecTests.cs
git commit -m "feat: add VoltageCodec voltage->VID conversion with clamping"
```

---

## Task 3: VoltageCodec — IsX3D detection

**Files:**
- Modify: `Utils/VoltageCodec.cs`
- Test: `Tests/ProfileCore.Tests/VoltageCodecTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `VoltageCodecTests.cs` inside the class:

```csharp
        [Theory]
        [InlineData("AMD Ryzen 9 9950X3D 16-Core Processor", true)]
        [InlineData("AMD Ryzen 7 5800X3D 8-Core Processor", true)]
        [InlineData("amd ryzen 7 7800x3d", true)]   // case-insensitive
        [InlineData("AMD Ryzen 9 5950X 16-Core Processor", false)]
        [InlineData("AMD Ryzen 9 9950X 16-Core Processor", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void IsX3D_detects_vcache_parts_by_name(string cpuName, bool expected)
        {
            Assert.Equal(expected, VoltageCodec.IsX3D(cpuName));
        }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test Tests/ProfileCore.Tests/ProfileCore.Tests.csproj`
Expected: FAIL to build — `IsX3D` not defined.

- [ ] **Step 3: Write minimal implementation**

In `Utils/VoltageCodec.cs`, add inside the class:

```csharp
        // True for 3D V-Cache parts (name contains "X3D"), whose cache CCD shares
        // the single core-voltage rail and tolerates less vcore. The library exposes
        // no cleaner signal, so this is a name check. Null/empty -> false.
        public static bool IsX3D(string cpuName)
        {
            return !string.IsNullOrEmpty(cpuName)
                && cpuName.IndexOf("X3D", StringComparison.OrdinalIgnoreCase) >= 0;
        }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test Tests/ProfileCore.Tests/ProfileCore.Tests.csproj`
Expected: PASS (all VoltageCodec cases incl. IsX3D).

- [ ] **Step 5: Commit**

```bash
git add Utils/VoltageCodec.cs Tests/ProfileCore.Tests/VoltageCodecTests.cs
git commit -m "feat: add VoltageCodec.IsX3D name detection"
```

---

## Task 4: Make SmuDecodeAdapter.IsSvi3 public

**Files:**
- Modify: `Utils/SmuDecodeAdapter.cs:49`

- [ ] **Step 1: Change visibility**

In `Utils/SmuDecodeAdapter.cs`, change:

```csharp
        private static bool IsSvi3(CodeName c)
```

to:

```csharp
        public static bool IsSvi3(CodeName c)
```

(No other change — `GetVidToVoltage` still calls it internally; this just lets `SettingsForm` pick the mode from the same source of truth.)

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build ZenStatesDebugTool.csproj`
Expected: `Build succeeded`, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add Utils/SmuDecodeAdapter.cs
git commit -m "refactor: expose SmuDecodeAdapter.IsSvi3 for the voltage path"
```

---

## Task 5: Voltage row + Apply voltage handler on the Freq (OC) tab

**Files:**
- Modify: `SettingsForm.cs` — `BuildFrequencyTab` (around lines 1158-1250), the tab warning label (lines 1172-1181), and add `ApplyOverclockVoltage()` near `ApplyAllCoreFrequencies` (around line 1270).

Context you need:
- `EnableOCMode(bool prochotEnabled = true)` exists at ~line 1135 and enables OC mode.
- `Hardware.Locked(Func<T>)` runs a hardware call under `Hardware.Sync`. `cpu.SetOverclockCpuVid(uint)` returns `SMU.Status`. `SMU` is in `ZenStates.Core` (already `using`-ed).
- `SetStatusText(string)` and `HandleError(string)` exist.
- `cpu.info.codeName` is the nested `Cpu.CodeName` (there is `using static ZenStates.Core.Cpu;`), which is what `SmuDecodeAdapter.IsSvi3` takes.
- `cpu.systemInfo.CpuName` is the CPU name string.
- The action bar (lines 1229-1243) currently holds "Apply all cores" and "Disable OC Mode" buttons; `root` is the scrollable `FlowLayoutPanel`.

- [ ] **Step 1: Add a field to hold the voltage input**

In `SettingsForm.cs`, directly below the `freqControls` field declaration (line ~1156, `private readonly Dictionary<int, NumericUpDown> freqControls = ...`), add:

```csharp
        // Single all-core manual-OC voltage input (SetOverclockCpuVid is package-wide;
        // there is no per-core voltage). Null until BuildFrequencyTab runs.
        private NumericUpDown _ocVoltageNud;
```

- [ ] **Step 2: Extend the tab warning text (voltage-first, all-CCD, X3D)**

In `BuildFrequencyTab`, replace the warning label's `Text` (lines ~1178-1180) with:

```csharp
                Text = "Manual OC overrides PBO / Curve Optimizer. Every core runs at the fixed clock " +
                       "you set below - no boost, no idle downclock. Core voltage is a SINGLE value " +
                       "applied to ALL cores on every CCD. Recommended order: set voltage FIRST, then " +
                       "frequency (applying voltage engages OC Mode and holds cores at ~2500 MHz until " +
                       "you apply frequencies). Too high a voltage or clock can hang, reboot, or damage the CPU."
```

- [ ] **Step 3: Add the voltage row and button, and the capability guard**

In `BuildFrequencyTab`, immediately AFTER the "All cores" bulk row is added (after the `root.Controls.Add(MakeBulkRow("All cores", allNud, ...));` block ends, ~line 1188) and BEFORE the `int ccdCount = GetCcdCount();` line, insert:

```csharp
            // Single all-core voltage row with its own Apply button. Voltage is applied
            // separately from frequency; recommended order is voltage first (see warning).
            bool x3d = VoltageCodec.IsX3D(cpu.systemInfo.CpuName);
            _ocVoltageNud = new NumericUpDown
            {
                Minimum = 0.700m,
                // X3D parts share the rail with the voltage-sensitive V-Cache CCD, so cap lower.
                Maximum = x3d ? 1.400m : 1.550m,
                DecimalPlaces = 3,
                Increment = 0.005m,
                Value = 1.200m,
                Width = 70,
                Margin = new Padding(0, 2, 0, 0)
            };
            var voltRow = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                AutoSize = true,
                WrapContents = false,
                Margin = new Padding(0, 0, 0, 4)
            };
            voltRow.Controls.Add(new Label { Text = "Core voltage (all cores)", AutoSize = true, Width = 130, Margin = new Padding(0, 6, 8, 0) });
            voltRow.Controls.Add(_ocVoltageNud);
            var applyVoltBtn = new Button { Text = "Apply voltage", AutoSize = true, UseVisualStyleBackColor = true, Margin = new Padding(6, 0, 0, 0) };
            applyVoltBtn.Click += (s, e) => ApplyOverclockVoltage();
            voltRow.Controls.Add(applyVoltBtn);

            // Capability guard: disable if the SMU defines no SetOverclockCpuVid message in
            // either mailbox (not the case for any current target, but future-proof).
            if (cpu.smu.Rsmu.SMU_MSG_SetOverclockCpuVid == 0
                && cpu.smu.Mp1Smu.SMU_MSG_SetOverclockCpuVid == 0)
            {
                _ocVoltageNud.Enabled = false;
                applyVoltBtn.Enabled = false;
            }
            else if (x3d)
            {
                voltRow.Controls.Add(new Label
                {
                    Text = "(X3D: shared with V-Cache CCD; capped 1.40 V)",
                    AutoSize = true,
                    ForeColor = Color.Firebrick,
                    Margin = new Padding(8, 6, 0, 0)
                });
            }

            root.Controls.Add(voltRow);
```

- [ ] **Step 4: Add the ApplyOverclockVoltage handler**

In `SettingsForm.cs`, directly AFTER the `ApplyAllCoreFrequencies()` method (ends ~line 1281), add:

```csharp
        // Applies the single all-core manual-OC voltage. Enables OC Mode first so the
        // fixed VID actually holds (outside OC Mode the SMU governs voltage). Recommended
        // to run this BEFORE applying frequencies: this pins the voltage while cores idle
        // at the OC-mode default (~2500 MHz), so frequency then ramps into an established
        // voltage floor rather than a high clock on an undefined voltage.
        private void ApplyOverclockVoltage()
        {
            if (cpu == null || _ocVoltageNud == null || !_ocVoltageNud.Enabled) return;

            double volts = (double)_ocVoltageNud.Value;
            bool svi3 = SmuDecodeAdapter.IsSvi3(cpu.info.codeName);
            uint vid = VoltageCodec.VoltageToVid(volts, svi3);

            EnableOCMode(true);
            bool ok = Hardware.Locked(() => cpu.SetOverclockCpuVid(vid) == SMU.Status.OK);
            if (ok)
                SetStatusText($"{volts:0.000} V (VID {vid}) applied - OC Mode on. Apply frequencies next.");
            else
                HandleError("Failed to set core voltage.");
        }
```

- [ ] **Step 5: Build to verify it compiles**

Run: `dotnet build ZenStatesDebugTool.csproj`
Expected: `Build succeeded`, 0 errors (the pre-existing `SendSmuCommand` obsolete warning is fine).

- [ ] **Step 6: Run the full test suite (nothing regressed)**

Run: `dotnet test Tests/ProfileCore.Tests/ProfileCore.Tests.csproj`
Expected: PASS — all prior tests plus the new VoltageCodec tests.

- [ ] **Step 7: Commit**

```bash
git add SettingsForm.cs
git commit -m "feat: add all-core OC voltage control to the Freq (OC) tab"
```

---

## Task 6: Manual verification notes (hardware)

**Files:** none (documentation of the manual check for whoever runs it on real hardware).

- [ ] **Step 1: Record the manual test steps**

These cannot be automated (require the physical CPUs). On each target machine:

1. Launch the app, open the **Freq (OC)** tab.
2. Confirm the voltage row shows "Core voltage (all cores)" defaulting to 1.200 V. On the 9950X3D confirm the box will not exceed 1.400 V and the X3D caption is shown; on the 5800X3D/5950X confirm it allows up to 1.550 V.
3. Set voltage to 1.200 V, click **Apply voltage**. Confirm the status line reads `1.200 V (VID <n>) applied - OC Mode on.` — VID 191 on the 9950X3D (SVI3), VID 56 on the 5800X3D (SVI2). Machine stays stable (cores idle ~2500 MHz).
4. Set a modest all-core frequency (e.g. 4000 MHz), click **Apply all cores**. Confirm cores run the fixed clock at the applied voltage.
5. Click **Disable OC Mode**; confirm normal boost behaviour returns.

- [ ] **Step 2: Commit (if any notes were added to docs)**

No code change; skip unless notes were written to a doc file.

---

## Self-Review

- **Spec coverage:**
  - VoltageCodec volts↔VID SVI2/SVI3 → Tasks 1-2. ✓
  - IsX3D → Task 3. ✓
  - IsSvi3 public → Task 4. ✓
  - Voltage row + Apply voltage button, X3D-capped max, package-wide/voltage-first warning, capability guard → Task 5. ✓
  - ApplyOverclockVoltage (EnableOCMode + SetOverclockCpuVid under Hardware lock, resolved-VID status) → Task 5. ✓
  - Out of scope (profiles, read-back, per-core voltage) → nothing added for these; correct. ✓
  - Tests (known values, round-trip, clamping, IsX3D) → Tasks 1-3. ✓
  - Manual hardware verification → Task 6. ✓
- **Placeholder scan:** No TBD/TODO; every code step shows full code. ✓
- **Type consistency:** `VoltageCodec.VoltageToVid(double,bool)`, `VidToVoltage(uint,bool)`, `IsX3D(string)`, `SmuDecodeAdapter.IsSvi3(CodeName)`, `_ocVoltageNud`, `ApplyOverclockVoltage()`, `cpu.SetOverclockCpuVid(uint) -> SMU.Status` used consistently across tasks. ✓
