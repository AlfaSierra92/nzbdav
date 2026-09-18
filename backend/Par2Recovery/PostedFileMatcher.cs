using System.Security.Cryptography;

namespace NzbWebDAV.Par2Recovery;

/// <summary>
/// Matches a DavItem's posted-file parts (the raw file, each rar volume, or each
/// multipart part) to the recovery set's files. Primary key: MD5 of the first 16KB,
/// exactly what FileDesc.File16kHash stores — robust against filename obfuscation.
/// </summary>
public static class PostedFileMatcher
{
    public const int HashLength = 16384;

    /// <param name="PartIndex">Index of the posted file within the DavItem (0 for DavNzbFile).</param>
    /// <param name="Length">Exact decoded length of the posted file in bytes.</param>
    public sealed record PostedPart(int PartIndex, long Length);

    /// <summary>
    /// Pairs each part with its recovery-set file. Parts whose head can't be read
    /// (dead first article) are left out of the result unless resolvable another way.
    /// </summary>
    /// <param name="readFirst16Kb">
    /// Reads the first min(16KB, length) decoded bytes of a part, or null when unavailable.
    /// </param>
    public static async Task<IReadOnlyDictionary<int, RecoverySetInfo.RecoverySetFile>> MatchAsync
    (
        IReadOnlyList<PostedPart> parts,
        IReadOnlyList<RecoverySetInfo.RecoverySetFile> files,
        Func<int, Task<byte[]?>> readFirst16Kb,
        CancellationToken ct = default
    )
    {
        var matches = new Dictionary<int, RecoverySetInfo.RecoverySetFile>();
        var filesByHash = files
            .GroupBy(x => Convert.ToHexString(x.Hash16k))
            .ToDictionary(g => g.Key, g => g.First());

        var unmatchedParts = new List<PostedPart>();
        foreach (var part in parts)
        {
            ct.ThrowIfCancellationRequested();
            var head = await readFirst16Kb(part.PartIndex).ConfigureAwait(false);
            var hash = head == null ? null : Convert.ToHexString(MD5.HashData(head));
            if (hash != null && filesByHash.TryGetValue(hash, out var file) && file.Length == part.Length)
                matches[part.PartIndex] = file;
            else
                unmatchedParts.Add(part);
        }

        // fallback: a part whose head is unreadable (dead first article) can still be
        // matched when its exact length is unique among the files not yet claimed.
        var unclaimedFiles = files.Except(matches.Values).ToList();
        foreach (var part in unmatchedParts)
        {
            var candidates = unclaimedFiles.Where(x => x.Length == part.Length).ToList();
            if (candidates.Count != 1) continue;
            matches[part.PartIndex] = candidates[0];
            unclaimedFiles.Remove(candidates[0]);
        }

        return matches;
    }
}
