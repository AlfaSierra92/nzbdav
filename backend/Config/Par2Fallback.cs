namespace NzbWebDAV.Config;

/// <summary>
/// What to do with an item when PAR2 repair cannot recover it (the recovery set
/// is insufficient, the par2 volumes are missing, or reconstruction failed).
/// Serialized to/from the <c>repair.par2.fallback</c> config value as a
/// lowercase hyphenated string.
/// </summary>
public enum Par2Fallback
{
    /// <summary>
    /// Today's behaviour: remove the item and let Radarr/Sonarr search for a
    /// replacement. The default, so that enabling PAR2 repair never changes
    /// what happens to items PAR2 could not save.
    /// </summary>
    ArrResearch,

    /// <summary>
    /// Leave the item in place and only record the failed health check. Useful
    /// for operators who would rather triage by hand than lose the item.
    /// </summary>
    MarkOnly,

    /// <summary>
    /// Delete the item outright without asking an Arr for a replacement.
    /// </summary>
    Delete,
}
