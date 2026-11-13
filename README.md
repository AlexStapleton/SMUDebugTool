ZenStatesDebugTool

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
