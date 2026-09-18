using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Tests.Database;

/// <summary>
/// The recovery blob is a single artifact holding both the repair metadata (which byte
/// ranges of which posted-file parts were recovered, and which segments are dead) and the
/// recovered payload bytes, so one DavItems.RecoveryBlobId column + one cleanup trigger
/// covers everything. Layout: [4-byte LE meta length][MemoryPack DavFileRecovery][payload].
/// </summary>
public class RecoveryBlobTests
{
    [Fact]
    public async Task WriteThenRead_RoundTripsMetadataAndPayload()
    {
        var meta = new DavFileRecovery
        {
            Id = Guid.NewGuid(),
            RepairedAt = DateTimeOffset.FromUnixTimeSeconds(1_752_000_000),
            Ranges =
            [
                new DavFileRecovery.RecoveredRange
                    { PartIndex = 0, FileOffset = 4096, Length = 8192, PayloadOffset = 0 },
                new DavFileRecovery.RecoveredRange
                    { PartIndex = 2, FileOffset = 0, Length = 100, PayloadOffset = 8192 },
            ],
            DeadSegments =
            [
                new DavFileRecovery.DeadSegmentSpan { PartIndex = 0, SegmentIndices = [3, 4] },
                new DavFileRecovery.DeadSegmentSpan { PartIndex = 2, SegmentIndices = [0] },
            ],
        };
        var payload = Enumerable.Range(0, 8292).Select(i => (byte)(i * 31)).ToArray();

        using var blob = new MemoryStream();
        await RecoveryBlob.WriteAsync(blob, meta, payload, CancellationToken.None);

        blob.Position = 0;
        var (readMeta, payloadStart) = await RecoveryBlob.ReadMetadataAsync(blob, CancellationToken.None);

        Assert.Equal(meta.Id, readMeta.Id);
        Assert.Equal(meta.RepairedAt, readMeta.RepairedAt);
        Assert.Equal(2, readMeta.Ranges.Length);
        Assert.Equal(4096, readMeta.Ranges[0].FileOffset);
        Assert.Equal(8192, readMeta.Ranges[0].Length);
        Assert.Equal(8192, readMeta.Ranges[1].PayloadOffset);
        Assert.Equal(2, readMeta.Ranges[1].PartIndex);
        Assert.Equal([3, 4], readMeta.DeadSegments[0].SegmentIndices);

        // payloadStart points at the payload section: reading a range there gives the payload bytes
        blob.Position = payloadStart + 8192;
        var tail = new byte[100];
        await blob.ReadExactlyAsync(tail);
        Assert.Equal(payload.AsSpan(8192, 100).ToArray(), tail);
    }
}
