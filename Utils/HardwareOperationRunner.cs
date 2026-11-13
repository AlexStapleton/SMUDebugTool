using System;
using System.Threading;
using System.Threading.Tasks;

namespace ZenStatesDebugTool
{
    public sealed class HardwareOperationRunner
    {
        private int busy;
        public bool IsBusy => Volatile.Read(ref busy) != 0;
        public async Task<T> RunAsync<T>(Func<T> action)
        {
            if (Interlocked.CompareExchange(ref busy, 1, 0) != 0)
                throw new InvalidOperationException("Another hardware operation is running.");
            try { return await Task.Run(action); }
            finally { Volatile.Write(ref busy, 0); }
        }
    }
}
