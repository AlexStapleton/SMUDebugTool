# Performance Analysis — SMUDebugTool

Tracking document for performance findings and remediation progress.

**Context:** WinForms tool that talks to the AMD Ryzen SMU through a kernel IO driver
(`ReadDword`/`WriteDword`/`ReadMsr`) and WMI. The dominant cost across the program is
**hardware IO calls**. The recurring problems are: IO run **synchronously on the UI
thread**, often in **tight polling loops**, against **unbounded-growth** data structures.

Status legend: `[ ]` todo · `[~]` in progress · `[x]` done

**Progress (2026-06-22):** Done — #1, #3, #4, #5, #7, #8, #9, #10. Partial — #2 (list
capped; UI-thread reads remain). Deferred — #6 (needs on-hardware testing, see below).
Build clean, all 13 tests pass.

---

## 🔴 Critical

### [x] 1. `PCIRangeMonitor.RefreshData` — O(n²), 2 threads/tick, full re-read every 500 ms, cross-thread UI access
**DONE:** Rewrote `PCIRangeMonitor.cs`. Addresses precomputed once into `uint[]`; grid rows
are index-aligned, so a refresh is a single O(n) positional pass (no `FirstOrDefault`
string match, no `IndexOf`). One background thread per tick (nested join-thread removed),
guarded by `Interlocked` so a slow read can't overlap the next tick. All UI mutation —
including the row `BackColor` — now happens inside a single `BeginInvoke`.
**Files:** `PCIRangeMonitor.cs:25-82`

Every 500 ms tick:
- Spawns a new `Thread` (`:57`), which calls `RefreshList()` that spawns *another* `Thread`
  and immediately `.Join()`s it (`:48-51`) — inner thread adds pure overhead.
- Re-reads the **entire** address range via `ReadDwordEx` every tick (`:30-47`) — no
  change detection at the IO layer.
- Matches old vs. new with `l.FirstOrDefault(x => x.Address == item.Address)` (`:63`) — a
  linear **string** scan per item → **O(n²)** — plus `list.IndexOf(item)` (`:69`), another O(n).
- Writes `dataGridViewPCIRange.Rows[rowIndex].DefaultCellStyle.BackColor` **from the
  background thread without `Invoke`** (`:70`) — cross-thread UI access (latent crash);
  only the final `Refresh()` is marshalled.

**Fix:** read into a `uint[]`, index rows positionally (no string match, no `IndexOf`),
drop the nested threads, do all UI mutation inside one `Invoke`. Turns a multi-thread
O(n²) churn into a single O(n) pass. **Highest-value change in the codebase.**

### [~] 2. `SMUMonitor` — 10 ms UI-thread polling + unbounded list
**PARTIAL:** List is now capped at `MaxRows = 2000` (oldest rows trimmed), fixing the
unbounded memory/grid growth. Kept the 10 ms interval (the monitor must catch every SMU
command). Still outstanding: reads remain on the UI thread, so a driver stall can still
freeze the window — left for the structural #6-style pass.

**Files:** `SMUMonitor.cs:33-76`

`MonitorTimer.Interval = 10` (100×/sec). Each tick runs 2–3 synchronous `CPU.ReadDword`
IO-driver calls **on the UI thread** (`:51-61`). On change, appends to `BindingList list`
which is **never trimmed** (`:64`) and forces a scroll every time. Steady UI-thread IO
load + unbounded memory + ever-slower grid.

**Fix:** cap the list (ring buffer / trim oldest); consider 25–50 ms interval. Reading on
the UI thread means any driver stall freezes the window.

### [x] 3. `MemoryDumper` blocks the UI thread and writes 4 bytes at a time
**DONE:** `MemoryDumper` core is now synchronous + buffered — DWORDs pack into a 64 KB
buffer and flush in bulk (no more per-DWORD `fs.Write`); the pointless internal
thread+`Join` was removed. `ButtonDump_Click` is now `async` and runs the dump via
`Task.Run`, with the button disabled during the dump, so the UI no longer freezes. IO
failures now report a real message instead of the misleading "Invalid address format!".

**Files:** `MemoryDumper.cs`, called at `SettingsForm.cs:ButtonDump_Click`

Spawns a thread then immediately `thread.Join()`s (`:70-71`) — work runs off-thread but
the **UI thread blocks** for the entire dump. Over a large range
(`0xC0000000–0xFFFFFFFF` ≈ 1 billion DWORDs) the window is frozen the whole time. Also
`fs.Write(bytes, 0, 4)` **per DWORD** — hundreds of millions of 4-byte writes.

**Fix:** run on `BackgroundWorker`/`Task` with progress (don't `Join` on the UI thread);
accumulate into a large byte buffer (e.g. 64 KB) and flush in bulk.

---

## 🟠 High

### [x] 4. `textBoxResult` prepend pattern is O(n) per write and never trimmed
**DONE:** Added a `PrependResult(string)` helper that prepends (preserving newest-on-top)
and caps total length at `MaxResultLength = 100_000`, so writes stay O(cap) and the log
can't grow unbounded. Routed all the scattered `textBoxResult.Text = … + textBoxResult.Text`
and `textBoxResult.Text += …` sites through it (the one-time constructor init left as-is).

**Files:** `SettingsForm.cs` (PrependResult + ~9 call sites)

`textBoxResult.Text = newText + textBoxResult.Text` reads the entire existing text,
concatenates, and reassigns — each call O(total length) and re-renders the whole control.
Log grows unbounded for process lifetime, so each write gets progressively slower.

**Fix:** use `AppendText` (append, not prepend) or a capped log; cap total length.

### [x] 5. String concatenation in scan loops — O(n²) result building
**DONE:** All three scan loops now build their output with `StringBuilder` / `AppendLine`
(added `using System.Text;`).

**Files:** `SettingsForm.cs:1792` (PciScan_DoWork), `:1889` (ReadMsr_Task), `:1961` (ReadCPUID_Task)

`result += $"..."` inside loops over a register range builds a huge string by repeated
concatenation. Quadratic in the number of registers scanned.

**Fix:** `StringBuilder`.

### [ ] 6. Startup does large amounts of synchronous hardware IO + WMI on the UI thread
**DEFERRED — needs on-hardware testing.** `new Cpu()` must run before any topology-dependent
UI build (`PopulateCCDList`, `InitCoreControl`, `BuildCcdBlocks`, `InitPBO`, `InitCS`), so a
naive "show window first" doesn't work — the refactor must split UI-construction from
value-population and marshal a background load back via `Invoke`, and the WMI path
(`PopulateWmiFunctions` + `ComboBoxAvailableCommands_SelectedIndexChanged`) is entangled.
A startup-ordering or cross-thread regression here would NOT be caught by the build or the
profile unit tests, and there's no Ryzen SMU in the dev environment to verify against.
**Plan:** (1) keep UI-layout build on the UI thread; (2) move the value reads
(`GetAllCurveShaperMargins`, per-core `GetPsmMarginSingleCore`, `GetBclk`,
`IsProchotEnabled`, `GetFMax`, `GetCoreMulti`) and `PopulateWmiFunctions` into a background
load fired from `Shown`, writing results back via `Invoke` with a "loading…" state;
(3) verify interactively on real hardware. Best done in a session where the app can be run.

**Files:** `SettingsForm.cs:51-202` (constructor / InitForm)

`InitForm` serially calls `GetAllCurveShaperMargins`, `GetPsmMarginSingleCore` **per core**
(`InitPBO:328-341`), `GetBclk`, `IsProchotEnabled`, `GetFMax`, `GetCoreMulti`, plus
`PopulateWmiFunctions` (WMI is slow). All blocking, all before the window is responsive →
slow, janky launch that scales with core count. Main contributor to startup latency.

**Fix:** show the window first, then populate via a background load / `async` with a
"loading…" state.

---

## 🟡 Medium

### [x] 7. `PowerTableMonitor.RefreshData` re-parses a formatted string every row every tick
**DONE:** Added an index-aligned `float[] maxes`; the refresh now compares against the
float max and only reformats the `Max` string when it actually grows — no more
`float.TryParse` of the formatted string per row per tick. The per-tick `Console.WriteLine`
was also removed (#9). The blanket `dataGridView1.Refresh()` was kept (only visible rows
repaint; switching to `INotifyPropertyChanged` would be a larger, riskier change).

**Files:** `PowerTableMonitor.cs:39-69`

`Max` is stored as a **formatted string** and `float.TryParse`d back every refresh
(`:52-57`) just to compare. `dataGridView1.Refresh()` forces a full repaint every tick
(`:68`); `Console.WriteLine("refreshing")` runs each tick (`:73`).

**Fix:** keep a `float[] maxes` alongside the list (compare floats, format only for
display); rely on `BindingList` change notifications instead of a blanket `Refresh()`.

### [x] 8. `Controls.Find($"checkBox{i}", true)` in loops
**DONE:** Added `coreCheckBoxes` dictionary + `GetCoreCheckBox(int)` helper that caches the
recursive lookup (runs at most once per core). All three call sites now use it; also made
them null-safe (the old `[0]` indexer would throw on a missing control).

**Files:** `SettingsForm.cs:261` (InitCoreControl), `:2600`, `:2613` (ButtonApplyCoreMap)

Recursive name search over the whole control tree, once per core. Cache the checkboxes in
a `Dictionary<int, CheckBox>` like the existing `coControls`/`freqControls`.

### [x] 9. `Console.WriteLine` in hot/per-status paths
**DONE:** `SetStatusText` Console write is now wrapped in `#if DEBUG`; removed the
per-tick `"refreshing"` write in `PowerTableMonitor`; the `PCIRangeMonitor` per-tick write
was dropped as part of the #1 rewrite.

**Files:** `SettingsForm.cs:992` (SetStatusText) and the monitors

Cheap individually, but in 10 ms / 500 ms loops it adds up and can block if stdout is
redirected. Gate behind `#if DEBUG` or a verbosity flag.

### [x] 10. `NUMAUtil.HighestNumaNode` P/Invokes on every access
**DONE:** `HighestNumaNode` now lazily caches the P/Invoke result in a nullable backing
field (`_highestNumaNode`); the value is fixed for the process lifetime, so it's queried
at most once.

**Files:** `NUMAUtil.cs:8-16`; evaluated repeatedly in `SettingsForm.cs:1723-1727`
(BtnPstateWrite_Click) and `:1756` (WritePstateClick)

---

## Effort summary

| Effort | Items |
|---|---|
| **Low** | #5 (StringBuilder), #10 (cache NUMA), #8 (cache checkboxes), #9 (gate Console), #2 (cap monitor lists) |
| **Medium** | #1 (rewrite PCIRangeMonitor), #7 (PowerTableMonitor float-max array), #4 (AppendText/capped log) |
| **Higher** | #6 (startup IO/WMI off UI thread), #3 (MemoryDumper async + buffered) |

**Suggested order:** start with #1 (only genuinely O(n²) *and* multi-threaded *and*
thread-unsafe path), then the low-effort batch (#5, #8, #9, #10), then #2/#4/#7, then the
structural #6/#3. Verify with `dotnet build` after each change.
