# Pinned core build

`ZenStates-Core` is the unmodified upstream submodule at
`3bb1dad5b6cccc14f063d6eacbcf891a309944f3`.

The sibling net48 project compiles that source explicitly for this application's
runtime, using upstream's NET20 compatibility branches, fixed version metadata,
and the embedded PawnIO payloads. It does not run upstream's native-file copy
commands or include additional WinIo drivers. The app continues to provide its
existing, hash-verified x64 InpOut DLL.

Prepare-Core verifies the source revision and clean tracked files, then changes
only Cpu's declaration to partial in the intermediate build directory.
CoreCurrentProcessor adds public current-processor MSR operations on the existing
driver, avoiding private-field reflection and broadcast writes in the P-state
transaction. Its caller retains checked processor affinity and register-field
preservation. No driver is constructed by this preparation/build step.

Upstream licenses remain in the submodule and are included in release packages.
