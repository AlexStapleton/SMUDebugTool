using System;
using System.Threading;
using System.Threading.Tasks;

namespace ZenStatesDebugTool
{
    // A single shutdown barrier for every caller sharing the driver. Closing is visible
    // before taking the hardware lock, so queued work cannot run after disposal.
    public sealed class HardwareLifetime
    {
        private readonly object sync;
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly object shutdownSync = new object();
        private Task shutdown;
        public HardwareLifetime(object sync) { this.sync = sync; }
        public CancellationToken ClosingToken => lifetime.Token;
        public bool IsClosing => lifetime.IsCancellationRequested;
        public void ThrowIfClosing() => ClosingToken.ThrowIfCancellationRequested();
        public Task ShutdownAsync(Action dispose)
        {
            lock (shutdownSync)
            {
                if (shutdown != null) return shutdown;
                lifetime.Cancel();
                return shutdown = Task.Run(() => { lock (sync) dispose(); });
            }
        }
    }

    public static partial class Hardware
    {
        public static CancellationToken ClosingToken => lifetime.ClosingToken;
        public static bool IsClosing => lifetime.IsClosing;
        public static void ThrowIfClosing() => lifetime.ThrowIfClosing();
        public static Task ShutdownAsync(Action dispose) => lifetime.ShutdownAsync(dispose);
        // Pollers skip a busy driver rather than accumulating blocked threads.
        public static bool TryRead<T>(Func<T> read, out T value)
        {
            value = default(T);
            if (IsClosing || !Monitor.TryEnter(Sync)) return false;
            try { if (IsClosing) return false; value = read(); return true; }
            finally { Monitor.Exit(Sync); }
        }
    }
}
