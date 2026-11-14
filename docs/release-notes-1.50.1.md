# Ryzen SDT v1.50.1

- Verify Curve Shaper writes with a fresh, status-checked RSMU getter packet. Compare low, medium and high offsets for the requested frequency tier; Confirmed requires all three to match.
- Show all three observed offsets in Details and preserve raw read/write commands, status codes and returned words in diagnostic reports and detached history snapshots.
- Keep mismatched requested values in the editor, record actual observations, and retain pending edits. Fatal errors/cancellation stop further commands without retries or rollback writes.
- Distinguish accepted-but-unverified results from mismatches. Missing/failed getters, short/out-of-range packets and inconclusive all-zero responses cannot produce false confirmation. Cached display/session values never participate in verification.

Some firmware returns an all-zero block instead of usable Curve Shaper data. An all-zero block remains inconclusive unless this getter has already returned a valid nonzero block during the same apply operation. Fresh zero readback after that evidence can confirm a reset. The program does not change settings merely to probe getter support. Firmware that does not provide usable readback cannot be honestly marked Confirmed.

Automated tests use fake packets/transports and do not tune hardware. Windows x64 and .NET Framework 4.8 required; extract the full ZIP before running.
