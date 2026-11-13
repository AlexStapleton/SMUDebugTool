using System.Threading;
using ZenStates.Core;

namespace ZenStatesDebugTool.Profiles
{
    public class ProfileApplier
    {
        public TuningResult Apply(Profile profile, Cpu cpu, CancellationToken cancellation = default(CancellationToken))
        {
            return Hardware.Locked(() => Apply(profile, new ZenStatesTuningTransport(cpu), cancellation));
        }
        public TuningResult Apply(Profile profile, ITuningTransport transport, CancellationToken cancellation = default(CancellationToken))
            => new TuningService(transport).Apply(profile, cancellation);
        public static bool IsFmaxSupported(Cpu cpu) => cpu.smu.Rsmu.SMU_MSG_SetBoostLimitFrequencyAllCores != 0
            || cpu.smu.Mp1Smu.SMU_MSG_SetBoostLimitFrequencyAllCores != 0;
    }
}
