using NzbWebDAV.Models;
using NzbWebDAV.Par2Recovery;

namespace NzbWebDAV.Tests.Par2Recovery;

/// <summary>
/// Pure math mapping between dead byte spans of a posted file and the global PAR2
/// input-slice numbering (slices are numbered file-by-file in Main-packet order;
/// fileSliceBase is the global index of the file's first slice).
/// </summary>
public class Par2SliceMapperTests
{
    [Fact]
    public void MissingSliceIndices_MapsDeadSpansToGlobalSliceIndices()
    {
        // file of 10 slices of 4096 bytes, starting at global slice 16.
        // dead span [5000, 13000) touches local slices 1..3 → global 17..19.
        var deadSpans = new[] { new LongRange(5000, 13000) };

        var missing = Par2SliceMapper.MissingSliceIndices(
            deadSpans, fileSliceBase: 16, fileLength: 40960, sliceSize: 4096);

        Assert.Equal([17, 18, 19], missing);
    }

    [Fact]
    public void SliceFileRange_ClampsFinalSliceToFileLength()
    {
        // file of 10000 bytes with 4096-byte slices → 3 slices; last covers [8192, 10000).
        var range = Par2SliceMapper.SliceFileRange(
            globalSliceIndex: 18, fileSliceBase: 16, fileLength: 10000, sliceSize: 4096);

        Assert.Equal(new LongRange(8192, 10000), range);
    }
}
