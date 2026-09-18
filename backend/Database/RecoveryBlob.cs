using System.Buffers.Binary;
using MemoryPack;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Database;

/// <summary>
/// Serializes a PAR2 repair result as a single blob: a small MemoryPack-encoded
/// <see cref="DavFileRecovery"/> header followed by the raw recovered payload bytes.
/// Layout: [4-byte little-endian meta length][meta][payload]. Keeping metadata and
/// payload in one blob lets a single DavItems.RecoveryBlobId column (and one cleanup
/// trigger) own the whole repair artifact, while the payload can still be read by
/// range (seek to payloadStart + RecoveredRange.PayloadOffset).
/// </summary>
public static class RecoveryBlob
{
    public static async Task WriteAsync
    (
        Stream destination,
        DavFileRecovery meta,
        ReadOnlyMemory<byte> payload,
        CancellationToken ct = default
    )
    {
        var metaBytes = MemoryPackSerializer.Serialize(meta);
        var lengthPrefix = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lengthPrefix, metaBytes.Length);
        await destination.WriteAsync(lengthPrefix, ct).ConfigureAwait(false);
        await destination.WriteAsync(metaBytes, ct).ConfigureAwait(false);
        await destination.WriteAsync(payload, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the metadata header from a recovery blob and returns it together with the
    /// absolute offset at which the payload section starts.
    /// </summary>
    public static async Task<(DavFileRecovery Meta, long PayloadStart)> ReadMetadataAsync
    (
        Stream source,
        CancellationToken ct = default
    )
    {
        var lengthPrefix = new byte[4];
        await source.ReadExactlyAsync(lengthPrefix, ct).ConfigureAwait(false);
        var metaLength = BinaryPrimitives.ReadInt32LittleEndian(lengthPrefix);
        if (metaLength <= 0 || (source.CanSeek && metaLength > source.Length - source.Position))
            throw new InvalidDataException("Invalid recovery blob metadata length.");

        var metaBytes = new byte[metaLength];
        await source.ReadExactlyAsync(metaBytes, ct).ConfigureAwait(false);
        var meta = MemoryPackSerializer.Deserialize<DavFileRecovery>(metaBytes)
                   ?? throw new InvalidDataException("Recovery blob metadata could not be deserialized.");

        return (meta, 4L + metaLength);
    }
}
