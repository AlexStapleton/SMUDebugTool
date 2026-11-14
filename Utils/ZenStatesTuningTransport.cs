using System;
using System.Linq;
using ZenStates.Core;
using ZenStates.Core.Hardware;
using ZenStates.Core.Hardware.Smu;

namespace ZenStatesDebugTool
{
    // Command routing and packets match the pinned ZenStates-Core source.
    public sealed class TuningCommands
    {
        public uint CoMp1, CoRsmu, CoGetRsmu, FmaxRsmu, FmaxMp1, FmaxGetRsmu;
        public uint CurveShaperSetRsmu, CurveShaperGetRsmu;
        public uint ScalarGetRsmu, TemperatureRsmu;
        public bool ApuFmaxArgument;
        private bool curveShaperGetterResponded;
        public static decimal? DecodeScalar(uint word)
        {
            float value = BitConverter.ToSingle(BitConverter.GetBytes(word), 0);
            // Converting float directly to decimal rounds to seven significant digits.
            // Preserve its actual value so a near-match cannot become Confirmed.
            return float.IsNaN(value) || float.IsInfinity(value) || value < 1 || value > 10 ? (decimal?)null : (decimal)(double)value;
        }
        public TuningReply Execute(TuningRequest r, bool read, uint coreMask, Func<string, uint, uint[], TuningReply> send)
        {
            var args = new uint[6];
            string route = "RSMU";
            uint command;
            if (r.Kind == TuningKind.CurveOptimizer)
            {
                command = read ? CoGetRsmu : CoMp1 != 0 ? CoMp1 : CoRsmu;
                if (!read && CoMp1 != 0) route = "MP1";
                args[0] = read ? coreMask : (coreMask & 0xFFF00000) | Utils.MakePsmMarginArg((int)r.Value);
            }
            else if (r.Kind == TuningKind.CurveShaper)
            {
                if (r.Index < 0 || r.Index >= 5) throw new ArgumentOutOfRangeException(nameof(r.Index));
                command = read ? CurveShaperGetRsmu : CurveShaperSetRsmu;
                if (!read) args[0] = CurveShaperCodec.Pack((int)r.Value, r.Medium, r.High) | 0x80U | (uint)r.Index;
            }
            else if (r.Kind == TuningKind.Fmax)
            {
                command = read ? FmaxGetRsmu : FmaxRsmu != 0 ? FmaxRsmu : FmaxMp1;
                if (!read && FmaxRsmu == 0) route = "MP1";
                args[0] = read ? (ApuFmaxArgument ? 3U : 0U) : (uint)r.Value & 0xFFFFF;
            }
            else if (r.Kind == TuningKind.Scalar && read) command = ScalarGetRsmu;
            else if (r.Kind == TuningKind.Temperature && !read)
            {
                if (r.Value != decimal.Truncate(r.Value) || r.Value < 40 || r.Value > 95)
                    throw new ArgumentOutOfRangeException(nameof(r.Value));
                command = TemperatureRsmu; args[0] = (uint)r.Value;
            }
            else throw new ArgumentOutOfRangeException(nameof(r.Kind));
            if (command == 0) return null;
            var reply = send(route, command, args) ?? new TuningReply { ReadIssue = "No transport response returned." };
            reply.Route = route;
            reply.Command = command;
            reply.TargetMask = r.Kind == TuningKind.CurveOptimizer ? (uint?)coreMask : null;
            if (r.Kind == TuningKind.CurveShaper) reply.TargetIdentity = "tier:" + r.Index;
            reply.Direct = read;
            if (read) { reply.Value = null; reply.Medium = reply.High = null; }
            var payload = reply.RawArguments ?? args;
            reply.RawArguments = (uint[])payload.Clone();
            if (read && reply.Status == 1)
            {
                if (r.Kind == TuningKind.CurveShaper)
                {
                    if (payload.Length < 5) { reply.ReadIssue = "Curve Shaper getter returned fewer than five tier words."; return reply; }
                    for (int tier = 0; tier < 5; tier++) {
                        CurveShaperCodec.Unpack(payload[tier], out int low, out int medium, out int high);
                        if (low < -50 || low > 30 || medium < -50 || medium > 30 || high < -50 || high > 30) {
                            reply.ReadIssue = "Curve Shaper getter returned a margin outside the supported packet range."; return reply;
                        }
                    }
                    bool nonzero = payload.Take(5).Any(word => (word & 0xFFFFFF00U) != 0);
                    if (!nonzero && !curveShaperGetterResponded) {
                        reply.ReadIssue = "Curve Shaper getter returned an all-zero margin block; the response is inconclusive."; return reply;
                    }
                    if (nonzero) curveShaperGetterResponded = true;
                    CurveShaperCodec.Unpack(payload[r.Index], out int observedLow, out int observedMedium, out int observedHigh);
                    reply.Value = observedLow; reply.Medium = observedMedium; reply.High = observedHigh;
                }
                else if (payload.Length > 0)
                {
                    if (r.Kind == TuningKind.Scalar)
                    {
                        decimal? scalar = DecodeScalar(payload[0]);
                        if (!scalar.HasValue)
                            reply.ReadIssue = "Scalar getter returned an invalid multiplier; readback is inconclusive.";
                        else reply.Value = scalar.Value;
                    }
                    else reply.Value = r.Kind == TuningKind.CurveOptimizer ? unchecked((int)payload[0]) : (decimal)payload[0];
                }
                else reply.ReadIssue = "Getter returned an empty payload; readback is inconclusive.";
            }
            return reply;
        }
    }

    public sealed class ZenStatesTuningTransport : ITuningTransport, IProfilePreflight
    {
        private readonly Cpu cpu;
        private readonly TuningCommands commands;
        private readonly TopologySnapshot topology;
        private readonly uint[] coreDisableMap;
        private readonly bool isApu;
        public ZenStatesTuningTransport(Cpu cpu)
        {
            this.cpu = cpu ?? throw new ArgumentNullException(nameof(cpu));
            topology = CoreLibraryAdapter.Topology(cpu);
            coreDisableMap = cpu.info.topology.coreDisableMap == null ? null : (uint[])cpu.info.topology.coreDisableMap.Clone();
            isApu = cpu.smu.SMU_TYPE >= SMU.SmuType.TYPE_APU0 && cpu.smu.SMU_TYPE <= SMU.SmuType.TYPE_APU2;
            commands = new TuningCommands
            {
                CoMp1 = cpu.smu.Mp1Smu.SMU_MSG_SetDldoPsmMargin,
                CoRsmu = cpu.smu.Rsmu.SMU_MSG_SetDldoPsmMargin,
                CoGetRsmu = cpu.smu.Rsmu.SMU_MSG_GetDldoPsmMargin,
                FmaxRsmu = cpu.smu.Rsmu.SMU_MSG_SetBoostLimitFrequencyAllCores,
                FmaxMp1 = cpu.smu.Mp1Smu.SMU_MSG_SetBoostLimitFrequencyAllCores,
                FmaxGetRsmu = cpu.smu.Rsmu.SMU_MSG_GetBoostLimitFrequency,
                CurveShaperSetRsmu = cpu.smu.Rsmu.SMU_MSG_SetCurveShaperMargin,
                CurveShaperGetRsmu = cpu.smu.Rsmu.SMU_MSG_GetCurveShaperMargin,
                ScalarGetRsmu = cpu.smu.Rsmu.SMU_MSG_GetPBOScalar,
                TemperatureRsmu = cpu.smu.Rsmu.SMU_MSG_SetTctlMax,
                ApuFmaxArgument = cpu.smu.SMU_TYPE == SMU.SmuType.TYPE_APU0
            };
        }
        public string ValidateProfile(Profiles.Profile profile) => topology.Validate(profile);
        public bool IsSupported(TuningRequest r, out string reason)
        {
            reason = "Command not defined by the bundled core library; skipped.";
            switch (r.Kind)
            {
                case TuningKind.CurveOptimizer:
                    if (topology.Find(r.Index) == null) { reason = topology.Error ?? "Core disabled or absent; skipped."; return false; }
                    // The bundled setter masks off flat APU core indices. Do not guess a new mapping.
                    if (isApu) { reason = "Per-core APU addressing is unvalidated in the bundled setter; skipped."; return false; }
                    return commands.CoMp1 != 0 || commands.CoRsmu != 0;
                case TuningKind.Fmax: return commands.FmaxRsmu != 0 || commands.FmaxMp1 != 0;
                case TuningKind.CurveShaper: return commands.CurveShaperSetRsmu != 0;
                case TuningKind.Temperature:
                    reason = "Temperature editing is enabled only for the reviewed Raphael/Granite Ridge RSMU contract.";
                    return CpuOverviewProvider.CanSetTemperature(cpu);
                default: return true;
            }
        }
        public TuningReply Write(TuningRequest r)
        {
            if (r.Kind == TuningKind.CurveOptimizer || r.Kind == TuningKind.Fmax || r.Kind == TuningKind.CurveShaper || r.Kind == TuningKind.Temperature)
            {
                var reply = commands.Execute(r, false, topology.Find(r.Index)?.Mask ?? 0, Send);
                if (reply != null && r.Kind == TuningKind.CurveOptimizer) reply.TargetIdentity = topology.Find(r.Index)?.Identity;
                return reply;
            }
            SMU.Status status;
            switch (r.Kind)
            {
                case TuningKind.Ppt: status = cpu.SetPPTLimit((uint)r.Value); break;
                case TuningKind.Tdc: status = cpu.SetTDCVDDLimit((uint)r.Value); break;
                case TuningKind.Edc: status = cpu.SetEDCVDDLimit((uint)r.Value); break;
                case TuningKind.Scalar: status = cpu.SetPBOScalar((uint)r.Value); break;
                default: throw new ArgumentOutOfRangeException(nameof(r));
            }
            return new TuningReply { Status = (uint)status, Route = "CPU API" };
        }
        public TuningReply Read(TuningRequest r) => commands.Execute(r, true, topology.Find(r.Index)?.Mask ?? 0, Send);
        private TuningReply Send(string route, uint command, uint[] args)
        {
            var original = args;
            var status = route == "MP1" ? cpu.smu.SendMp1Command(command, ref args) : cpu.smu.SendRsmuCommand(command, ref args);
            if (!ReferenceEquals(original, args) && args != null)
                Array.Copy(args, original, Math.Min(args.Length, original.Length));
            return new TuningReply { Status = (uint)status, RawArguments = args == null ? new uint[0] : (uint[])args.Clone() };
        }
    }
}
