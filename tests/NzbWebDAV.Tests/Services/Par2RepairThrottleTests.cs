using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

[Collection("Par2Repair")]
public class Par2RepairThrottleTests : IDisposable
{
    public Par2RepairThrottleTests() => Par2RepairThrottle.ResetForTests();
    public void Dispose() => Par2RepairThrottle.ResetForTests();

    [Fact]
    public void FirstRepairEntersWhenNothingIsRunning()
    {
        Assert.True(Par2RepairThrottle.TryEnter(1));
        Assert.Equal(1, Par2RepairThrottle.InFlight);
    }

    [Fact]
    public void SecondRepairIsRefusedAtACapOfOne()
    {
        // refusing rather than queueing: a waiting repair would block the
        // health-check loop behind another item's multi-minute download.
        Assert.True(Par2RepairThrottle.TryEnter(1));
        Assert.False(Par2RepairThrottle.TryEnter(1));
        Assert.Equal(1, Par2RepairThrottle.InFlight);
    }

    [Fact]
    public void ExitFreesACapacitySlot()
    {
        Assert.True(Par2RepairThrottle.TryEnter(1));
        Par2RepairThrottle.Exit();
        Assert.Equal(0, Par2RepairThrottle.InFlight);
        Assert.True(Par2RepairThrottle.TryEnter(1));
    }

    [Fact]
    public void ARaisedCapAdmitsMoreRepairs()
    {
        Assert.True(Par2RepairThrottle.TryEnter(3));
        Assert.True(Par2RepairThrottle.TryEnter(3));
        Assert.True(Par2RepairThrottle.TryEnter(3));
        Assert.False(Par2RepairThrottle.TryEnter(3));
    }

    [Fact]
    public void ExitNeverDrivesTheCountNegative()
    {
        // a double-release from a bad finally block must not permanently
        // inflate capacity.
        Par2RepairThrottle.Exit();
        Assert.Equal(0, Par2RepairThrottle.InFlight);
    }
}
