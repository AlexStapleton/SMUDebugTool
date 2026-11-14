using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using ZenStates.Core;
using ZenStates.Core.Hardware;

namespace ZenStatesDebugTool
{
    public sealed class OverviewReading
    {
        public string Name { get; }
        public string Value { get; }
        public string Unit { get; }
        public string Source { get; }
        public string Evidence { get; }
        public OverviewReading(string name, decimal? value, string unit, string source, string evidence)
        { Name = name; Value = value?.ToString("0.###", CultureInfo.InvariantCulture) ?? "Unavailable"; Unit = unit; Source = source; Evidence = evidence; }
    }
    public sealed class CpuOverviewSnapshot
    {
        public DateTime StartedUtc { get; }
        public DateTime FinishedUtc { get; }
        public string Identity { get; }
        public bool CanSetTemperature { get; }
        public IReadOnlyList<OverviewReading> Readings { get; }
        public IReadOnlyList<string> Commands { get; }
        public CpuOverviewSnapshot(DateTime started, DateTime finished, string identity, bool canSetTemperature,
            IEnumerable<OverviewReading> readings, IEnumerable<string> commands)
        {
            StartedUtc = started; FinishedUtc = finished; Identity = identity; CanSetTemperature = canSetTemperature;
            Readings = Array.AsReadOnly(readings.ToArray()); Commands = Array.AsReadOnly(commands.ToArray());
        }
        public string Report => Identity + Environment.NewLine + $"Snapshot {StartedUtc:O} to {FinishedUtc:O}. Sequential reads, not simultaneous samples." + Environment.NewLine +
            "Command defined does not establish firmware support. This report sends no tuning writes. Fused/configured limits are not active limits." + Environment.NewLine +
            string.Join(Environment.NewLine, Readings.Select(r => $"{r.Name}: {r.Value} {r.Unit}; {r.Source}; {r.Evidence}")) + Environment.NewLine +
            "Command map (defined, write acceptance untested by this report):" + Environment.NewLine + string.Join(Environment.NewLine, Commands);
    }

    // Only allowlisted reads. No fallback routes, setter probes or retries.
    public sealed class OverviewReadPlan
    {
        public uint Scalar, FusedPower, FusedVddTdc, FusedSocTdc, Configured;
        public string ConfiguredRoute = "RSMU";
        public IReadOnlyList<OverviewReading> Read(Func<string, uint, uint[], TuningReply> send, CancellationToken cancellation)
        {
            var rows = new List<OverviewReading>();
            bool stopped = false;
            TuningReply Query(uint command, string route)
            {
                cancellation.ThrowIfCancellationRequested();
                if (command == 0 || stopped) return null;
                var args = new uint[6];
                var reply = send(route, command, args) ?? new TuningReply();
                reply.RawArguments = (uint[])(reply.RawArguments ?? args).Clone();
                reply.Route = route; reply.Command = command;
                if (!reply.Status.HasValue || TuningService.IsFatal(reply.Status)) stopped = true;
                return reply;
            }
            string Evidence(TuningReply reply) => reply == null ? stopped ? "Not read after transport failure." : "No command defined."
                : $"{reply.Route} cmd=0x{reply.Command:X}; status={TuningResultFormatter.StatusText(reply.Status)}; raw=" + string.Join(",", reply.RawArguments.Select(v => $"0x{v:X8}"));
            uint? Word(TuningReply reply) => reply?.Status == 1 && reply.RawArguments.Length > 0 ? (uint?)reply.RawArguments[0] : null;
            var scalar = Query(Scalar, "RSMU");
            decimal? multiplier = null;
            if (Word(scalar).HasValue) multiplier = TuningCommands.DecodeScalar(Word(scalar).Value);
            rows.Add(new OverviewReading("PBO Scalar", multiplier, "x", "Fresh direct getter", Evidence(scalar) + (multiplier.HasValue ? "" : " No usable multiplier.")));
            foreach (var item in new[] { Tuple.Create("Fused power limit", FusedPower), Tuple.Create("Fused VDD TDC limit", FusedVddTdc), Tuple.Create("Fused SoC TDC limit", FusedSocTdc) }) {
                var reply = Query(item.Item2, "RSMU"); var raw = Word(reply);
                rows.Add(new OverviewReading(item.Item1, raw > 0 && raw < int.MaxValue ? (decimal?)raw.Value : null,
                    "raw firmware units", "Fused default; not active limit", Evidence(reply)));
            }
            var configured = Query(Configured, ConfiguredRoute); var word = Word(configured);
            uint power = word.HasValue ? (word.Value >> 16) & 0xFF : 0, temperature = word.HasValue ? word.Value & 0xFF : 0;
            rows.Add(new OverviewReading("Configured power limit", power > 0 ? (decimal?)power : null, "W", "Configured snapshot; not active PPT", Evidence(configured)));
            rows.Add(new OverviewReading("Configured temperature limit", temperature >= 40 && temperature <= 115 ? (decimal?)temperature : null, "°C", "Configured snapshot; not runtime ceiling verification", Evidence(configured)));
            return rows.AsReadOnly();
        }
    }

    public static class CpuOverviewProvider
    {
        public static bool CanSetTemperature(Cpu cpu) => cpu != null && cpu.smu != null && cpu.smu.Rsmu.SMU_MSG_SetTctlMax != 0 &&
            (cpu.info.codeName == Cpu.CodeName.Raphael || cpu.info.codeName == Cpu.CodeName.GraniteRidge);

        public static CpuOverviewSnapshot Capture(Cpu cpu, TopologySnapshot topology, CancellationToken cancellation)
        {
            DateTime started = DateTime.UtcNow;
            cancellation.ThrowIfCancellationRequested();
            // Reflection over metadata only: do not send every defined command.
            var commands = new List<string>();
            foreach (var mailbox in new[] { Tuple.Create("RSMU", (object)cpu.smu.Rsmu), Tuple.Create("MP1", (object)cpu.smu.Mp1Smu) })
                foreach (var property in mailbox.Item2.GetType().GetProperties().Where(p => p.Name.StartsWith("SMU_MSG_", StringComparison.Ordinal) && p.PropertyType == typeof(uint)).OrderBy(p => p.Name)) {
                    uint id = (uint)property.GetValue(mailbox.Item2);
                    if (id != 0) commands.Add($"{mailbox.Item1} 0x{id:X}: {property.Name.Substring(8)} — defined; not probed");
                }
            bool desktop = cpu.info.codeName == Cpu.CodeName.Raphael || cpu.info.codeName == Cpu.CodeName.GraniteRidge;
            var plan = new OverviewReadPlan {
                Scalar = cpu.smu.Rsmu.SMU_MSG_GetPBOScalar,
                // Fused/configured packet decoding is restricted to this reviewed desktop contract.
                FusedPower = desktop ? cpu.smu.Rsmu.SMU_MSG_GetPboFusedPowerLimit : 0,
                FusedVddTdc = desktop ? cpu.smu.Rsmu.SMU_MSG_GetPboFusedVrmVddTdcCurrent : 0,
                FusedSocTdc = desktop ? cpu.smu.Rsmu.SMU_MSG_GetPboFusedVrmSocTdcCurrent : 0,
                Configured = desktop ? (cpu.smu.Rsmu.SMU_MSG_GetSustainedPowerAndThmLimit != 0 ? cpu.smu.Rsmu.SMU_MSG_GetSustainedPowerAndThmLimit : cpu.smu.Mp1Smu.SMU_MSG_GetSustainedPowerAndThmLimit) : 0,
                ConfiguredRoute = cpu.smu.Rsmu.SMU_MSG_GetSustainedPowerAndThmLimit != 0 ? "RSMU" : "MP1"
            };
            bool failed = false;
            var rows = plan.Read((route, command, args) => {
                var status = route == "RSMU" ? cpu.smu.SendRsmuCommand(command, ref args) : cpu.smu.SendMp1Command(command, ref args);
                if (TuningService.IsFatal((uint)status)) failed = true;
                return new TuningReply { Status = (uint)status, RawArguments = args ?? new uint[0] };
            }, cancellation).ToList();
            void Temperature(string name, Func<float?> read) {
                cancellation.ThrowIfCancellationRequested();
                if (failed) { rows.Add(new OverviewReading(name, null, "°C", "SMN temperature", "Not read after transport failure.")); return; }
                try {
                    float? value = read();
                    rows.Add(new OverviewReading(name, value.HasValue && !float.IsNaN(value.Value) && !float.IsInfinity(value.Value) && value >= -49 && value <= 150 ? (decimal?)value.Value : null,
                        "°C", "Fresh SMN temperature", "Physical scope: " + name));
                } catch (Exception ex) { rows.Add(new OverviewReading(name, null, "°C", "SMN temperature", ex.Message)); failed = true; }
            }
            Temperature("CPU temperature", cpu.GetCpuTemperature);
            if (topology.Error == null)
                foreach (int ccd in topology.Targets.Select(t => t.Ccd).Distinct().OrderBy(c => c)) Temperature("CCD " + ccd + " temperature", () => cpu.GetSingleCcdTemperature((uint)ccd));
            cancellation.ThrowIfCancellationRequested();
            uint? tableStatus = null;
            if (!failed) {
                try { tableStatus = (uint)cpu.RefreshPowerTable(); } catch { tableStatus = null; }
            }
            foreach (Svi3Rail rail in Enum.GetValues(typeof(Svi3Rail)))
                foreach (Svi3Reading reading in Enum.GetValues(typeof(Svi3Reading))) {
                    cancellation.ThrowIfCancellationRequested();
                    bool mapped = tableStatus == 1 && cpu.powerTable != null && cpu.powerTable.HasSvi3Reading(rail, reading);
                    float value = mapped ? cpu.powerTable.GetSvi3Reading(rail, reading) : float.NaN;
                    string unit = reading == Svi3Reading.Vid || reading == Svi3Reading.Voltage ? "V" : reading == Svi3Reading.Current ? "A" : "°C";
                    bool valid = mapped && !float.IsNaN(value) && !float.IsInfinity(value) && value >= 0 &&
                        (unit == "V" ? value > 0 && value < 3 : unit == "A" ? value < 2000 : value > 0 && value < 150);
                    rows.Add(new OverviewReading(rail + " / " + reading, valid ? (decimal?)value : null, unit,
                        reading == Svi3Reading.Vid ? "SMU rail request; not per-core VID" : "SVI3 rail telemetry; not per-core",
                        $"PM table 0x{cpu.smu.TableVersion:X}; refresh={TuningResultFormatter.StatusText(tableStatus)}; " + (mapped ? "layout mapped" : "no fresh mapped field")));
                }
            return new CpuOverviewSnapshot(started, DateTime.UtcNow,
                $"{cpu.info.cpuName} / {cpu.info.codeName}; board {cpu.systemInfo?.MbVendor} {cpu.systemInfo?.MbName}; BIOS {cpu.systemInfo?.BiosVersion}; SMU 0x{cpu.smu.Version:X}; topology {topology.Source} {topology.Signature}; app {typeof(CpuOverviewProvider).Assembly.GetName().Version}; core pin 3bb1dad5b6cccc14f063d6eacbcf891a309944f3",
                CanSetTemperature(cpu), rows, commands);
        }
    }
}

