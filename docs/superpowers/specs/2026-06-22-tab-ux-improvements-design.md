# Tab UX Improvements — Design

**Date:** 2026-06-22
**Status:** Approved

## Problem

1. **Duplicated frequency-OC features.** Manual frequency overclocking exists in two places:
   - **CPU tab** — multiplier-based: *All-Core Frequency* (dropdown x5.50–x70) + Apply, and
     *Single-Core Frequency* (multiplier dropdown + core selector) + Apply. Uses
     `SetFrequencyAllCore` / `SetFrequencySingleCore`. Setting a single core in OC mode
     strands the rest at the SMU default (~2500 MHz).
   - **Freq (OC) tab** — per-core MHz fields, bulk All/CCD setters, "Apply all cores"
     (writes every core so none strand), Disable OC Mode. Newer and more robust.
2. **Too many tabs (11).** CPU, SMU, PCI, MSR, CPUID, PBO, Curve Shaper, AMD ACPI, PStates,
   Info, Freq (OC). No way for a user to hide the ones they don't use or pick a startup tab.

## Goals

- Remove the duplicated frequency-OC controls; make the Freq (OC) tab the single home for
  manual frequency OC.
- Let the user show/hide individual tabs from a menu, persisted across runs.
- Let the user choose which tab is selected on startup, persisted across runs.

## Non-goals

- No change to the Freq (OC) tab's behavior. No change to any other tab's features.
- No reorganization of tabs beyond show/hide. No theming/skinning.

## Part 1 — CPU tab cleanup

**Remove** from `tableLayoutPanel8` (CPU tab) in the designer: `label14`, `comboBoxACF`,
`buttonApplyAC`, `label16`, `comboBoxSCF`, `comboBoxCore`, `buttonApplySC` (declarations,
instantiation, property setup, `Controls.Add`, field declarations, event wireups). Reflow
the panel so PROCHOT (`checkBoxPROCHOT` + `buttonApplyPROCHOT`) sits at the top.

**Keep on CPU tab:** Core Control group (enable/disable map, SMT, X3D) and PROCHOT.

**Delete now-dead code in `SettingsForm.cs`:**
- Handlers `ButtonApplyAC_Click`, `ButtonApplySC_Click`.
- Helpers `ApplyFrequencyAllCoreSetting`, `ApplyFrequencySingleCoreSetting`,
  `PopulateFrequencyList`, `PopulateCCDList`.
- `InitForm` calls that populate `comboBoxACF`/`comboBoxSCF`/`comboBoxCore` and
  `comboBoxCore.SelectedIndex = 0`.
- `ApplyCurrentMulti` and the `cpu.GetCoreMulti()` read in `StartHardwareLoad` (these existed
  only to preselect the now-removed multiplier combos — a welcome trim of the #6 loader).

**Delete now-unused types:** `Utils/FrequencyListItem.cs`, `Utils/CoreListItem.cs` (referenced
only by the removed controls).

The Freq (OC) tab is unchanged and becomes the sole manual-OC surface.

## Part 2 — Tab visibility + default tab

### UI

A `MenuStrip` docked to the top of the form (built in code, matching the app's existing
pattern of constructing UI in code), with a single **View** menu:
- A checkable item per tab (label = tab title; ✓ = visible). Toggling shows/hides that tab
  and persists immediately.
- A separator, then a **Default Tab ▸** submenu: one radio-checked item per tab; selecting
  sets the startup tab and persists.

### Components

- **`UiSettings`** (data): `HashSet<string> HiddenTabs`, `string DefaultTabKey`. Plain
  serializable POCO.
- **`UiSettingsManager`** (`Utils/`): `Load()` / `Save(UiSettings)` to `settings.json` in
  `AppDomain.CurrentDomain.BaseDirectory` via Newtonsoft. `Load()` returns a default instance
  (nothing hidden, no default) when the file is missing or unparseable — never throws on read.
- **`TabVisibilityController`**: constructed with the `TabControl` and the canonical ordered
  list of `(key, TabPage, title)`. Responsibilities:
  - Apply visibility: rebuild `tabControl1.TabPages` to contain only non-hidden tabs in
    canonical order (stable regardless of toggle order).
  - Build/refresh the View menu items and Default Tab submenu.
  - Resolve and select the startup tab.
  - Persist via `UiSettingsManager` on every change.
  - Pure resolution logic exposed as static, testable methods:
    - `OrderedVisibleKeys(canonical, hidden)` → ordered visible keys.
    - `ResolveStartupTabKey(canonical, hidden, storedDefault)` → the key to select on startup
      (stored default if visible; else first visible; else null).

### Keys

Each tab has a stable string key = `TabPage.Name` (e.g. `tabPageCPU`). The dynamically built
Freq (OC) tab is given `Name = "tabPageFreqOC"`. Keys are independent of tab index.

### Persistence model

Store **hidden** keys (not visible keys) so tabs introduced in future versions default to
visible. Store the default key as a string. Unknown keys in the file (renamed/removed tabs)
are ignored.

### Rules / edge cases

- **Last visible tab:** unchecking the only remaining visible tab is rejected — the item
  stays checked and a status hint is shown ("At least one tab must stay visible.").
- **Startup default hidden:** select the first visible tab; keep the stored default key so it
  is honored again if the tab is re-shown.
- **No default set / first run:** all tabs visible; startup selects the first tab (CPU).
- **Info tab:** its existing result-panel-collapse behavior (`TabControl1_Selected`) is
  preserved — the controller only adds/removes pages and sets the selected tab.

### Wiring

After all tabs are constructed (including the dynamic Freq (OC) tab in `BuildFrequencyTab`),
`SettingsForm` builds the menu, constructs the `TabVisibilityController` with the canonical
list, loads settings, applies visibility, builds the View menu, and selects the startup tab.

## Testing

- **Unit (xUnit, existing `ProfileCore.Tests` project):**
  - `UiSettingsManager`: round-trip save/load; missing file → defaults; corrupt file →
    defaults; hidden set and default key persist.
  - `TabVisibilityController` static resolvers: `OrderedVisibleKeys` preserves canonical order
    and excludes hidden; `ResolveStartupTabKey` handles visible default, hidden default
    (→ first visible), empty/unknown default, and all-hidden (→ null).
- **Manual:** toggle tabs and restart → visibility persists; set a default and restart →
  starts on it; hide the default tab → starts on first visible; try to hide the last tab →
  rejected with hint; confirm CPU tab no longer shows the old frequency controls and Freq (OC)
  still works.

## Persistence file example (`settings.json`)

```json
{
  "HiddenTabs": ["tabPageCPUID", "tabPagePci"],
  "DefaultTabKey": "tabPagePbo"
}
```
