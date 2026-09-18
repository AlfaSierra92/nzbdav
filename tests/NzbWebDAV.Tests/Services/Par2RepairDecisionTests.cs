using NzbWebDAV.Config;
using NzbWebDAV.Services;

namespace NzbWebDAV.Tests.Services;

public class Par2RepairDecisionTests
{
    [Theory]
    [InlineData(Par2Fallback.ArrResearch)]
    [InlineData(Par2Fallback.MarkOnly)]
    [InlineData(Par2Fallback.Delete)]
    public void RepairedItemsAreKeptRegardlessOfFallback(Par2Fallback fallback)
    {
        // the fallback only describes what to do when PAR2 could NOT save the item.
        // A successful repair must never be followed by a delete or an Arr search.
        Assert.Equal(
            Par2RepairNextStep.KeepRepaired,
            Par2RepairDecision.Resolve(Par2RepairOutcome.Repaired, fallback));
    }

    [Theory]
    [InlineData(Par2RepairOutcome.NotAttempted)]
    [InlineData(Par2RepairOutcome.Infeasible)]
    [InlineData(Par2RepairOutcome.Failed)]
    public void UnrepairableItemsFollowTheArrResearchFallback(Par2RepairOutcome outcome)
    {
        // arr-research is the default and reproduces the pre-PAR2 behaviour exactly.
        Assert.Equal(
            Par2RepairNextStep.ArrResearch,
            Par2RepairDecision.Resolve(outcome, Par2Fallback.ArrResearch));
    }

    [Theory]
    [InlineData(Par2RepairOutcome.NotAttempted)]
    [InlineData(Par2RepairOutcome.Infeasible)]
    [InlineData(Par2RepairOutcome.Failed)]
    public void UnrepairableItemsCanBeLeftInPlace(Par2RepairOutcome outcome)
    {
        Assert.Equal(
            Par2RepairNextStep.MarkOnly,
            Par2RepairDecision.Resolve(outcome, Par2Fallback.MarkOnly));
    }

    [Theory]
    [InlineData(Par2RepairOutcome.NotAttempted)]
    [InlineData(Par2RepairOutcome.Infeasible)]
    [InlineData(Par2RepairOutcome.Failed)]
    public void UnrepairableItemsCanBeDeletedOutright(Par2RepairOutcome outcome)
    {
        Assert.Equal(
            Par2RepairNextStep.Delete,
            Par2RepairDecision.Resolve(outcome, Par2Fallback.Delete));
    }
}
