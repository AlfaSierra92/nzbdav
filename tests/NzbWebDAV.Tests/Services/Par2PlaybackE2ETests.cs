using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;
using NzbWebDAV.Streams;

namespace NzbWebDAV.Tests.Services;

/// <summary>
/// Phase 2 exit criteria (docs/par2-repair-design.md): after a PAR2 repair, the file
/// must stream end-to-end through the previously-dead range — the overlay supplies the
/// reconstructed bytes while the dead articles are never fetched again.
/// </summary>
[Collection("Par2Repair")]
public class Par2PlaybackE2ETests
{
    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task RepairedFile_PlaysAndSeeks_WhenFirstOrLastArticleIsMissing(int deadIndex)
    {
        var release = new Par2RepairServiceTests.FixtureRelease();
        await using var context = release.Ctx;
        release.Client.MarkDead(release.DataSegmentIds[deadIndex]);
        var result = await new Par2RepairService(release.Client).RepairAsync(release.DavItem, release.DbClient);
        Assert.Equal(Par2RepairOutcome.Repaired, result.Outcome);

        await using var stream = (await RecoveryOverlay.TryCreateNzbFileStreamAsync(
            release.DavItem, release.DataSegmentIds, release.Client, 0))!;
        var actual = new byte[release.Data.Length];
        await stream.ReadExactlyAsync(actual);
        Assert.Equal(release.Data, actual);
        stream.Position = deadIndex * 8192 + 100;
        var window = new byte[1024];
        await stream.ReadExactlyAsync(window);
        Assert.Equal(release.Data.AsSpan(deadIndex * 8192 + 100, 1024).ToArray(), window);
    }

    [Fact]
    public async Task RepairedFile_StreamsByteExact_ThroughPreviouslyDeadRange()
    {
        // import → articles die → health check would find damage → repair → playback.
        var release = new Par2RepairServiceTests.FixtureRelease();
        await using var _ = release.Ctx;
        release.Client.MarkDead(release.DataSegmentIds[2], release.DataSegmentIds[3]);
        var repair = await new Par2RepairService(release.Client)
            .RepairAsync(release.DavItem, release.DbClient, CancellationToken.None);
        Assert.Equal(Par2RepairOutcome.Repaired, repair.Outcome);

        // full sequential playback: byte-exact despite two dead articles
        var nzbFile = await release.DbClient.GetDavNzbFileAsync(release.DavItem);
        await using var stream = (await RecoveryOverlay.TryCreateNzbFileStreamAsync(
            release.DavItem, nzbFile!.SegmentIds, release.Client, articleBufferSize: 0, CancellationToken.None))!;
        Assert.NotNull(stream);
        var played = new byte[release.Data.Length];
        await stream.ReadExactlyAsync(played);
        Assert.Equal(release.Data, played);

        // a mid-file seek landing inside the dead range also plays through
        stream.Seek(20_000, SeekOrigin.Begin);
        var window = new byte[20_000];
        await stream.ReadExactlyAsync(window);
        Assert.Equal(release.Data.AsSpan(20_000, 20_000).ToArray(), window);
    }

    [Fact]
    public async Task RepairedRarItem_StreamsInnerFileByteExact_ThroughPreviouslyDeadRange()
    {
        // the overlay sits at the posted-file (volume) layer, underneath the rar decode
        // stack: the inner file plays through a dead range without the decoder noticing.
        var release = new Par2RepairServiceTests.FixtureRelease();
        await using var _ = release.Ctx;
        var rarItem = release.AsRarItem(innerOffset: 1000, innerLength: 60000);
        release.Client.MarkDead(release.DataSegmentIds[2], release.DataSegmentIds[3]);
        var repair = await new Par2RepairService(release.Client)
            .RepairAsync(rarItem, release.DbClient, CancellationToken.None);
        Assert.Equal(Par2RepairOutcome.Repaired, repair.Outcome);

        var rarFile = await release.DbClient.GetDavRarFileAsync(rarItem);
        var partStreamFactory = await RecoveryOverlay.TryCreatePartStreamFactoryAsync(
            rarItem, release.Client, articleBufferSize: 0, CancellationToken.None);
        Assert.NotNull(partStreamFactory);
        // Mirrors DatabaseStoreRarFile: legacy DavRarFile records are fully resolved, so
        // they are wrapped in a transient DavMultipartFile with a null resolver.
        var transient = new DavMultipartFile
        {
            Id = rarFile!.Id,
            Metadata = rarFile.ToDavMultipartFileMeta(),
        };
        await using var stream = new DavMultipartFileStream(
            transient.Metadata.FileParts,
            release.Client,
            articleBufferSize: 0,
            partStreamFactory: partStreamFactory);

        var played = new byte[60000];
        await stream.ReadExactlyAsync(played);
        Assert.Equal(release.Data.AsSpan(1000, 60000).ToArray(), played);
    }
}
