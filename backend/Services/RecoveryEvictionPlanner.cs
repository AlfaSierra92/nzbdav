namespace NzbWebDAV.Services;

/// <summary>
/// Chooses which existing PAR2 repairs to give up on when the recovery-storage
/// budget cannot fit a new one. Evicting a repair makes its item unplayable again
/// -- the overlay was the only thing covering its dead articles -- so the caller
/// routes every evicted item through an Arr re-search rather than silently
/// dropping the bytes.
/// </summary>
public static class RecoveryEvictionPlanner
{
    /// <param name="RepairedAt">Blob file write time; used only for ordering.</param>
    public sealed record Candidate(Guid DavItemId, Guid BlobId, long SizeBytes, DateTimeOffset RepairedAt);

    /// <param name="Evict">Repairs to give up, oldest first. Empty when none are needed.</param>
    /// <param name="FitsAfterEviction">
    /// False when the incoming repair cannot fit even with every existing repair given
    /// up. The caller must then refuse it, and <see cref="Evict"/> is empty.
    /// </param>
    public sealed record EvictionPlan(IReadOnlyList<Candidate> Evict, bool FitsAfterEviction);

    public static EvictionPlan Plan(
        IReadOnlyList<Candidate> existing, long capBytes, long incomingBytes)
    {
        // 0 means unlimited: never give up a repair we already paid to compute.
        if (capBytes <= 0) return new EvictionPlan([], true);

        // A repair bigger than the entire budget can never fit. Evicting for it would
        // destroy every existing repair AND still overrun the cap -- pure loss -- so
        // refuse it while leaving what is already on disk untouched.
        if (incomingBytes > capBytes) return new EvictionPlan([], false);

        var total = existing.Sum(x => x.SizeBytes) + incomingBytes;
        if (total <= capBytes) return new EvictionPlan([], true);

        var evicted = new List<Candidate>();

        // Skip candidates whose blob has vanished. They report size 0 and a MinValue
        // timestamp, so they would sort oldest and be evicted first -- costing a DavItem
        // and an Arr re-search while freeing nothing, and leaving the budget no better off.
        foreach (var candidate in existing.Where(x => x.SizeBytes > 0).OrderBy(x => x.RepairedAt))
        {
            if (total <= capBytes) break;
            evicted.Add(candidate);
            total -= candidate.SizeBytes;
        }

        return new EvictionPlan(evicted, total <= capBytes);
    }
}
