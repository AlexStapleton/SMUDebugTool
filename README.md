ZenStatesDebugTool

### Roadmap progress

The [capability and Curve Optimizer roadmap](docs/capability-and-CO-roadmap.md)
is the living tracker for implemented changes, validation and remaining hardware gates.
The implementation adds **Tools → CPU overview and capabilities**, on-demand
readings, scalar verification and a separate temperature-ceiling action. **Tools →
CO optimizer** guides preparation, a zero/current-CO reference, supervised VID
balancing under an owned Cinebench 2024.1.0 load, fixed-offset validation and an
explicit keep/save/restore decision. It captures per-core VID through the existing
driver, journals verified one-step trials and pauses on missing evidence or safety
incidents. There is no HWiNFO or CSV dependency. Equal requested VID does not prove
stability. See the [guided optimizer guide](docs/guided-co-optimizer.md) and
[v1.52.0 notes](docs/release-notes-1.52.0.md) for its scope and hardware acceptance gates.

Startup now skips unused inventory scans and loads P-state/AMD ACPI data on demand.
**Tools → Startup timings** separates window visibility from tuning readiness;
see [startup performance and measurement](docs/startup-performance.md).

The [performance audit](docs/performance-audit-2026-11-15.md) records measured
rendering/allocation improvements, cooperative scans, streamed exports and the
remaining hardware-dependent profiling work. Reproducible synthetic probes are included.

### Build and package this fork

Windows x64, .NET Framework 4.8 and the .NET SDK specified in `global.json` are required.
Clone with `git clone --recurse-submodules`, or run `git submodule update --init --recursive`
in an existing checkout. ZenStates-Core is pinned and built through the reviewed net48
compatibility project described in [vendor/README.md](vendor/README.md).

```powershell
dotnet restore ZenStatesDebugTool.sln
dotnet build ZenStatesDebugTool.sln -c Release --no-restore
dotnet test ZenStatesDebugTool.sln -c Release --no-build
./Tests/Package.Tests.ps1
./scripts/Package-Release.ps1 -OutputDirectory artifacts
```

CI supplies the verified package as a downloadable artifact. Release tags run the same
build/test/package sequence and publish the tested ZIP, checksum and build manifest.
The tests/package inspector never start the hardware entry point.

See [v1.48.0 changes](docs/release-notes-1.48.0.md) and
[implementation/validation notes](docs/remaining-updates-implementation.md). The Language
menu offers English and Simplified Chinese captions; diagnostic reports retain an English
fallback and invariant numeric formatting.

![Screenshot](screenshot.png "SMU Debug Tool")

### Projects used  
[RTCSharp (github)](https://github.com/tomrus88/RTCSharp)  
[ryzen_smu (gitlab)](https://gitlab.com/leogx9r/ryzen_smu/)  
[ryzen_nb_smu (github)](https://github.com/flygoat/ryzen_nb_smu)  
[zenpower (github)](https://github.com/ocerman/zenpower)  
[Linux kernel (github)](https://github.com/torvalds/linux)  
[AMD's public documentation](https://www.amd.com/en/support/tech-docs)  

# Upstream updates

The daily [Upstream sync workflow](.github/workflows/upstream-sync.yml) opens a
reviewable merge PR and runs CI when `irusanov/SMUDebugTool` changes. Conflicts are
reported in one issue; the bot never force-pushes, merges master or publishes a
release. See [the synchronization policy and reconciliation ledger](docs/upstream-sync.md).
