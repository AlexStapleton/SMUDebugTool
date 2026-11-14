# Tuning edit tracking and apply previews

Scope: the profile-supported Curve Optimizer, FMax, Curve Shaper and PBO limit editors.
Every GUI apply for those settings previews a frozen request set. Headless startup
retains its existing noninteractive contract and validation. Register/manual-OC editors
retain their dedicated workflows.

- Capture an editor baseline before asynchronous initialization. Highlight changes to
  it and show saved values, last CPU observations and last accepted requests separately.
  Initial defaults and session fallback values never become claimed CPU measurements.
- CPU refresh updates observations and the discard baseline without overwriting a
  pending draft. Only in-range observations may populate controls. Direct result
  readbacks retain their source/time; unavailable readback remains unavailable.
- Apply changes filters the current enabled editor snapshot by baseline. A Curve
  Shaper tier is one atomic three-band request. Preserve physical core bindings and
  sparse tier indices. Apply all retains its explicit whole-form scope.
- Preview previous/requested values, baseline source, saved value, last CPU report and
  send/leave-unchanged action. Validate before showing, then execute exactly the
  approved snapshot. Opening or cancelling preview does not read or write hardware.
- Saving changes saved-value metadata only. Loading a profile creates local edits.
  Discard restores editor baselines and target inclusion without file or CPU writes.
- Advance only successful/accepted baselines. A mismatch/rejection/error/skip never
  clears pending edits; accepted-but-unverified values are explicitly labeled.
  An edit made while a command runs survives its result.
- Keep the last 20 actions in memory as detached result snapshots, newest first.
  Selecting history only changes the displayed report; it cannot replay a request.

Validation: pure tests for snapshot isolation, sparse deltas/bindings, draft/saved/CPU
  provenance, partial outcomes and history bounds; STA UI tests for highlight/discard,
  preview cancellation and history selection; Release build and package checks.
No automated validation initializes Cpu, tunes registers or removes drivers.
