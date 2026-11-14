# Ryzen SDT v1.50.0

- Restore one-click Apply for normal profile-supported tuning. Preview changes is a separate, optional action; edits, validation, exact request snapshots and partial-result tracking remain.
- Fix the PStates tab by moving BCLK into the same content-sized grid below frequency and VID. Narrow windows scroll instead of placing BCLK over the VID fields.
- Replace the clipped history dropdown with a History button and resizable, wrapping table. Every retained action has a readable time/action/result row and read-only Details. Activity log stays reachable even before the first tuning action.
- Guide AMD ACPI input using firmware-provided lists or min/max/step metadata. Preserve valid zero choices and declared list lengths. Explain missing metadata rather than showing an unexplained disabled Values field.
- Name common memory timings and show their units, examples and firmware-value snapshots. tCL is CAS latency in memory clock cycles. Known ACPI PPT/TDC/EDC use displayed W/A with explicit firmware-unit conversion. Unknown voltage encodings stay labeled raw.
- Validate ACPI range/step/enumeration and 32-bit input before sending. Replace the old silent 16-bit cutoff with the actual 32-bit transport contract. Report firmware acceptance, rejection or unknown outcomes visibly without claiming live hardware verification.
- Add concise guidance and Help to every supported tab, including number formats, units, structured-editor alternatives and decoded register/SMU names. Keep technical command identities and raw reports available.

ACPI firmware values may be setup values; acceptance does not establish that a change is active, and restart may be required. Firmware-specific limits are queried, not guessed. Long technical help has English fallback alongside translated captions.

Automated validation uses fake metadata/transports and native-control fixtures, including narrow/large-font layouts. No test tunes hardware or changes an installed driver. Windows x64 and .NET Framework 4.8 required; extract the full ZIP before running.
