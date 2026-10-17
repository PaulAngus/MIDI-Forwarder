using MidiForwarder.Core;

namespace MidiForwarder.Core.Tests;

public sealed class MidiDeviceDiscoveryTests
{
    [Fact]
    public async Task ConcurrentRefreshesShareOneWorkerAndTheNextRefreshRetries()
    {
        using var release = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        var discovery = new MidiDeviceDiscovery(() =>
        {
            Assert.True(Thread.CurrentThread.IsThreadPoolThread);
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) { throw new TimeoutException("Test did not release discovery."); }
            return new(false, false, "Legacy", [], [], []);
        });
        Task<MidiDeviceSnapshot> first = discovery.RefreshAsync();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task<MidiDeviceSnapshot> second = discovery.RefreshAsync();
            Assert.Same(first, second);
            Assert.Equal(1, calls);
            release.Set();
            Assert.Same(await first, await second);
            await discovery.RefreshAsync();
            Assert.Equal(2, calls);
        }
        finally { release.Set(); await first.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [Fact]
    public async Task AFailedDiscoveryDoesNotPreventAnExplicitRetry()
    {
        int calls = 0;
        var discovery = new MidiDeviceDiscovery(() => ++calls == 1 ? throw new IOException("Driver unavailable.")
            : new(true, true, "Recovered", [], [], []));

        await Assert.ThrowsAsync<IOException>(() => discovery.RefreshAsync());
        MidiDeviceSnapshot result = await discovery.RefreshAsync();

        Assert.Equal(2, calls);
        Assert.True(result.UseMidiServices);
        Assert.True(result.CanCreateVirtualPorts);
    }
}
