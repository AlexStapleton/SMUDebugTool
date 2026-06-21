# Multiple Profiles + External Activation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the single Curve Optimizer profile with multiple named JSON profiles, add PBO power-limit (PPT/TDC/EDC/scalar) tuning, and let external programs activate a profile via `SMUDebugTool.exe --applyprofile <name>` (silent apply, then exit).

**Architecture:** Extract a WinForms-free profile core (`Profile` data model, `ProfileManager` disk layer, `ProfileApplier` hardware applier) so the logic is unit-testable without a CPU and reused by both the GUI and the headless CLI path. `SettingsForm` and `Program.Main` delegate to these classes.

**Tech Stack:** C# / .NET Framework 4.5, WinForms, Newtonsoft.Json (already referenced), `ZenStates-Core.dll` (prebuilt), Microsoft.Win32.TaskScheduler. Unit tests in a separate SDK-style xUnit project run via `dotnet test`.

---

## Spec

See [docs/superpowers/specs/2026-06-21-multiple-profiles-design.md](../specs/2026-06-21-multiple-profiles-design.md).

## Design Decisions (locked at plan time)

- **Namespace:** new classes live in `ZenStatesDebugTool.Profiles`, files under `Profiles/`.
- **`frequency` field deferred.** The spec listed "frequency," but the only frequency
  control is the manual-OC `SetFrequencyAllCore`/`SetFrequencySingleCore` path — a distinct,
  riskier subsystem with no clean read-back. `Profile` fields are nullable/optional, so a
  `Frequency` field can be added later without breaking existing JSON. The first cut
  captures: **CO margins, Curve Shaper margins, fmax, PPT, TDC, EDC, PBO scalar.**
- **New UI controls are created programmatically** (like the existing `BuildCoActionBar`),
  NOT via the WinForms designer. The `.Designer.cs` (~200 KB) and `.resx` are left untouched
  to avoid designer/resx corruption.
- **Profile core stays dependency-light:** `Profile.cs` and `ProfileManager.cs` reference
  only `System.*` + `Newtonsoft.Json` (NO WinForms, NO ZenStates.Core), so the test project
  can compile them directly by linking the source files.

## Build & Test Commands (use these exact commands)

Build the main app (PowerShell tool):

```powershell
$msbuild = "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
& $msbuild "I:\Coding-Projects\SMUDebugTool-master\ZenStatesDebugTool.sln" /p:Configuration=Debug /v:m
```
Expected: `Build succeeded. 0 Error(s)`.
> If it fails with missing `Newtonsoft.Json`/`TaskScheduler` packages, the `packages\` folder
> needs restoring — open the solution once in Visual Studio, or run `nuget restore` if
> `nuget.exe` is available. The unit-test project below does NOT need this.

Run unit tests (PowerShell tool):

```powershell
dotnet test "I:\Coding-Projects\SMUDebugTool-master\Tests\ProfileCore.Tests\ProfileCore.Tests.csproj"
```

## File Structure

| File | Responsibility |
|------|----------------|
| `Profiles/Profile.cs` (new) | Plain JSON-serializable data model. No deps beyond `System.*`. |
| `Profiles/ProfileManager.cs` (new) | Disk layer: list/load/save/delete + legacy migration + name validation. Deps: `System.*`, Newtonsoft.Json. |
| `Profiles/ProfileApplier.cs` (new) | Applies a `Profile` to hardware via `Cpu`. Deps: ZenStates.Core. Not unit-tested. |
| `Tests/ProfileCore.Tests/ProfileCore.Tests.csproj` (new) | SDK-style xUnit project; links `Profile.cs` + `ProfileManager.cs`. |
| `Tests/ProfileCore.Tests/ProfileTests.cs` (new) | Round-trip serialization tests. |
| `Tests/ProfileCore.Tests/ProfileManagerTests.cs` (new) | List/load/save/delete/migration tests. |
| `Program.cs` (modify) | Branch to headless apply when `--applyprofile <name>` is present. |
| `SettingsForm.cs` (modify) | Profile dropdown + Apply/Save/Save As/Delete, PBO controls, startup dropdown; delegates to the core classes. |
| `ZenStatesDebugTool.csproj` (modify) | Add the three new `Compile` includes. |

---

## Milestone 1 — Profile core + tests (no app changes yet)

### Task 1: `Profile` model + test project scaffold

**Files:**
- Create: `Profiles/Profile.cs`
- Create: `Tests/ProfileCore.Tests/ProfileCore.Tests.csproj`
- Test: `Tests/ProfileCore.Tests/ProfileTests.cs`

- [ ] **Step 1: Create the `Profile` model**

`Profiles/Profile.cs`:
```csharp
using System.Collections.Generic;

namespace ZenStatesDebugTool.Profiles
{
    public class CurveShaperTier
    {
        public int Low { get; set; }
        public int Medium { get; set; }
        public int High { get; set; }
    }

    public class Profile
    {
        public string Name { get; set; }

        // Core index -> CO margin value.
        public Dictionary<int, int> CoMargins { get; set; } = new Dictionary<int, int>();

        // Exactly 5 tiers in order: min(0), low(1), med(2), high(3), max(4).
        // Null = "do not apply Curve Shaper".
        public List<CurveShaperTier> CurveShaperTiers { get; set; }

        public decimal? Fmax { get; set; }

        // PBO power limits (units verified against the DLL in Task 9).
        public int? PptWatts { get; set; }
        public int? TdcAmps { get; set; }
        public int? EdcAmps { get; set; }
        public int? PboScalar { get; set; }
    }
}
```

- [ ] **Step 2: Create the test project file**

`Tests/ProfileCore.Tests/ProfileCore.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net48</TargetFramework>
    <IsPackable>false</IsPackable>
    <LangVersion>latest</LangVersion>
    <RootNamespace>ProfileCore.Tests</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.11.1" />
    <PackageReference Include="xunit" Version="2.9.2" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.2" />
    <PackageReference Include="Newtonsoft.Json" Version="13.0.3" />
  </ItemGroup>
  <ItemGroup>
    <!-- Compile the WinForms-free core directly into the test assembly. -->
    <Compile Include="..\..\Profiles\Profile.cs" Link="Profile.cs" />
    <Compile Include="..\..\Profiles\ProfileManager.cs" Link="ProfileManager.cs" />
  </ItemGroup>
</Project>
```
> Note: `ProfileManager.cs` is referenced here but is created in Task 2. Until then, build
> the test for this task with only `Profile.cs` — temporarily remove the `ProfileManager.cs`
> line if running Task 1 in isolation. If executing tasks in order, create the empty
> `Profiles/ProfileManager.cs` stub now (Task 2 fills it) so the project compiles.

- [ ] **Step 3: Write the failing test**

`Tests/ProfileCore.Tests/ProfileTests.cs`:
```csharp
using System.Collections.Generic;
using Newtonsoft.Json;
using Xunit;
using ZenStatesDebugTool.Profiles;

namespace ProfileCore.Tests
{
    public class ProfileTests
    {
        [Fact]
        public void Profile_round_trips_through_json()
        {
            var profile = new Profile
            {
                Name = "Gaming",
                CoMargins = new Dictionary<int, int> { { 0, -20 }, { 5, -15 } },
                CurveShaperTiers = new List<CurveShaperTier>
                {
                    new CurveShaperTier { Low = 1, Medium = 2, High = 3 }
                },
                Fmax = 5200m,
                PptWatts = 142,
                TdcAmps = 95,
                EdcAmps = 140,
                PboScalar = 3
            };

            var json = JsonConvert.SerializeObject(profile);
            var restored = JsonConvert.DeserializeObject<Profile>(json);

            Assert.Equal("Gaming", restored.Name);
            Assert.Equal(-20, restored.CoMargins[0]);
            Assert.Equal(-15, restored.CoMargins[5]);
            Assert.Equal(2, restored.CurveShaperTiers[0].Medium);
            Assert.Equal(5200m, restored.Fmax);
            Assert.Equal(142, restored.PptWatts);
            Assert.Equal(3, restored.PboScalar);
        }
    }
}
```

- [ ] **Step 4: Run the test, verify it passes**

Run: `dotnet test "I:\Coding-Projects\SMUDebugTool-master\Tests\ProfileCore.Tests\ProfileCore.Tests.csproj"`
Expected: 1 passing test. (This test exercises only serialization; it passes once the model compiles.)

- [ ] **Step 5: Commit** *(skip if repo is not under git — see handoff note)*

```bash
git add Profiles/Profile.cs Tests/ProfileCore.Tests/
git commit -m "feat: add Profile model and test project scaffold"
```

---

### Task 2: `ProfileManager` save/load/list/delete

**Files:**
- Create: `Profiles/ProfileManager.cs`
- Test: `Tests/ProfileCore.Tests/ProfileManagerTests.cs`

- [ ] **Step 1: Write the failing tests**

`Tests/ProfileCore.Tests/ProfileManagerTests.cs`:
```csharp
using System;
using System.IO;
using System.Linq;
using Xunit;
using ZenStatesDebugTool.Profiles;

namespace ProfileCore.Tests
{
    public class ProfileManagerTests : IDisposable
    {
        private readonly string _dir;
        public ProfileManagerTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "smu_profiles_" + Guid.NewGuid().ToString("N"));
        }
        public void Dispose()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
        }

        [Fact]
        public void Save_then_Load_returns_equivalent_profile()
        {
            var mgr = new ProfileManager(_dir);
            mgr.Save(new Profile { Name = "Quiet", Fmax = 4800m, PptWatts = 88 });

            var loaded = mgr.Load("Quiet");

            Assert.Equal("Quiet", loaded.Name);
            Assert.Equal(4800m, loaded.Fmax);
            Assert.Equal(88, loaded.PptWatts);
        }

        [Fact]
        public void List_returns_saved_names_sorted()
        {
            var mgr = new ProfileManager(_dir);
            mgr.Save(new Profile { Name = "Zulu" });
            mgr.Save(new Profile { Name = "Alpha" });

            Assert.Equal(new[] { "Alpha", "Zulu" }, mgr.List().ToArray());
        }

        [Fact]
        public void Delete_removes_profile()
        {
            var mgr = new ProfileManager(_dir);
            mgr.Save(new Profile { Name = "Temp" });
            mgr.Delete("Temp");
            Assert.Empty(mgr.List());
            Assert.Null(mgr.Load("Temp"));
        }

        [Fact]
        public void Load_missing_returns_null()
        {
            var mgr = new ProfileManager(_dir);
            Assert.Null(mgr.Load("Nope"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("bad/name")]
        [InlineData("bad:name")]
        public void IsValidName_rejects_invalid(string name)
        {
            Assert.False(ProfileManager.IsValidName(name));
        }

        [Fact]
        public void Save_invalid_name_throws()
        {
            var mgr = new ProfileManager(_dir);
            Assert.Throws<ArgumentException>(() => mgr.Save(new Profile { Name = "a/b" }));
        }
    }
}
```

- [ ] **Step 2: Run tests, verify they fail to compile**

Run: `dotnet test "I:\Coding-Projects\SMUDebugTool-master\Tests\ProfileCore.Tests\ProfileCore.Tests.csproj"`
Expected: build error — `ProfileManager` does not exist (or stub has no members).

- [ ] **Step 3: Implement `ProfileManager`**

`Profiles/ProfileManager.cs`:
```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json;

namespace ZenStatesDebugTool.Profiles
{
    public class ProfileManager
    {
        public const string LegacyFileName = "co_profile.txt";
        private readonly string _dir;

        public ProfileManager(string profilesDirectory)
        {
            _dir = profilesDirectory ?? throw new ArgumentNullException(nameof(profilesDirectory));
        }

        public static bool IsValidName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;
            return name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }

        public void EnsureDirectory()
        {
            if (!Directory.Exists(_dir)) Directory.CreateDirectory(_dir);
        }

        private string PathFor(string name) => Path.Combine(_dir, name + ".json");

        public IEnumerable<string> List()
        {
            if (!Directory.Exists(_dir)) return new List<string>();
            return Directory.GetFiles(_dir, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public Profile Load(string name)
        {
            if (!IsValidName(name)) throw new ArgumentException("Invalid profile name.", nameof(name));
            var path = PathFor(name);
            if (!File.Exists(path)) return null;
            var profile = JsonConvert.DeserializeObject<Profile>(File.ReadAllText(path));
            if (profile != null) profile.Name = name; // filename is the source of truth
            return profile;
        }

        public void Save(Profile profile)
        {
            if (profile == null) throw new ArgumentNullException(nameof(profile));
            if (!IsValidName(profile.Name)) throw new ArgumentException("Invalid profile name.");
            EnsureDirectory();
            File.WriteAllText(PathFor(profile.Name),
                JsonConvert.SerializeObject(profile, Formatting.Indented));
        }

        public void Delete(string name)
        {
            if (!IsValidName(name)) throw new ArgumentException("Invalid profile name.");
            var path = PathFor(name);
            if (File.Exists(path)) File.Delete(path);
        }

        // --- Legacy migration (Task 3 adds tests/behavior) ---

        public bool MigrateLegacyIfNeeded()
        {
            EnsureDirectory();
            if (List().Any()) return false;
            var legacy = Path.Combine(_dir, LegacyFileName);
            if (!File.Exists(legacy)) return false;
            var profile = ParseLegacy(File.ReadAllLines(legacy));
            Save(profile);
            return true;
        }

        public static Profile ParseLegacy(IEnumerable<string> lines)
        {
            var profile = new Profile { Name = "Default" };
            foreach (var line in lines)
            {
                if (line.StartsWith("["))
                {
                    var values = line.Replace("[", "").Replace("]", "").Replace(" ", "").Split(',');
                    if (values.Length == 2
                        && int.TryParse(values[0], out int index)
                        && int.TryParse(values[1], out int margin))
                    {
                        profile.CoMargins[index] = margin;
                    }
                }
                else if (line.StartsWith("fmax="))
                {
                    if (decimal.TryParse(line.Substring(5), NumberStyles.Any,
                            CultureInfo.InvariantCulture, out decimal fmax))
                    {
                        profile.Fmax = fmax;
                    }
                }
            }
            return profile;
        }
    }
}
```

- [ ] **Step 4: Run tests, verify they pass**

Run: `dotnet test "I:\Coding-Projects\SMUDebugTool-master\Tests\ProfileCore.Tests\ProfileCore.Tests.csproj"`
Expected: all tests pass.

- [ ] **Step 5: Commit**

```bash
git add Profiles/ProfileManager.cs Tests/ProfileCore.Tests/ProfileManagerTests.cs
git commit -m "feat: add ProfileManager disk layer with tests"
```

---

### Task 3: Legacy migration

**Files:**
- Modify: `Profiles/ProfileManager.cs` (already has `ParseLegacy`/`MigrateLegacyIfNeeded` from Task 2)
- Test: `Tests/ProfileCore.Tests/ProfileManagerTests.cs`

- [ ] **Step 1: Add failing migration tests**

Append to `ProfileManagerTests`:
```csharp
        [Fact]
        public void ParseLegacy_reads_margins_and_fmax()
        {
            var lines = new[] { "[0,-25]", "[3,-10]", "fmax=5050" };
            var profile = ProfileManager.ParseLegacy(lines);

            Assert.Equal("Default", profile.Name);
            Assert.Equal(-25, profile.CoMargins[0]);
            Assert.Equal(-10, profile.CoMargins[3]);
            Assert.Equal(5050m, profile.Fmax);
        }

        [Fact]
        public void MigrateLegacyIfNeeded_creates_Default_from_legacy_file()
        {
            Directory.CreateDirectory(_dir);
            File.WriteAllLines(Path.Combine(_dir, ProfileManager.LegacyFileName),
                new[] { "[0,-20]", "fmax=4900" });
            var mgr = new ProfileManager(_dir);

            bool migrated = mgr.MigrateLegacyIfNeeded();

            Assert.True(migrated);
            var loaded = mgr.Load("Default");
            Assert.Equal(-20, loaded.CoMargins[0]);
            Assert.Equal(4900m, loaded.Fmax);
        }

        [Fact]
        public void MigrateLegacyIfNeeded_is_noop_when_profiles_exist()
        {
            var mgr = new ProfileManager(_dir);
            mgr.Save(new Profile { Name = "Existing" });
            File.WriteAllLines(Path.Combine(_dir, ProfileManager.LegacyFileName), new[] { "[0,-20]" });

            Assert.False(mgr.MigrateLegacyIfNeeded());
        }
```

- [ ] **Step 2: Run tests, verify pass**

Run: `dotnet test "I:\Coding-Projects\SMUDebugTool-master\Tests\ProfileCore.Tests\ProfileCore.Tests.csproj"`
Expected: all tests pass (the implementation already exists from Task 2).

- [ ] **Step 3: Commit**

```bash
git add Tests/ProfileCore.Tests/ProfileManagerTests.cs
git commit -m "test: cover legacy co_profile.txt migration"
```

---

## Milestone 2 — Hardware applier

### Task 4: `ProfileApplier` + wire new files into the app build

**Files:**
- Create: `Profiles/ProfileApplier.cs`
- Modify: `ZenStatesDebugTool.csproj` (add three `Compile` includes)

> No automated test: this class calls into `ZenStates-Core.dll`, which needs real hardware.
> It is verified by (a) a clean compile of the main app and (b) manual hardware testing later.
> The PBO setters are intentionally left as guarded no-ops here and filled in Task 9 after the
> DLL signatures are verified.

- [ ] **Step 1: Create `ProfileApplier`**

`Profiles/ProfileApplier.cs`:
```csharp
using System.Collections.Generic;
using ZenStates.Core;

namespace ZenStatesDebugTool.Profiles
{
    public class ApplyResult
    {
        public bool Success { get; private set; } = true;
        public List<string> Messages { get; } = new List<string>();
        public void Fail(string m) { Success = false; Messages.Add(m); }
        public void Info(string m) { Messages.Add(m); }
    }

    public class ProfileApplier
    {
        public ApplyResult Apply(Profile profile, Cpu cpu)
        {
            var result = new ApplyResult();
            if (profile == null) { result.Fail("Profile is null."); return result; }
            if (cpu == null) { result.Fail("CPU not available."); return result; }

            ApplyCoMargins(profile, cpu, result);
            ApplyFmax(profile, cpu, result);
            ApplyCurveShaper(profile, cpu, result);
            ApplyPboLimits(profile, cpu, result); // filled in Task 9
            return result;
        }

        private void ApplyCoMargins(Profile p, Cpu cpu, ApplyResult r)
        {
            if (p.CoMargins == null || p.CoMargins.Count == 0) return;
            if (cpu.smu.Rsmu.SMU_MSG_SetDldoPsmMargin == 0) { r.Info("CO not supported; skipped."); return; }
            foreach (var kv in p.CoMargins)
            {
                if (!IsCoreEnabled(cpu, kv.Key)) continue;
                cpu.SetPsmMarginSingleCore(EncodeCoreMarginBitmask(cpu, kv.Key), kv.Value);
            }
        }

        private void ApplyFmax(Profile p, Cpu cpu, ApplyResult r)
        {
            if (!p.Fmax.HasValue) return;
            if (!cpu.SetFMax((uint)p.Fmax.Value)) r.Fail("Failed to set fmax.");
        }

        private void ApplyCurveShaper(Profile p, Cpu cpu, ApplyResult r)
        {
            if (p.CurveShaperTiers == null) return;
            for (int tier = 0; tier < p.CurveShaperTiers.Count && tier < 5; tier++)
            {
                var t = p.CurveShaperTiers[tier];
                if (t == null) continue;
                if (cpu.SetCurveShaperMargin(marginHigh: t.High, marginMedium: t.Medium,
                        marginLow: t.Low, tier) != SMU.Status.OK)
                    r.Fail($"Failed to set Curve Shaper tier {tier}.");
            }
        }

        private void ApplyPboLimits(Profile p, Cpu cpu, ApplyResult r)
        {
            // Filled in Task 9 after verifying SetPPTLimit / SetTDCSOCLimit /
            // SetEDCSOCLimit / SetPBOScalar signatures and units against the DLL.
        }

        // Replicated from SettingsForm so the apply path is form-independent.
        private static bool IsCoreEnabled(Cpu cpu, int coreIndex)
        {
            int mapIndex = coreIndex / 8;
            int coreInGroup = coreIndex % 8;
            return mapIndex >= 0
                && mapIndex < cpu.info.topology.coreDisableMap.Length
                && ((~cpu.info.topology.coreDisableMap[mapIndex] >> coreInGroup) & 1) == 1;
        }

        private static uint EncodeCoreMarginBitmask(Cpu cpu, int coreIndex, int coresPerCCD = 8)
        {
            if (cpu.smu.SMU_TYPE >= SMU.SmuType.TYPE_APU0 && cpu.smu.SMU_TYPE <= SMU.SmuType.TYPE_APU2)
                return (uint)coreIndex;
            int ccdIndex = coreIndex / coresPerCCD;
            int localCoreIndex = coreIndex % coresPerCCD;
            int ccdMask = ccdIndex << 8;
            int mask = ccdMask | localCoreIndex;
            return (uint)(mask << 20);
        }
    }
}
```

- [ ] **Step 2: Add the three files to the app project**

In `ZenStatesDebugTool.csproj`, inside the first `<ItemGroup>` that holds `<Compile Include=...>`
entries (the one starting at `CpuSingleton.cs`), add:
```xml
    <Compile Include="Profiles\Profile.cs" />
    <Compile Include="Profiles\ProfileManager.cs" />
    <Compile Include="Profiles\ProfileApplier.cs" />
```

- [ ] **Step 3: Build the app, verify it compiles**

```powershell
$msbuild = "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
& $msbuild "I:\Coding-Projects\SMUDebugTool-master\ZenStatesDebugTool.sln" /p:Configuration=Debug /v:m
```
Expected: `Build succeeded. 0 Error(s)`.
> If `SetCurveShaperMargin`, `SetFMax`, or `SetPsmMarginSingleCore` signatures don't match,
> compare against their existing call sites in `SettingsForm.cs` (lines ~220, ~2314, ~2381)
> and adjust.

- [ ] **Step 4: Commit**

```bash
git add Profiles/ProfileApplier.cs ZenStatesDebugTool.csproj
git commit -m "feat: add ProfileApplier and wire profile core into app build"
```

---

## Milestone 3 — GUI: multiple-profile management

### Task 5: Initialize `ProfileManager`/`ProfileApplier` in the form + migrate on startup

**Files:**
- Modify: `SettingsForm.cs` (fields near line 36-45; constructor near line 53-66)

- [ ] **Step 1: Add fields**

In `SettingsForm.cs`, near the existing `private readonly string profilesPath;` (line 36), add:
```csharp
        private ProfileManager profileManager;
        private readonly ProfileApplier profileApplier = new ProfileApplier();
        private ComboBox comboBoxProfiles;
```
Add `using ZenStatesDebugTool.Profiles;` to the using block at the top of the file.

- [ ] **Step 2: Initialize + migrate in the constructor**

In the constructor `try` block (after `profilesPath = ...; defaultsPath = ...;`, line ~56),
before `cpu = new Cpu();`, add:
```csharp
                profileManager = new ProfileManager(profilesPath);
                profileManager.EnsureDirectory();
                profileManager.MigrateLegacyIfNeeded();
```

- [ ] **Step 3: Build, verify compiles**

```powershell
$msbuild = "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
& $msbuild "I:\Coding-Projects\SMUDebugTool-master\ZenStatesDebugTool.sln" /p:Configuration=Debug /v:m
```
Expected: `Build succeeded.`

- [ ] **Step 4: Commit**

```bash
git add SettingsForm.cs
git commit -m "feat: init ProfileManager and migrate legacy profile on startup"
```

---

### Task 6: Gather/apply profile <-> UI helpers

**Files:**
- Modify: `SettingsForm.cs` (add methods; place them near the existing `BtnSaveCOProfile_Click`, ~line 1986)

> These two methods are the bridge between the UI controls and the `Profile` model. `coControls`
> (CO `NumericUpDown`s) and the `cs_*` Curve Shaper controls already exist. PBO controls are
> added in Task 9; the `// PBO:` lines below are wired then.

- [ ] **Step 1: Add `GatherProfileFromUi`**

```csharp
        private Profile GatherProfileFromUi(string name)
        {
            var profile = new Profile { Name = name };

            if (cpu.smu.Rsmu.SMU_MSG_SetDldoPsmMargin != 0)
            {
                for (var i = 0; i < GetPhysicalCoreCount(); i++)
                {
                    NumericUpDown control = GetCOControl(i);
                    if (control != null && control.Enabled)
                        profile.CoMargins[i] = Convert.ToInt32(control.Value);
                }
            }

            profile.CurveShaperTiers = new List<CurveShaperTier>
            {
                new CurveShaperTier { Low = (int)cs_min_low.Value,  Medium = (int)cs_min_med.Value,  High = (int)cs_min_high.Value },
                new CurveShaperTier { Low = (int)cs_low_low.Value,  Medium = (int)cs_low_med.Value,  High = (int)cs_low_high.Value },
                new CurveShaperTier { Low = (int)cs_med_low.Value,  Medium = (int)cs_med_med.Value,  High = (int)cs_med_high.Value },
                new CurveShaperTier { Low = (int)cs_high_low.Value, Medium = (int)cs_high_med.Value, High = (int)cs_high_high.Value },
                new CurveShaperTier { Low = (int)cs_max_low.Value,  Medium = (int)cs_max_med.Value,  High = (int)cs_max_high.Value },
            };

            profile.Fmax = numericUpDownFmax.Value;

            // PBO: filled in Task 9
            // profile.PptWatts = (int)numericUpDownPpt.Value; etc.

            return profile;
        }
```

- [ ] **Step 2: Add `ApplyProfileToUi`**

```csharp
        private void ApplyProfileToUi(Profile profile)
        {
            if (profile == null) return;

            if (profile.CoMargins != null && cpu.smu.Rsmu.SMU_MSG_SetDldoPsmMargin != 0)
            {
                foreach (var kv in profile.CoMargins)
                {
                    NumericUpDown control = GetCOControl(kv.Key);
                    if (control != null && control.Enabled)
                        control.Value = Math.Max(control.Minimum, Math.Min(control.Maximum, kv.Value));
                }
            }

            if (profile.CurveShaperTiers != null && profile.CurveShaperTiers.Count >= 5)
            {
                SetCsTier(cs_min_low,  cs_min_med,  cs_min_high,  profile.CurveShaperTiers[0]);
                SetCsTier(cs_low_low,  cs_low_med,  cs_low_high,  profile.CurveShaperTiers[1]);
                SetCsTier(cs_med_low,  cs_med_med,  cs_med_high,  profile.CurveShaperTiers[2]);
                SetCsTier(cs_high_low, cs_high_med, cs_high_high, profile.CurveShaperTiers[3]);
                SetCsTier(cs_max_low,  cs_max_med,  cs_max_high,  profile.CurveShaperTiers[4]);
            }

            if (profile.Fmax.HasValue)
                numericUpDownFmax.Value =
                    Math.Max(numericUpDownFmax.Minimum, Math.Min(numericUpDownFmax.Maximum, profile.Fmax.Value));

            // PBO: filled in Task 9
        }

        private static void SetCsTier(NumericUpDown low, NumericUpDown med, NumericUpDown high, CurveShaperTier tier)
        {
            if (tier == null) return;
            low.Value = Math.Max(low.Minimum, Math.Min(low.Maximum, tier.Low));
            med.Value = Math.Max(med.Minimum, Math.Min(med.Maximum, tier.Medium));
            high.Value = Math.Max(high.Minimum, Math.Min(high.Maximum, tier.High));
        }
```

- [ ] **Step 3: Build, verify compiles**

```powershell
$msbuild = "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
& $msbuild "I:\Coding-Projects\SMUDebugTool-master\ZenStatesDebugTool.sln" /p:Configuration=Debug /v:m
```
Expected: `Build succeeded.`
> If a `cs_*` control name doesn't resolve, confirm the exact names in `InitCS` (`SettingsForm.cs` ~line 321-339).

- [ ] **Step 4: Commit**

```bash
git add SettingsForm.cs
git commit -m "feat: add UI<->Profile gather/apply helpers"
```

---

### Task 7: Profile dropdown + Apply / Save / Save As / Delete buttons

**Files:**
- Modify: `SettingsForm.cs` (`BuildCoActionBar`, ~line 385-428)

- [ ] **Step 1: Extend the action bar**

At the end of `BuildCoActionBar` (after `flowLayoutPanelCcdActions.Controls.Add(allIncBtn);`,
line 427), add:
```csharp
            comboBoxProfiles = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 140,
                Margin = new Padding(12, 0, 6, 0)
            };
            comboBoxProfiles.SelectedIndexChanged += ComboBoxProfiles_SelectedIndexChanged;

            Button applyProfileBtn = MakeBarButton("Apply Profile", ButtonApplyProfile_Click);
            Button saveBtn = MakeBarButton("Save", ButtonSaveProfile_Click);
            Button saveAsBtn = MakeBarButton("Save As…", ButtonSaveAsProfile_Click);
            Button deleteBtn = MakeBarButton("Delete", ButtonDeleteProfile_Click);

            flowLayoutPanelCcdActions.Controls.Add(comboBoxProfiles);
            flowLayoutPanelCcdActions.Controls.Add(applyProfileBtn);
            flowLayoutPanelCcdActions.Controls.Add(saveBtn);
            flowLayoutPanelCcdActions.Controls.Add(saveAsBtn);
            flowLayoutPanelCcdActions.Controls.Add(deleteBtn);

            RefreshProfileList(null);
        }

        private Button MakeBarButton(string text, EventHandler onClick)
        {
            var btn = new Button
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 0, 4, 0),
                Padding = new Padding(6, 0, 6, 0),
                Text = text,
                UseVisualStyleBackColor = true
            };
            btn.Click += onClick;
            return btn;
        }
```
> Note: the closing brace `}` shown above replaces the original closing brace of
> `BuildCoActionBar`. Make sure the method has exactly one closing brace.

- [ ] **Step 2: Add the handlers + list refresh**

Add these methods to the form (e.g. right after `BuildCoActionBar`):
```csharp
        private void RefreshProfileList(string select)
        {
            if (comboBoxProfiles == null) return;
            comboBoxProfiles.SelectedIndexChanged -= ComboBoxProfiles_SelectedIndexChanged;
            comboBoxProfiles.Items.Clear();
            foreach (var n in profileManager.List())
                comboBoxProfiles.Items.Add(n);
            if (select != null && comboBoxProfiles.Items.Contains(select))
                comboBoxProfiles.SelectedItem = select;
            else if (comboBoxProfiles.Items.Count > 0)
                comboBoxProfiles.SelectedIndex = 0;
            comboBoxProfiles.SelectedIndexChanged += ComboBoxProfiles_SelectedIndexChanged;
        }

        private string SelectedProfileName => comboBoxProfiles?.SelectedItem as string;

        private void ComboBoxProfiles_SelectedIndexChanged(object sender, EventArgs e)
        {
            var name = SelectedProfileName;
            if (string.IsNullOrEmpty(name)) return;
            var profile = profileManager.Load(name);
            ApplyProfileToUi(profile);
            SetStatusText($"Profile '{name}' loaded into the form. Use 'Apply Profile' to apply to CPU.");
        }

        private void ButtonApplyProfile_Click(object sender, EventArgs e)
        {
            var name = SelectedProfileName;
            if (string.IsNullOrEmpty(name)) { HandleError("No profile selected."); return; }
            var result = profileApplier.Apply(profileManager.Load(name), cpu);
            SetStatusText(result.Success
                ? $"Profile '{name}' applied."
                : "Apply finished with errors: " + string.Join("; ", result.Messages));
        }

        private void ButtonSaveProfile_Click(object sender, EventArgs e)
        {
            var name = SelectedProfileName;
            if (string.IsNullOrEmpty(name)) { ButtonSaveAsProfile_Click(sender, e); return; }
            profileManager.Save(GatherProfileFromUi(name));
            SetStatusText($"Profile '{name}' saved.");
        }

        private void ButtonSaveAsProfile_Click(object sender, EventArgs e)
        {
            string name = PromptForProfileName();
            if (name == null) return;
            if (!ProfileManager.IsValidName(name)) { HandleError("Invalid profile name."); return; }
            profileManager.Save(GatherProfileFromUi(name));
            RefreshProfileList(name);
            SetStatusText($"Profile '{name}' saved.");
        }

        private void ButtonDeleteProfile_Click(object sender, EventArgs e)
        {
            var name = SelectedProfileName;
            if (string.IsNullOrEmpty(name)) return;
            if (MessageBox.Show($"Delete profile '{name}'?", "Confirm",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            profileManager.Delete(name);
            RefreshProfileList(null);
            SetStatusText($"Profile '{name}' deleted.");
        }

        private string PromptForProfileName()
        {
            using (var form = new Form())
            using (var input = new TextBox { Dock = DockStyle.Top })
            using (var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Dock = DockStyle.Bottom })
            {
                form.Text = "Profile name";
                form.Width = 300;
                form.Height = 120;
                form.FormBorderStyle = FormBorderStyle.FixedDialog;
                form.StartPosition = FormStartPosition.CenterParent;
                form.AcceptButton = ok;
                form.Controls.Add(input);
                form.Controls.Add(ok);
                return form.ShowDialog(this) == DialogResult.OK && input.Text.Trim().Length > 0
                    ? input.Text.Trim()
                    : null;
            }
        }
```

- [ ] **Step 3: Build, verify compiles**

```powershell
$msbuild = "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
& $msbuild "I:\Coding-Projects\SMUDebugTool-master\ZenStatesDebugTool.sln" /p:Configuration=Debug /v:m
```
Expected: `Build succeeded.`

- [ ] **Step 4: Manual verification (hardware)**

Run `bin\Debug\SMUDebugTool.exe`, go to the PBO tab:
- The action bar shows a profile dropdown + Apply Profile / Save / Save As… / Delete.
- "Save As…" with a name creates `profiles\<name>.json`; it appears in the dropdown.
- Selecting a profile loads its values into the CO + Curve Shaper + fmax controls.
- "Delete" removes it.
Confirm `profiles\<name>.json` content looks correct.

- [ ] **Step 5: Commit**

```bash
git add SettingsForm.cs
git commit -m "feat: profile dropdown with apply/save/save-as/delete"
```

---

## Milestone 4 — External activation (headless CLI)

### Task 8: `--applyprofile <name>` headless apply + exit codes

**Files:**
- Modify: `Program.cs` (full replacement below)

- [ ] **Step 1: Replace `Program.cs`**

```csharp
using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using ZenStates.Core;
using ZenStatesDebugTool.Profiles;

namespace ZenStatesDebugTool
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            string[] args = Environment.GetCommandLineArgs();
            string profileName = GetApplyProfileName(args);
            bool isApply = args.Any(a => a.ToLower() == "--applyprofile");

            if (isApply)
            {
                Environment.Exit(RunHeadlessApply(profileName));
                return;
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += ApplicationThreadException;
            try
            {
                Form MainForm = new SettingsForm();
                string appString = $"{Application.ProductName} {Application.ProductVersion.Substring(0, Application.ProductVersion.LastIndexOf('.'))}";
#if DEBUG
                appString += " (debug)";
#endif
                MainForm.Text = appString;
                Application.Run(MainForm);
            }
            catch (ApplicationException ex)
            {
                Console.WriteLine(ex.Message);
                Application.Exit();
            }
        }

        // Returns the token after "--applyprofile", or null if none/absent.
        private static string GetApplyProfileName(string[] args)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i].ToLower() == "--applyprofile")
                {
                    string next = args[i + 1];
                    return next.StartsWith("--") ? null : next;
                }
            }
            return null;
        }

        // 0 = success, 1 = failure. No UI in this path.
        private static int RunHeadlessApply(string profileName)
        {
            string profilesPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "profiles");
            string logPath = Path.Combine(profilesPath, "apply.log");

            if (string.IsNullOrEmpty(profileName))
            {
                Log(logPath, "No profile name supplied to --applyprofile; doing nothing.");
                return 1;
            }

            Cpu cpu = null;
            try
            {
                var manager = new ProfileManager(profilesPath);
                var profile = manager.Load(profileName);
                if (profile == null)
                {
                    Log(logPath, $"Profile '{profileName}' not found.");
                    return 1;
                }

                cpu = new Cpu();
                var result = new ProfileApplier().Apply(profile, cpu);
                Log(logPath, $"Applied '{profileName}'. Success={result.Success}. "
                    + string.Join("; ", result.Messages));
                return result.Success ? 0 : 1;
            }
            catch (Exception ex)
            {
                Log(logPath, $"Error applying '{profileName}': {ex.Message}");
                return 1;
            }
            finally
            {
                cpu?.Dispose();
            }
        }

        private static void Log(string logPath, string message)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(logPath));
                File.AppendAllText(logPath, $"{DateTime.Now:s} {message}{Environment.NewLine}");
            }
            catch { /* logging must never throw in the headless path */ }
        }

        static void ApplicationThreadException(object sender, System.Threading.ThreadExceptionEventArgs e)
        {
            MessageBox.Show(e.Exception.Message, Properties.Resources.Error);
        }
    }
}
```

- [ ] **Step 2: Remove the now-dead in-form apply path**

In `SettingsForm.cs`, the constructor reads `--applyprofile` into `isApplyProfile` (lines 58-62)
and `InitForm` calls `ApplyCOProfile()` when set (lines 198-203). Since activation is now
headless and never constructs the form, delete that behavior:
- Remove the `args`/`isApplyProfile` loop (lines 58-62) and the `private readonly string[] args;`
  / `private readonly bool isApplyProfile;` fields (lines 43-44).
- Remove the `if (isApplyProfile) { ApplyCOProfile(); InitPBO(); tabControl1.SelectedTab = tabPagePbo; }`
  block (lines 198-203).
- Delete the now-unused `ApplyCOProfile()` method (lines 208-224) and `defaultsPath` field +
  its assignment (only referenced by legacy save/load, replaced by the profile UI).
> If `defaultsPath` or `LoadCOProfile`/`BtnSaveCOProfile_Click`/`BtnLoadCOProfile_Click` are
> still referenced by designer-wired buttons, leave those methods in place for now (they are
> superseded by the profile UI but harmless); only remove the `isApplyProfile` startup path.

- [ ] **Step 3: Build, verify compiles**

```powershell
$msbuild = "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
& $msbuild "I:\Coding-Projects\SMUDebugTool-master\ZenStatesDebugTool.sln" /p:Configuration=Debug /v:m
```
Expected: `Build succeeded.`

- [ ] **Step 4: Manual verification**

From an elevated prompt in `bin\Debug`:
- `SMUDebugTool.exe --applyprofile Default` → no window appears; `profiles\apply.log` gets a
  `Success=True` line; `echo %ERRORLEVEL%` (cmd) or `$LASTEXITCODE` (pwsh) is `0`.
- `SMUDebugTool.exe --applyprofile Nope` → no window; log shows "not found"; exit code `1`.
- `SMUDebugTool.exe` (no args) → GUI launches normally.

- [ ] **Step 5: Commit**

```bash
git add Program.cs SettingsForm.cs
git commit -m "feat: headless --applyprofile <name> activation with exit codes"
```

---

## Milestone 5 — PBO power limits

### Task 9: Verify DLL signatures, add PBO controls, wire apply + profile fields

**Files:**
- Modify: `Profiles/ProfileApplier.cs` (`ApplyPboLimits`)
- Modify: `SettingsForm.cs` (build PBO controls; extend gather/apply)

- [ ] **Step 1: Verify the DLL method signatures (do this first)**

```powershell
$dll = "I:\Coding-Projects\SMUDebugTool-master\Prebuilt\ZenStates-Core.dll"
$asm = [System.Reflection.Assembly]::LoadFile($dll)
$t = $asm.GetType("ZenStates.Core.Cpu")
$t.GetMethods() | Where-Object { $_.Name -match 'PPT|TDC|EDC|PBOScalar' } | ForEach-Object { $_.ToString() }
```
Record the exact return types and parameter types/units. Use them in Step 2 and Step 3.
> Expectation from the DLL string table: `SetPPTLimit`, `SetTDCSOCLimit`, `SetEDCSOCLimit`,
> `SetPBOScalar`, and `GetPBOScalar` exist. If a setter takes `uint` watts/amps directly,
> the casts below are correct; if it takes milliwatts/centi-amps, scale accordingly and note
> it in a code comment. If the SOC-rail variant is wrong for the user-facing limit, switch to
> the VDD SMU message per the call pattern used elsewhere in the form.

- [ ] **Step 2: Implement `ApplyPboLimits`** (adjust to the signatures from Step 1)

Replace the empty `ApplyPboLimits` body in `Profiles/ProfileApplier.cs`:
```csharp
        private void ApplyPboLimits(Profile p, Cpu cpu, ApplyResult r)
        {
            // Signatures/units confirmed via the reflection dump in Task 9, Step 1.
            if (p.PptWatts.HasValue && cpu.SetPPTLimit((uint)p.PptWatts.Value) != SMU.Status.OK)
                r.Fail("Failed to set PPT limit.");
            if (p.TdcAmps.HasValue && cpu.SetTDCSOCLimit((uint)p.TdcAmps.Value) != SMU.Status.OK)
                r.Fail("Failed to set TDC limit.");
            if (p.EdcAmps.HasValue && cpu.SetEDCSOCLimit((uint)p.EdcAmps.Value) != SMU.Status.OK)
                r.Fail("Failed to set EDC limit.");
            if (p.PboScalar.HasValue && cpu.SetPBOScalar((uint)p.PboScalar.Value) != SMU.Status.OK)
                r.Fail("Failed to set PBO scalar.");
        }
```
> If a setter returns `bool` instead of `SMU.Status`, change the guard to `!cpu.SetXxx(...)`.

- [ ] **Step 3: Build to confirm the applier compiles against the real signatures**

```powershell
$msbuild = "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
& $msbuild "I:\Coding-Projects\SMUDebugTool-master\ZenStatesDebugTool.sln" /p:Configuration=Debug /v:m
```
Expected: `Build succeeded.` (Fix casts/return-type guards until it does.)

- [ ] **Step 4: Add PBO numeric controls (programmatically)**

Add a field near `comboBoxProfiles` (Task 5, Step 1):
```csharp
        private NumericUpDown numericUpDownPpt, numericUpDownTdc, numericUpDownEdc, numericUpDownPboScalar;
```
Add a builder method and call it from `InitPboLayout` (`SettingsForm.cs` ~line 379, after
`BuildCcdBlocks();`):
```csharp
        private void BuildPboLimitControls()
        {
            // Reuse the CO action bar row for the PBO limit inputs.
            numericUpDownPpt       = MakeLimitBox(0, 1000);
            numericUpDownTdc       = MakeLimitBox(0, 1000);
            numericUpDownEdc       = MakeLimitBox(0, 1000);
            numericUpDownPboScalar = MakeLimitBox(1, 10);

            flowLayoutPanelCcdActions.Controls.Add(new Label { Text = "PPT", AutoSize = true, Margin = new Padding(12, 6, 2, 0) });
            flowLayoutPanelCcdActions.Controls.Add(numericUpDownPpt);
            flowLayoutPanelCcdActions.Controls.Add(new Label { Text = "TDC", AutoSize = true, Margin = new Padding(8, 6, 2, 0) });
            flowLayoutPanelCcdActions.Controls.Add(numericUpDownTdc);
            flowLayoutPanelCcdActions.Controls.Add(new Label { Text = "EDC", AutoSize = true, Margin = new Padding(8, 6, 2, 0) });
            flowLayoutPanelCcdActions.Controls.Add(numericUpDownEdc);
            flowLayoutPanelCcdActions.Controls.Add(new Label { Text = "Scalar", AutoSize = true, Margin = new Padding(8, 6, 2, 0) });
            flowLayoutPanelCcdActions.Controls.Add(numericUpDownPboScalar);
        }

        private NumericUpDown MakeLimitBox(int min, int max)
        {
            return new NumericUpDown
            {
                Minimum = min,
                Maximum = max,
                Width = 60,
                Margin = new Padding(0, 3, 0, 0)
            };
        }
```
Call `BuildPboLimitControls();` at the end of `InitPboLayout`.

- [ ] **Step 5: Include PBO fields in gather/apply**

In `GatherProfileFromUi` (Task 6), replace the `// PBO: filled in Task 9` comment with:
```csharp
            profile.PptWatts = (int)numericUpDownPpt.Value;
            profile.TdcAmps = (int)numericUpDownTdc.Value;
            profile.EdcAmps = (int)numericUpDownEdc.Value;
            profile.PboScalar = (int)numericUpDownPboScalar.Value;
```
In `ApplyProfileToUi` (Task 6), replace its `// PBO: filled in Task 9` comment with:
```csharp
            if (profile.PptWatts.HasValue) numericUpDownPpt.Value = Clamp(numericUpDownPpt, profile.PptWatts.Value);
            if (profile.TdcAmps.HasValue) numericUpDownTdc.Value = Clamp(numericUpDownTdc, profile.TdcAmps.Value);
            if (profile.EdcAmps.HasValue) numericUpDownEdc.Value = Clamp(numericUpDownEdc, profile.EdcAmps.Value);
            if (profile.PboScalar.HasValue) numericUpDownPboScalar.Value = Clamp(numericUpDownPboScalar, profile.PboScalar.Value);
```
Add the helper:
```csharp
        private static decimal Clamp(NumericUpDown c, int v) => Math.Max(c.Minimum, Math.Min(c.Maximum, v));
```
> Read-back: PBO PPT/TDC/EDC have no confirmed live getter, so on profile load these controls
> show the profile's stored value (per spec). PBO scalar may use `GetPBOScalar` if you want a
> live refresh button later — out of scope here.

- [ ] **Step 6: Build, verify compiles**

```powershell
$msbuild = "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
& $msbuild "I:\Coding-Projects\SMUDebugTool-master\ZenStatesDebugTool.sln" /p:Configuration=Debug /v:m
```
Expected: `Build succeeded.`

- [ ] **Step 7: Manual verification (hardware — be careful with power limits)**

- Set conservative PPT/TDC/EDC/scalar values, Save As a profile, confirm they appear in the JSON.
- Re-select another profile then back: values restore from the file.
- "Apply Profile" applies without error; verify via the tool's monitor / a sensor app that the
  limits changed as expected. Start with small/safe deltas.

- [ ] **Step 8: Commit**

```bash
git add Profiles/ProfileApplier.cs SettingsForm.cs
git commit -m "feat: PBO PPT/TDC/EDC/scalar controls, apply, and profile storage"
```

---

## Milestone 6 — Startup auto-apply by name

### Task 10: Separate startup-profile dropdown + named scheduled task

**Files:**
- Modify: `SettingsForm.cs` (`AddTaskToScheduler` ~line 2106; startup checkbox handler; `InitPBO` ~line 375)

- [ ] **Step 1: Make the scheduled task carry an explicit profile name**

Change `AddTaskToScheduler` (line 2106) to accept a profile name and bake it into the args:
```csharp
        static void AddTaskToScheduler(string taskName, string executablePath, string profileName, int delaySeconds = 0)
        {
            using (TaskService taskService = new TaskService())
            {
                TaskDefinition taskDefinition = taskService.NewTask();
                taskDefinition.RegistrationInfo.Description = "Run Ryzen SMU Debug Tool on user logon to apply a CO/PBO profile. Automatically created by RyzenSDT.";
                taskDefinition.Principal.UserId = WindowsIdentity.GetCurrent().Name;
                taskDefinition.Principal.RunLevel = TaskRunLevel.Highest;
                taskDefinition.Principal.LogonType = TaskLogonType.InteractiveToken;

                LogonTrigger logonTrigger = new LogonTrigger { Delay = TimeSpan.FromSeconds(delaySeconds) };
                taskDefinition.Triggers.Add(logonTrigger);

                taskDefinition.Actions.Add(new ExecAction(executablePath, $"--applyprofile \"{profileName}\""));
                taskService.RootFolder.RegisterTaskDefinition(taskName, taskDefinition);
            }
        }
```

- [ ] **Step 2: Add the startup-profile dropdown + read-back helper**

Add a field:
```csharp
        private ComboBox comboBoxStartupProfile;
```
Add a helper that reads the profile name back out of the existing task's arguments:
```csharp
        private static string GetStartupProfileFromTask(string taskName)
        {
            using (TaskService taskService = new TaskService())
            {
                Task task = taskService.GetTask(taskName);
                var exec = task?.Definition.Actions.OfType<ExecAction>().FirstOrDefault();
                if (exec == null) return null;
                // args look like: --applyprofile "Name"
                int idx = exec.Arguments.IndexOf("--applyprofile", StringComparison.OrdinalIgnoreCase);
                if (idx < 0) return null;
                string rest = exec.Arguments.Substring(idx + "--applyprofile".Length).Trim().Trim('"');
                return rest.Length > 0 ? rest : null;
            }
        }
```

- [ ] **Step 3: Build and populate the startup dropdown next to the checkbox**

In `InitPBO` (line 375, where `checkBoxApplyCOStartup.Checked = TaskExists("RyzenSDT");` is), add
after that line:
```csharp
            if (comboBoxStartupProfile == null)
            {
                comboBoxStartupProfile = new ComboBox
                {
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    Width = 140
                };
                // Place it just right of the startup checkbox.
                comboBoxStartupProfile.Location = new Point(
                    checkBoxApplyCOStartup.Right + 8, checkBoxApplyCOStartup.Top - 2);
                checkBoxApplyCOStartup.Parent.Controls.Add(comboBoxStartupProfile);
                comboBoxStartupProfile.SelectedIndexChanged += ComboBoxStartupProfile_SelectedIndexChanged;
            }
            comboBoxStartupProfile.Items.Clear();
            foreach (var n in profileManager.List())
                comboBoxStartupProfile.Items.Add(n);
            string startupName = GetStartupProfileFromTask("RyzenSDT");
            if (startupName != null && comboBoxStartupProfile.Items.Contains(startupName))
                comboBoxStartupProfile.SelectedItem = startupName;
            else if (comboBoxStartupProfile.Items.Count > 0)
                comboBoxStartupProfile.SelectedIndex = 0;
```

- [ ] **Step 4: Wire the checkbox + dropdown to (re)register the task**

Find the existing `checkBoxApplyCOStartup` CheckedChanged handler (search
`checkBoxApplyCOStartup` in `SettingsForm.cs`) and ensure its body registers/removes the task
using the dropdown selection:
```csharp
        private void RegisterOrRemoveStartupTask()
        {
            string name = comboBoxStartupProfile?.SelectedItem as string;
            if (checkBoxApplyCOStartup.Checked && !string.IsNullOrEmpty(name))
            {
                if (TaskExists("RyzenSDT")) RemoveTaskFromScheduler("RyzenSDT");
                AddTaskToScheduler("RyzenSDT", Application.ExecutablePath, name, 5);
            }
            else if (!checkBoxApplyCOStartup.Checked && TaskExists("RyzenSDT"))
            {
                RemoveTaskFromScheduler("RyzenSDT");
            }
        }

        private void ComboBoxStartupProfile_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (checkBoxApplyCOStartup.Checked) RegisterOrRemoveStartupTask();
        }
```
Call `RegisterOrRemoveStartupTask();` from the checkbox's CheckedChanged handler (replacing any
old `AddTaskToScheduler("RyzenSDT", Application.ExecutablePath, ...)` call, which now lacks the
required `profileName` argument).
> Search for every existing call to `AddTaskToScheduler(` and update it to pass the selected
> profile name, or route it through `RegisterOrRemoveStartupTask()`.

- [ ] **Step 5: Build, verify compiles**

```powershell
$msbuild = "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
& $msbuild "I:\Coding-Projects\SMUDebugTool-master\ZenStatesDebugTool.sln" /p:Configuration=Debug /v:m
```
Expected: `Build succeeded.`

- [ ] **Step 6: Manual verification**

- Tick "apply on startup" with a profile selected in the startup dropdown → Task Scheduler shows
  `RyzenSDT` with arguments `--applyprofile "<name>"`.
- Change the startup dropdown → task re-registers with the new name.
- Untick → task removed.
- Reopen the app → the startup dropdown shows the name read back from the task.

- [ ] **Step 7: Commit**

```bash
git add SettingsForm.cs
git commit -m "feat: per-profile logon auto-apply with separate startup dropdown"
```

---

## Self-Review (completed at write time)

- **Spec coverage:** multiple profiles (Tasks 1-3, 7) ✓; CO+CurveShaper+fmax+PBO contents
  (Tasks 1, 6, 9) ✓; `--applyprofile <name>` silent + exit (Task 8) ✓; no-name → do nothing +
  non-zero exit (Task 8) ✓; JSON one-file-per-profile + migration (Tasks 2-3) ✓; PBO plumbing
  with signature verification (Task 9) ✓; separate startup dropdown, no active.txt (Task 10) ✓;
  unit tests for core (Tasks 1-3) ✓. **Deferred (documented):** `frequency` field — design
  decision above; nullable schema keeps it addable later.
- **Placeholder scan:** no TBD/TODO left in steps; every code step shows full code.
- **Type consistency:** `Profile`, `CurveShaperTier`, `ProfileManager` (List/Load/Save/Delete/
  IsValidName/MigrateLegacyIfNeeded/ParseLegacy/LegacyFileName), `ProfileApplier.Apply` →
  `ApplyResult` are used identically across tasks. `GatherProfileFromUi`/`ApplyProfileToUi`/
  `MakeBarButton`/`RefreshProfileList`/`Clamp` names consistent. PBO control names
  (`numericUpDownPpt/Tdc/Edc/PboScalar`) consistent between Tasks 9 gather/apply.
