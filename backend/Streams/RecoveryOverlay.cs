using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Streams;

/// <summary>
/// Builds overlay-wrapped streams for PAR2-repaired items: the underlying usenet stream
/// is constructed without the dead segment ids (so nothing ever fetches or seek-probes a
/// dead article) and recovered ranges are served from the item's recovery blob.
/// </summary>
public static class RecoveryOverlay
{
    /// <summary>
    /// The overlay-wrapped stream for a plain nzb file (a single posted file, part 0),
    /// or null when the item has no usable recovery blob.
    /// </summary>
    public static async Task<Stream?> TryCreateNzbFileStreamAsync
    (
        DavItem davItem,
        string[] segmentIds,
        INntpClient usenetClient,
        int articleBufferSize,
        CancellationToken ct = default
    )
    {
        if (davItem.RecoveryBlobId == null || davItem.FileSize == null) return null;
        var blobStream = BlobStore.ReadBlob(davItem.RecoveryBlobId.Value);
        if (blobStream == null) return null;

        try
        {
            var (meta, payloadStart) = await RecoveryBlob.ReadMetadataAsync(blobStream, ct).ConfigureAwait(false);
            return CreateForPart(
                meta, payloadStart, blobStream, partIndex: 0,
                segmentIds, davItem.FileSize.Value, usenetClient, articleBufferSize);
        }
        catch
        {
            await blobStream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// A per-part stream factory for rar/multipart items (used by DavMultipartFileStream),
    /// or null when the item has no usable recovery blob. Each invocation opens its own
    /// view of the recovery blob so part streams keep independent positions.
    /// </summary>
    public static async Task<Func<int, DavMultipartFile.FilePart, Stream>?> TryCreatePartStreamFactoryAsync
    (
        DavItem davItem,
        INntpClient usenetClient,
        int articleBufferSize,
        CancellationToken ct = default
    )
    {
        if (davItem.RecoveryBlobId == null) return null;
        var blobId = davItem.RecoveryBlobId.Value;

        DavFileRecovery meta;
        long payloadStart;
        await using (var metaStream = BlobStore.ReadBlob(blobId))
        {
            if (metaStream == null) return null;
            (meta, payloadStart) = await RecoveryBlob.ReadMetadataAsync(metaStream, ct).ConfigureAwait(false);
        }

        return (partIndex, filePart) =>
        {
            var blobStream = BlobStore.ReadBlob(blobId)
                             ?? throw new FileNotFoundException($"Recovery blob {blobId} is missing.");
            return CreateForPart(
                meta, payloadStart, blobStream, partIndex,
                filePart.SegmentIds, filePart.SegmentIdByteRange.Count, usenetClient, articleBufferSize);
        };
    }

    /// <summary>
    /// The overlay-wrapped stream for one posted-file part. Takes ownership of
    /// <paramref name="blobStream"/> (each part needs its own, positions are independent).
    /// </summary>
    public static Stream CreateForPart
    (
        DavFileRecovery meta,
        long payloadStart,
        Stream blobStream,
        int partIndex,
        string[] segmentIds,
        long partLength,
        INntpClient usenetClient,
        int articleBufferSize
    )
    {
        var ranges = meta.Ranges.Where(x => x.PartIndex == partIndex).ToArray();

        var dead = meta.DeadSegments.Where(x => x.PartIndex == partIndex)
            .SelectMany(x => x.SegmentIndices).ToHashSet();
        var aliveIds = segmentIds.Where((_, index) => !dead.Contains(index)).ToArray();
        // This branch seeks using actual yEnc offsets; the overlay explicitly seeks
        // past each recovered range, preserving the original posted-file positions.
        var underlying = usenetClient.GetFileStream(aliveIds, partLength, articleBufferSize);
        return new RecoveryOverlayStream(underlying, partLength, ranges, blobStream, payloadStart);
    }
}
