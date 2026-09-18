using NzbWebDAV.Clients.RadarrSonarr;

namespace NzbWebDAV.Services;

/// <summary>
/// Asks the Radarr/Sonarr instance that owns a library link to remove the item and
/// search for a replacement. Extracted from <see cref="HealthCheckService"/> so the
/// PAR2 storage-budget eviction can reuse it rather than duplicating the matching
/// rules, and so the behaviour is testable for the first time.
/// </summary>
public static class ArrResearchService
{
    /// <summary>
    /// Finds the Arr whose root folders contain <paramref name="symlinkOrStrmPath"/>
    /// and asks it to remove-and-search. Returns whether an Arr accepted the request.
    /// </summary>
    /// <remarks>
    /// Only the owning Arr is tried. If it has no matching media item, later
    /// instances are deliberately NOT asked -- the caller falls back to deleting the
    /// link rather than handing the path to an unrelated Arr.
    /// </remarks>
    public static async Task<bool> TryRemoveAndSearchAsync(
        IEnumerable<ArrClient> arrClients, string symlinkOrStrmPath)
    {
        foreach (var arrClient in arrClients)
        {
            var rootFolders = await arrClient.GetRootFolders().ConfigureAwait(false);
            if (!rootFolders.Any(x => symlinkOrStrmPath.StartsWith(x.Path!))) continue;

            return await arrClient.RemoveAndSearch(symlinkOrStrmPath).ConfigureAwait(false);
        }

        return false;
    }
}
