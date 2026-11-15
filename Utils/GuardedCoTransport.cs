using System;
using System.Threading;

namespace ZenStatesDebugTool
{
    // Evaluate the live guard at the final setter boundary, after potentially slow reads.
    public sealed class GuardedCoTransport : ITuningTransport
    {
        private readonly ITuningTransport inner;
        private readonly Func<string> guard;
        private readonly CancellationToken cancellation;
        public GuardedCoTransport(ITuningTransport inner, Func<string> guard, CancellationToken cancellation)
        { this.inner = inner; this.guard = guard; this.cancellation = cancellation; }
        public bool IsSupported(TuningRequest request, out string reason) => inner.IsSupported(request, out reason);
        public TuningReply Read(TuningRequest request) { cancellation.ThrowIfCancellationRequested(); return inner.Read(request); }
        public TuningReply Write(TuningRequest request)
        {
            cancellation.ThrowIfCancellationRequested(); string issue = guard();
            if (issue != null) throw new InvalidOperationException(issue);
            cancellation.ThrowIfCancellationRequested(); return inner.Write(request);
        }
    }
}
