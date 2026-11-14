# Ryzen SDT v1.49.0

- Highlight pending CO, FMax, Curve Shaper and PBO limit edits. Tooltips and apply previews distinguish editor baselines, saved values, last CPU observations and accepted requests without readback.
- Add Apply changes alongside Apply all settings. Only edited, included settings are sent by Apply changes; Curve Shaper changes preserve tier indices and physical core bindings are retained.
- Preview the exact request set before GUI tuning applies. Cancel sends no commands; the approved snapshot is isolated from later edits. Zero power limits are explicitly shown as leave unchanged.
- Add Discard edits. It restores local baselines and target inclusion without changing files or sending CPU commands. Saving a profile leaves hardware edits pending.
- Preserve pending drafts across CPU refresh, record observation source/time, and retain pending edits after rejected/mismatched/failed writes. Accepted writes without readback remain labeled unverified.
- Keep the latest 20 actions in a readable session history. Selecting an action displays its detached results and cannot replay it. Complete diagnostic reports remain available on demand.

Profile-supported GUI tuning gains previews; headless startup retains its existing noninteractive validation. The P-state, manual-OC and raw-register editors retain their dedicated controls and confirmations. CPU observations are last-known values, not continuous telemetry. Session history and unsaved drafts are not persisted after exit.

Automated tests use fake transports and control fixtures; live firmware behavior remains a hardware-validation item. Windows x64 and .NET Framework 4.8 required. Extract the full ZIP before running.
