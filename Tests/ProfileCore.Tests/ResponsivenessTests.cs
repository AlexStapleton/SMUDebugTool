using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Xunit;
using ZenStatesDebugTool;

namespace ProfileCore.Tests
{
    public class ResponsivenessTests
    {
        [Fact]
        public void StaticLifetimeUsesTheSameInitializedDriverLock()
        {
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            var lifetime = typeof(Hardware).GetField("lifetime", flags).GetValue(null);
            var sync = typeof(HardwareLifetime).GetField("sync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(lifetime);
            Assert.Same(Hardware.Sync, sync);
        }
        [Fact]
        public async Task SlowOperationAllowsWinFormsHeartbeatAndRejectsOverlappingWork()
        {
            var finished = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                using (var release = new ManualResetEventSlim())
                using (var context = new ApplicationContext())
                using (var timer = new System.Windows.Forms.Timer { Interval = 15 })
                {
                    var runner = new HardwareOperationRunner();
                    int ticks = 0;
                    timer.Tick += (sender, args) => { if (++ticks >= 5) release.Set(); };
                    SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
                    timer.Start();
                    // No Cpu construction, driver initialization, or hardware access.
                    async void Run()
                    {
                        try
                        {
                            int uiThread = Thread.CurrentThread.ManagedThreadId;
                            var operation = runner.RunAsync(() => { Assert.NotEqual(uiThread, Thread.CurrentThread.ManagedThreadId); release.Wait(3000); return 42; });
                            await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(() => 0));
                            Assert.Equal(42, await operation);
                            Assert.False(runner.IsBusy);
                            Assert.Equal(7, await runner.RunAsync(() => 7));
                            finished.TrySetResult(ticks);
                        }
                        catch (Exception ex) { finished.TrySetException(ex); }
                        finally { timer.Stop(); context.ExitThread(); }
                    }
                    Run();
                    Application.Run(context);
                }
            })
            { IsBackground = true };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            Assert.Same(finished.Task, await Task.WhenAny(finished.Task, Task.Delay(10000)));
            Assert.True(await finished.Task >= 5, "UI heartbeat stopped while the operation was running.");
        }

        [Fact]
        public async Task RunnerReleasesGateAfterFailure()
        {
            var runner = new HardwareOperationRunner();
            await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync<int>(() => throw new InvalidOperationException("driver error")));
            Assert.Equal(1, await runner.RunAsync(() => 1));
        }

        [Fact]
        public async Task ShutdownCancelsQueuedWorkWaitsForCurrentCommandAndDisposesOnce()
        {
            var sync = new object();
            var lifetime = new HardwareLifetime(sync);
            int disposed = 0;
            using (var entered = new ManualResetEventSlim())
            using (var release = new ManualResetEventSlim())
            {
                var command = Task.Run(() => { lock (sync) { entered.Set(); release.Wait(5000); } });
                Assert.True(entered.Wait(5000));
                var shutdown = lifetime.ShutdownAsync(() => Interlocked.Increment(ref disposed));
                try
                {
                    Assert.True(lifetime.ClosingToken.IsCancellationRequested);
                    Assert.Throws<OperationCanceledException>(() => lifetime.ThrowIfClosing());
                    Assert.False(shutdown.IsCompleted);
                    Assert.Same(shutdown, lifetime.ShutdownAsync(() => Interlocked.Increment(ref disposed)));
                }
                finally { release.Set(); }
                await command;
                await shutdown;
                Assert.Equal(1, disposed);
            }
        }
    }
}
