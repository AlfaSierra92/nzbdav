using NzbWebDAV.Database.Models;
using NzbWebDAV.Streams;

namespace NzbWebDAV.Tests.Streams;

/// <summary>
/// The overlay serves recovered byte ranges from the recovery blob's payload and
/// everything else from the underlying (dead-segment-filtered) stream, seeking the
/// underlying stream past covered ranges so it never touches bytes that no longer
/// exist on usenet.
/// </summary>
public class RecoveryOverlayStreamTests
{
    private const int FileLength = 1000;

    private static byte[] Original()
        => Enumerable.Range(0, FileLength).Select(i => (byte)(i * 7)).ToArray();

    private static DavFileRecovery.RecoveredRange Range(long fileOffset, long length, long payloadOffset)
        => new() { PartIndex = 0, FileOffset = fileOffset, Length = length, PayloadOffset = payloadOffset };

    /// <summary>
    /// underlying stream: original bytes with the covered ranges garbled — the overlay
    /// must never surface them; blob stream: payload section preceded by header noise.
    /// </summary>
    private static RecoveryOverlayStream MakeOverlay(params DavFileRecovery.RecoveredRange[] ranges)
    {
        var original = Original();
        var underlying = Original();
        var payload = new MemoryStream();
        payload.Write("head"u8); // simulated metadata header before the payload section
        foreach (var range in ranges)
        {
            payload.Write(original.AsSpan((int)range.FileOffset, (int)range.Length));
            for (var i = range.FileOffset; i < range.FileOffset + range.Length; i++)
                underlying[i] = 0xEE;
        }

        payload.Position = 0;
        return new RecoveryOverlayStream(
            new MemoryStream(underlying), FileLength, ranges, payload, payloadStart: 4);
    }

    [Fact]
    public async Task SequentialRead_StitchesUnderlyingAndRecoveredBytes()
    {
        await using var overlay = MakeOverlay(Range(300, 200, 0), Range(800, 200, 200));

        var read = new byte[FileLength];
        await overlay.ReadExactlyAsync(read);

        Assert.Equal(Original(), read);
    }

    [Fact]
    public async Task SeekIntoCoveredRange_ReadsAcrossTheBoundary()
    {
        // a player seeking mid-file can land inside a recovered range; the read must
        // start from payload bytes and continue seamlessly into live bytes.
        await using var overlay = MakeOverlay(Range(300, 200, 0));

        overlay.Seek(450, SeekOrigin.Begin);
        var read = new byte[200];
        await overlay.ReadExactlyAsync(read);

        Assert.Equal(Original().AsSpan(450, 200).ToArray(), read);
    }

    [Fact]
    public async Task RecoveredRangeAtEndOfFile_ReturnsEofAfterLastByte()
    {
        await using var overlay = MakeOverlay(Range(900, 100, 0));

        overlay.Seek(950, SeekOrigin.Begin);
        var read = new byte[100];
        var count = await overlay.ReadAtLeastAsync(read, 100, throwOnEndOfStream: false);

        Assert.Equal(50, count);
        Assert.Equal(Original().AsSpan(950, 50).ToArray(), read[..50]);
        Assert.Equal(0, await overlay.ReadAsync(read));
    }
}
