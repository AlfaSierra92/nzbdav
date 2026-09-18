using NzbWebDAV.Par2Recovery.Packets;

namespace NzbWebDAV.Par2Recovery;

/// <summary>
/// The assembled view of one PAR2 recovery set: slice size, the protected files in
/// Main-packet order (which defines the global input-slice numbering), each file's
/// global slice base, and its per-slice IFSC checksums for verifying reconstructions.
/// </summary>
public sealed class RecoverySetInfo
{
    public required int SliceSize { get; init; }
    public required int TotalSlices { get; init; }
    public required IReadOnlyList<RecoverySetFile> Files { get; init; }

    public sealed class RecoverySetFile
    {
        public required byte[] FileId { get; init; }
        public required string FileName { get; init; }
        public required long Length { get; init; }
        public required byte[] Hash16k { get; init; }

        /// <summary>Global index of this file's first input slice.</summary>
        public required int SliceBase { get; init; }

        public required int SliceCount { get; init; }

        /// <summary>Per-slice MD5+CRC32 from the file's IFSC packet (empty when absent).</summary>
        public required Ifsc.SliceChecksum[] SliceChecksums { get; init; }
    }

    /// <summary>
    /// Builds the recovery-set view from parsed packets. Files appear in the Main
    /// packet's recovery-set order; a FileDesc (and optionally an Ifsc) is matched to
    /// each recovery-set file id.
    /// </summary>
    public static RecoverySetInfo Build(IReadOnlyCollection<Par2Packet> packets)
    {
        var main = packets.OfType<Main>().FirstOrDefault()
                   ?? throw new InvalidDataException("PAR2 recovery set has no Main packet.");
        var fileDescs = packets.OfType<FileDesc>()
            .GroupBy(x => Convert.ToHexString(x.FileID))
            .ToDictionary(g => g.Key, g => g.First());
        var ifscs = packets.OfType<Ifsc>()
            .GroupBy(x => Convert.ToHexString(x.FileId))
            .ToDictionary(g => g.Key, g => g.First());

        var sliceSize = checked((int)main.SliceSize);
        var files = new List<RecoverySetFile>(main.RecoverySetFileIds.Length);
        var sliceBase = 0;
        foreach (var fileId in main.RecoverySetFileIds)
        {
            var key = Convert.ToHexString(fileId);
            if (!fileDescs.TryGetValue(key, out var desc))
                throw new InvalidDataException($"PAR2 recovery set is missing the FileDesc packet for file {key}.");

            var length = checked((long)desc.FileLength);
            var sliceCount = (int)((length + sliceSize - 1) / sliceSize);
            files.Add(new RecoverySetFile
            {
                FileId = fileId,
                FileName = desc.FileName,
                Length = length,
                Hash16k = desc.File16kHash,
                SliceBase = sliceBase,
                SliceCount = sliceCount,
                SliceChecksums = ifscs.TryGetValue(key, out var ifsc) ? ifsc.SliceChecksums : [],
            });
            sliceBase += sliceCount;
        }

        return new RecoverySetInfo
        {
            SliceSize = sliceSize,
            TotalSlices = sliceBase,
            Files = files,
        };
    }
}
