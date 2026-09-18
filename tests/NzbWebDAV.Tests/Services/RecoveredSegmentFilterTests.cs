using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class RecoveredSegmentFilterTests
{
    private static DavFileRecovery RecoveryWith(params (int PartIndex, int[] Indices)[] spans) => new()
    {
        DeadSegments = spans
            .Select(x => new DavFileRecovery.DeadSegmentSpan { PartIndex = x.PartIndex, SegmentIndices = x.Indices })
            .ToArray()
    };

    [Fact]
    public void UnrepairedItemKeepsEverySegment()
    {
        var parts = new[] { new[] { "a", "b", "c" } };
        Assert.Equal(new[] { "a", "b", "c" }, RecoveredSegmentFilter.Filter(parts, null));
    }

    [Fact]
    public void RecoveredSegmentsAreSkipped()
    {
        // "b" was dead and has been reconstructed into the recovery blob, so the
        // health check must not ask usenet for it -- that would throw and condemn
        // an item that is actually being served correctly.
        var parts = new[] { new[] { "a", "b", "c" } };
        var recovery = RecoveryWith((0, [1]));
        Assert.Equal(new[] { "a", "c" }, RecoveredSegmentFilter.Filter(parts, recovery));
    }

    [Fact]
    public void DeadIndicesApplyPerPartNotGlobally()
    {
        // rar volumes: index 0 of part 1 is a different segment from index 0 of part 0.
        var parts = new[] { new[] { "a0", "a1" }, new[] { "b0", "b1" } };
        var recovery = RecoveryWith((1, [0]));
        Assert.Equal(new[] { "a0", "a1", "b1" }, RecoveredSegmentFilter.Filter(parts, recovery));
    }

    [Fact]
    public void PartsFlattenInOrder()
    {
        var parts = new[] { new[] { "a0" }, new[] { "b0", "b1" } };
        Assert.Equal(new[] { "a0", "b0", "b1" }, RecoveredSegmentFilter.Filter(parts, null));
    }

    [Fact]
    public void OutOfRangeAndUnknownPartIndicesAreIgnored()
    {
        // a stale recovery blob must degrade to "filter nothing" rather than throw
        // and take the whole health check down.
        var parts = new[] { new[] { "a", "b" } };
        var recovery = RecoveryWith((0, [7]), (9, [0]));
        Assert.Equal(new[] { "a", "b" }, RecoveredSegmentFilter.Filter(parts, recovery));
    }

    [Fact]
    public void EveryIndexDeadYieldsNoSegments()
    {
        // a fully recovered part has nothing left to health check.
        var parts = new[] { new[] { "a", "b" } };
        Assert.Empty(RecoveredSegmentFilter.Filter(parts, RecoveryWith((0, [0, 1]))));
    }
}
