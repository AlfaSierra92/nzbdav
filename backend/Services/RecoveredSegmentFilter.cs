using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Services;

/// <summary>
/// Drops the segments that a PAR2 repair has already reconstructed from a health
/// check's segment list. Those articles are genuinely gone from usenet, but their
/// bytes are served from the recovery blob, so asking for them would throw
/// <c>UsenetArticleNotFoundException</c> and condemn a file that streams fine.
/// </summary>
public static class RecoveredSegmentFilter
{
    /// <param name="parts">
    /// Each posted-file part's segment ids, in the same order
    /// <see cref="Par2RepairService"/> assigned part indices: a single part for a
    /// DavNzbFile, one per rar volume for a DavRarFile.
    /// </param>
    /// <param name="recovery">The item's recovery metadata, or null if never repaired.</param>
    public static List<string> Filter(IReadOnlyList<string[]> parts, DavFileRecovery? recovery)
    {
        var deadByPart = recovery?.DeadSegments
            .GroupBy(x => x.PartIndex)
            .ToDictionary(g => g.Key, g => g.SelectMany(x => x.SegmentIndices).ToHashSet());

        var segments = new List<string>();
        for (var partIndex = 0; partIndex < parts.Count; partIndex++)
        {
            var part = parts[partIndex];
            HashSet<int>? deadIndices = null;
            deadByPart?.TryGetValue(partIndex, out deadIndices);
            for (var i = 0; i < part.Length; i++)
            {
                // an index past the end of the part, or a part index we don't have,
                // simply never matches here: a stale blob filters nothing rather
                // than throwing and taking the health check down.
                if (deadIndices != null && deadIndices.Contains(i)) continue;
                segments.Add(part[i]);
            }
        }

        return segments;
    }
}
