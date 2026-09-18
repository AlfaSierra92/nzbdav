using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class RecoveryEvictionPlannerTests
{
    private static readonly DateTimeOffset Base = new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);

    private static RecoveryEvictionPlanner.Candidate Candidate(int ageDays, long size) =>
        new(Guid.NewGuid(), Guid.NewGuid(), size, Base.AddDays(-ageDays));

    [Fact]
    public void UnlimitedBudgetNeverEvicts()
    {
        var existing = new[] { Candidate(10, 500), Candidate(1, 500) };
        var plan = RecoveryEvictionPlanner.Plan(existing, capBytes: 0, incomingBytes: 9999);

        Assert.Empty(plan.Evict);
        Assert.True(plan.FitsAfterEviction);
    }

    [Fact]
    public void NothingIsEvictedWhenTheIncomingRepairFits()
    {
        var existing = new[] { Candidate(10, 400) };
        var plan = RecoveryEvictionPlanner.Plan(existing, capBytes: 1000, incomingBytes: 500);

        Assert.Empty(plan.Evict);
        Assert.True(plan.FitsAfterEviction);
    }

    [Fact]
    public void TheOldestRepairIsEvictedFirst()
    {
        var oldest = Candidate(30, 400);
        var newest = Candidate(1, 400);
        var plan = RecoveryEvictionPlanner.Plan([newest, oldest], capBytes: 1000, incomingBytes: 400);

        Assert.Equal([oldest.BlobId], plan.Evict.Select(x => x.BlobId));
        Assert.True(plan.FitsAfterEviction);
    }

    [Fact]
    public void EvictionContinuesUntilTheIncomingRepairFits()
    {
        var oldest = Candidate(30, 300);
        var middle = Candidate(20, 300);
        var newest = Candidate(1, 300);
        var plan = RecoveryEvictionPlanner.Plan([newest, middle, oldest], capBytes: 1000, incomingBytes: 600);

        Assert.Equal([oldest.BlobId, middle.BlobId], plan.Evict.Select(x => x.BlobId));
        Assert.True(plan.FitsAfterEviction);
    }

    [Fact]
    public void EverythingIsEvictedWhenTheIncomingRepairAloneFillsTheBudget()
    {
        var a = Candidate(30, 300);
        var b = Candidate(1, 300);
        var plan = RecoveryEvictionPlanner.Plan([a, b], capBytes: 1000, incomingBytes: 1000);

        Assert.Equal(2, plan.Evict.Count);
        Assert.True(plan.FitsAfterEviction);
    }

    [Fact]
    public void CandidatesWhoseBlobHasVanishedAreNotEvicted()
    {
        // a blob that is already gone reports size 0 and sorts oldest, so it would be
        // evicted first -- deleting a DavItem and triggering an Arr re-search while
        // freeing nothing. Skip it and take the oldest candidate that actually helps.
        var vanished = new RecoveryEvictionPlanner.Candidate(
            Guid.NewGuid(), Guid.NewGuid(), SizeBytes: 0, RepairedAt: DateTimeOffset.MinValue);
        var real = Candidate(10, 400);
        var plan = RecoveryEvictionPlanner.Plan([vanished, real], capBytes: 1000, incomingBytes: 700);

        Assert.Equal([real.BlobId], plan.Evict.Select(x => x.BlobId));
        Assert.True(plan.FitsAfterEviction);
    }

    [Fact]
    public void AVanishedBlobDoesNotCountTowardsTheBudgetEither()
    {
        // it occupies no disk, so it must not push an otherwise-fitting repair over.
        var vanished = new RecoveryEvictionPlanner.Candidate(
            Guid.NewGuid(), Guid.NewGuid(), SizeBytes: 0, RepairedAt: DateTimeOffset.MinValue);
        var plan = RecoveryEvictionPlanner.Plan([vanished], capBytes: 1000, incomingBytes: 900);

        Assert.Empty(plan.Evict);
        Assert.True(plan.FitsAfterEviction);
    }

    [Fact]
    public void ARepairTooLargeForTheWholeBudgetIsRefusedWithoutEvictingAnything()
    {
        // room that can never exist must not be bought by destroying repairs: giving
        // up every existing repair AND still exceeding the budget is pure loss.
        var a = Candidate(30, 300);
        var plan = RecoveryEvictionPlanner.Plan([a], capBytes: 1000, incomingBytes: 5000);

        Assert.False(plan.FitsAfterEviction);
        Assert.Empty(plan.Evict);
    }

    [Fact]
    public void AnIncomingRepairExactlyFillingTheBudgetFits()
    {
        // boundary: total == cap is within budget, so this must not be refused.
        var plan = RecoveryEvictionPlanner.Plan([], capBytes: 1000, incomingBytes: 1000);

        Assert.True(plan.FitsAfterEviction);
        Assert.Empty(plan.Evict);
    }
}
