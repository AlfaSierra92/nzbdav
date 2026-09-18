using System.Security.Cryptography;
using NzbWebDAV.Par2Recovery;

namespace NzbWebDAV.Tests.Par2Recovery;

/// <summary>
/// PAR2 protects files *as posted*; a DavItem's parts (the file itself, rar volumes, or
/// multipart parts) must be matched to the recovery set's FileDescs before slices can be
/// numbered. Primary match: MD5 of the first 16KB (obfuscation-proof, same as import
/// time). Fallback when the part's head is unreadable (dead first article): a unique
/// file length.
/// </summary>
public class PostedFileMatcherTests
{
    private static RecoverySetInfo.RecoverySetFile MakeFile(string name, long length, byte[] hash16K, int sliceBase)
        => new()
        {
            FileId = MD5.HashData(System.Text.Encoding.ASCII.GetBytes(name)),
            FileName = name,
            Length = length,
            Hash16k = hash16K,
            SliceBase = sliceBase,
            SliceCount = (int)((length + 4095) / 4096),
            SliceChecksums = [],
        };

    private static byte[] Head(byte seed, int length = 16384)
        => Enumerable.Range(0, length).Select(i => (byte)(i * seed)).ToArray();

    [Fact]
    public async Task Match_PairsPartsToFiles_ByMd5OfFirst16Kb()
    {
        // two rar volumes with identical lengths — only the 16KB hash can tell them apart.
        var headA = Head(3);
        var headB = Head(7);
        var files = new[]
        {
            MakeFile("x.r01", 100_000, MD5.HashData(headB), sliceBase: 25),
            MakeFile("x.r00", 100_000, MD5.HashData(headA), sliceBase: 0),
        };
        var parts = new[] { new PostedFileMatcher.PostedPart(0, 100_000), new PostedFileMatcher.PostedPart(1, 100_000) };

        var matches = await PostedFileMatcher.MatchAsync(
            parts, files,
            readFirst16Kb: partIndex => Task.FromResult<byte[]?>(partIndex == 0 ? headA : headB),
            CancellationToken.None);

        Assert.Equal("x.r00", matches[0].FileName);
        Assert.Equal("x.r01", matches[1].FileName);
    }

    [Fact]
    public async Task Match_FallsBackToUniqueLength_WhenHeadIsUnreadable()
    {
        // part 0's first article is dead → no 16KB hash. Its length (55555) is unique
        // among unmatched files → still matched. Parts 1 and 2 share a length and are
        // both unreadable → ambiguous, left unmatched.
        var files = new[]
        {
            MakeFile("movie.mkv", 55_555, MD5.HashData(Head(3)), sliceBase: 0),
            MakeFile("x.r00", 100_000, MD5.HashData(Head(5)), sliceBase: 14),
            MakeFile("x.r01", 100_000, MD5.HashData(Head(7)), sliceBase: 39),
        };
        var parts = new[]
        {
            new PostedFileMatcher.PostedPart(0, 55_555),
            new PostedFileMatcher.PostedPart(1, 100_000),
            new PostedFileMatcher.PostedPart(2, 100_000),
        };

        var matches = await PostedFileMatcher.MatchAsync(
            parts, files,
            readFirst16Kb: _ => Task.FromResult<byte[]?>(null),
            CancellationToken.None);

        var match = Assert.Single(matches);
        Assert.Equal(0, match.Key);
        Assert.Equal("movie.mkv", match.Value.FileName);
    }
}
