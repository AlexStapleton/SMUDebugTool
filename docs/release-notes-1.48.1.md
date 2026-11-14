# Ryzen SDT v1.48.1

- Replace the main panel's raw tuning log with a readable summary and table for the latest action. Group repeated successful core/tier results; keep individual errors visible. Details shows every setting, with technical diagnostics hidden until requested. The bounded activity log and complete copyable reports remain available.
- Add a separate Apply button beside PPT, TDC, EDC and Scalar. Each sends only that field. Rename the profile action to Apply all settings so its whole-form scope is explicit. A zero power limit still means leave unchanged.
- Fix driver diagnostics resolving Windows-relative service paths such as `System32\Drivers\inpoutx64.sys` against the application folder. These now resolve against the Windows directory, allowing diagnostics to hash the actual installed file when accessible.
- Include the original registry ImagePath in reports, label unavailable hashes, and explain missing/inaccessible files or unsupported paths. Ambiguous paths are rejected instead of inheriting the current working directory.
- Preserve the ownership and shared-use checks: an existing installation is never adopted merely because its path or hash matches. Normal exit retains the shared driver.

Includes all v1.48.0 features. Automated tests do not initialize hardware, write registers or stop/remove an installed driver. Real firmware behavior, driver maintenance and mixed-monitor DPI still require hardware validation.

Windows x64 and .NET Framework 4.8 required. Extract the full ZIP before running.
