using NzbWebDAV.Config;

namespace NzbWebDAV.Services;

/// <summary>What the health check should do once PAR2 repair has had its turn.</summary>
public enum Par2RepairNextStep
{
    /// <summary>PAR2 rebuilt the missing bytes; leave the item in place.</summary>
    KeepRepaired,

    /// <summary>Remove the item and ask Radarr/Sonarr for a replacement.</summary>
    ArrResearch,

    /// <summary>Leave the item alone and only record that it needs attention.</summary>
    MarkOnly,

    /// <summary>Delete the item without asking an Arr for a replacement.</summary>
    Delete,
}

/// <summary>
/// Maps a PAR2 repair outcome plus the operator's configured fallback onto the
/// action the health check takes next. Pure so it can be tested without standing
/// up <see cref="HealthCheckService"/>.
/// </summary>
public static class Par2RepairDecision
{
    public static Par2RepairNextStep Resolve(Par2RepairOutcome outcome, Par2Fallback fallback)
    {
        // a successful repair always wins: the fallback describes what to do only
        // when PAR2 could not save the item.
        if (outcome == Par2RepairOutcome.Repaired) return Par2RepairNextStep.KeepRepaired;

        return fallback switch
        {
            Par2Fallback.MarkOnly => Par2RepairNextStep.MarkOnly,
            Par2Fallback.Delete => Par2RepairNextStep.Delete,
            _ => Par2RepairNextStep.ArrResearch,
        };
    }
}
