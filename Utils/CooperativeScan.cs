using System;
using System.Threading;

namespace ZenStatesDebugTool
{
    public static class CooperativeScan
    {
        // Publish only a complete pass. A busy driver drops the pass instead of queuing it.
        public static bool ReadBatches(int count, int batchSize, Action<int> read, Func<Action, bool> tryBatch, CancellationToken cancellation, Action yield = null)
        {
            if (count < 0 || batchSize < 1 || batchSize > 64) throw new ArgumentOutOfRangeException();
            for (int start = 0; start < count;) {
                cancellation.ThrowIfCancellationRequested();
                int first = start, end = start + Math.Min(count - start, batchSize);
                if (!tryBatch(() => { for (int i = first; i < end; i++) { cancellation.ThrowIfCancellationRequested(); read(i); } })) return false;
                if (end < count) { if (yield != null) yield(); else Thread.Yield(); }
                start = end;
            }
            return true;
        }
    }
}
