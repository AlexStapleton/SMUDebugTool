using System;
using ZenStates.Core;

namespace ZenStatesDebugTool
{
    // Command routing and packets match the shipped Prebuilt/ZenStates-Core.dll.
    public sealed class TuningCommands
    {
        public uint CoMp1, CoRsmu, CoGetRsmu, FmaxRsmu, FmaxMp1, FmaxGetRsmu;
        public bool ApuFmaxArgument;
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
            else
            {
                command = read ? FmaxGetRsmu : FmaxRsmu != 0 ? FmaxRsmu : FmaxMp1;
                if (!read && FmaxRsmu == 0) route = "MP1";
                args[0] = read ? (ApuFmaxArgument ? 3U : 0U) : (uint)r.Value & 0xFFFFF;
            }
            if (command == 0) return null;
            var reply = send(route, command, args);
            reply.Route = route;
            reply.Command = command;
            reply.TargetMask = r.Kind == TuningKind.CurveOptimizer ? (uint?)coreMask : null;
            reply.Direct = read;
            if (read && reply.Status == 1) reply.Value = r.Kind == TuningKind.CurveOptimizer ? unchecked((int)args[0]) : (decimal)args[0];
            return reply;
        }
    }

    public sealed class ZenStatesTuningTransport : ITuningTransport
    {
        private readonly Cpu cpu;
        private readonly TuningCommands commands;
        private readonly uint[] coreDisableMap;
        private readonly bool isApu, curveShaperSupported;
        public ZenStatesTuningTransport(Cpu cpu)
        {
            this.cpu = cpu ?? throw new ArgumentNullException(nameof(cpu));
            coreDisableMap = cpu.info.topology.coreDisableMap == null ? null : (uint[])cpu.info.topology.coreDisableMap.Clone();
            isApu = cpu.smu.SMU_TYPE >= SMU.SmuType.TYPE_APU0 && cpu.smu.SMU_TYPE <= SMU.SmuType.TYPE_APU2;
            curveShaperSupported = cpu.smu.Rsmu.SMU_MSG_SetCurveShaperMargin != 0;
            commands = new TuningCommands
            {
                CoMp1 = cpu.smu.Mp1Smu.SMU_MSG_SetDldoPsmMargin,
                CoRsmu = cpu.smu.Rsmu.SMU_MSG_SetDldoPsmMargin,
                CoGetRsmu = cpu.smu.Rsmu.SMU_MSG_GetDldoPsmMargin,
                FmaxRsmu = cpu.smu.Rsmu.SMU_MSG_SetBoostLimitFrequencyAllCores,
                FmaxMp1 = cpu.smu.Mp1Smu.SMU_MSG_SetBoostLimitFrequencyAllCores,
                FmaxGetRsmu = cpu.smu.Rsmu.SMU_MSG_GetBoostLimitFrequency,
                ApuFmaxArgument = cpu.smu.SMU_TYPE == SMU.SmuType.TYPE_APU0
            };
        }
        public bool IsSupported(TuningRequest r, out string reason)
        {
            reason = "Command not defined by the bundled core library; skipped.";
            switch (r.Kind)
            {
                case TuningKind.CurveOptimizer:
                    if (!CoreTopology.IsCoreEnabled(coreDisableMap, r.Index)) { reason = "Core disabled or absent; skipped."; return false; }
                    // The bundled setter masks off flat APU core indices. Do not guess a new mapping.
                    if (isApu) { reason = "Per-core APU addressing is unvalidated in the bundled setter; skipped."; return false; }
                    return commands.CoMp1 != 0 || commands.CoRsmu != 0;
                case TuningKind.Fmax: return commands.FmaxRsmu != 0 || commands.FmaxMp1 != 0;
                case TuningKind.CurveShaper: return curveShaperSupported;
                default: return true;
            }
        }
        public TuningReply Write(TuningRequest r)
        {
            if (r.Kind == TuningKind.CurveOptimizer || r.Kind == TuningKind.Fmax)
                return commands.Execute(r, false, CoreTopology.EncodeCoreMarginBitmask(false, r.Index), Send);
            SMU.Status status;
            switch (r.Kind)
            {
                case TuningKind.CurveShaper: status = cpu.SetCurveShaperMargin(r.High, r.Medium, (int)r.Value, r.Index); break;
                case TuningKind.Ppt: status = cpu.SetPPTLimit((uint)r.Value); break;
                case TuningKind.Tdc: status = cpu.SetTDCVDDLimit((uint)r.Value); break;
                case TuningKind.Edc: status = cpu.SetEDCVDDLimit((uint)r.Value); break;
                case TuningKind.Scalar: status = cpu.SetPBOScalar((uint)r.Value); break;
                default: throw new ArgumentOutOfRangeException(nameof(r));
            }
            return new TuningReply { Status = (uint)status, Route = "CPU API" };
        }
        public TuningReply Read(TuningRequest r) => commands.Execute(r, true, CoreTopology.EncodeCoreMarginBitmask(false, r.Index), Send);
        private TuningReply Send(string route, uint command, uint[] args)
        {
            var original = args;
            var status = route == "MP1" ? cpu.smu.SendMp1Command(command, ref args) : cpu.smu.SendRsmuCommand(command, ref args);
            if (!ReferenceEquals(original, args) && args != null)
                Array.Copy(args, original, Math.Min(args.Length, original.Length));
            return new TuningReply { Status = (uint)status };
        }
    }
}
