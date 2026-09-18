using System.Text;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Extensions;
using NzbWebDAV.Services;
using NzbWebDAV.Tests.Fakes;

namespace NzbWebDAV.Tests.Services;

[Collection("Par2Repair")]
public class Par2RepairServiceTests
{
    private static string FixtureDir => Path.Combine(AppContext.BaseDirectory, "Fixtures", "Par2");

    private static DavItem MakeUsenetFileItem(Guid? nzbBlobId, long fileSize = 65536) => new()
    {
        Id = Guid.NewGuid(),
        IdPrefix = Guid.NewGuid().GetFiveLengthPrefix(),
        CreatedAt = DateTime.Now,
        ParentId = DavItem.ContentFolder.Id,
        Name = $"data-{Guid.NewGuid():N}.bin",
        Path = $"/content/data-{Guid.NewGuid():N}.bin",
        FileSize = fileSize,
        Type = DavItem.ItemType.UsenetFile,
        SubType = DavItem.ItemSubType.NzbFile,
        NzbBlobId = nzbBlobId,
    };

    /// <summary>
    /// A complete in-memory release built from the par2cmdline fixture: data.bin split
    /// into 8 articles of 8KB (2 slices each), plus the par2 index and volume files
    /// (6 recovery slices total), an NZB blob describing them, and a mounted DavItem.
    /// Shared with the playback E2E tests.
    /// </summary>
    public sealed class FixtureRelease
    {
        public SyntheticUsenetClient Client { get; } = new();
        public byte[] Data { get; }
        public string[] DataSegmentIds { get; }
        public DavItem DavItem { get; }
        public DavDatabaseContext Ctx { get; }
        public DavDatabaseClient DbClient { get; }

        // test classes run in parallel but share one sqlite file; create the schema once
        private static readonly Lock DbInitLock = new();
        private static bool _dbInitialized;

        /// <param name="obfuscatePar2Names">
        /// Give the par2 volumes arbitrary NZB subjects, as an obfuscated release does,
        /// so they can only be found by their content rather than a .par2 extension.
        /// </param>
        public FixtureRelease(bool obfuscatePar2Names = false)
        {
            Data = File.ReadAllBytes(Path.Combine(FixtureDir, "data.bin"));
            DataSegmentIds = Client.AddFile("data", Data, segmentSize: 8192);

            var files = new List<(string Name, string[] SegmentIds)> { ("data.bin", DataSegmentIds) };
            var obfuscatedIndex = 0;
            foreach (var par2Name in new[] { "data.par2", "data.vol0+1.par2", "data.vol1+2.par2", "data.vol3+3.par2" })
            {
                var bytes = File.ReadAllBytes(Path.Combine(FixtureDir, par2Name));
                var subject = obfuscatePar2Names ? $"a1b2c3d4e5f6{obfuscatedIndex++:D2}" : par2Name;
                files.Add((subject, Client.AddFile(par2Name, bytes, segmentSize: 65536)));
            }

            var nzbBlobId = Guid.NewGuid();
            BlobStore.WriteBlob(nzbBlobId, (Stream)new MemoryStream(BuildNzbXml(files))).GetAwaiter().GetResult();

            Ctx = new DavDatabaseContext();
            lock (DbInitLock)
            {
                if (!_dbInitialized)
                {
                    Ctx.Database.EnsureCreated();
                    _dbInitialized = true;
                }
            }

            DbClient = new DavDatabaseClient(Ctx);
            DavItem = MakeUsenetFileItem(nzbBlobId);
            Ctx.Items.Add(DavItem);
            Ctx.NzbFiles.Add(new DavNzbFile { Id = DavItem.Id, SegmentIds = DataSegmentIds });
            Ctx.SaveChanges();
        }

        /// <summary>
        /// Remounts the release as a rar item: data.bin plays the role of a single rar
        /// volume whose inner-file data sits at [innerOffset, innerOffset+innerLength).
        /// </summary>
        public DavItem AsRarItem(long innerOffset, long innerLength)
        {
            var item = new DavItem
            {
                Id = Guid.NewGuid(),
                IdPrefix = Guid.NewGuid().GetFiveLengthPrefix(),
                CreatedAt = DateTime.Now,
                ParentId = DavItem.ParentId,
                Name = $"inner-{Guid.NewGuid():N}.mkv",
                Path = $"/content/inner-{Guid.NewGuid():N}.mkv",
                FileSize = innerLength,
                Type = NzbWebDAV.Database.Models.DavItem.ItemType.UsenetFile,
                SubType = NzbWebDAV.Database.Models.DavItem.ItemSubType.RarFile,
                NzbBlobId = DavItem.NzbBlobId,
            };
            Ctx.Items.Add(item);
            Ctx.RarFiles.Add(new DavRarFile
            {
                Id = item.Id,
                RarParts =
                [
                    new DavRarFile.RarPart
                    {
                        SegmentIds = DataSegmentIds,
                        PartSize = Data.Length,
                        Offset = innerOffset,
                        ByteCount = innerLength,
                    },
                ],
            });
            Ctx.SaveChanges();
            return item;
        }

        private static byte[] BuildNzbXml(IEnumerable<(string Name, string[] SegmentIds)> files)
        {
            var sb = new StringBuilder();
            sb.Append("""<?xml version="1.0" encoding="utf-8"?><nzb xmlns="http://www.newzbin.com/DTD/2003/nzb">""");
            foreach (var (name, segmentIds) in files)
            {
                sb.Append($"""<file subject="[1/1] - &quot;{name}&quot; yEnc (1/{segmentIds.Length})"><segments>""");
                foreach (var id in segmentIds)
                    sb.Append($"""<segment bytes="1">{id}</segment>""");
                sb.Append("</segments></file>");
            }

            sb.Append("</nzb>");
            return Encoding.UTF8.GetBytes(sb.ToString());
        }
    }

    [Fact]
    public async Task Repair_IsNotAttempted_WhenItemHasNoNzbBlob()
    {
        // items imported before NZB persistence existed can't be par2-repaired:
        // without the original NZB there is no way to find the recovery volumes.
        await using var ctx = new DavDatabaseContext();
        var service = new Par2RepairService(new SyntheticUsenetClient());

        var result = await service.RepairAsync(
            MakeUsenetFileItem(nzbBlobId: null), new DavDatabaseClient(ctx), CancellationToken.None);

        Assert.Equal(Par2RepairOutcome.NotAttempted, result.Outcome);
    }

    [Fact]
    public async Task Repair_DoesNotWriteHealthHistory_ThatIsTheCallersJob()
    {
        // the health check writes its own Par2Repaired result after a successful repair.
        // If the service wrote one too, every auto-triggered repair would produce two
        // history rows that disagree on health status, double-counting on the health page.
        var release = new FixtureRelease();
        await using var _ = release.Ctx;
        release.Client.MarkDead(release.DataSegmentIds[2], release.DataSegmentIds[3]);
        var service = new Par2RepairService(release.Client);

        var result = await service.RepairAsync(release.DavItem, release.DbClient, CancellationToken.None);

        Assert.Equal(Par2RepairOutcome.Repaired, result.Outcome);
        Assert.Empty(release.Ctx.HealthCheckResults.Where(x => x.DavItemId == release.DavItem.Id));
    }

    [Fact]
    public async Task Repair_IsRefused_AndPersistsNothing_WhenItExceedsTheStorageBudget()
    {
        // a repair larger than the whole budget must be refused outright. It must not
        // evict existing repairs to buy room that can never exist, and must leave no
        // orphan blob or dangling RecoveryBlobId behind.
        var release = new FixtureRelease();
        await using var _ = release.Ctx;
        release.Client.MarkDead(release.DataSegmentIds[2], release.DataSegmentIds[3]);

        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "repair.par2.max-storage-bytes", ConfigValue = "1" }
        ]);
        var service = new Par2RepairService(release.Client, configManager);

        var result = await service.RepairAsync(release.DavItem, release.DbClient, CancellationToken.None);

        Assert.Equal(Par2RepairOutcome.Infeasible, result.Outcome);
        Assert.Null(result.RecoveryBlobId);
        Assert.Null(release.DavItem.RecoveryBlobId);
    }

    [Fact]
    public async Task Repair_Succeeds_WhenTheStorageBudgetIsLargeEnough()
    {
        // the same repair under a generous budget still succeeds, so the refusal above
        // is the budget doing its job rather than the budget path breaking repairs.
        var release = new FixtureRelease();
        await using var _ = release.Ctx;
        release.Client.MarkDead(release.DataSegmentIds[2], release.DataSegmentIds[3]);

        var configManager = new ConfigManager();
        configManager.UpdateValues([
            new ConfigItem { ConfigName = "repair.par2.max-storage-bytes", ConfigValue = "104857600" }
        ]);
        var service = new Par2RepairService(release.Client, configManager);

        var result = await service.RepairAsync(release.DavItem, release.DbClient, CancellationToken.None);

        Assert.Equal(Par2RepairOutcome.Repaired, result.Outcome);
        Assert.NotNull(result.RecoveryBlobId);
    }

    [Fact]
    public async Task Repair_FindsPar2Volumes_WhenTheirNamesAreObfuscated()
    {
        // obfuscated releases give their par2 volumes arbitrary subjects, so matching
        // on a .par2 extension finds nothing and the release can never be repaired.
        // The volumes must be identified by their content instead.
        var release = new FixtureRelease(obfuscatePar2Names: true);
        await using var _ = release.Ctx;
        release.Client.MarkDead(release.DataSegmentIds[2], release.DataSegmentIds[3]);
        var service = new Par2RepairService(release.Client);

        var result = await service.RepairAsync(release.DavItem, release.DbClient, CancellationToken.None);

        Assert.Equal(Par2RepairOutcome.Repaired, result.Outcome);
        Assert.NotNull(result.RecoveryBlobId);
    }

    [Fact]
    public async Task Repair_ReconstructsDeadArticles_AndPersistsOnlyRecoveredBytes()
    {
        // articles 2 and 3 die (bytes 16384..32768 = slices 4..7). With 6 surviving
        // recovery slices the repair must succeed, persisting exactly the missing bytes.
        var release = new FixtureRelease();
        await using var _ = release.Ctx;
        release.Client.MarkDead(release.DataSegmentIds[2], release.DataSegmentIds[3]);
        var service = new Par2RepairService(release.Client);

        var result = await service.RepairAsync(release.DavItem, release.DbClient, CancellationToken.None);

        Assert.Equal(Par2RepairOutcome.Repaired, result.Outcome);
        Assert.NotNull(result.RecoveryBlobId);
        Assert.Equal(result.RecoveryBlobId, release.DavItem.RecoveryBlobId);

        // the recovery blob holds the metadata and exactly the reconstructed byte range
        await using var blob = BlobStore.ReadBlob(result.RecoveryBlobId!.Value)!;
        var (meta, payloadStart) = await RecoveryBlob.ReadMetadataAsync(blob);
        Assert.Equal(release.DavItem.Id, meta.Id);
        var range = Assert.Single(meta.Ranges);
        Assert.Equal(0, range.PartIndex);
        Assert.Equal(16384, range.FileOffset);
        Assert.Equal(16384, range.Length);
        var deadSpan = Assert.Single(meta.DeadSegments);
        Assert.Equal([2, 3], deadSpan.SegmentIndices);

        blob.Position = payloadStart + range.PayloadOffset;
        var recovered = new byte[range.Length];
        await blob.ReadExactlyAsync(recovered);
        Assert.Equal(release.Data.AsSpan(16384, 16384).ToArray(), recovered);

        // the history entry belongs to whoever triggered the repair, not to the service;
        // Repair_DoesNotWriteHealthHistory_ThatIsTheCallersJob pins that.
    }

    [Fact]
    public async Task Repair_IsInfeasible_WhenMoreSlicesAreMissingThanRecoverySlicesSurvive()
    {
        // 4 dead articles = 8 missing slices, but the release only carries 6 recovery
        // slices. The repair must give up cleanly and persist nothing.
        var release = new FixtureRelease();
        await using var _ = release.Ctx;
        release.Client.MarkDead(
            release.DataSegmentIds[2], release.DataSegmentIds[3],
            release.DataSegmentIds[5], release.DataSegmentIds[6]);
        var service = new Par2RepairService(release.Client);

        var result = await service.RepairAsync(release.DavItem, release.DbClient, CancellationToken.None);

        Assert.Equal(Par2RepairOutcome.Infeasible, result.Outcome);
        Assert.Null(release.DavItem.RecoveryBlobId);
    }

    [Fact]
    public async Task Repair_ReconstructsRarVolumeBytes_ForRarItems()
    {
        // PAR2 protects the *posted* volume, not the inner file: the recovered range
        // must be expressed in volume byte space so the rar decoder never notices.
        var release = new FixtureRelease();
        await using var _ = release.Ctx;
        var rarItem = release.AsRarItem(innerOffset: 1000, innerLength: 60000);
        release.Client.MarkDead(release.DataSegmentIds[2], release.DataSegmentIds[3]);
        var service = new Par2RepairService(release.Client);

        var result = await service.RepairAsync(rarItem, release.DbClient, CancellationToken.None);

        Assert.Equal(Par2RepairOutcome.Repaired, result.Outcome);
        await using var blob = BlobStore.ReadBlob(result.RecoveryBlobId!.Value)!;
        var (meta, payloadStart) = await RecoveryBlob.ReadMetadataAsync(blob);
        var range = Assert.Single(meta.Ranges);
        Assert.Equal(0, range.PartIndex);
        Assert.Equal(16384, range.FileOffset);
        Assert.Equal(16384, range.Length);

        blob.Position = payloadStart;
        var recovered = new byte[range.Length];
        await blob.ReadExactlyAsync(recovered);
        Assert.Equal(release.Data.AsSpan(16384, 16384).ToArray(), recovered);
    }

    [Fact]
    public async Task Repair_Fails_AndPersistsNothing_WhenAPresentArticleIsCorrupt()
    {
        // one "surviving" article carries corrupted bytes → the recovery math produces
        // garbage → IFSC verification must reject it and nothing may be persisted.
        var release = new FixtureRelease();
        await using var _ = release.Ctx;
        release.Client.MarkDead(release.DataSegmentIds[2]);
        var corrupt = release.Data[(5 * 8192)..(6 * 8192)];
        corrupt[100] ^= 0xFF;
        release.Client.AddArticle(release.DataSegmentIds[5], 5 * 8192, corrupt);
        var service = new Par2RepairService(release.Client);

        var result = await service.RepairAsync(release.DavItem, release.DbClient, CancellationToken.None);

        Assert.Equal(Par2RepairOutcome.Failed, result.Outcome);
        Assert.Null(release.DavItem.RecoveryBlobId);
    }
}
