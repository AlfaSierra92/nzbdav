using MemoryPack;

namespace NzbWebDAV.Database.Models;

/// <summary>
/// Metadata for PAR2-recovered bytes of a usenet file (see docs/par2-repair-design.md).
/// Persisted together with the recovered payload bytes in a single recovery blob
/// referenced by DavItem.RecoveryBlobId (layout handled by <see cref="Database.RecoveryBlob"/>).
/// </summary>
[MemoryPackable(GenerateType.VersionTolerant)]
public partial class DavFileRecovery
{
    /// <summary>Foreign key to DavItem.Id.</summary>
    [MemoryPackOrder(0)]
    public Guid Id { get; set; }

    [MemoryPackOrder(1)]
    public DateTimeOffset RepairedAt { get; set; }

    /// <summary>Byte ranges of the posted files that were reconstructed.</summary>
    [MemoryPackOrder(2)]
    public RecoveredRange[] Ranges { get; set; } = [];

    /// <summary>
    /// Which segments of each posted-file part were dead at repair time. The read path
    /// filters these segment ids out of the underlying stream so it never attempts to
    /// fetch (or seek-probe) an article that is known to be gone.
    /// </summary>
    [MemoryPackOrder(3)]
    public DeadSegmentSpan[] DeadSegments { get; set; } = [];

    [MemoryPackable(GenerateType.VersionTolerant)]
    public partial class RecoveredRange
    {
        /// <summary>Which posted file this applies to (0 for DavNzbFile; part index for rar/multipart).</summary>
        [MemoryPackOrder(0)]
        public int PartIndex { get; set; }

        /// <summary>Byte offset within the posted file where the recovered range starts.</summary>
        [MemoryPackOrder(1)]
        public long FileOffset { get; set; }

        /// <summary>Length of the recovered range in bytes.</summary>
        [MemoryPackOrder(2)]
        public long Length { get; set; }

        /// <summary>Where these bytes live within the recovery blob's payload section.</summary>
        [MemoryPackOrder(3)]
        public long PayloadOffset { get; set; }
    }

    [MemoryPackable(GenerateType.VersionTolerant)]
    public partial class DeadSegmentSpan
    {
        [MemoryPackOrder(0)]
        public int PartIndex { get; set; }

        /// <summary>Indices into the part's SegmentIds array of the dead segments.</summary>
        [MemoryPackOrder(1)]
        public int[] SegmentIndices { get; set; } = [];
    }
}
