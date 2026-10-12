namespace MidiForwarder.Core.Tests;

public sealed class ProcessInstanceTests
{
    [Fact]
    public void DuplicateInstanceIsRejectedWithoutReleasingTheRunningInstance()
    {
        string name = "MidiForwarder-test-" + Guid.NewGuid().ToString("N");
        using (var first = ProcessInstance.TryAcquire(name))
        {
            Assert.NotNull(first);
            using var second = ProcessInstance.TryAcquire(name);
            Assert.Null(second);
            using var third = ProcessInstance.TryAcquire(name);
            Assert.Null(third);
        }
        using var restarted = ProcessInstance.TryAcquire(name);
        Assert.NotNull(restarted);
    }
}
