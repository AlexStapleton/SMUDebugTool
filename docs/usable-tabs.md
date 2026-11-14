# Usable tuning tabs

Normal Apply executes a frozen, validated request once. Preview changes is optional
and can send its approved snapshot through that same apply path. Headless startup
keeps its noninteractive contract. Dedicated P-state write controls retain their
existing write behavior. Changing a displayed field alone never sends a command.

P-state VID and base-clock controls share an auto-sized table, with BCLK below VID.
There are no remaining absolute-position BCLK siblings. Guidance wraps above each
tab's scrollable content. Tests include the complete P-state/VID/BCLK composition,
not just the isolated VID rows.

History is a separate resizable table with wrapping cells. Main results remain on
the latest action. History Details only reads detached snapshots; it cannot replay
an action. The raw Activity log remains accessible without a tuning result.

## AMD ACPI metadata

The command's raw name and ID remain the routing identity. Friendly names affect
display only. Firmware property flags choose range/list lookup order; missing or
invalid responses fall back to the alternate metadata method, then to an explicitly
unknown domain. Metadata must match the command ID. Lists respect their declared
length (maximum 100) and keep zero. Ranges require ordered bounds and a nonzero step.
All metadata queries remain serialized on the hardware worker; stale selections
cannot install another command's values.

Known timing names use memory cycles. The documented memory-clock command uses MHz;
PPT/TDC/EDC IDs use firmware mW/mA and display W/A with exact scaling. Raw numbers,
unknown voltage units and firmware snapshots are identified explicitly. Input is
validated before invoking WMI. Zero current from range metadata is ambiguous and is
not displayed as a measured zero. Reads of metadata are not writes.

RunCommand responses need a complete status word at byte offset 2 before the UI
claims firmware acceptance. Zero status means firmware success, separately from
SMU status conventions. No active-value verification is inferred. Missing/truncated
responses remain unknown; unknown status codes retain their numeric value.

## Sources

- [AMD Ryzen Master RAM guide](https://docs.amd.com/r/en-US/68886-ryzen-master-user-guide/RAM): timing names and units.
- [AMD's WMI interface proposal, October 2026](https://lore-kernel.gnuweeb.org/platform-driver-x86/20261010190543.563161-4-Shyam-sundar.S-k@amd.com/): MOF fields, property flags, metadata methods, command units, status layout and firmware activation semantics. Firmware capability/shape checks remain necessary; this is not evidence that every BIOS returns every field.
- Pinned `vendor/ZenStates-Core/Hardware/WMI.cs`: this application's existing eight-byte command/argument transport.

Unit/fixture tests cover range steps, zero choices, missing/mismatched metadata,
scaling above 16 bits, malformed inputs, status parsing, hidden-tab enablement,
history access and layout. Hardware-specific firmware behavior still needs testing.
