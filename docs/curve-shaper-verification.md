# Curve Shaper verification

The apply transport now implements the pinned core's RSMU setter/getter packet
contract directly. Setter argument 0 contains high/medium/low signed bytes in
bits 31:24/23:16/15:8, bit 7 set and a frequency-tier index in bits 6:0. Other
arguments are zero. The getter takes six zero arguments and returns five tier
words in minimum/low/medium/high/maximum frequency order. Its sixth word is unused.
Command IDs come from the CPU's mailbox configuration, not a new CPU-ID guess.

After each accepted write, issue a fresh getter under the existing serialized
hardware operation. Check its status before decoding anything. Compare all three
returned band values for the requested tier. A failed status, missing getter,
short packet, out-of-contract band or incomplete/derived observation cannot verify.
Fatal I/O failures and cancellation stop the batch without retries or rollback.

All-zero blocks are a known ambiguous response on this interface. Until this
getter has returned a valid block with nonzero margin bytes during the same apply, an all-zero
block remains inconclusive, including for a requested zero tier. After that evidence,
fresh zero words can confirm a reset. This capability evidence is local to the
transport instance; cached requested/display values are never used as readback.
Unused low bytes and the sixth word do not establish that evidence. The program
does not introduce probe writes to establish support.

Keep observed low/medium/high values and raw returned words in result snapshots,
read-only Details and reports. Write/read statuses remain separate. On a mismatch,
retain the user's requested inputs, update the discard baseline to the observed
values, and keep the edits pending. Session display fallback uses observed values
for a mismatch, rather than substituting the failed request as an actual observation.

Tests simulate routed packets, all five tiers, signed bands, every mismatch position,
missing/failed/truncated getters, ambiguous zeros, zero resets after evidence,
cancellation/fatal errors, snapshots and edit/cache behavior. No live SMU writes are
performed by tests. Actual BIOS readback support remains a hardware test.

Contract references: `vendor/ZenStates-Core/Hardware/Smu/Commands/SetCurveShaperMargin.cs`,
`GetCurveShaperMargin.cs`, `BaseSMUCommand.cs`, and `Settings/Zen4Settings.cs` at the
locked core revision. The public convenience getter discards the status and is not
used for verification.
