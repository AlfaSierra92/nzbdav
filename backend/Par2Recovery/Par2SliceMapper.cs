using NzbWebDAV.Models;

namespace NzbWebDAV.Par2Recovery;

/// <summary>
/// Maps between byte ranges of a posted file and the global PAR2 input-slice numbering.
/// Global numbering: slices are numbered file-by-file in the Main packet's recovery-set
/// order; a file of length L contributes ceil(L / sliceSize) slices starting at its
/// fileSliceBase.
/// </summary>
public static class Par2SliceMapper
{
    /// <summary>
    /// The global indices of every slice whose byte range intersects any of the given
    /// dead spans of the file. These are the slices that must be reconstructed.
    /// </summary>
    public static int[] MissingSliceIndices
    (
        IEnumerable<LongRange> deadSpans,
        int fileSliceBase,
        long fileLength,
        int sliceSize
    )
    {
        var indices = new SortedSet<int>();
        foreach (var span in deadSpans)
        {
            var firstSlice = (int)(span.StartInclusive / sliceSize);
            var lastSlice = (int)((span.EndExclusive - 1) / sliceSize);
            for (var slice = firstSlice; slice <= lastSlice; slice++)
                indices.Add(fileSliceBase + slice);
        }

        return indices.ToArray();
    }

    /// <summary>
    /// The byte range within the file covered by the given global slice index,
    /// clamped to the file length for the final (partial) slice.
    /// </summary>
    public static LongRange SliceFileRange
    (
        int globalSliceIndex,
        int fileSliceBase,
        long fileLength,
        int sliceSize
    )
    {
        var localSlice = globalSliceIndex - fileSliceBase;
        var start = (long)localSlice * sliceSize;
        var end = Math.Min(start + sliceSize, fileLength);
        return new LongRange(start, end);
    }
}
