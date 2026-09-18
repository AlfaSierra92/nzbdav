using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Models;
using NzbWebDAV.Models.Nzb;
using NzbWebDAV.Par2Recovery;
using NzbWebDAV.Par2Recovery.Packets;
using NzbWebDAV.Utils;
using Serilog;

namespace NzbWebDAV.Services;

public enum Par2RepairOutcome
{
    /// <summary>Repair was not applicable (no NZB blob, no par2 data, or nothing to repair).</summary>
    NotAttempted = 0,

    /// <summary>Missing slices were reconstructed, verified, and persisted.</summary>
    Repaired = 1,

    /// <summary>More slices are missing than surviving recovery slices can reconstruct.</summary>
    Infeasible = 2,

    /// <summary>Repair was attempted but failed (verification mismatch or unexpected error).</summary>
    Failed = 3,
}

public sealed record Par2RepairResult(Par2RepairOutcome Outcome, string Message, Guid? RecoveryBlobId = null);

/// <summary>
/// Reconstructs the missing articles of a usenet file from the PAR2 recovery volumes
/// posted alongside it, persisting only the recovered bytes (docs/par2-repair-design.md).
/// Invoked by health checks and the manual repair endpoint.
/// </summary>
/// <param name="configManager">
/// Supplies the concurrency cap and storage budget. Null in tests that exercise the
/// reconstruction pipeline directly, in which case the defaults apply: one repair at
/// a time and an unlimited storage budget.
/// </param>
public class Par2RepairService(INntpClient usenetClient, ConfigManager? configManager = null)
{
    private int MaxConcurrentRepairs => configManager?.GetPar2MaxConcurrentRepairs() ?? 1;
    private long MaxStorageBytes => configManager?.GetPar2MaxStorageBytes() ?? 0;

    private sealed record PostedPart(int PartIndex, string[] SegmentIds, long Length);

    private sealed record PartDamage(PostedPart Part, int[] DeadSegmentIndices, List<LongRange> DeadSpans);

    private sealed record Par2Volume(string[] SegmentIds, long Size);

    private sealed record RecoverySliceSource(Par2Volume Volume, Par2VolumeScanner.RecoverySlicePointer Pointer);

    public async Task<Par2RepairResult> RepairAsync
    (
        DavItem davItem,
        DavDatabaseClient dbClient,
        CancellationToken ct = default
    )
    {
        ct.ThrowIfCancellationRequested();
        if (davItem.Type != DavItem.ItemType.UsenetFile || davItem.NzbBlobId == null)
            return new Par2RepairResult(Par2RepairOutcome.NotAttempted,
                "Item has no persisted NZB to locate par2 recovery volumes with.");

        var maxConcurrent = MaxConcurrentRepairs;
        if (!Par2RepairThrottle.TryEnter(maxConcurrent))
        {
            Log.Information(
                "Skipping PAR2 repair of {Path}: already running the maximum of {MaxConcurrent} simultaneous repair(s).",
                davItem.Path, maxConcurrent);
            return new Par2RepairResult(Par2RepairOutcome.NotAttempted,
                $"Already running the maximum of {maxConcurrent} simultaneous PAR2 repair(s).");
        }

        try
        {
            return await RepairCoreAsync(davItem, dbClient, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e) when (!ct.IsCancellationRequested)
        {
            Log.Error(e, "PAR2 repair of {Path} failed unexpectedly: {Message}", davItem.Path, e.Message);
            return new Par2RepairResult(Par2RepairOutcome.Failed, $"Unexpected error during PAR2 repair: {e.Message}");
        }
        finally
        {
            Par2RepairThrottle.Exit();
        }
    }

    private async Task<Par2RepairResult> RepairCoreAsync(DavItem davItem, DavDatabaseClient dbClient, CancellationToken ct)
    {
        // 1. load the original NZB (load-bearing: it locates the par2 recovery volumes)
        NzbDocument nzb;
        await using (var nzbStream = BlobStore.ReadBlob(davItem.NzbBlobId!.Value))
        {
            if (nzbStream == null)
                return new Par2RepairResult(Par2RepairOutcome.NotAttempted, "The item's NZB blob is missing.");
            nzb = await NzbDocument.LoadAsync(nzbStream).ConfigureAwait(false);
        }

        // 2. resolve the posted files this item consists of
        var parts = await LoadPostedPartsAsync(davItem, dbClient, ct).ConfigureAwait(false);
        if (parts == null)
            return new Par2RepairResult(Par2RepairOutcome.NotAttempted,
                "Item type is not supported for PAR2 repair.");

        // 3. scan par2 volumes: metadata packets in full, recovery slices as pointers
        var (metadataPackets, sliceSources) = await ScanPar2VolumesAsync(nzb, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (metadataPackets.Count == 0)
            return new Par2RepairResult(Par2RepairOutcome.NotAttempted,
                "The NZB contains no readable par2 recovery data.");
        var recoverySet = RecoverySetInfo.Build(metadataPackets);

        // 4. match posted files to the recovery set
        var matcherParts = parts.Select(p => new PostedFileMatcher.PostedPart(p.PartIndex, p.Length)).ToList();
        var matches = await PostedFileMatcher.MatchAsync(
            matcherParts, recoverySet.Files, partIndex => ReadFirst16KbAsync(parts[partIndex], ct), ct
        ).ConfigureAwait(false);

        // conservative v1: every recovery-set file must be accounted for by a posted part,
        // because every surviving input slice must be streamed through the recovery math.
        var unmatchedFiles = recoverySet.Files.Except(matches.Values).ToList();
        if (unmatchedFiles.Count > 0)
            return new Par2RepairResult(Par2RepairOutcome.Infeasible,
                $"Recovery-set file '{unmatchedFiles[0].FileName}' could not be matched to this item's posted files.");

        // 5. identify damage: stat every segment, bound dead spans with neighbour yEnc headers
        var damages = new List<PartDamage>();
        foreach (var part in parts)
            damages.Add(await IdentifyDamageAsync(part, ct).ConfigureAwait(false));
        if (damages.All(x => x.DeadSegmentIndices.Length == 0))
            return new Par2RepairResult(Par2RepairOutcome.NotAttempted, "No missing articles were found.");

        Log.Information(
            "Attempting PAR2 repair of {Path}: {DeadSegments} dead article(s) across {DamagedParts} posted-file part(s).",
            davItem.Path,
            damages.Sum(x => x.DeadSegmentIndices.Length),
            damages.Count(x => x.DeadSegmentIndices.Length > 0));
        if (damages.Any(x => x.DeadSegmentIndices.Length > 0 && !matches.ContainsKey(x.Part.PartIndex)))
            return new Par2RepairResult(Par2RepairOutcome.Infeasible,
                "A damaged posted file could not be matched to the par2 recovery set.");

        // 6. map dead spans to global missing slices
        var missingSlices = new SortedSet<int>();
        var missingByPart = new Dictionary<int, int[]>();
        foreach (var damage in damages.Where(x => x.DeadSpans.Count > 0))
        {
            var file = matches[damage.Part.PartIndex];
            var partMissing = Par2SliceMapper.MissingSliceIndices(
                damage.DeadSpans, file.SliceBase, file.Length, recoverySet.SliceSize);
            missingByPart[damage.Part.PartIndex] = partMissing;
            foreach (var slice in partMissing) missingSlices.Add(slice);
        }

        // 7. feasibility: enough distinct surviving recovery slices?
        var missing = missingSlices.ToArray();
        var distinctSources = sliceSources
            .GroupBy(x => x.Pointer.Exponent)
            .Select(g => g.First())
            .OrderBy(x => x.Pointer.Exponent)
            .ToList();
        if (distinctSources.Count < missing.Length)
        {
            // the single most common operator question is "why couldn't this repair?",
            // so the found-versus-needed figures go to the log, not just the result.
            Log.Information(
                "PAR2 repair of {Path} is infeasible: {MissingSlices} slice(s) missing but only " +
                "{SurvivingRecoverySlices} recovery slice(s) survive.",
                davItem.Path, missing.Length, distinctSources.Count);
            return new Par2RepairResult(Par2RepairOutcome.Infeasible,
                $"{missing.Length} slices are missing but only {distinctSources.Count} recovery slices survive.");
        }

        // 8. download the recovery slices the reconstruction will use
        var recoverySlices = await DownloadRecoverySlicesAsync(
            distinctSources, missing.Length, recoverySet.SliceSize, ct).ConfigureAwait(false);
        if (recoverySlices.Count < missing.Length)
        {
            Log.Information(
                "PAR2 repair of {Path} is infeasible: only {DownloadedSlices} of the {RequiredSlices} " +
                "required recovery slices could be downloaded; the rest have died since the scan.",
                davItem.Path, recoverySlices.Count, missing.Length);
            return new Par2RepairResult(Par2RepairOutcome.Infeasible,
                $"Only {recoverySlices.Count} of the {missing.Length} required recovery slices could be downloaded.");
        }

        // 9. stream every surviving input slice through the recovery math
        var reconstructor = new ReedSolomon.StreamingReconstructor(
            recoverySet.SliceSize, recoverySet.TotalSlices, missing, recoverySlices);
        foreach (var part in parts)
        {
            var damage = damages.First(x => x.Part.PartIndex == part.PartIndex);
            await FeedPresentSlicesAsync(
                part, matches[part.PartIndex], damage, missingSlices, recoverySet.SliceSize, reconstructor, ct
            ).ConfigureAwait(false);
        }

        var rebuilt = reconstructor.Solve();

        // 10. verify every reconstructed slice against its IFSC checksum before trusting it
        for (var i = 0; i < missing.Length; i++)
        {
            var file = recoverySet.Files.First(x =>
                missing[i] >= x.SliceBase && missing[i] < x.SliceBase + x.SliceCount);
            var localSlice = missing[i] - file.SliceBase;
            if (file.SliceChecksums.Length <= localSlice)
                return new Par2RepairResult(Par2RepairOutcome.Failed,
                    "The par2 recovery set has no IFSC checksums to verify the reconstruction against.");
            if (!MD5.HashData(rebuilt[i]).AsSpan().SequenceEqual(file.SliceChecksums[localSlice].Md5))
                return new Par2RepairResult(Par2RepairOutcome.Failed,
                    "A reconstructed slice failed IFSC verification; nothing was persisted.");
        }

        // 11. persist only the recovered bytes + overlay metadata; mark the item repaired
        var blobId = await PersistRecoveryAsync(
            davItem, matches, missingByPart, missing, rebuilt, damages, recoverySet.SliceSize, ct
        ).ConfigureAwait(false);

        Log.Information(
            "Repaired {Path} from PAR2: reconstructed {SliceCount} slice(s) into {RecoveredBytes} bytes of recovery data.",
            davItem.Path, missing.Length, BlobStore.GetBlobSize(blobId));

        var fitsBudget = await EnforceStorageBudgetAsync(davItem, blobId, dbClient, ct).ConfigureAwait(false);
        if (!fitsBudget)
        {
            // the blob was written before its size was known; nothing references it yet,
            // so delete it directly rather than leaving an orphan for the cleanup queue.
            BlobStore.Delete(blobId);
            return new Par2RepairResult(Par2RepairOutcome.Infeasible,
                "The recovered data is larger than the configured recovery-storage budget.");
        }

        var oldBlobId = davItem.RecoveryBlobId;
        if (oldBlobId != null)
            dbClient.Ctx.BlobCleanupItems.Add(new BlobCleanupItem { Id = oldBlobId.Value });
        davItem.RecoveryBlobId = blobId;
        dbClient.Ctx.Items.Update(davItem);

        // Deliberately no HealthCheckResult here: whoever triggered the repair owns the
        // history entry. Writing one as well would give every auto-triggered repair two
        // rows that disagree on health status and double-count on the health page.
        await dbClient.Ctx.SaveChangesAsync(ct).ConfigureAwait(false);

        return new Par2RepairResult(Par2RepairOutcome.Repaired,
            $"Reconstructed {missing.Length} missing slice(s).", blobId);
    }

    /// <summary>
    /// The posted files making up this item, or null when the subtype isn't supported.
    /// PAR2 protects files as posted: the raw file for NzbFile items, each rar volume
    /// for RarFile items. (MultipartFile items are not supported yet — their parts can
    /// be windows into a posted file rather than whole posted files.)
    /// </summary>
    private static async Task<List<PostedPart>?> LoadPostedPartsAsync(
        DavItem davItem, DavDatabaseClient dbClient, CancellationToken ct)
    {
        if (davItem.SubType == DavItem.ItemSubType.NzbFile && davItem.FileSize != null)
        {
            var nzbFile = await dbClient.GetDavNzbFileAsync(davItem, ct).ConfigureAwait(false);
            if (nzbFile == null) return null;
            return [new PostedPart(0, nzbFile.SegmentIds, davItem.FileSize.Value)];
        }

        if (davItem.SubType == DavItem.ItemSubType.RarFile)
        {
            var rarFile = await dbClient.GetDavRarFileAsync(davItem, ct).ConfigureAwait(false);
            if (rarFile == null) return null;
            return rarFile.RarParts
                .Select((part, i) => new PostedPart(i, part.SegmentIds, part.PartSize))
                .ToList();
        }

        return null;
    }

    private async Task<(List<Par2Packet> Metadata, List<RecoverySliceSource> Sources)> ScanPar2VolumesAsync(
        NzbDocument nzb, CancellationToken ct)
    {
        var metadata = new List<Par2Packet>();
        var sources = new List<RecoverySliceSource>();
        var par2Files = nzb.Files
            .Where(x => x.GetSubjectFileName().EndsWith(".par2", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Obfuscated releases give their par2 volumes arbitrary subjects, so matching on
        // the extension finds nothing and the release could never be repaired. Fall back
        // to identifying them by content, as the import path does. Only when the filename
        // pass found nothing, so a normally-named release pays no extra article fetches.
        if (par2Files.Count == 0)
            par2Files = await FindPar2FilesByContentAsync(nzb, ct).ConfigureAwait(false);

        foreach (var par2File in par2Files)
        {
            try
            {
                var size = await usenetClient.GetFileSizeAsync(par2File, ct).ConfigureAwait(false);
                var volume = new Par2Volume(par2File.GetSegmentIds(), size);
                await using var stream = usenetClient.GetFileStream(volume.SegmentIds, size, articleBufferSize: 0);
                var scan = await Par2VolumeScanner.ScanAsync(stream, size, ct).ConfigureAwait(false);
                metadata.AddRange(scan.MetadataPackets);
                sources.AddRange(scan.RecoverySlices.Select(p => new RecoverySliceSource(volume, p)));
            }
            catch (UsenetArticleNotFoundException)
            {
                // the volume itself is damaged — whatever it contributed before the dead
                // article was already collected by the scanner; skip the rest of it.
            }
        }

        return (metadata, sources);
    }

    /// <summary>First min(16KB, length) decoded bytes of a part, or null when unreadable.</summary>
    /// <summary>
    /// Identifies par2 volumes by their packet magic bytes rather than their filename,
    /// for releases whose subjects are obfuscated. Mirrors the import path's detection
    /// (GetPar2FileDescriptorsStep). One dead article only skips its own file.
    /// </summary>
    private async Task<List<NzbFile>> FindPar2FilesByContentAsync(NzbDocument nzb, CancellationToken ct)
    {
        var found = new List<NzbFile>();
        foreach (var file in nzb.Files)
        {
            if (file.Segments.Count == 0) continue;
            try
            {
                // body only: magic-byte detection needs the bytes, not the yEnc headers
                var body = await usenetClient
                    .DecodedBodyAsync(file.Segments[0].MessageId, ct)
                    .ConfigureAwait(false);
                await using var bodyStream = body.Stream!;

                var buffer = new byte[16 * 1024];
                var totalRead = 0;
                while (totalRead < buffer.Length)
                {
                    var read = await bodyStream
                        .ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead), ct)
                        .ConfigureAwait(false);
                    if (read == 0) break;
                    totalRead += read;
                }

                if (totalRead > 0 && Par2.HasPar2MagicBytes(buffer)) found.Add(file);
            }
            catch (UsenetArticleNotFoundException)
            {
                // this file's head is gone; it cannot be identified, so skip it
            }
        }

        if (found.Count > 0)
            Log.Information(
                "Found {Count} par2 volume(s) by magic bytes; the release's par2 names are obfuscated.",
                found.Count);

        return found;
    }

    private async Task<byte[]?> ReadFirst16KbAsync(PostedPart part, CancellationToken ct)
    {
        try
        {
            var length = (int)Math.Min(PostedFileMatcher.HashLength, part.Length);
            await using var stream = usenetClient.GetFileStream(part.SegmentIds, part.Length, articleBufferSize: 0);
            var buffer = new byte[length];
            await stream.ReadExactlyAsync(buffer, ct).ConfigureAwait(false);
            return buffer;
        }
        catch (UsenetArticleNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Stats every segment of the part and derives the dead byte spans, bounded by the
    /// yEnc headers of the neighbouring present segments (or the part's edges).
    /// </summary>
    private async Task<PartDamage> IdentifyDamageAsync(PostedPart part, CancellationToken ct)
    {
        var alive = new bool[part.SegmentIds.Length];
        for (var i = 0; i < part.SegmentIds.Length; i++)
        {
            var stat = await usenetClient.StatAsync(part.SegmentIds[i], ct).ConfigureAwait(false);
            alive[i] = stat.ArticleExists;
        }

        var deadIndices = new List<int>();
        var deadSpans = new List<LongRange>();
        for (var i = 0; i < alive.Length; i++)
        {
            if (alive[i]) continue;
            var runStart = i;
            while (i < alive.Length && !alive[i]) deadIndices.Add(i++);
            var runEndExclusive = i; // index of the first present segment after the run (or end)

            var spanStart = runStart == 0
                ? 0L
                : await SegmentEndAsync(part.SegmentIds[runStart - 1], ct).ConfigureAwait(false);
            var spanEnd = runEndExclusive == alive.Length
                ? part.Length
                : await SegmentStartAsync(part.SegmentIds[runEndExclusive], ct).ConfigureAwait(false);
            deadSpans.Add(new LongRange(spanStart, spanEnd));
        }

        return new PartDamage(part, deadIndices.ToArray(), deadSpans);
    }

    private async Task<long> SegmentStartAsync(string segmentId, CancellationToken ct)
    {
        var header = await usenetClient.GetYencHeadersAsync(segmentId, ct).ConfigureAwait(false);
        return header.PartOffset;
    }

    private async Task<long> SegmentEndAsync(string segmentId, CancellationToken ct)
    {
        var header = await usenetClient.GetYencHeadersAsync(segmentId, ct).ConfigureAwait(false);
        return header.PartOffset + header.PartSize;
    }

    /// <summary>
    /// Downloads the first <paramref name="needed"/> recovery slices by seeking straight
    /// to each catalogued pointer. Sources whose articles died since scanning are skipped.
    /// </summary>
    private async Task<List<(uint Exponent, byte[] Data)>> DownloadRecoverySlicesAsync(
        List<RecoverySliceSource> sources, int needed, int sliceSize, CancellationToken ct)
    {
        var slices = new List<(uint, byte[])>(needed);
        foreach (var source in sources)
        {
            if (slices.Count == needed) break;
            if (source.Pointer.Length != sliceSize) continue;
            try
            {
                await using var stream = usenetClient.GetFileStream(
                    source.Volume.SegmentIds, source.Volume.Size, articleBufferSize: 0);
                stream.Seek(source.Pointer.DataOffset, SeekOrigin.Begin);
                var data = new byte[source.Pointer.Length];
                await stream.ReadExactlyAsync(data, ct).ConfigureAwait(false);
                slices.Add((source.Pointer.Exponent, data));
            }
            catch (UsenetArticleNotFoundException)
            {
                // this recovery slice died since the scan; try the next one
            }
        }

        return slices;
    }

    /// <summary>
    /// Streams the part's present slices through the reconstructor. Dead segment ids are
    /// filtered out of the underlying stream so no fetch (or seek probe) ever touches
    /// them; missing slices are skipped by seeking, everything else is read sequentially.
    /// </summary>
    private async Task FeedPresentSlicesAsync(
        PostedPart part,
        RecoverySetInfo.RecoverySetFile file,
        PartDamage damage,
        SortedSet<int> missingSlices,
        int sliceSize,
        ReedSolomon.StreamingReconstructor reconstructor,
        CancellationToken ct)
    {
        var dead = damage.DeadSegmentIndices.ToHashSet();
        var aliveIds = part.SegmentIds.Where((_, index) => !dead.Contains(index)).ToArray();
        await using var stream = usenetClient.GetFileStream(aliveIds, part.Length, articleBufferSize: 0);
        var buffer = new byte[sliceSize];
        for (var local = 0; local < file.SliceCount; local++)
        {
            ct.ThrowIfCancellationRequested();
            var global = file.SliceBase + local;
            var range = Par2SliceMapper.SliceFileRange(global, file.SliceBase, file.Length, sliceSize);
            if (missingSlices.Contains(global)) continue;
            if (stream.Position != range.StartInclusive) stream.Seek(range.StartInclusive, SeekOrigin.Begin);
            var count = (int)range.Count;
            await stream.ReadExactlyAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            reconstructor.FeedPresentSlice(global, buffer.AsSpan(0, count));
        }
    }

    /// <summary>
    /// Keeps total recovered data within the configured budget by giving up the oldest
    /// repairs. Evicting one makes its item unplayable again -- the overlay was the only
    /// thing covering its dead articles -- so each evicted item is removed and handed to
    /// its Arr for a fresh search. Removing the DavItem fires the cleanup trigger, which
    /// queues the blob for deletion, so no blob is orphaned.
    /// </summary>
    /// <returns>False when the repair does not fit and must be refused.</returns>
    private async Task<bool> EnforceStorageBudgetAsync(
        DavItem davItem, Guid incomingBlobId, DavDatabaseClient dbClient, CancellationToken ct)
    {
        var capBytes = MaxStorageBytes;
        if (capBytes <= 0) return true;

        var others = await dbClient.Ctx.Items
            .Where(x => x.RecoveryBlobId != null && x.Id != davItem.Id)
            .Select(x => new { x.Id, BlobId = x.RecoveryBlobId!.Value })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var candidates = others
            .Select(x => new RecoveryEvictionPlanner.Candidate(
                x.Id, x.BlobId, BlobStore.GetBlobSize(x.BlobId), BlobStore.GetBlobWriteTime(x.BlobId)))
            .ToList();

        var incomingBytes = BlobStore.GetBlobSize(incomingBlobId);
        var plan = RecoveryEvictionPlanner.Plan(candidates, capBytes, incomingBytes);

        if (!plan.FitsAfterEviction)
        {
            Log.Warning(
                "Refusing the PAR2 repair of {Path}: its {IncomingBytes} bytes of recovery data cannot fit " +
                "the {CapBytes} byte budget even after giving up every existing repair. Nothing was evicted.",
                davItem.Path, incomingBytes, capBytes);
            return false;
        }

        var toEvict = plan.Evict;
        if (toEvict.Count == 0) return true;

        Log.Information(
            "PAR2 recovery storage budget of {CapBytes} bytes exceeded by {Path}; evicting {Count} older repair(s).",
            capBytes, davItem.Path, toEvict.Count);

        var arrClients = configManager?.GetArrConfig().GetArrClients() ?? [];
        foreach (var candidate in toEvict)
        {
            var evictedItem = await dbClient.Ctx.Items
                .FirstOrDefaultAsync(x => x.Id == candidate.DavItemId, ct)
                .ConfigureAwait(false);
            if (evictedItem == null) continue;

            var link = configManager == null ? null : OrganizedLinksUtil.GetLink(evictedItem, configManager);
            var accepted = link != null && await ArrResearchService
                .TryRemoveAndSearchAsync(arrClients, link)
                .ConfigureAwait(false);

            // the item goes either way: without its recovery blob it cannot be served.
            // Removing it fires TR_DavItems_Delete_AddRecoveryBlobCleanup, which frees
            // the blob. The library link is deliberately left alone -- reclaiming disk
            // should not silently delete a user's symlink.
            dbClient.Ctx.Items.Remove(evictedItem);
            Log.Information(
                "Evicted PAR2 repair for {EvictedPath}, freeing {FreedBytes} bytes. Arr re-search accepted: {Accepted}.",
                evictedItem.Path, candidate.SizeBytes, accepted);
        }

        return true;
    }

    /// <summary>
    /// Builds the recovery blob: contiguous missing slices merge into one recovered range
    /// per run, clamped to the file tail, with the payload bytes concatenated in order.
    /// </summary>
    private static async Task<Guid> PersistRecoveryAsync(
        DavItem davItem,
        IReadOnlyDictionary<int, RecoverySetInfo.RecoverySetFile> matches,
        IReadOnlyDictionary<int, int[]> missingByPart,
        int[] missing,
        byte[][] rebuilt,
        List<PartDamage> damages,
        int sliceSize,
        CancellationToken ct)
    {
        var rebuiltBySlice = missing
            .Select((slice, i) => (slice, bytes: rebuilt[i]))
            .ToDictionary(x => x.slice, x => x.bytes);

        var ranges = new List<DavFileRecovery.RecoveredRange>();
        using var payload = new MemoryStream();
        foreach (var (partIndex, partMissing) in missingByPart.OrderBy(x => x.Key))
        {
            var file = matches[partIndex];
            DavFileRecovery.RecoveredRange? current = null;
            foreach (var slice in partMissing)
            {
                var range = Par2SliceMapper.SliceFileRange(slice, file.SliceBase, file.Length, sliceSize);
                var bytes = rebuiltBySlice[slice].AsMemory(0, (int)range.Count);
                if (current != null && current.FileOffset + current.Length == range.StartInclusive)
                {
                    current.Length += range.Count;
                }
                else
                {
                    current = new DavFileRecovery.RecoveredRange
                    {
                        PartIndex = partIndex,
                        FileOffset = range.StartInclusive,
                        Length = range.Count,
                        PayloadOffset = payload.Length,
                    };
                    ranges.Add(current);
                }

                await payload.WriteAsync(bytes, ct).ConfigureAwait(false);
            }
        }

        var meta = new DavFileRecovery
        {
            Id = davItem.Id,
            RepairedAt = DateTimeOffset.UtcNow,
            Ranges = ranges.ToArray(),
            DeadSegments = damages
                .Where(x => x.DeadSegmentIndices.Length > 0)
                .Select(x => new DavFileRecovery.DeadSegmentSpan
                {
                    PartIndex = x.Part.PartIndex,
                    SegmentIndices = x.DeadSegmentIndices,
                })
                .ToArray(),
        };

        using var blobStream = new MemoryStream();
        await RecoveryBlob.WriteAsync(blobStream, meta, payload.ToArray(), ct).ConfigureAwait(false);
        blobStream.Position = 0;
        var blobId = Guid.NewGuid();
        await BlobStore.WriteBlob(blobId, (Stream)blobStream).ConfigureAwait(false);
        return blobId;
    }
}
