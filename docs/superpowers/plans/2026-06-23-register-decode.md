# Human-Readable Register Decoding Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Append human-readable, named bit-field breakdowns (with computed frequency/voltage) to recognized MSR/PCI/CPUID reads and scans, resolve SMU command IDs to names, and add named/scaled columns to the PMTable view — falling back to raw output for anything unknown.

**Architecture:** A pure, WinForms-free, hardware-free decoding core lives in `Utils/` and is compiled into the test project for full unit-test coverage. A thin main-project-only adapter bridges the core to ZenStates-Core (reflection over SMU mailboxes, generation-aware VID→voltage selection, PM-table structure). The existing read/scan handlers call the core and append its output; the SMU monitor and PMTable view consume the resolvers.

**Tech Stack:** C# (net48, old-style csproj for the app; SDK-style net48 for tests), WinForms, xUnit, ZenStates-Core.dll (prebuilt).

**Reference spec:** `docs/superpowers/specs/2026-06-22-register-decode-design.md`

**Build/test commands (per project memory — use `dotnet`, not VS MSBuild):**
- Build app: `dotnet build ZenStatesDebugTool.csproj`
- Run tests: `dotnet test Tests/ProfileCore.Tests/ProfileCore.Tests.csproj`

**Conventions to respect:**
- All CPU/SMU/PCI/MSR hardware access goes through `Hardware.Sync`/`Hardware.Locked`; never hold the lock across a synchronous `Invoke`. The decoding core touches no hardware, so it is safe to call inside or outside the lock.
- New `.cs` files MUST be registered with `<Compile Include>` in `ZenStatesDebugTool.csproj` (old-style, no globbing). Test-compiled files ALSO need a `<Compile Include … Link=…>` entry in `Tests/ProfileCore.Tests/ProfileCore.Tests.csproj`.
- Namespace for `Utils/` files is flat `ZenStatesDebugTool` (matches `CoreTopology`, `Hardware`, etc.).

---

## File Structure

**New (pure core — compiled into BOTH app and test projects):**
- `Utils/RegisterDefinition.cs` — `RegisterKind`, `DecodeContext`, `FieldDefinition`, `RegisterDefinition`.
- `Utils/RegisterDecoder.cs` — `Extract` + `Decode`.
- `Utils/RegisterCatalog.cs` — curated static catalog + `TryGet`.
- `Utils/SmuCommandNames.cs` — pure `Build` / `Resolve`.
- `Utils/PmTableLabeling.cs` — `SensorInfo`, `LabeledRow`, `Label`.

**New (main-project-only adapter — depends on ZenStates-Core, NOT in tests):**
- `Utils/SmuDecodeAdapter.cs` — reflection over SMU mailboxes, generation-aware VID→voltage, PM-table structure projection.

**New (tests):**
- `Tests/ProfileCore.Tests/RegisterDecoderTests.cs`
- `Tests/ProfileCore.Tests/SmuCommandNamesTests.cs`
- `Tests/ProfileCore.Tests/PmTableLabelingTests.cs`

**Modified:**
- `ZenStatesDebugTool.csproj` — register 6 new app files.
- `Tests/ProfileCore.Tests/ProfileCore.Tests.csproj` — link 5 pure core files.
- `SettingsForm.cs` — build context + maps; append decode in MSR/PCI/CPUID single reads and scans; SMU command name in `ApplySettings`.
- `SMUMonitor.cs` — resolve `Cmd` to name.
- `PowerTableMonitor.cs` — add Name/Scaled columns.

---

## Task 1: Core data model + bit extraction

**Files:**
- Create: `Utils/RegisterDefinition.cs`
- Create: `Utils/RegisterDecoder.cs` (Extract only in this task)
- Modify: `ZenStatesDebugTool.csproj` (register both)
- Modify: `Tests/ProfileCore.Tests/ProfileCore.Tests.csproj` (link both)
- Test: `Tests/ProfileCore.Tests/RegisterDecoderTests.cs`

- [ ] **Step 1: Create the data model**

`Utils/RegisterDefinition.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace ZenStatesDebugTool
{
    public enum RegisterKind { Msr, Pci, Cpuid }

    // Optional helpers a derived line may need (e.g. generation-aware VID->voltage).
    // Supplied by the caller; when a needed delegate is null the derived line is skipped.
    public sealed class DecodeContext
    {
        public Func<uint, double> VidToVoltage;
        public static readonly DecodeContext None = new DecodeContext();
    }

    // A named raw bit-field [HighBit:LowBit] within the register value.
    public sealed class FieldDefinition
    {
        public string Name { get; }
        public int HighBit { get; }
        public int LowBit { get; }

        public FieldDefinition(string name, int highBit, int lowBit)
        {
            Name = name;
            HighBit = highBit;
            LowBit = lowBit;
        }
    }

    // A recognized register: friendly name, raw bit-fields, and optional derived
    // lines (e.g. "Frequency: 4200 MHz") computed from the whole value + context.
    public sealed class RegisterDefinition
    {
        public RegisterKind Kind { get; }
        public uint Address { get; }
        public string Name { get; }
        public string Description { get; }
        public IReadOnlyList<FieldDefinition> Fields { get; }
        public IReadOnlyList<Func<ulong, DecodeContext, string>> Derived { get; }

        public RegisterDefinition(
            RegisterKind kind, uint address, string name, string description,
            IReadOnlyList<FieldDefinition> fields,
            IReadOnlyList<Func<ulong, DecodeContext, string>> derived = null)
        {
            Kind = kind;
            Address = address;
            Name = name;
            Description = description;
            Fields = fields ?? new List<FieldDefinition>();
            Derived = derived ?? new List<Func<ulong, DecodeContext, string>>();
        }
    }
}
```

- [ ] **Step 2: Create RegisterDecoder with Extract only**

`Utils/RegisterDecoder.cs`:

```csharp
using System;

namespace ZenStatesDebugTool
{
    public static class RegisterDecoder
    {
        // Extract bits [hi:lo] (inclusive) from a 64-bit value.
        public static ulong Extract(ulong value, int hi, int lo)
        {
            if (lo < 0 || hi < lo || hi > 63)
                throw new ArgumentOutOfRangeException(nameof(hi), "Invalid bit range.");

            int width = hi - lo + 1;
            ulong mask = width >= 64 ? ulong.MaxValue : (1UL << width) - 1UL;
            return (value >> lo) & mask;
        }
    }
}
```

- [ ] **Step 3: Register both files in the app csproj**

In `ZenStatesDebugTool.csproj`, add after `<Compile Include="Utils\TabVisibilityController.cs" />` (line ~132):

```xml
    <Compile Include="Utils\RegisterDefinition.cs" />
    <Compile Include="Utils\RegisterDecoder.cs" />
```

- [ ] **Step 4: Link both files into the test csproj**

In `Tests/ProfileCore.Tests/ProfileCore.Tests.csproj`, inside the `<ItemGroup>` that has the `<Compile Include="..\..\Utils\...">` entries, add:

```xml
    <Compile Include="..\..\Utils\RegisterDefinition.cs" Link="RegisterDefinition.cs" />
    <Compile Include="..\..\Utils\RegisterDecoder.cs" Link="RegisterDecoder.cs" />
```

- [ ] **Step 5: Write the failing test for Extract**

`Tests/ProfileCore.Tests/RegisterDecoderTests.cs`:

```csharp
using ZenStatesDebugTool;
using Xunit;

namespace ProfileCore.Tests
{
    public class RegisterDecoderTests
    {
        [Theory]
        [InlineData(0xA8UL, 7, 0, 0xA8UL)]          // low byte
        [InlineData(0x80000000_00000000UL, 63, 63, 1UL)] // top bit
        [InlineData(0x000A08A8UL, 13, 8, 8UL)]      // CpuDfsId field
        [InlineData(0x000A08A8UL, 21, 14, 40UL)]    // CpuVid field
        [InlineData(0xFFFFFFFF_FFFFFFFFUL, 63, 0, 0xFFFFFFFF_FFFFFFFFUL)] // full width
        public void Extract_returns_expected_bits(ulong value, int hi, int lo, ulong expected)
        {
            Assert.Equal(expected, RegisterDecoder.Extract(value, hi, lo));
        }
    }
}
```

- [ ] **Step 6: Run the test, expect PASS (Extract already implemented)**

Run: `dotnet test Tests/ProfileCore.Tests/ProfileCore.Tests.csproj`
Expected: all tests PASS (the new file compiles and Extract behaves). If the test assembly fails to compile, fix the `<Compile Include>` paths from Steps 3–4.

- [ ] **Step 7: Commit**

```bash
git add Utils/RegisterDefinition.cs Utils/RegisterDecoder.cs ZenStatesDebugTool.csproj Tests/ProfileCore.Tests/ProfileCore.Tests.csproj Tests/ProfileCore.Tests/RegisterDecoderTests.cs
git commit -m "feat: register-decode core model + bit extraction"
```

---

## Task 2: RegisterCatalog + Decode (unknown returns empty)

**Files:**
- Create: `Utils/RegisterCatalog.cs`
- Modify: `Utils/RegisterDecoder.cs` (add `Decode`)
- Modify: `ZenStatesDebugTool.csproj`, `Tests/ProfileCore.Tests/ProfileCore.Tests.csproj` (register/link catalog)
- Test: `Tests/ProfileCore.Tests/RegisterDecoderTests.cs`

- [ ] **Step 1: Create the catalog (empty to start) with TryGet**

`Utils/RegisterCatalog.cs`:

```csharp
using System.Collections.Generic;

namespace ZenStatesDebugTool
{
    public static class RegisterCatalog
    {
        // Keyed by (Kind, Address/Leaf). Populated in later tasks.
        private static readonly Dictionary<(RegisterKind, uint), RegisterDefinition> Map =
            new Dictionary<(RegisterKind, uint), RegisterDefinition>();

        public static bool TryGet(RegisterKind kind, uint address, out RegisterDefinition def)
            => Map.TryGetValue((kind, address), out def);

        // Used by the population helpers in later tasks.
        internal static void Add(RegisterDefinition def) => Map[(def.Kind, def.Address)] = def;
    }
}
```

- [ ] **Step 2: Add Decode to RegisterDecoder**

In `Utils/RegisterDecoder.cs`, add a `using System.Text;` at the top and this method inside the class:

```csharp
        // Returns a formatted, human-readable block for a recognized register,
        // or "" when the register is unknown. Never throws.
        public static string Decode(RegisterKind kind, uint address, ulong value, DecodeContext context = null)
        {
            if (!RegisterCatalog.TryGet(kind, address, out RegisterDefinition def))
                return "";

            DecodeContext ctx = context ?? DecodeContext.None;
            var sb = new StringBuilder();
            sb.AppendLine($"{def.Name} (0x{address:X8}) - {def.Description}");

            foreach (FieldDefinition f in def.Fields)
            {
                ulong fieldVal;
                try { fieldVal = Extract(value, f.HighBit, f.LowBit); }
                catch { continue; }

                string bits = f.HighBit == f.LowBit ? $"{f.HighBit}" : $"{f.HighBit}:{f.LowBit}";
                sb.AppendLine($"  {f.Name} [{bits}] = 0x{fieldVal:X} ({fieldVal})");
            }

            foreach (var derive in def.Derived)
            {
                string line;
                try { line = derive(value, ctx); }
                catch { line = null; }
                if (!string.IsNullOrEmpty(line))
                    sb.AppendLine($"  -> {line}");
            }

            return sb.ToString();
        }
```

- [ ] **Step 3: Register/link the catalog file**

In `ZenStatesDebugTool.csproj` (after the RegisterDecoder line from Task 1):

```xml
    <Compile Include="Utils\RegisterCatalog.cs" />
```

In `Tests/ProfileCore.Tests/ProfileCore.Tests.csproj`:

```xml
    <Compile Include="..\..\Utils\RegisterCatalog.cs" Link="RegisterCatalog.cs" />
```

- [ ] **Step 4: Write the failing test for unknown register**

Add to `RegisterDecoderTests.cs`:

```csharp
        [Fact]
        public void Decode_unknown_register_returns_empty_string()
        {
            Assert.Equal("", RegisterDecoder.Decode(RegisterKind.Msr, 0xDEADBEEF, 0x12345678UL));
        }
```

- [ ] **Step 5: Run the test, expect PASS**

Run: `dotnet test Tests/ProfileCore.Tests/ProfileCore.Tests.csproj`
Expected: PASS (catalog is empty, so unknown returns "").

- [ ] **Step 6: Commit**

```bash
git add Utils/RegisterCatalog.cs Utils/RegisterDecoder.cs ZenStatesDebugTool.csproj Tests/ProfileCore.Tests/ProfileCore.Tests.csproj Tests/ProfileCore.Tests/RegisterDecoderTests.cs
git commit -m "feat: register decode dispatch with empty-on-unknown fallback"
```

---

## Task 3: PStateDef MSRs (fields + frequency + voltage)

**Files:**
- Modify: `Utils/RegisterCatalog.cs`
- Test: `Tests/ProfileCore.Tests/RegisterDecoderTests.cs`

Bit layout (verified against `SettingsForm.cs:1739` `CalculatePstateDetails`):
CpuFid [7:0], CpuDfsId [13:8], CpuVid [21:14], IddValue [29:22], IddDiv [31:30], PstateEn [63].
Frequency uses the exact in-code expression `(CpuFid * 25 / (CpuDfsId * 12.5)) * 100` MHz (`SettingsForm.cs:1817`), in floating point. Voltage is computed only when `ctx.VidToVoltage` is supplied (production injects the generation-aware library function; tests inject a stub).

- [ ] **Step 1: Populate PStateDef 0–7 in the catalog**

In `Utils/RegisterCatalog.cs`, add `using System;` and `using System.Collections.Generic;` (already present), and add a static constructor that registers the P-state definitions:

```csharp
        static RegisterCatalog()
        {
            AddPStateDefs();
        }

        private const uint PStateDef0 = 0xC0010064;

        private static void AddPStateDefs()
        {
            for (uint i = 0; i < 8; i++)
            {
                uint addr = PStateDef0 + i;
                uint index = i; // capture per-iteration for the name
                Add(new RegisterDefinition(
                    RegisterKind.Msr, addr,
                    $"PStateDef{index}", $"P-State {index} definition",
                    new List<FieldDefinition>
                    {
                        new FieldDefinition("CpuFid", 7, 0),
                        new FieldDefinition("CpuDfsId", 13, 8),
                        new FieldDefinition("CpuVid", 21, 14),
                        new FieldDefinition("IddValue", 29, 22),
                        new FieldDefinition("IddDiv", 31, 30),
                        new FieldDefinition("PstateEn", 63, 63),
                    },
                    new List<Func<ulong, DecodeContext, string>>
                    {
                        Frequency,
                        Voltage,
                    }));
            }
        }

        private static string Frequency(ulong value, DecodeContext ctx)
        {
            uint fid = (uint)RegisterDecoder.Extract(value, 7, 0);
            uint did = (uint)RegisterDecoder.Extract(value, 13, 8);
            if (did == 0) return null; // avoid divide-by-zero; nothing to show
            double mhz = (fid * 25.0 / (did * 12.5)) * 100.0;
            return $"Frequency: {mhz:0} MHz";
        }

        private static string Voltage(ulong value, DecodeContext ctx)
        {
            if (ctx?.VidToVoltage == null) return null;
            uint vid = (uint)RegisterDecoder.Extract(value, 21, 14);
            return $"Voltage: {ctx.VidToVoltage(vid):0.000} V";
        }
```

- [ ] **Step 2: Write the failing test**

Add to `RegisterDecoderTests.cs`:

```csharp
        // CpuFid=0xA8(168), CpuDfsId=8, CpuVid=0x28(40), PstateEn=1 (bit 63).
        // eax = (40<<14)|(8<<8)|168 = 0x000A08A8 ; edx = 0x80000000 (PstateEn).
        private const ulong SamplePStateDef = (0x80000000UL << 32) | 0x000A08A8UL;

        private static readonly DecodeContext Svi2Ctx =
            new DecodeContext { VidToVoltage = v => 1.55 - 0.00625 * v };

        [Fact]
        public void Decode_pstatedef_shows_name_fields_and_derived_values()
        {
            string s = RegisterDecoder.Decode(RegisterKind.Msr, 0xC0010064, SamplePStateDef, Svi2Ctx);

            Assert.Contains("PStateDef0 (0xC0010064) - P-State 0 definition", s);
            Assert.Contains("CpuFid [7:0] = 0xA8 (168)", s);
            Assert.Contains("CpuDfsId [13:8] = 0x8 (8)", s);
            Assert.Contains("CpuVid [21:14] = 0x28 (40)", s);
            Assert.Contains("PstateEn [63] = 0x1 (1)", s);
            Assert.Contains("-> Frequency: 4200 MHz", s);
            Assert.Contains("-> Voltage: 1.300 V", s);
        }

        [Fact]
        public void Decode_pstatedef_skips_voltage_when_no_context_helper()
        {
            string s = RegisterDecoder.Decode(RegisterKind.Msr, 0xC0010064, SamplePStateDef);
            Assert.Contains("Frequency: 4200 MHz", s);
            Assert.DoesNotContain("Voltage:", s);
        }

        [Fact]
        public void Decode_pstatedef_address_offsets_resolve()
        {
            // 0xC001006B is PStateDef7.
            string s = RegisterDecoder.Decode(RegisterKind.Msr, 0xC001006B, SamplePStateDef, Svi2Ctx);
            Assert.Contains("PStateDef7 (0xC001006B)", s);
        }
```

- [ ] **Step 3: Run the test, expect PASS**

Run: `dotnet test Tests/ProfileCore.Tests/ProfileCore.Tests.csproj`
Expected: PASS. If a `Contains` fails, align the format strings in `Decode` (Task 2 Step 2) and the catalog with the asserted text exactly.

- [ ] **Step 4: Commit**

```bash
git add Utils/RegisterCatalog.cs Tests/ProfileCore.Tests/RegisterDecoderTests.cs
git commit -m "feat: decode PStateDef MSRs with frequency and voltage"
```

---

## Task 4: HWCR, PState status/control MSRs, and CPUID leaf 1

**Files:**
- Modify: `Utils/RegisterCatalog.cs`
- Test: `Tests/ProfileCore.Tests/RegisterDecoderTests.cs`

- [ ] **Step 1: Add the remaining curated registers**

In `Utils/RegisterCatalog.cs`, extend the static constructor and add helpers:

```csharp
        static RegisterCatalog()
        {
            AddPStateDefs();
            AddMiscMsrs();
            AddCpuidLeaves();
        }

        private static void AddMiscMsrs()
        {
            Add(new RegisterDefinition(
                RegisterKind.Msr, 0xC0010015, "HWCR", "Hardware Configuration",
                new List<FieldDefinition>
                {
                    new FieldDefinition("SmmLock", 0, 0),
                    new FieldDefinition("TlbCacheDis", 3, 3),
                    new FieldDefinition("Cpb (boost) Dis", 25, 25),
                    new FieldDefinition("EffFreqReadOnlyLock", 30, 30),
                }));

            Add(new RegisterDefinition(
                RegisterKind.Msr, 0xC0010061, "PStateCurLim", "P-State Current Limit",
                new List<FieldDefinition>
                {
                    new FieldDefinition("CurPstateLimit", 2, 0),
                    new FieldDefinition("PstateMaxVal", 6, 4),
                }));

            Add(new RegisterDefinition(
                RegisterKind.Msr, 0xC0010062, "PStateCtl", "P-State Control",
                new List<FieldDefinition>
                {
                    new FieldDefinition("PstateCmd", 2, 0),
                }));

            Add(new RegisterDefinition(
                RegisterKind.Msr, 0xC0010063, "PStateStat", "P-State Status",
                new List<FieldDefinition>
                {
                    new FieldDefinition("CurPstate", 2, 0),
                }));
        }

        private static void AddCpuidLeaves()
        {
            // Decodes the EAX output of CPUID leaf 0x00000001 (family/model/stepping).
            Add(new RegisterDefinition(
                RegisterKind.Cpuid, 0x00000001, "CPUID_1_EAX", "Family/Model/Stepping (EAX)",
                new List<FieldDefinition>
                {
                    new FieldDefinition("Stepping", 3, 0),
                    new FieldDefinition("BaseModel", 7, 4),
                    new FieldDefinition("BaseFamily", 11, 8),
                    new FieldDefinition("ExtModel", 19, 16),
                    new FieldDefinition("ExtFamily", 27, 20),
                }));
        }
```

- [ ] **Step 2: Write the failing test**

Add to `RegisterDecoderTests.cs`:

```csharp
        [Fact]
        public void Decode_hwcr_resolves_name_and_fields()
        {
            string s = RegisterDecoder.Decode(RegisterKind.Msr, 0xC0010015, 0x02000000UL);
            Assert.Contains("HWCR (0xC0010015) - Hardware Configuration", s);
            Assert.Contains("Cpb (boost) Dis [25] = 0x1 (1)", s);
        }

        [Fact]
        public void Decode_cpuid_leaf1_eax_resolves_family_model()
        {
            // eax for a Zen part, e.g. 0x00A20F12: ExtFamily=0xA, BaseFamily=0xF, ExtModel=2, Stepping=2.
            string s = RegisterDecoder.Decode(RegisterKind.Cpuid, 0x00000001, 0x00A20F12UL);
            Assert.Contains("CPUID_1_EAX (0x00000001)", s);
            Assert.Contains("Stepping [3:0] = 0x2 (2)", s);
            Assert.Contains("BaseFamily [11:8] = 0xF (15)", s);
            Assert.Contains("ExtFamily [27:20] = 0xA (10)", s);
        }
```

- [ ] **Step 3: Run the test, expect PASS**

Run: `dotnet test Tests/ProfileCore.Tests/ProfileCore.Tests.csproj`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add Utils/RegisterCatalog.cs Tests/ProfileCore.Tests/RegisterDecoderTests.cs
git commit -m "feat: decode HWCR, P-state status/control MSRs, CPUID leaf 1"
```

---

## Task 5: SMU command name resolver (pure)

**Files:**
- Create: `Utils/SmuCommandNames.cs`
- Modify: `ZenStatesDebugTool.csproj`, `Tests/ProfileCore.Tests/ProfileCore.Tests.csproj`
- Test: `Tests/ProfileCore.Tests/SmuCommandNamesTests.cs`

- [ ] **Step 1: Create the pure resolver**

`Utils/SmuCommandNames.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;

namespace ZenStatesDebugTool
{
    // Pure mapping from SMU command IDs to their names. The IDs are read from the
    // live mailbox by the main-project adapter; this part is hardware-free so it
    // can be unit-tested.
    public static class SmuCommandNames
    {
        // Builds value -> name. Zero-valued (unsupported/unset) messages are
        // skipped; multiple names sharing a value are joined with "/".
        public static Dictionary<uint, string> Build(IEnumerable<KeyValuePair<string, uint>> messages)
        {
            var map = new Dictionary<uint, string>();
            if (messages == null) return map;

            foreach (var m in messages)
            {
                if (m.Value == 0) continue;
                if (map.TryGetValue(m.Value, out string existing))
                {
                    if (!existing.Split('/').Contains(m.Key))
                        map[m.Value] = existing + "/" + m.Key;
                }
                else
                {
                    map[m.Value] = m.Key;
                }
            }
            return map;
        }

        // Returns the resolved name, or null when not found.
        public static string Resolve(IReadOnlyDictionary<uint, string> map, uint value)
            => map != null && map.TryGetValue(value, out string name) ? name : null;
    }
}
```

- [ ] **Step 2: Register/link the file**

`ZenStatesDebugTool.csproj`:

```xml
    <Compile Include="Utils\SmuCommandNames.cs" />
```

`Tests/ProfileCore.Tests/ProfileCore.Tests.csproj`:

```xml
    <Compile Include="..\..\Utils\SmuCommandNames.cs" Link="SmuCommandNames.cs" />
```

- [ ] **Step 3: Write the failing test**

`Tests/ProfileCore.Tests/SmuCommandNamesTests.cs`:

```csharp
using System.Collections.Generic;
using ZenStatesDebugTool;
using Xunit;

namespace ProfileCore.Tests
{
    public class SmuCommandNamesTests
    {
        private static KeyValuePair<string, uint> Kv(string k, uint v)
            => new KeyValuePair<string, uint>(k, v);

        [Fact]
        public void Build_maps_values_to_names_and_skips_zero()
        {
            var map = SmuCommandNames.Build(new[]
            {
                Kv("SetPBOScalar", 0x26),
                Kv("GetTableVersion", 0x05),
                Kv("Unsupported", 0x00),
            });

            Assert.Equal("SetPBOScalar", SmuCommandNames.Resolve(map, 0x26));
            Assert.Equal("GetTableVersion", SmuCommandNames.Resolve(map, 0x05));
            Assert.False(map.ContainsKey(0x00));
        }

        [Fact]
        public void Build_joins_names_sharing_a_value()
        {
            var map = SmuCommandNames.Build(new[]
            {
                Kv("SetMaxCpuFreq", 0x10),
                Kv("AltName", 0x10),
            });
            Assert.Equal("SetMaxCpuFreq/AltName", SmuCommandNames.Resolve(map, 0x10));
        }

        [Fact]
        public void Resolve_returns_null_for_unknown()
        {
            var map = SmuCommandNames.Build(new[] { Kv("X", 0x01) });
            Assert.Null(SmuCommandNames.Resolve(map, 0x99));
        }
    }
}
```

- [ ] **Step 4: Run the test, expect PASS**

Run: `dotnet test Tests/ProfileCore.Tests/ProfileCore.Tests.csproj`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Utils/SmuCommandNames.cs ZenStatesDebugTool.csproj Tests/ProfileCore.Tests/ProfileCore.Tests.csproj Tests/ProfileCore.Tests/SmuCommandNamesTests.cs
git commit -m "feat: pure SMU command-name resolver"
```

---

## Task 6: PMTable labeling (pure)

**Files:**
- Create: `Utils/PmTableLabeling.cs`
- Modify: `ZenStatesDebugTool.csproj`, `Tests/ProfileCore.Tests/ProfileCore.Tests.csproj`
- Test: `Tests/ProfileCore.Tests/PmTableLabelingTests.cs`

- [ ] **Step 1: Create the pure labeler**

`Utils/PmTableLabeling.cs`:

```csharp
using System.Collections.Generic;
using System.Globalization;

namespace ZenStatesDebugTool
{
    // A WinForms-free projection of one ZenStates SmuSensorDefinition.
    public struct SensorInfo
    {
        public string Name;
        public float Scale;
        public SensorInfo(string name, float scale) { Name = name; Scale = scale; }
    }

    public sealed class LabeledRow
    {
        public int Index;
        public uint Offset;
        public float Raw;
        public string Name;   // "" when the offset is unlabeled
        public string Scaled; // "" when the offset is unlabeled
    }

    public static class PmTableLabeling
    {
        // Pairs each table slot (offset = index*4) with its sensor name/scaled
        // value when the structure defines it; blank otherwise.
        public static List<LabeledRow> Label(float[] table, IReadOnlyDictionary<uint, SensorInfo> structure)
        {
            var rows = new List<LabeledRow>();
            if (table == null) return rows;

            for (int i = 0; i < table.Length; i++)
            {
                uint offset = (uint)(i * 4);
                var row = new LabeledRow
                {
                    Index = i,
                    Offset = offset,
                    Raw = table[i],
                    Name = "",
                    Scaled = "",
                };

                if (structure != null && structure.TryGetValue(offset, out SensorInfo info))
                {
                    row.Name = info.Name ?? "";
                    row.Scaled = (table[i] * info.Scale).ToString("F3", CultureInfo.InvariantCulture);
                }

                rows.Add(row);
            }
            return rows;
        }
    }
}
```

- [ ] **Step 2: Register/link the file**

`ZenStatesDebugTool.csproj`:

```xml
    <Compile Include="Utils\PmTableLabeling.cs" />
```

`Tests/ProfileCore.Tests/ProfileCore.Tests.csproj`:

```xml
    <Compile Include="..\..\Utils\PmTableLabeling.cs" Link="PmTableLabeling.cs" />
```

- [ ] **Step 3: Write the failing test**

`Tests/ProfileCore.Tests/PmTableLabelingTests.cs`:

```csharp
using System.Collections.Generic;
using ZenStatesDebugTool;
using Xunit;

namespace ProfileCore.Tests
{
    public class PmTableLabelingTests
    {
        [Fact]
        public void Label_applies_name_and_scale_for_known_offsets()
        {
            var table = new[] { 10.0f, 20.0f, 30.0f };
            var structure = new Dictionary<uint, SensorInfo>
            {
                { 0u, new SensorInfo("PPT", 1.0f) },
                { 8u, new SensorInfo("EDC", 0.5f) },
            };

            var rows = PmTableLabeling.Label(table, structure);

            Assert.Equal(3, rows.Count);
            Assert.Equal("PPT", rows[0].Name);
            Assert.Equal("10.000", rows[0].Scaled);
            Assert.Equal((uint)8, rows[2].Offset);
            Assert.Equal("EDC", rows[2].Name);
            Assert.Equal("15.000", rows[2].Scaled); // 30 * 0.5
        }

        [Fact]
        public void Label_leaves_unknown_offsets_blank()
        {
            var rows = PmTableLabeling.Label(new[] { 1.0f, 2.0f }, null);
            Assert.All(rows, r => Assert.Equal("", r.Name));
            Assert.All(rows, r => Assert.Equal("", r.Scaled));
            Assert.Equal((uint)4, rows[1].Offset);
        }
    }
}
```

- [ ] **Step 4: Run the test, expect PASS**

Run: `dotnet test Tests/ProfileCore.Tests/ProfileCore.Tests.csproj`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add Utils/PmTableLabeling.cs ZenStatesDebugTool.csproj Tests/ProfileCore.Tests/ProfileCore.Tests.csproj Tests/ProfileCore.Tests/PmTableLabelingTests.cs
git commit -m "feat: pure PMTable labeling"
```

---

## Task 7: Main-project adapter (ZenStates bridge)

This file depends on ZenStates-Core, so it lives in the app project ONLY (not linked into tests). It is verified by the app build, not unit tests.

**Files:**
- Create: `Utils/SmuDecodeAdapter.cs`
- Modify: `ZenStatesDebugTool.csproj`

- [ ] **Step 1: Create the adapter**

`Utils/SmuDecodeAdapter.cs`:

```csharp
using System.Collections.Generic;
using System.Reflection;
using ZenStates.Core;

namespace ZenStatesDebugTool
{
    // Bridges the pure decode core to ZenStates-Core. Reflection + library calls
    // live here so the core stays hardware-free and unit-testable.
    public static class SmuDecodeAdapter
    {
        // Reads every SMU_MSG_* uint property from the three mailboxes.
        public static IEnumerable<KeyValuePair<string, uint>> ReadMessages(SMU smu)
        {
            if (smu == null) yield break;
            foreach (var pair in ReadMailbox(smu.Rsmu))
                yield return pair;
            foreach (var pair in ReadMailbox(smu.Mp1Smu))
                yield return pair;
            foreach (var pair in ReadMailbox(smu.Hsmp))
                yield return pair;
        }

        private static IEnumerable<KeyValuePair<string, uint>> ReadMailbox(object mailbox)
        {
            if (mailbox == null) yield break;
            foreach (PropertyInfo p in mailbox.GetType().GetProperties())
            {
                if (!p.Name.StartsWith("SMU_MSG_")) continue;
                if (p.PropertyType != typeof(uint)) continue;
                uint value;
                try { value = (uint)p.GetValue(mailbox); }
                catch { continue; }
                yield return new KeyValuePair<string, uint>(
                    p.Name.Substring("SMU_MSG_".Length), value);
            }
        }

        // Generation-aware VID -> voltage, reusing the library's own conversions.
        // SVI3 set = Zen4 and later; everything else uses SVI2.
        public static System.Func<uint, double> GetVidToVoltage(CpuCodeName codeName)
        {
            if (IsSvi3(codeName))
                return v => ZenStates.Core.Utils.VidToVoltageSVI3(v);
            return v => ZenStates.Core.Utils.VidToVoltage(v);
        }

        private static bool IsSvi3(CpuCodeName c)
        {
            switch (c)
            {
                case CpuCodeName.Raphael:
                case CpuCodeName.GraniteRidge:
                case CpuCodeName.DragonRange:
                case CpuCodeName.Phoenix:
                case CpuCodeName.Phoenix2:
                case CpuCodeName.HawkPoint:
                case CpuCodeName.StrixPoint:
                case CpuCodeName.StrixHalo:
                case CpuCodeName.KrackanPoint:
                case CpuCodeName.KrackanPoint2:
                case CpuCodeName.Genoa:
                case CpuCodeName.Bergamo:
                case CpuCodeName.Turin:
                case CpuCodeName.TurinD:
                case CpuCodeName.StormPeak:    // Zen4 Threadripper
                case CpuCodeName.ShimadaPeak:  // Zen5 Threadripper
                    return true;
                default:
                    return false;
            }
        }

        // Projects the library's PM-table structure into the pure SensorInfo dict.
        // Returns null when the layout is undefined for this firmware.
        public static Dictionary<uint, SensorInfo> GetPmTableStructure(Cpu cpu)
        {
            if (cpu?.RyzenSmu == null || !cpu.RyzenSmu.IsPmTableLayoutDefined)
                return null;

            var result = new Dictionary<uint, SensorInfo>();
            foreach (var kv in cpu.RyzenSmu.GetPmTableStructure())
                result[kv.Key] = new SensorInfo(kv.Value.Name, kv.Value.Scale);
            return result;
        }
    }
}
```

- [ ] **Step 2: Register the file (app project only)**

`ZenStatesDebugTool.csproj`:

```xml
    <Compile Include="Utils\SmuDecodeAdapter.cs" />
```

Do NOT add it to the test csproj — it references ZenStates-Core, which the test project does not.

- [ ] **Step 3: Build the app to verify it compiles**

Run: `dotnet build ZenStatesDebugTool.csproj`
Expected: Build succeeded. If a member name is wrong (e.g. `Mp1Smu`, `RyzenSmu`, `IsPmTableLayoutDefined`, `VidToVoltageSVI3`), fix it against the spec's Assumption-validation section.

- [ ] **Step 4: Commit**

```bash
git add Utils/SmuDecodeAdapter.cs ZenStatesDebugTool.csproj
git commit -m "feat: ZenStates adapter for SMU names, voltage plane, PM-table structure"
```

---

## Task 8: Wire decode into single reads (MSR / PCI / CPUID)

**Files:**
- Modify: `SettingsForm.cs` (add fields + init; `ButtonMsrRead_Click`, `ShowResult`, `ButtonCPUIDRead_Click`)

- [ ] **Step 1: Add decode-context/name-map fields**

In `SettingsForm.cs`, near the `private readonly Cpu cpu;` field (line 31), add:

```csharp
        private DecodeContext decodeContext = DecodeContext.None;
        private IReadOnlyDictionary<uint, string> smuNameMap;
```

(If `System.Collections.Generic` is not already imported in this file, add `using System.Collections.Generic;` at the top.)

- [ ] **Step 2: Initialize them once the CPU is known**

In the method that populates the info labels (the block at `SettingsForm.cs:117` that sets `cpuInfoLabel.Text` etc.), add after `cpuIdLabel.Text = …` (line 125):

```csharp
                decodeContext = new DecodeContext
                {
                    VidToVoltage = SmuDecodeAdapter.GetVidToVoltage(cpu.info.codeName)
                };
                smuNameMap = SmuCommandNames.Build(SmuDecodeAdapter.ReadMessages(cpu.smu));
```

- [ ] **Step 3: Append decode in MSR single read**

In `ButtonMsrRead_Click` (`SettingsForm.cs:2049`), replace the `if (ok) { … }` body so it reads:

```csharp
            if (ok)
            {
                textBoxMsrEdx.Text = $"0x{edx:X8}";
                textBoxMsrEax.Text = $"0x{eax:X8}";

                ulong value = ((ulong)edx << 32) | eax;
                string decoded = RegisterDecoder.Decode(RegisterKind.Msr, msr, value, decodeContext);
                if (!string.IsNullOrEmpty(decoded))
                    PrependResult(decoded + Environment.NewLine);
            }
```

- [ ] **Step 4: Append decode in PCI read (inside ShowResult)**

In `ShowResult(uint data)` (`SettingsForm.cs:1264`), after `PrependResult(responseString);` add:

```csharp
            if (TryConvertToUintNoThrow(textBoxPciAddress.Text, out uint pciAddr))
            {
                string decoded = RegisterDecoder.Decode(RegisterKind.Pci, pciAddr, data, decodeContext);
                if (!string.IsNullOrEmpty(decoded))
                    PrependResult(decoded + Environment.NewLine);
            }
```

Then add this non-throwing parse helper next to `TryConvertToUint` (`SettingsForm.cs:1212` area):

```csharp
        private static bool TryConvertToUintNoThrow(string text, out uint address)
        {
            address = 0;
            try { address = Convert.ToUInt32(text.Trim().ToLowerInvariant(), 16); return true; }
            catch { return false; }
        }
```

- [ ] **Step 5: Append decode in CPUID single read**

In `ButtonCPUIDRead_Click` (`SettingsForm.cs:2131`), inside `if (ok) { … }`, after the four `textBoxCPUID*.Text = …` assignments add:

```csharp
                string decoded = RegisterDecoder.Decode(RegisterKind.Cpuid, index, eax, decodeContext);
                if (!string.IsNullOrEmpty(decoded))
                    PrependResult(decoded + Environment.NewLine);
```

- [ ] **Step 6: Build the app**

Run: `dotnet build ZenStatesDebugTool.csproj`
Expected: Build succeeded.

- [ ] **Step 7: Commit**

```bash
git add SettingsForm.cs
git commit -m "feat: append decoded breakdown to MSR/PCI/CPUID single reads"
```

---

## Task 9: Wire decode into scans (MSR / PCI / CPUID)

**Files:**
- Modify: `SettingsForm.cs` (`ReadMsr_Task`, `PciScan_DoWork`, `ReadCPUID_Task`)

The scan tasks build a `StringBuilder` and show it in `ResultForm`. Decode is pure, so calling it inside the existing `lock (Hardware.Sync)` block is safe (no `Invoke` happens there).

- [ ] **Step 1: MSR scan**

In `ReadMsr_Task` (`SettingsForm.cs:2009`), replace the `if (cpu.ReadMsr(...)) { result.AppendLine(...); }` block with:

```csharp
                        if (cpu.ReadMsr(startReg, ref eax, ref edx))
                        {
                            result.AppendLine($"0x{startReg:X8}: 0x{edx:X8} 0x{eax:X8}");
                            string decoded = RegisterDecoder.Decode(
                                RegisterKind.Msr, startReg, ((ulong)edx << 32) | eax, decodeContext);
                            if (!string.IsNullOrEmpty(decoded))
                                result.Append(decoded);
                        }
```

- [ ] **Step 2: PCI scan**

In `PciScan_DoWork` (`SettingsForm.cs:1916`), replace the body of the `while` loop with:

```csharp
                    while (startReg <= endReg)
                    {
                        var data = cpu.ReadDword(startReg);
                        result.AppendLine($"0x{startReg:X8}: 0x{data:X8} {Convert.ToString(data, 2).PadLeft(32, '0')}");
                        string decoded = RegisterDecoder.Decode(RegisterKind.Pci, startReg, data, decodeContext);
                        if (!string.IsNullOrEmpty(decoded))
                            result.Append(decoded);
                        startReg += 4;
                    }
```

- [ ] **Step 3: CPUID scan**

In `ReadCPUID_Task` (`SettingsForm.cs:2083`), locate the loop that appends each leaf's `EAX EBX ECX EDX` line (around `SettingsForm.cs:2107`–`2118`). Immediately after the `result.AppendLine(...)` that writes a leaf's row, add (use the same `index`, `eax` variables in scope):

```csharp
                        string decoded = RegisterDecoder.Decode(RegisterKind.Cpuid, index, eax, decodeContext);
                        if (!string.IsNullOrEmpty(decoded))
                            result.Append(decoded);
```

If the loop appends rows in more than one place (standard vs extended leaves), add the same three lines after each `AppendLine` that writes a row.

- [ ] **Step 4: Build the app**

Run: `dotnet build ZenStatesDebugTool.csproj`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add SettingsForm.cs
git commit -m "feat: decode recognized registers inline in MSR/PCI/CPUID scans"
```

---

## Task 10: SMU command names in the monitor and Apply

**Files:**
- Modify: `SMUMonitor.cs` (resolve `Cmd`)
- Modify: `SettingsForm.cs` (`ApplySettings` shows command name)

- [ ] **Step 1: Build the name map in SMUMonitor**

In `SMUMonitor.cs`, add a field near the other readonly fields (line ~24):

```csharp
        private readonly IReadOnlyDictionary<uint, string> nameMap;
```

Add `using System.Collections.Generic;` at the top if missing. In the constructor (`SMUMonitor.cs:35`), after `CPU = cpu;` add:

```csharp
            nameMap = SmuCommandNames.Build(SmuDecodeAdapter.ReadMessages(cpu.smu));
```

- [ ] **Step 2: Resolve the Cmd value when building each row**

In `PollOnce` (`SMUMonitor.cs:90`), replace the `Cmd = $"0x{msg:X2}",` assignment in the `new SmuMonitorItem { … }` initializer with:

```csharp
                Cmd = ResolveCmd(msg),
```

Add this helper method to the class:

```csharp
        private string ResolveCmd(uint msg)
        {
            string name = SmuCommandNames.Resolve(nameMap, msg);
            return name != null ? $"0x{msg:X2} ({name})" : $"0x{msg:X2}";
        }
```

- [ ] **Step 3: Show the command name in ApplySettings**

In `ApplySettings` (`SettingsForm.cs:1294`), inside `if (status == SMU.Status.OK) { … }` (around `SettingsForm.cs:1329`), before `ShowResultMessageBox(args);` add:

```csharp
                    string cmdName = SmuCommandNames.Resolve(smuNameMap, command);
                    if (cmdName != null)
                        PrependResult($"CMD: 0x{command:X} ({cmdName}){Environment.NewLine}");
```

- [ ] **Step 4: Build the app**

Run: `dotnet build ZenStatesDebugTool.csproj`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add SMUMonitor.cs SettingsForm.cs
git commit -m "feat: resolve SMU command IDs to names in monitor and Apply"
```

---

## Task 11: PMTable Name + Scaled columns

**Files:**
- Modify: `PowerTableMonitor.cs` (add columns, populate from structure)

Keep the existing `Index`, `Offset`, `Value`, `Max` columns unchanged. Add `Name` and `Scaled` after them; the grid auto-generates columns from the item properties in declaration order.

- [ ] **Step 1: Add the two properties to the row item**

In `PowerTableMonitor.cs`, extend `PowerMonitorItem` (line ~17) so it ends with:

```csharp
        private class PowerMonitorItem
        {
            public string Index { get; set; }
            public string Offset { get; set; }
            public string Value { get; set; }
            public string Max { get; set; }
            public string Name { get; set; }
            public string Scaled { get; set; }
        }
```

- [ ] **Step 2: Cache the structure once**

Add a field near `maxes` (line ~16):

```csharp
        private IReadOnlyDictionary<uint, SensorInfo> structure;
```

Add `using System.Collections.Generic;` at the top if missing. In the constructor (`PowerTableMonitor.cs:76`), after `CPU = cpu;` add:

```csharp
            lock (Hardware.Sync)
                structure = SmuDecodeAdapter.GetPmTableStructure(cpu);
```

(The existing constructor already does a locked `cpu.RefreshPowerTable()` right after; placing this adjacent keeps all locked startup work together. `structure` may be null when the layout is undefined.)

- [ ] **Step 3: Populate Name/Scaled in FillInData**

In `FillInData` (`PowerTableMonitor.cs:25`), replace the `list.Add(new PowerMonitorItem { … })` block with:

```csharp
                uint offset = (uint)(i * 4);
                string name = "";
                string scaled = "";
                if (structure != null && structure.TryGetValue(offset, out SensorInfo info))
                {
                    name = info.Name ?? "";
                    scaled = (table[i] * info.Scale).ToString("F3", CultureInfo.InvariantCulture);
                }

                list.Add(new PowerMonitorItem
                {
                    Index = $"{i:D4}",
                    Offset = $"0x{(i * 4):X4}",
                    Value = valueStr,
                    Max = valueStr,
                    Name = name,
                    Scaled = scaled,
                });
```

- [ ] **Step 4: Refresh the Scaled value each tick**

In `RefreshData` (`PowerTableMonitor.cs:44`), inside the `for` loop after `item.Value = current.ToString(...)`, add:

```csharp
                if (structure != null && structure.TryGetValue((uint)(index * 4), out SensorInfo info))
                    item.Scaled = (current * info.Scale).ToString("F3", CultureInfo.InvariantCulture);
```

(`Name` is static, so it only needs setting in `FillInData`.)

- [ ] **Step 5: Build the app**

Run: `dotnet build ZenStatesDebugTool.csproj`
Expected: Build succeeded.

- [ ] **Step 6: Commit**

```bash
git add PowerTableMonitor.cs
git commit -m "feat: add Name and Scaled columns to PMTable view"
```

---

## Task 12: Full verification

**Files:** none (verification only)

- [ ] **Step 1: Run the full test suite**

Run: `dotnet test Tests/ProfileCore.Tests/ProfileCore.Tests.csproj`
Expected: All tests PASS (the prior suite plus the new RegisterDecoder, SmuCommandNames, and PmTableLabeling tests).

- [ ] **Step 2: Build the app in Release**

Run: `dotnet build ZenStatesDebugTool.csproj -c Release`
Expected: Build succeeded, no warnings introduced by the new files.

- [ ] **Step 3: Manual smoke test (requires AMD hardware + admin)**

Document results in the PR description. On a Ryzen machine, run the app as administrator and verify:
- MSR tab: read `0xC0010064` → results pane shows `PStateDef0 (0xC0010064)` with CpuFid/CpuDfsId/CpuVid/PstateEn lines, plus `Frequency` and `Voltage` matching the P-state tab.
- CPUID tab: read leaf `0x00000001` → shows family/model/stepping fields.
- PCI tab: read an arbitrary address → raw output only (no decode), confirming the unknown-fallback.
- MSR scan over `0xC0010064`–`0xC001006B` → each P-state row followed by its decode block.
- SMU monitor: trigger SMU activity → `Cmd` column shows `0xNN (Name)` for recognized commands.
- PMTable window: `Name` and `Scaled` columns populated on a supported firmware; blank (raw still shown) when `IsPmTableLayoutDefined` is false.

- [ ] **Step 4: Final confirmation commit (if any docs updated)**

```bash
git add -A
git commit -m "docs: record register-decode manual verification results"
```

---

## Self-Review notes (author)

- **Spec coverage:** decode core (Tasks 1–4), SMU names (Tasks 5, 10), PMTable (Tasks 6, 11), scans (Task 9), single reads (Task 8), adapter/voltage-plane/PstateEn (Task 7). PCI stays raw beyond the catalog by design (Decode returns "" for uncatalogued addresses). All spec sections map to a task.
- **Voltage:** never hardcoded — injected via `DecodeContext.VidToVoltage` from `SmuDecodeAdapter.GetVidToVoltage(codeName)`, selecting SVI2 vs SVI3 per the spec.
- **Test isolation:** only pure files (Tasks 1–6) are linked into the test project; `SmuDecodeAdapter` (ZenStates-dependent) is app-only and build-verified.
- **Type consistency:** `RegisterKind`, `DecodeContext`, `FieldDefinition`, `RegisterDefinition`, `RegisterDecoder.Decode/Extract`, `RegisterCatalog.TryGet/Add`, `SmuCommandNames.Build/Resolve`, `SensorInfo`, `LabeledRow`, `PmTableLabeling.Label`, and `SmuDecodeAdapter.*` are used consistently across tasks.
