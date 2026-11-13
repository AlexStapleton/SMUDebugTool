# Ryzen SDT v1.48.0

- Build from a pinned ZenStates-Core source checkout with a deterministic net48 compatibility project. Retains the existing x64 InpOut DLL; adds no new native driver.
- Resolve per-core CO/manual-OC targets through validated APOB data, with bounded legacy fallback. Physical core bindings are shared by GUI and startup profiles. Changed or ambiguous mappings fail preflight; an explicit review dialog can load fully matched bindings without changing the saved file.
- Add a separate P-state VID editor for known SVI2/SVI3 register layouts. Writes read each processor's original register, preserve frequency and unrelated bits, check affinity and verify the VID field. Derived VID voltage is labeled separately from measured voltage. No settings are changed merely by opening the editor.
- Add driver diagnostics and explicit maintenance. Normal exit retains the shared driver. Removal requires recorded ownership, the expected file/path/hash, no participating instance and explicit acknowledgment about external consumers. Existing unowned installations are retained.
- Initialize hardware on a worker with progress and cancellation. Retain bounded monitor polling, operation serialization and coordinated shutdown.
- Enable native WinForms Per-Monitor V2 DPI behavior, resizable dialogs and font-aware result-row sizing. Add English/Simplified Chinese captions and a persisted Language menu; technical logs and numeric data retain invariant formats and English diagnostic fallback.
- Add deterministic package generation, file hashes, dependency locking, build provenance, CI artifacts and tag-driven releases. Packages are built from fresh outputs and verified without running the hardware entry point.

Automated tests use fake hardware/maintenance environments and metadata. They do not initialize Cpu, write registers or remove an installed driver. Real firmware behavior, live multi-group VID writes, actual shared-driver maintenance and mixed-monitor DPI appearance still require hardware validation.

Windows x64 and .NET Framework 4.8 requirements remain. Extract the full ZIP before running. Back up profiles before testing a new hardware-library build; old profile files remain readable and are never rewritten on load.
