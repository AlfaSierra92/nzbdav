namespace NzbWebDAV.Api.Controllers.RepairItem;

public class RepairItemResponse : BaseApiResponse
{
    /// <summary>The Par2RepairOutcome name: NotAttempted, Repaired, Infeasible or Failed.</summary>
    public string? Outcome { get; init; }

    /// <summary>Human-readable detail explaining the outcome.</summary>
    public string? Message { get; init; }
}
