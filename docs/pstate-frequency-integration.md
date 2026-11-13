# P-state frequency integration

The editor and MSR decoder share the same frequency codec. Family 17h/19h use
FID [7:0], DID [13:8] and `200 * FID / DID` MHz. Family 1Ah uses FID [11:0]
and `FID * 5` MHz, with no DID. Unknown families retain raw MSR output and
cannot use the frequency editor. These are register-definition frequencies;
they are not a measurement of the core's live clock under CPPC/PBO.

The frequency handling and selection/validation behavior are adapted from
irusanov/SMUDebugTool commits
[2c55d18](https://github.com/irusanov/SMUDebugTool/commit/2c55d18d930338f0f32b7b8afbc7c5df2216f248)
and [fdac4ff](https://github.com/irusanov/SMUDebugTool/commit/fdac4ff33784a07e816bea78f2026508a9eeacc4).
The Family 1Ah format is also implemented in
[Linux cpupower](https://github.com/torvalds/linux/blob/master/tools/power/cpupower/utils/helpers/amd.c).

## Editing and targeting

- P0–P7 keep their actual MSR IDs. Disabled or unreadable states stay visible.
- A successful read of an enabled state is required before Write becomes
  available. Selecting another state or failing a read clears that eligibility.
- Input validation handles pasted text as well as typing. Invalid values are
  rejected, never clamped into a different register value.
- On each active logical processor, the writer pins its worker thread, reads
  that processor's current register, changes only the frequency fields, applies
  the existing HWCR[21] TSC workaround, writes, and verifies frequency readback.
- The frequency masks are 0x3FFF for Family 17h/19h and 0xFFF for Family 1Ah.
  Every other PStateDef bit is retained, including VID, IDD, PstateEn, EDX, and
  Family 1Ah bits [13:12]. There are no voltage-writing controls in this change.
- Targets come from Windows' active processor-group masks, not NUMA node IDs
  or an assumed flat CPU count. A failed affinity change prevents that target's
  write. Affinity scopes restore in finally/Dispose; short-lived dedicated I/O
  threads also keep group restrictions off the UI and thread-pool threads.
- Hardware.Sync covers each complete operation. A later failure reports the
  target and number of processors already verified. Completed writes are not
  automatically rolled back.

The bundled core's `Cpu.WriteMsr` broadcasts one register value. To preserve
each processor's own non-frequency bits, HardwareService binds its existing
`_pawnAmd` driver and uses the driver's single-processor ReadMsr/WriteMsr methods
inside the checked affinity scope. This is a private-field compatibility bridge;
if the DLL changes shape, P-state writes are unavailable. A metadata-only test
checks that contract. A future core-library migration should replace this bridge
with a supported public per-processor API.

## Validation

The Windows Release solution build and full net48 suite pass. Coverage includes:

- legacy FID 168 / DID 8 = 4200 MHz and Zen 5 FID 1000 = 5000 MHz;
- FID values above 255, field bounds, reserved low FIDs and invalid text;
- preservation of all non-frequency bits with independent register patterns;
- family-aware decoder fields, raw unknown-context output and split SVI3 VID;
- initial-read, disabled-state, preparation, write, readback and affinity failures;
- partial completion and each target's distinct original register value;
- sparse group masks and processor 63;
- actual Windows affinity selection and restoration on exception, without MSR I/O.

The tests do not initialize Cpu or a low-level driver. MSR transactions use fake
read/write delegates. The bundled-library compatibility test inspects metadata
only. Native affinity tests change only their own test thread's scheduling.

Manual WinForms and Ryzen hardware verification remain outstanding:

1. On a Family 17h/19h and Family 1Ah system, record CPU model/family and raw
   PStateDef registers, then compare editor and decoder output. Zen 5 DID should
   be inactive. Include P0 and P7 where readable.
2. Verify selecting another state, failed reads and invalid pasted inputs leave
   Write disabled. Unknown layouts and PstateEn=0 remain read only.
3. On supported hardware, first write the existing frequency fields and verify
   readback. Compare all non-frequency bits with the original values on every
   target. Exercise changed frequencies only with settings already validated for
   that machine.
4. Validate representative multi-group hardware before claiming that hardware
   coverage. Simulated group masks are not a substitute for a real multi-group
   MSR transaction. Record firmware rejections and partial outcomes accurately.

## Follow-on work

Keep the ZenStates-Core/APOB migration, CO/FMax result reporting, driver cleanup
and release artifact packaging in independent changes.
