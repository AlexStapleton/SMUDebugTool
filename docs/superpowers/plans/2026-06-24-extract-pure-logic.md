# Extract Pure Logic from SettingsForm (Maintainability Phase 1) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract the pure, hardware-free P-state and Curve Shaper bit-math out of the 3,089-line `SettingsForm` into small, unit-tested classes, then rewire the form to call them — with zero behavior change.

**Architecture:** This is Phase 1 of the A-06 "god class" maintainability work. It targets only the *pure* computations (no UI, no `cpu`/`Hardware` access), which are the highest value-per-risk slice: they become independently unit-testable (the stated A-06 benefit) and they close the untested-bit-math test gap. The form keeps doing all hardware IO and UI; it just delegates the math. Later phases (a `HardwareService` facade, a partial-class UI split, a `ReportService`) are separate follow-on plans and are NOT in scope here.

**Tech Stack:** C# (net48), WinForms (app), xUnit (tests), `dotnet build`/`dotnet test`.

**Branch & build:** Work on a local branch off `master` (e.g. `refactor/extract-pure-logic`). Build/test with `dotnet` (NOT VS MSBuild). The app is a WinForms exe; rewired form code is verified by **Debug build + on-hardware smoke test**, the extracted pure classes by **unit tests**. If `bin/Debug/SMUDebugTool.exe` is locked, the running app must be closed before a Debug build.

**Conventions:**
- `Utils/` files use the flat `ZenStatesDebugTool` namespace (matches `CoreTopology`, `RegisterDecoder`, etc.).
- The app csproj (`ZenStatesDebugTool.csproj`) is old-style: every new `.cs` needs a `<Compile Include>`.
- Pure files must be free of any `ZenStates.Core`/WinForms reference and ALSO linked into `Tests/ProfileCore.Tests/ProfileCore.Tests.csproj` via `<Compile Include="..\..\Utils\X.cs" Link="X.cs" />`.
- Behavior must be byte-for-byte identical — these extractions preserve the existing expressions exactly (verified against the current code).

---

## File Structure

**New (pure, compiled into BOTH app and test projects):**
- `Utils/PStateMath.cs` — `PStateValues` struct + `Decode`, `Encode`, `FrequencyMhz`. Replaces `CalculatePstateDetails`, the inline `eax = …` assembly, and the `(fid * 25 / (did * 12.5)) * 100` frequency expression.
- `Utils/CurveShaperCodec.cs` — `UnpackMargin`, `Unpack`, `Pack`, `IsAllZero`. Replaces `ConvertMarginToInt`, the per-tier `>> 8/16/24 & 0xFF` decode, `PackCurveShaperTier`, and `IsAllZero`.

**New (tests):**
- `Tests/ProfileCore.Tests/PStateMathTests.cs`
- `Tests/ProfileCore.Tests/CurveShaperCodecTests.cs`

**Modified:**
- `ZenStatesDebugTool.csproj` — register the 2 new files.
- `Tests/ProfileCore.Tests/ProfileCore.Tests.csproj` — link the 2 new files.
- `SettingsForm.cs` — delegate the pstate + curve-shaper math to the new classes; delete the now-unused private helpers.

---

## Task 1: Create `PStateMath` (pure) + tests

**Files:**
- Create: `Utils/PStateMath.cs`
- Modify: `ZenStatesDebugTool.csproj`, `Tests/ProfileCore.Tests/ProfileCore.Tests.csproj`
- Test: `Tests/ProfileCore.Tests/PStateMathTests.cs`

The existing logic this preserves (from `SettingsForm.cs`):
- `CalculatePstateDetails`: `IddDiv = eax >> 30; IddVal = eax >> 22 & 0xFF; CpuVid = eax >> 14 & 0xFF; CpuDfsId = eax >> 8 & 0x3F; CpuFid = eax & 0xFF;`
- `BtnPstateWrite_Click` assembly: `eax = (IddDiv & 0xFF) << 30 | (IddVal & 0xFF) << 22 | (CpuVid & 0xFF) << 14 | (did & 0xFF) << 8 | (fid & 0xFF);`
- Frequency (`BtnPstateRead_Click` / `PstateFidDid_KeyUp`): `(CpuFid * 25 / (CpuDfsId * 12.5)) * 100`.

- [ ] **Step 1: Create `Utils/PStateMath.cs`**

```csharp
namespace ZenStatesDebugTool
{
    // Decoded fields of an AMD PStateDef MSR (MSRC001_006[4..B]) EAX word.
    public struct PStateValues
    {
        public uint IddDiv;
        public uint IddVal;
        public uint CpuVid;
        public uint CpuDfsId;
        public uint CpuFid;
    }

    // Pure P-state bit math, extracted verbatim from SettingsForm so it can be unit-tested.
    public static class PStateMath
    {
        public static PStateValues Decode(uint eax)
        {
            return new PStateValues
            {
                IddDiv = eax >> 30,
                IddVal = eax >> 22 & 0xFF,
                CpuVid = eax >> 14 & 0xFF,
                CpuDfsId = eax >> 8 & 0x3F,
                CpuFid = eax & 0xFF,
            };
        }

        public static uint Encode(uint iddDiv, uint iddVal, uint cpuVid, uint cpuDfsId, uint cpuFid)
        {
            return (iddDiv & 0xFF) << 30
                 | (iddVal & 0xFF) << 22
                 | (cpuVid & 0xFF) << 14
                 | (cpuDfsId & 0xFF) << 8
                 | (cpuFid & 0xFF);
        }

        // MHz from FID/DID using the same expression the UI has always used.
        // Returns 0 when did == 0 (avoids divide-by-zero; the old UI seeded did=1).
        public static double FrequencyMhz(uint cpuFid, uint cpuDfsId)
        {
            if (cpuDfsId == 0) return 0;
            return (cpuFid * 25 / (cpuDfsId * 12.5)) * 100;
        }
    }
}
```

- [ ] **Step 2: Register the file in both csprojs**

In `ZenStatesDebugTool.csproj`, after `<Compile Include="Utils\SmuDecodeAdapter.cs" />`:

```xml
    <Compile Include="Utils\PStateMath.cs" />
```

In `Tests/ProfileCore.Tests/ProfileCore.Tests.csproj`, in the Utils `<Compile Include … Link=…>` group:

```xml
    <Compile Include="..\..\Utils\PStateMath.cs" Link="PStateMath.cs" />
```

- [ ] **Step 3: Write the failing tests**

Create `Tests/ProfileCore.Tests/PStateMathTests.cs`:

```csharp
using ZenStatesDebugTool;
using Xunit;

namespace ProfileCore.Tests
{
    public class PStateMathTests
    {
        // eax for CpuFid=0xA8(168), CpuDfsId=8, CpuVid=0x28(40): (40<<14)|(8<<8)|168 = 0x000A08A8.
        private const uint SampleEax = 0x000A08A8;

        [Fact]
        public void Decode_extracts_fields()
        {
            PStateValues v = PStateMath.Decode(SampleEax);
            Assert.Equal(168u, v.CpuFid);
            Assert.Equal(8u, v.CpuDfsId);
            Assert.Equal(40u, v.CpuVid);
            Assert.Equal(0u, v.IddVal);
            Assert.Equal(0u, v.IddDiv);
        }

        [Fact]
        public void Encode_round_trips_with_decode()
        {
            PStateValues v = PStateMath.Decode(SampleEax);
            uint encoded = PStateMath.Encode(v.IddDiv, v.IddVal, v.CpuVid, v.CpuDfsId, v.CpuFid);
            Assert.Equal(SampleEax, encoded);
        }

        [Fact]
        public void FrequencyMhz_matches_legacy_formula()
        {
            // (168 * 25 / (8 * 12.5)) * 100 = 4200
            Assert.Equal(4200.0, PStateMath.FrequencyMhz(168, 8));
        }

        [Fact]
        public void FrequencyMhz_returns_zero_when_did_zero()
        {
            Assert.Equal(0.0, PStateMath.FrequencyMhz(168, 0));
        }
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test Tests/ProfileCore.Tests/ProfileCore.Tests.csproj`
Expected: PASS (4 new tests; total was 60 → 64). If the test assembly fails to compile, fix the `<Compile Include>` paths from Step 2.

- [ ] **Step 5: Commit**

```bash
git add Utils/PStateMath.cs ZenStatesDebugTool.csproj Tests/ProfileCore.Tests/ProfileCore.Tests.csproj Tests/ProfileCore.Tests/PStateMathTests.cs
git commit -m "refactor: extract pure PStateMath (decode/encode/frequency) with tests"
```
End the commit body with:
Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>

---

## Task 2: Rewire SettingsForm P-state sites to `PStateMath`

**Files:**
- Modify: `SettingsForm.cs` (`BtnPstateRead_Click`, `BtnPstateWrite_Click`, `PstateFidDid_KeyUp`, delete `CalculatePstateDetails`)

This task is build-verified + on-hardware smoke (these handlers aren't unit-tested). Locate methods by name.

- [ ] **Step 1: Rewire `BtnPstateRead_Click`**

Replace the block that declares the five `uint Idd…/Cpu…` locals, calls `CalculatePstateDetails`, and sets the text boxes:

```csharp
            uint IddDiv = 0x0;
            uint IddVal = 0x0;
            uint CpuVid = 0x0;
            uint CpuDfsId = 0x0;
            uint CpuFid = 0x0;

            CalculatePstateDetails(eax, ref IddDiv, ref IddVal, ref CpuVid, ref CpuDfsId, ref CpuFid);

            pstateDid.Text = Convert.ToString(CpuDfsId, 10);
            pstateFid.Text = Convert.ToString(CpuFid, 10);
            pstateFrequency.Text = (CpuFid * 25 / (CpuDfsId * 12.5)) * 100 + "MHz";
```

with:

```csharp
            PStateValues pv = PStateMath.Decode(eax);

            pstateDid.Text = Convert.ToString(pv.CpuDfsId, 10);
            pstateFid.Text = Convert.ToString(pv.CpuFid, 10);
            pstateFrequency.Text = PStateMath.FrequencyMhz(pv.CpuFid, pv.CpuDfsId) + "MHz";
```

- [ ] **Step 2: Rewire `BtnPstateWrite_Click`**

Replace the five-local declaration + `CalculatePstateDetails` call + the `eax = (IddDiv & 0xFF) << 30 | …` assembly:

```csharp
            uint IddDiv = 0x0;
            uint IddVal = 0x0;
            uint CpuVid = 0x0;
            uint CpuDfsId = 0x0;
            uint CpuFid = 0x0;

            uint readEax = 0, readEdx = 0;
            bool pstateReadOk = Hardware.Locked(() => cpu.ReadMsr(Convert.ToUInt32(Convert.ToInt64(0xC0010064) + pstateId), ref readEax, ref readEdx));
            eax = readEax; edx = readEdx;
            if (!pstateReadOk)
            {
                SetStatusText($@"Error reading PState {pstateId}!");
                return;
            }

            CalculatePstateDetails(eax, ref IddDiv, ref IddVal, ref CpuVid, ref CpuDfsId, ref CpuFid);

            eax = (IddDiv & 0xFF) << 30 | (IddVal & 0xFF) << 22 | (CpuVid & 0xFF) << 14 | (uint.Parse(pstateDid.Text) & 0xFF) << 8 | uint.Parse(pstateFid.Text) & 0xFF;
```

with (preserves the read, the IddDiv/IddVal/CpuVid taken from the current MSR, and the user-entered DID/FID):

```csharp
            uint readEax = 0, readEdx = 0;
            bool pstateReadOk = Hardware.Locked(() => cpu.ReadMsr(Convert.ToUInt32(Convert.ToInt64(0xC0010064) + pstateId), ref readEax, ref readEdx));
            eax = readEax; edx = readEdx;
            if (!pstateReadOk)
            {
                SetStatusText($@"Error reading PState {pstateId}!");
                return;
            }

            PStateValues pv = PStateMath.Decode(eax);
            eax = PStateMath.Encode(pv.IddDiv, pv.IddVal, pv.CpuVid,
                uint.Parse(pstateDid.Text), uint.Parse(pstateFid.Text));
```

(Note: the five `uint IddDiv … CpuFid` locals declared earlier in the method, before the read, are now unused — delete those five declaration lines too.)

- [ ] **Step 3: Rewire `PstateFidDid_KeyUp`**

Replace:

```csharp
            pstateFrequency.Text = (fid * 25 / (did * 12.5)) * 100 + "MHz";
```

with:

```csharp
            pstateFrequency.Text = PStateMath.FrequencyMhz((uint)fid, (uint)did) + "MHz";
```

(The existing `int fid`/`int did` locals and the `if (did == 0) did = 1;` guard stay as-is; `FrequencyMhz` also guards did==0, so behavior is unchanged.)

- [ ] **Step 4: Delete the now-unused `CalculatePstateDetails`**

Remove the whole method:

```csharp
        public static void CalculatePstateDetails(uint eax, ref uint IddDiv, ref uint IddVal, ref uint CpuVid, ref uint CpuDfsId, ref uint CpuFid)
        {
            IddDiv = eax >> 30;
            IddVal = eax >> 22 & 0xFF;
            CpuVid = eax >> 14 & 0xFF;
            CpuDfsId = eax >> 8 & 0x3F;
            CpuFid = eax & 0xFF;
        }
```

Then confirm there are no remaining references: `grep -n "CalculatePstateDetails" SettingsForm.cs` should return nothing.

- [ ] **Step 5: Build + test**

Run: `dotnet build ZenStatesDebugTool.csproj -c Debug`
Expected: Build succeeded (if `bin/Debug` exe copy is locked, close the running app first).
Run: `dotnet test Tests/ProfileCore.Tests/ProfileCore.Tests.csproj`
Expected: 64 passed.

- [ ] **Step 6: Commit**

```bash
git add SettingsForm.cs
git commit -m "refactor: use PStateMath in SettingsForm; drop CalculatePstateDetails"
```
End the commit body with:
Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>

- [ ] **Step 7: On-hardware smoke test (manual)**

On a Ryzen machine: P-state tab → select a P-state → **Read** shows the same DID/FID/frequency as before; typing DID/FID updates the frequency label; **Write** still applies (verify against expectation). No behavior change vs. v1.44.4.

---

## Task 3: Create `CurveShaperCodec` (pure) + tests

**Files:**
- Create: `Utils/CurveShaperCodec.cs`
- Modify: `ZenStatesDebugTool.csproj`, `Tests/ProfileCore.Tests/ProfileCore.Tests.csproj`
- Test: `Tests/ProfileCore.Tests/CurveShaperCodecTests.cs`

Preserves the existing logic from `SettingsForm.cs`:
- `ConvertMarginToInt(uint value) => (sbyte)(unchecked(value));`
- `ApplyCurveShaperValues` decode: `ConvertMarginToInt(csValues[tier] >> 8 & 0xFF)` (low), `>> 16 & 0xFF` (med), `>> 24 & 0xFF` (high).
- `PackCurveShaperTier(low, med, high) => ((uint)(byte)(sbyte)low << 8) | ((uint)(byte)(sbyte)med << 16) | ((uint)(byte)(sbyte)high << 24);`
- `IsAllZero(uint[])`.

- [ ] **Step 1: Create `Utils/CurveShaperCodec.cs`**

```csharp
namespace ZenStatesDebugTool
{
    // Pure Curve Shaper margin packing/unpacking, extracted verbatim from SettingsForm.
    // A tier word packs three signed-byte margins: low in bits 8-15, med 16-23, high 24-31.
    public static class CurveShaperCodec
    {
        // One margin byte (0-255) -> signed margin (-128..127). Matches the old
        // ConvertMarginToInt; the unchecked cast is required (default context narrows uint->sbyte).
        public static int UnpackMargin(uint value) => (sbyte)(unchecked(value));

        public static void Unpack(uint tierWord, out int low, out int med, out int high)
        {
            low = UnpackMargin(tierWord >> 8 & 0xFF);
            med = UnpackMargin(tierWord >> 16 & 0xFF);
            high = UnpackMargin(tierWord >> 24 & 0xFF);
        }

        public static uint Pack(int low, int med, int high)
        {
            return ((uint)(byte)(sbyte)low << 8)
                 | ((uint)(byte)(sbyte)med << 16)
                 | ((uint)(byte)(sbyte)high << 24);
        }

        public static bool IsAllZero(uint[] values)
        {
            if (values == null) return true;
            foreach (uint v in values)
                if (v != 0) return false;
            return true;
        }
    }
}
```

- [ ] **Step 2: Register the file in both csprojs**

`ZenStatesDebugTool.csproj` (after the `PStateMath.cs` entry):

```xml
    <Compile Include="Utils\CurveShaperCodec.cs" />
```

`Tests/ProfileCore.Tests/ProfileCore.Tests.csproj` (Utils group):

```xml
    <Compile Include="..\..\Utils\CurveShaperCodec.cs" Link="CurveShaperCodec.cs" />
```

- [ ] **Step 3: Write the failing tests**

Create `Tests/ProfileCore.Tests/CurveShaperCodecTests.cs`:

```csharp
using ZenStatesDebugTool;
using Xunit;

namespace ProfileCore.Tests
{
    public class CurveShaperCodecTests
    {
        [Theory]
        [InlineData(0u, 0)]
        [InlineData(5u, 5)]
        [InlineData(0xFFu, -1)]   // 255 -> -1
        [InlineData(0x80u, -128)] // 128 -> -128
        [InlineData(0x7Fu, 127)]
        public void UnpackMargin_treats_byte_as_signed(uint raw, int expected)
        {
            Assert.Equal(expected, CurveShaperCodec.UnpackMargin(raw));
        }

        [Fact]
        public void Pack_then_Unpack_round_trips_including_negatives()
        {
            uint word = CurveShaperCodec.Pack(-5, 10, -30);
            CurveShaperCodec.Unpack(word, out int low, out int med, out int high);
            Assert.Equal(-5, low);
            Assert.Equal(10, med);
            Assert.Equal(-30, high);
        }

        [Fact]
        public void Pack_places_margins_in_bits_8_16_24()
        {
            // low=1, med=2, high=3 -> 0x03020100
            Assert.Equal(0x03020100u, CurveShaperCodec.Pack(1, 2, 3));
        }

        [Fact]
        public void IsAllZero_true_for_null_and_zeros_false_otherwise()
        {
            Assert.True(CurveShaperCodec.IsAllZero(null));
            Assert.True(CurveShaperCodec.IsAllZero(new uint[] { 0, 0, 0 }));
            Assert.False(CurveShaperCodec.IsAllZero(new uint[] { 0, 1, 0 }));
        }
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test Tests/ProfileCore.Tests/ProfileCore.Tests.csproj`
Expected: PASS (8 new tests; total 64 → 72).

- [ ] **Step 5: Commit**

```bash
git add Utils/CurveShaperCodec.cs ZenStatesDebugTool.csproj Tests/ProfileCore.Tests/ProfileCore.Tests.csproj Tests/ProfileCore.Tests/CurveShaperCodecTests.cs
git commit -m "refactor: extract pure CurveShaperCodec (pack/unpack) with tests"
```
End the commit body with:
Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>

---

## Task 4: Rewire SettingsForm Curve Shaper sites to `CurveShaperCodec`

**Files:**
- Modify: `SettingsForm.cs` (`ApplyCurveShaperValues`, `InitCS`, `ButtonApplyCS_Click`, delete `ConvertMarginToInt`, `PackCurveShaperTier`, `IsAllZero`)

- [ ] **Step 1: Rewire `ApplyCurveShaperValues`**

Replace the decode loop body:

```csharp
                CsGrid[tier][0].Value = ConvertMarginToInt(csValues[tier] >> 8 & 0xFF);
                CsGrid[tier][1].Value = ConvertMarginToInt(csValues[tier] >> 16 & 0xFF);
                CsGrid[tier][2].Value = ConvertMarginToInt(csValues[tier] >> 24 & 0xFF);
```

with:

```csharp
                CurveShaperCodec.Unpack(csValues[tier], out int low, out int med, out int high);
                CsGrid[tier][0].Value = low;
                CsGrid[tier][1].Value = med;
                CsGrid[tier][2].Value = high;
```

- [ ] **Step 2: Rewire `InitCS` to use `CurveShaperCodec.IsAllZero`**

Replace `if (IsAllZero(hw) && _lastAppliedCurveShaper != null)` with:

```csharp
            if (CurveShaperCodec.IsAllZero(hw) && _lastAppliedCurveShaper != null)
```

- [ ] **Step 3: Rewire `ButtonApplyCS_Click` to use `CurveShaperCodec.Pack`**

Replace the cache-population loop:

```csharp
                _lastAppliedCurveShaper = new uint[5];
                for (int tier = 0; tier < 5; tier++)
                    _lastAppliedCurveShaper[tier] = PackCurveShaperTier(
                        (int)CsGrid[tier][0].Value,
                        (int)CsGrid[tier][1].Value,
                        (int)CsGrid[tier][2].Value);
```

with:

```csharp
                _lastAppliedCurveShaper = new uint[5];
                for (int tier = 0; tier < 5; tier++)
                    _lastAppliedCurveShaper[tier] = CurveShaperCodec.Pack(
                        (int)CsGrid[tier][0].Value,
                        (int)CsGrid[tier][1].Value,
                        (int)CsGrid[tier][2].Value);
```

- [ ] **Step 4: Delete the now-unused private helpers**

Remove these three methods from `SettingsForm.cs`:

```csharp
        private static int ConvertMarginToInt(uint value)
        {
            return (sbyte)(unchecked(value));
        }
```

```csharp
        private static bool IsAllZero(uint[] values)
        {
            if (values == null) return true;
            foreach (uint v in values)
                if (v != 0) return false;
            return true;
        }
```

```csharp
        // Packs one tier's low/med/high margins into the GetAllCurveShaperMargins layout.
        private static uint PackCurveShaperTier(int low, int med, int high)
        {
            return ((uint)(byte)(sbyte)low << 8)
                 | ((uint)(byte)(sbyte)med << 16)
                 | ((uint)(byte)(sbyte)high << 24);
        }
```

Then confirm no stragglers: `grep -nE "ConvertMarginToInt|PackCurveShaperTier|\bIsAllZero\b" SettingsForm.cs` returns nothing.

- [ ] **Step 5: Build + test**

Run: `dotnet build ZenStatesDebugTool.csproj -c Debug`
Expected: Build succeeded.
Run: `dotnet test Tests/ProfileCore.Tests/ProfileCore.Tests.csproj`
Expected: 72 passed.

- [ ] **Step 6: Commit**

```bash
git add SettingsForm.cs
git commit -m "refactor: use CurveShaperCodec in SettingsForm; drop inline pack/unpack helpers"
```
End the commit body with:
Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>

- [ ] **Step 7: On-hardware smoke test (manual)**

Curve Shaper tab → enter margins → **Apply** → **Refresh** shows the same values (the v1.44.3 last-applied fallback still works); values decode/encode identically to v1.44.4.

---

## Task 5: Final verification

**Files:** none.

- [ ] **Step 1: Full build (both configs) + tests**

Run: `dotnet build ZenStatesDebugTool.csproj -c Debug` → succeeded.
Run: `dotnet build ZenStatesDebugTool.csproj -c Release` → succeeded (close the app if the exe is locked).
Run: `dotnet test Tests/ProfileCore.Tests/ProfileCore.Tests.csproj` → 72 passed.

- [ ] **Step 2: Confirm SettingsForm shrank and no behavior changed**

`wc -l SettingsForm.cs` (≈ 40-50 lines smaller). `git diff master --stat`. Confirm the diff only moves math into the new classes + rewires call sites (no logic changes).

---

## Subsequent phases (separate follow-on plans, NOT in this plan)

These are the rest of the A-06 god-class reduction. Each should get its own spec/plan after Phase 1 lands, because they touch hardware-write paths and need per-method detail derived at execution time:

- **Phase 2 — `HardwareService` facade.** Wrap `cpu` + `Hardware.Sync` behind intent-revealing, data-returning methods (`ScanMsrRange()→uint[]`, `SetPboLimits(...)`, `ReadCurveShaper()`, …); the form formats/renders. Centralizes locking; biggest structural reduction. Do one register group at a time (MSR → PCI → CPUID → PBO/CO/CS), build + smoke each.
- **Phase 3 — Partial-class UI split.** Move the `Build*`/`Make*` layout methods into `SettingsForm.Layout.cs` (and `.Profiles.cs`, `.Hardware.cs`) partials — zero behavior change, pure readability/line-count win.
- **Phase 4 — `ReportService` + WMI helper.** Extract `GenerateReportJson` and the WMI command logic.

---

## Self-Review notes (author)

- **Scope:** Phase 1 only (pure math). Phases 2-4 explicitly deferred to their own plans (scope-check).
- **Behavior preservation:** every extracted expression is copied verbatim (decode shifts/masks, the `(fid*25/(did*12.5))*100` formula, the `(sbyte)(unchecked(value))` cast, the pack shifts). The only added behavior is `FrequencyMhz`'s `did==0 → 0` guard, which can't change existing call sites (`BtnPstateRead` only computes after a successful read with a real DID; `PstateFidDid_KeyUp` already forced `did=1`).
- **Test isolation:** both new files are ZenStates/WinForms-free and linked into the test project; no `SmuDecodeAdapter`-style hardware coupling.
- **Type consistency:** `PStateValues`/`PStateMath.Decode/Encode/FrequencyMhz` and `CurveShaperCodec.UnpackMargin/Unpack/Pack/IsAllZero` are used with identical signatures across tasks.
- **Counts:** test total goes 60 → 64 (Task 1) → 72 (Task 3); referenced consistently.
