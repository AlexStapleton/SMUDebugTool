# v1.51.0 — first roadmap milestone

- Add **Tools → CPU overview and capabilities**, a resizable read-only snapshot with wrapped cells and a copyable evidence report. Opening or refreshing sends no tuning writes and starts no polling timer.
- Report CPU, board/BIOS, SMU, topology and dependency identity; distinguish RSMU/MP1 command definitions from proven firmware support.
- Show a fresh, status-checked scalar getter; CPU and physical CCD temperatures; and mapped SVI3 rail VID, voltage, current and VRM temperature after a successful PM-table refresh. Rail VID is not an individual Core VID. Failed/missing/unmapped data stays unavailable.
- Verify scalar writes through the shared GUI/headless apply path. Decode its floating-point getter, reject invalid data, and report mismatches without replacing requested values.
- Add a separate, one-click temperature-ceiling action for reviewed Raphael/Granite Ridge command maps. The 40–95 °C interval is a software guard, not a BIOS-reported range. Each request uses one RSMU command without fallback or retry. Firmware can reject it; an accepted request remains unverified because no active-ceiling getter is established. This action does not disable thermal protections or enter OC mode, and is not added to profiles/startup.
- Show each available fused-limit command's own status and raw word separately. Keep fused values explicitly in raw firmware units; configured power/temperature snapshots are not active PPT or runtime-ceiling verification. Decoding is restricted to the reviewed desktop contract.
- Track all roadmap areas in `docs/capability-and-CO-roadmap.md`, with delivered work separated from pending firmware validation and future CO recording/assistance.

Validation uses fake transports and synthetic, driver-free UI fixtures. No automated test constructs a live Cpu, tunes hardware, or removes a driver. Real firmware acceptance, telemetry agreement and actual DPI behavior still need user testing. This milestone contains no CO auto-tuning or HWiNFO integration.
