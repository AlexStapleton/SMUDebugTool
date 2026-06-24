# Human-readable register decoding — design

Date: 2026-06-22

## Problem

The MSR, PCI, CPUID, SMU, and PMTable views show raw hex. Reading a value
means manually splitting bits and remembering what each field means. The goal
is a "knowledge layer" that, for registers the tool recognizes, shows a
friendly register name and a labeled bit-field breakdown (including computed
values like frequency and voltage), while falling back to raw output for
anything unknown.

## Decisions (from brainstorming)

- **What "human readable" means:** decode bit-fields by name *and* label the
  register. Not just decimal/binary reformatting.
- **Coverage:** a curated Ryzen-focused set, compiled in (not an external
  JSON file).
- **Presentation:** decoded text appended inline below the existing
  HEX/INT/BIN output in the shared results pane for single reads; scan output
  decodes inline within the existing `ResultForm` popup.
- **Trigger:** automatic on every read — if the address matches a known
  register the decode is appended with no extra clicks. Unknown registers
  behave exactly as today.
- **SMU tab:** translate the numeric `Cmd` value into the SMU command name.
- **PMTable:** **add** new "Name" and "Scaled" columns; **keep** the existing
  raw `Value`/`Max` columns as reference.
- **PCI:** there is no enumerable PCI register set — the PCI tab is a generic
  SMN/PCI-config address reader. Decoding covers only a few well-known fixed
  addresses (possibly none); everything else stays raw.

## Architecture (approach A: declarative catalog + pure decoders)

Three independent units, each hardware-free so they are unit-testable in the
existing `ProfileCore.Tests` project and naturally respect the
`Hardware.Sync` lock convention (they never touch hardware).

### Component 1 — Core register decoder (MSR / CPUID / known PCI)

New files under `Utils/`:

- `RegisterDefinition.cs`
  - `enum RegisterKind { Msr, Pci, Cpuid }`
  - `class FieldDefinition { string Name; int HighBit; int LowBit;
    Func<ulong,string> Format /* nullable, for computed values */ }`
  - `class RegisterDefinition { RegisterKind Kind; uint Address /* MSR addr,
    PCI addr, or CPUID leaf */; string Name; string Description;
    IReadOnlyList<FieldDefinition> Fields }`
- `RegisterCatalog.cs` — static curated set keyed by `(Kind, Address)` with
  `bool TryGet(RegisterKind, uint, out RegisterDefinition)`.
- `RegisterDecoder.cs` — pure logic:
  - `string Decode(RegisterKind kind, uint address, ulong value)` returns the
    formatted block, or `""` when the register is unknown.
  - one shared `static ulong Extract(ulong value, int hi, int lo)`.
  - each field's optional `Format` delegate is invoked inside a try/catch so a
    bad computation degrades to the raw field value and never throws.

Value widths:
- **MSR** — 64-bit: `value = ((ulong)edx << 32) | eax`.
- **PCI** — 32-bit.
- **CPUID** — leaf-indexed; fields reference the relevant output register.
  (The existing family/model "Decode" button stays as-is.)

#### Initial curated set

- **MSR:** PStateDef 0–7 (`0xC0010064`–`0xC001006B`). The bit fields and
  Frequency are verified against the existing `CalculatePstateDetails`
  (`SettingsForm.cs:1739`): CpuFid [7:0], CpuDfsId [13:8] (mask `0x3F`),
  CpuVid [21:14], IddValue [29:22], IddDiv [31:30]. Computed Frequency uses the
  exact in-code expression `(CpuFid * 25 / (CpuDfsId * 12.5)) * 100` MHz
  (`SettingsForm.cs:1817`) — done in floating point to avoid integer-division
  error. Also: HWCR (`0xC0010015`), PStateCurLim (`0xC0010061`), PStateCtl
  (`0xC0010062`), PStateStat (`0xC0010063`).
  - **New fields, not currently computed by the tool (now validated — see
    Assumption validation):** `PstateEn` is bit 63, i.e. in the high dword
    `edx`, so it requires the 64-bit value, not just `eax`. Voltage from CpuVid
    must **not** be hardcoded — reuse ZenStates-Core's own
    `Utils.VidToVoltage(vid)` (SVI2) or `Utils.VidToVoltageSVI3(vid)` (Zen4+),
    selected by CPU generation via `cpu.info.codeName` (the library already
    branches on `CodeName`, cf. `Cpu.GetSVI2Info(CodeName)`). The two planes
    give very different results, so generation selection is mandatory.
- **CPUID:** leaf `0x00000001` (family/base model/ext model/stepping from
  eax); `0x80000008` if straightforward.
- **PCI:** none required initially; structure supports adding fixed addresses
  later.

#### Example output (MSR `0xC0010064`)

```
PStateDef0 (0xC0010064) — P-State 0 definition
  CpuFid   [7:0]    0xA8 (168)
  CpuDfsId [13:8]   0x08 (8)
  CpuVid   [21:14]  0x28 (40)
  PstateEn [63]     1            (new field; see lower-confidence note)
  -> Frequency: 4200 MHz         (= 200*168/8, verified against in-code math)
  -> Voltage:   1.300 V          (= 1.55 - 0.00625*40; formula NOT yet in tool)
```

### Component 2 — SMU command resolver

`Utils/SmuCommandNames.cs`:

- A thin adapter reflects once over the live mailboxes exposed by `cpu.smu`
  (type `SMU`): `Rsmu` (`RSMUMailbox`), `Mp1Smu` (`MP1Mailbox`), and `Hsmp`
  (`HSMPMailbox`), reading their `SMU_MSG_*` `uint` properties to build a
  `value -> name(s)` map (the IDs differ per CPU, so they are read from the
  running machine). Zero-valued / unsupported messages are skipped; if several
  names share a value they are joined.
- The map-building logic takes a plain `IEnumerable<(string name, uint value)>`
  so it is pure and unit-testable; reflection is isolated in a tiny wrapper.

Consumers:
- `SMUMonitor` `Cmd` column — append the resolved name, e.g.
  `0x26 (SetPBOScalar)`.
- The Settings-tab `ApplySettings` result — show the command name alongside the
  numeric command.

### Component 3 — PMTable labeling

In `PowerTableMonitor`:

- Call `cpu.RyzenSmu.GetPmTableStructure()` (returns
  `Dictionary<uint offset, SmuSensorDefinition { string Name; SensorType Type;
  float Scale }>`), gated by `cpu.RyzenSmu.IsPmTableLayoutDefined`, behind
  `Hardware.Sync`. (`cpu.RyzenSmu` is a public property of type `RyzenSmu`;
  note `cpu.smu` is the unrelated `SMU` type and does **not** expose these.)
- Add two columns to the grid item model: **Name** (sensor name by offset) and
  **Scaled** (value with the sensor `Scale` applied, formatted per the
  `SensorType`). Keep the existing `Index`, `Offset`, `Value`, and `Max`
  columns unchanged as raw reference.
- When the layout is undefined, the new columns are blank and the view behaves
  as today.
- The join (structure dict + `float[]` -> labeled rows) is a pure function and
  is unit-tested; only the structure fetch touches the library.

## Data flow (single read)

```
Read button
  -> Hardware.Locked(read)         (existing)
  -> populate textboxes            (existing)
  -> decoder.Decode(kind, addr, value)
  -> if non-empty: PrependResult(block)
```

Hook points:
- MSR: `ButtonMsrRead_Click` (`SettingsForm.cs:2049`) — also starts using the
  results pane, which today it does not.
- PCI: inside `ShowResult(uint)` (`SettingsForm.cs:1264`).
- CPUID: `ButtonCPUIDRead_Click` (`SettingsForm.cs:2131`) — also starts using
  the results pane.

## Data flow (scans)

The MSR scan (`ReadMsr_Task`), CPUID scan (`ReadCPUID_Task`), and PCI scan
build a `StringBuilder` shown in `ResultForm`. Each routes per-register output
through `RegisterDecoder.Decode`, appending the decode inline under each
register's raw line. Unknown registers print raw only.

## Error handling

- `RegisterDecoder.Decode` is total: unknown/invalid input returns `""`, never
  throws.
- Field `Format` delegates are wrapped; a failed computation falls back to the
  raw field value.
- `SmuCommandNames` reflection failures degrade to "no name resolved" (numeric
  only).
- PMTable: missing/undefined layout -> blank new columns, no crash.

## Testing (`ProfileCore.Tests`, all hardware-free)

- `RegisterDecoderTests` — known raw values produce exact decoded strings
  (incl. P-state frequency/voltage math); unknown addresses yield `""`;
  `Extract` boundary cases (bit 0, bit 63, full-width fields).
- `SmuCommandNamesTests` — map building from a synthetic
  `(name, value)` list: resolution, collisions, zero-value skipping.
- `PmTableLabelingTests` — labeling join from a synthetic structure dict +
  `float[]`: named/scaled output and undefined-layout fallback.

## Assumption validation (checked against code + ZenStates-Core.dll)

Verified before finalizing this spec:

- **P-state bit fields & frequency** — confirmed against `CalculatePstateDetails`
  (`SettingsForm.cs:1739`) and the frequency expression (`SettingsForm.cs:1817`).
  My `200*FID/DID` shorthand is mathematically identical to the in-code
  `(FID*25/(DID*12.5))*100`; the spec now cites the exact expression.
- **MSR read width** — `cpu.ReadMsr(addr, ref eax, ref edx)`; 64-bit value is
  `((ulong)edx << 32) | eax`. Confirmed (`SettingsForm.cs:2049`, `2009`).
- **SMU command tables** — `cpu.smu` is type `SMU`, exposing `Rsmu`
  (`RSMUMailbox`), `Mp1Smu` (`MP1Mailbox`), `Hsmp` (`HSMPMailbox`). The
  `SMU_MSG_*` members are `uint`. Corrected: the MP1 property is `Mp1Smu`, not
  `Mp1`.
- **PM-table structure** — reachable via the public `cpu.RyzenSmu` property
  (type `RyzenSmu`), method `GetPmTableStructure()` →
  `Dictionary<uint, SmuSensorDefinition{ string Name; SensorType Type; float
  Scale }>`, gated by `IsPmTableLayoutDefined`. Corrected: this is **not** on
  `cpu.smu` (`RyzenSmu` and `SMU` are unrelated types, both deriving from
  `object`).
- **Hook points** — PCI read calls `ShowResult(data)` (`SettingsForm.cs:1376`);
  MSR/CPUID single reads currently write only to textboxes (`2049`, `2131`);
  MSR/PCI/CPUID scans build a per-register `StringBuilder` shown via
  `ResultForm` (`2009`, `1916`, `2083`). All confirmed.
- **PCI has no enumerable register set** — the tab reads arbitrary SMN/PCI
  addresses via `cpu.ReadDword`/`ReadDwordEx` (`PCIRangeMonitor.cs:57`,
  `SettingsForm.cs:1370`). Confirmed.

Previously-flagged items, now resolved:

- **Voltage formula — CONFIRMED, with a correction.** Executing the library
  directly: `Utils.VidToVoltage(40) = 1.3`, `VidToVoltage(0) = 1.55`,
  `VidToVoltage(1) = 1.54375` → exactly the SVI2 plane `1.55 - 0.00625*VID`.
  However `Utils.VidToVoltageSVI3(40) = 0.445` — Zen4/Zen5 use a different
  plane. Conclusion: do **not** hardcode the formula; call the library's
  `VidToVoltage` / `VidToVoltageSVI3` selected by `cpu.info.codeName`. Grep
  confirmed no VID→voltage math exists in this repo today (only false-positive
  `RowHeadersVisible` hits).
- **PstateEn at bit 63 — CONFIRMED.** Per AMD PPR (Family 17h/19h
  MSRC001_006[4..B]), bit 63 of the PStateDef MSR is `PstateEn`. Grep confirmed
  the repo decodes no bit-63 / high-dword field anywhere today. The library
  models only the *current* HW p-state status (`Cpu.HwPstateStatus`:
  CurCpuFid/DfsId/Vid/HwPstate) — it does not expose a PStateDef enable bit, so
  this field is new work; it is a documented constant, not a guess.

## Out of scope

- External/editable register definitions (JSON).
- Broad/comprehensive AMD register coverage.
- Decoding arbitrary (non-catalogued) PCI addresses.
- Changes to write paths (MSR/PCI write behavior is unchanged).
