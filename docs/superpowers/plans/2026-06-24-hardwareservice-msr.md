# HardwareService Facade — MSR Slice (Phase 2a) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Introduce a `HardwareService` facade that owns `Hardware.Sync` locking and CPU access, and move the MSR read/write/scan paths in `SettingsForm` onto it — with zero behavior change.

**Architecture:** First slice of the Phase 2 god-class reduction. `HardwareService` wraps the form's `Cpu` and exposes intent-revealing methods that return **data** (not formatted strings); the form gathers inputs and renders/decodes. MSR only this slice; PCI/CPUID/OC/PBO follow the same pattern in later slices.

**Tech Stack:** C# (net48), WinForms, ZenStates-Core, xUnit (existing suite, unchanged).

**Reference spec:** `docs/superpowers/specs/2026-06-24-hardwareservice-msr-design.md`

**Branch & build:** Already on `feature/hardwareservice-msr` (off `master`). Build/test with `dotnet` (NOT VS MSBuild). `HardwareService` wraps `cpu`, so it is **not unit-testable** — this slice is verified by **clean build + on-hardware smoke**; the existing 72 tests must still pass (no pure logic moves). If `bin/Debug/SMUDebugTool.exe` is locked, close the running app before a Debug build.

**Conventions:**
- `Utils/` files use the flat `ZenStatesDebugTool` namespace.
- App csproj (`ZenStatesDebugTool.csproj`) is old-style: new `.cs` needs a `<Compile Include>`. `HardwareService.cs` references `ZenStates.Core`, so it is app-only — do NOT link it into the test project.
- Behavior must be identical; all extracted expressions are preserved verbatim.

---

## File Structure

**New (app-only — references `ZenStates.Core`):**
- `Utils/HardwareService.cs` — `MsrReadResult` struct, `MsrReading` struct, and `HardwareService` (ctor + `ReadMsr`/`WriteMsr`/`ScanMsrRange`). Owns `Hardware.Sync`/`Hardware.Locked`.

**Modified:**
- `ZenStatesDebugTool.csproj` — register the new file.
- `SettingsForm.cs` — add a `HardwareService` field (built next to `cpu`); rewire `ButtonMsrRead_Click`, `ButtonMsrWrite_Click`, `ButtonMsrScan_Click`, `ReadMsr_Task`.

---

## Task 1: Create `HardwareService` (MSR slice)

**Files:**
- Create: `Utils/HardwareService.cs`
- Modify: `ZenStatesDebugTool.csproj`

This file is hardware-coupled (wraps `cpu`), so it is build-verified only — no unit test.

- [ ] **Step 1: Create `Utils/HardwareService.cs`**

```csharp
using System.Collections.Generic;
using ZenStates.Core;

namespace ZenStatesDebugTool
{
    // Result of a single MSR read.
    public struct MsrReadResult
    {
        public bool Ok;
        public uint Eax;
        public uint Edx;
        public MsrReadResult(bool ok, uint eax, uint edx) { Ok = ok; Eax = eax; Edx = edx; }
    }

    // One successful row of an MSR range scan.
    public struct MsrReading
    {
        public uint Address;
        public uint Eax;
        public uint Edx;
        public MsrReading(uint address, uint eax, uint edx) { Address = address; Eax = eax; Edx = edx; }
    }

    // Facade over the CPU/SMU hardware that owns the Hardware.Sync locking, so callers
    // gather inputs and render output without touching cpu/Hardware directly. This is the
    // MSR slice (Phase 2a); PCI/CPUID/OC/PBO are added in later slices.
    public sealed class HardwareService
    {
        private readonly Cpu cpu;

        public HardwareService(Cpu cpu)
        {
            this.cpu = cpu;
        }

        public MsrReadResult ReadMsr(uint msr)
        {
            uint eax = 0, edx = 0;
            bool ok = Hardware.Locked(() => cpu.ReadMsr(msr, ref eax, ref edx));
            return new MsrReadResult(ok, eax, edx);
        }

        public bool WriteMsr(uint msr, uint eax, uint edx)
        {
            return Hardware.Locked(() => cpu.WriteMsr(msr, eax, edx));
        }

        // Reads start..end inclusive under a single lock; returns only successful reads.
        // Preserves the original scan loop semantics (while msr <= end; msr++).
        public List<MsrReading> ScanMsrRange(uint start, uint end)
        {
            var readings = new List<MsrReading>();
            lock (Hardware.Sync)
            {
                uint msr = start;
                while (msr <= end)
                {
                    uint eax = 0, edx = 0;
                    if (cpu.ReadMsr(msr, ref eax, ref edx))
                        readings.Add(new MsrReading(msr, eax, edx));
                    msr += 1;
                }
            }
            return readings;
        }
    }
}
```

- [ ] **Step 2: Register the file in the app csproj only**

In `ZenStatesDebugTool.csproj`, after `<Compile Include="Utils\SmuDecodeAdapter.cs" />` (or any other `Utils\*` entry):

```xml
    <Compile Include="Utils\HardwareService.cs" />
```

Do NOT add it to `Tests/ProfileCore.Tests/ProfileCore.Tests.csproj` — it references `ZenStates.Core`, which the test project does not.

- [ ] **Step 3: Build**

Run: `dotnet build ZenStatesDebugTool.csproj`
Expected: Build succeeded (a single pre-existing CS0618 warning is fine). If a `ref`-in-lambda or `Hardware.Locked` overload error appears, note it — but the patterns here mirror the existing `ButtonMsrRead_Click`/`ButtonMsrScan_Click` code exactly, so it should compile.

- [ ] **Step 4: Confirm tests still pass (unchanged)**

Run: `dotnet test Tests/ProfileCore.Tests/ProfileCore.Tests.csproj`
Expected: 72 passed (no test change — this file isn't testable and nothing else moved).

- [ ] **Step 5: Commit**

```bash
git add Utils/HardwareService.cs ZenStatesDebugTool.csproj
git commit -m "feat: add HardwareService facade with MSR read/write/scan"
```
End the commit body with:
Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>

---

## Task 2: Add the field and rewire MSR single read/write

**Files:**
- Modify: `SettingsForm.cs` (field + constructor; `ButtonMsrRead_Click`, `ButtonMsrWrite_Click`)

- [ ] **Step 1: Add the `HardwareService` field**

In `SettingsForm.cs`, immediately after the line `private readonly Cpu cpu;` (the `cpu` field declaration), add:

```csharp
        private readonly HardwareService hardware;
```

- [ ] **Step 2: Construct it next to `cpu`**

In the constructor, the line `cpu = new Cpu();` exists inside the `try` block. Immediately AFTER it, add:

```csharp
                hardware = new HardwareService(cpu);
```

(Match the surrounding indentation. `hardware` is a readonly field assigned in the constructor, same as `cpu`.)

- [ ] **Step 3: Rewire `ButtonMsrRead_Click`**

Replace the whole method body:

```csharp
        private void ButtonMsrRead_Click(object sender, EventArgs e)
        {
            TryConvertToUint(textBoxMsrAddress.Text, out uint msr);
            uint eax = default, edx = default;
            uint rEax = 0, rEdx = 0;
            bool ok = Hardware.Locked(() => cpu.ReadMsr(msr, ref rEax, ref rEdx));
            eax = rEax; edx = rEdx;
            if (ok)
            {
                textBoxMsrEdx.Text = $"0x{edx:X8}";
                textBoxMsrEax.Text = $"0x{eax:X8}";

                ulong value = ((ulong)edx << 32) | eax;
                string decoded = RegisterDecoder.Decode(RegisterKind.Msr, msr, value, decodeContext);
                if (!string.IsNullOrEmpty(decoded))
                    PrependResult(decoded + Environment.NewLine);
            }
        }
```

with:

```csharp
        private void ButtonMsrRead_Click(object sender, EventArgs e)
        {
            TryConvertToUint(textBoxMsrAddress.Text, out uint msr);
            MsrReadResult r = hardware.ReadMsr(msr);
            if (r.Ok)
            {
                textBoxMsrEdx.Text = $"0x{r.Edx:X8}";
                textBoxMsrEax.Text = $"0x{r.Eax:X8}";

                ulong value = ((ulong)r.Edx << 32) | r.Eax;
                string decoded = RegisterDecoder.Decode(RegisterKind.Msr, msr, value, decodeContext);
                if (!string.IsNullOrEmpty(decoded))
                    PrependResult(decoded + Environment.NewLine);
            }
        }
```

- [ ] **Step 4: Rewire `ButtonMsrWrite_Click`**

Replace:

```csharp
            if (!Hardware.Locked(() => cpu.WriteMsr(msr, eax, edx)))
            {
                HandleError($@"Error writing MSR {textBoxMsrAddress.Text}!");
                return;
            }
```

with:

```csharp
            if (!hardware.WriteMsr(msr, eax, edx))
            {
                HandleError($@"Error writing MSR {textBoxMsrAddress.Text}!");
                return;
            }
```

(The three `TryConvertToUint(...)` lines above it and the `SetStatusText("Write OK.")` below stay unchanged.)

- [ ] **Step 5: Build + test**

Run: `dotnet build ZenStatesDebugTool.csproj -c Debug`
Expected: Build succeeded.
Run: `dotnet test Tests/ProfileCore.Tests/ProfileCore.Tests.csproj`
Expected: 72 passed.

- [ ] **Step 6: Commit**

```bash
git add SettingsForm.cs
git commit -m "refactor: route MSR read/write through HardwareService"
```
End the commit body with:
Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>

- [ ] **Step 7: On-hardware smoke (manual)**

MSR tab → Read a known MSR (e.g. `0xC0010064`): eax/edx textboxes + the decode block match v1.44.4. Write path reports OK/error as before.

---

## Task 3: Rewire the MSR scan (gather range on the UI thread)

**Files:**
- Modify: `SettingsForm.cs` (`ButtonMsrScan_Click`, `ReadMsr_Task`, two new fields)

This also fixes a latent cross-thread read: today `ReadMsr_Task` reads `textBoxMsrStart/End.Text` on the background thread. We gather them on the UI thread and pass via fields.

- [ ] **Step 1: Add scan-range fields**

In `SettingsForm.cs`, near the other MSR-related private fields (or just above `ReadMsr_Task`), add:

```csharp
        private uint _msrScanStart;
        private uint _msrScanEnd;
```

- [ ] **Step 2: Rewire `ButtonMsrScan_Click` to gather the range on the UI thread**

Replace:

```csharp
        private void ButtonMsrScan_Click(object sender, EventArgs e)
        {
            RunBackgroundTask(ReadMsr_Task, Scan_WorkerCompleted);
        }
```

with:

```csharp
        private void ButtonMsrScan_Click(object sender, EventArgs e)
        {
            try
            {
                TryConvertToUint(textBoxMsrStart.Text, out _msrScanStart);
                TryConvertToUint(textBoxMsrEnd.Text, out _msrScanEnd);
            }
            catch (ApplicationException ex)
            {
                HandleError(ex.Message);
                return;
            }
            RunBackgroundTask(ReadMsr_Task, Scan_WorkerCompleted);
        }
```

- [ ] **Step 3: Rewire `ReadMsr_Task` to use the service**

Replace the whole method:

```csharp
        private void ReadMsr_Task(object sender, DoWorkEventArgs e)
        {
            try
            {
                Invoke(new MethodInvoker(delegate
                {
                    SetStatusText("Scanning MSR range, please wait...");
                }));

                var result = new StringBuilder("MSR         EDX(63-32) EAX(31-0)" + Environment.NewLine);

                TryConvertToUint(textBoxMsrStart.Text, out uint startReg);
                TryConvertToUint(textBoxMsrEnd.Text, out uint endReg);

                lock (Hardware.Sync)
                {
                    while (startReg <= endReg)
                    {
                        uint eax = default, edx = default;
                        if (cpu.ReadMsr(startReg, ref eax, ref edx))
                        {
                            result.AppendLine($"0x{startReg:X8}: 0x{edx:X8} 0x{eax:X8}");
                            string decoded = RegisterDecoder.Decode(
                                RegisterKind.Msr, startReg, ((ulong)edx << 32) | eax, decodeContext);
                            if (!string.IsNullOrEmpty(decoded))
                                result.Append(decoded);
                        }

                        startReg += 1;
                    }
                }

                ShowResultForm("MSR Scan result", result.ToString());
            }
            catch (ApplicationException ex)
            {
                Invoke(new MethodInvoker(delegate
                {
                    SetButtonsState();
                    HandleError(ex.Message);
                }));
            }
        }
```

with:

```csharp
        private void ReadMsr_Task(object sender, DoWorkEventArgs e)
        {
            Invoke(new MethodInvoker(delegate
            {
                SetStatusText("Scanning MSR range, please wait...");
            }));

            var result = new StringBuilder("MSR         EDX(63-32) EAX(31-0)" + Environment.NewLine);

            foreach (MsrReading reading in hardware.ScanMsrRange(_msrScanStart, _msrScanEnd))
            {
                result.AppendLine($"0x{reading.Address:X8}: 0x{reading.Edx:X8} 0x{reading.Eax:X8}");
                string decoded = RegisterDecoder.Decode(
                    RegisterKind.Msr, reading.Address, ((ulong)reading.Edx << 32) | reading.Eax, decodeContext);
                if (!string.IsNullOrEmpty(decoded))
                    result.Append(decoded);
            }

            ShowResultForm("MSR Scan result", result.ToString());
        }
```

(The range parse moved to `ButtonMsrScan_Click` (UI thread), so the task no longer reads textboxes or needs the `ApplicationException` catch; `Scan_WorkerCompleted` still re-enables the buttons, and any hardware exception surfaces via the BackgroundWorker as before.)

- [ ] **Step 4: Build + test**

Run: `dotnet build ZenStatesDebugTool.csproj -c Debug`
Expected: Build succeeded.
Run: `dotnet test Tests/ProfileCore.Tests/ProfileCore.Tests.csproj`
Expected: 72 passed.

- [ ] **Step 5: Commit**

```bash
git add SettingsForm.cs
git commit -m "refactor: route MSR scan through HardwareService; gather range on UI thread"
```
End the commit body with:
Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>

- [ ] **Step 6: On-hardware smoke (manual)**

MSR tab → Scan a small range (e.g. `0xC0010064`–`0xC001006B`): the result window shows the same rows + decode as v1.44.4. A bad start/end value now shows an inline error instead of a background failure.

---

## Task 4: Final verification

**Files:** none.

- [ ] **Step 1: Builds + tests**

Run: `dotnet build ZenStatesDebugTool.csproj -c Debug` → succeeded.
Run: `dotnet build ZenStatesDebugTool.csproj -c Release` → succeeded (close the app if the exe is locked).
Run: `dotnet test Tests/ProfileCore.Tests/ProfileCore.Tests.csproj` → 72 passed.

- [ ] **Step 2: Confirm the MSR paths no longer touch cpu/Hardware directly**

`grep -nE "cpu\.ReadMsr|cpu\.WriteMsr" SettingsForm.cs` should return nothing (all MSR access now goes through `hardware`). `grep -n "hardware\." SettingsForm.cs` shows the three call sites.

- [ ] **Step 3: Confirm scope**

`git diff origin/master --stat` shows only `Utils/HardwareService.cs` (new), `ZenStatesDebugTool.csproj`, `SettingsForm.cs`, and the spec/plan docs. No other files, no version bump, no test changes.

---

## Subsequent slices (separate plans, NOT here)

Following this exact pattern, later PRs add to `HardwareService` and rewire their handlers: **PCI** (read/write/scan), **CPUID** (read/scan), **OC/PROCHOT**, **PBO/CO/CS/FMax/P-state**, and the **SMU mailbox** (`ApplySettings`/`ScanSmuRange`). Each is its own slice/spec/plan.

---

## Self-Review notes (author)

- **Spec coverage:** `HardwareService` MSR methods (Task 1); field+construction+read/write rewire (Task 2); scan rewire + UI-thread gather/threading-fix (Task 3); build+smoke verification (Task 4). All spec sections map to a task.
- **Behavior preservation:** `ReadMsr`/`WriteMsr` wrap the same `cpu` calls in `Hardware.Locked` exactly as the originals; `ScanMsrRange` preserves the `while (msr <= end); msr++` loop and the success-only collection; rendering/decoding strings are byte-identical.
- **Testability:** `HardwareService` is hardware-coupled (app-only, not test-linked), so verification is build + smoke — consistent with `SmuDecodeAdapter`. The 72 unit tests are unaffected.
- **Type consistency:** `MsrReadResult { Ok, Eax, Edx }`, `MsrReading { Address, Eax, Edx }`, and `HardwareService.ReadMsr/WriteMsr/ScanMsrRange` are used identically across tasks.
- **Threading:** scan range is now gathered on the UI thread (fields), removing the cross-thread textbox read; fields are written before `RunWorkerAsync` and read on the worker after, so there is no race.
