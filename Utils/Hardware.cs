using System;

namespace ZenStatesDebugTool
{
    // Serializes all low-level hardware access (SMU mailbox, PCI/MSR/CPUID reads and
    // writes, physical-memory reads, power-table refresh) behind a single process-wide
    // lock.
    //
    // Why this exists: there is exactly one Cpu/driver instance, but it is touched from
    // several threads — the startup hardware-load thread, the BackgroundWorker scans, the
    // UI-thread button handlers, and each monitor dialog's own poll loop/timer
    // (SMUMonitor, PCIRangeMonitor, PowerTableMonitor). An SMU transaction is stateful
    // (write CMD -> write ARG -> poll RSP); two readers interleaving on the same mailbox
    // corrupt each other's results or hang. Funnelling every access through Sync makes
    // those transactions atomic with respect to one another.
    //
    // The lock is reentrant (Monitor is per-thread recursive), so coarse-grained locking
    // of a whole UI-thread operation that internally calls other locked helpers is safe.
    // The one rule: do NOT hold the lock across a synchronous Control.Invoke back to the
    // UI thread — if the UI thread is itself waiting on the lock that is a cross-thread
    // deadlock. Background loops therefore lock only their hardware reads and marshal UI
    // updates (BeginInvoke / post-lock Invoke) outside the locked region.
    public static class Hardware
    {
        public static readonly object Sync = new object();

        public static void Locked(Action action)
        {
            lock (Sync) action();
        }

        public static T Locked<T>(Func<T> func)
        {
            lock (Sync) return func();
        }
    }
}
