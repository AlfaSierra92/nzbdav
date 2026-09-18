using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Api.Controllers.RepairItem;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Tests.Fakes;
using NzbWebDAV.Services;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Tests.Services;

[Collection("Par2Repair")]
public class Par2IntegrationTests
{
    private sealed class TestStreamingClient : UsenetStreamingClient
    {
        public TestStreamingClient(INntpClient client, ConfigManager config)
            : base(config, new WebsocketManager()) => ReplaceUnderlyingClient(client);
    }

    private static ConfigManager Configure(string library, bool enabled, string fallback)
    {
        var config = new ConfigManager();
        config.UpdateValues([
            new ConfigItem { ConfigName = "media.library-dir", ConfigValue = library },
            new ConfigItem { ConfigName = "repair.par2.enable", ConfigValue = enabled.ToString() },
            new ConfigItem { ConfigName = "repair.par2.fallback", ConfigValue = fallback },
        ]);
        return config;
    }

    private static (string Directory, string Link) CreateLibrary(DavItem item)
    {
        var directory = Path.Combine(TestConfig.ConfigPath, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var link = Path.Combine(directory, "movie.strm");
        File.WriteAllText(link, $"http://localhost/view/.ids/{item.Id}.mkv");
        return (directory, link);
    }

    private static Task CheckAsync(HealthCheckService service, Par2RepairServiceTests.FixtureRelease release)
        => (Task)typeof(HealthCheckService)
            .GetMethod("PerformHealthCheck", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [release.DavItem, release.DbClient, 1, CancellationToken.None])!;

    [Fact]
    public async Task HealthCheck_RepairsBeforeReplacement_AndNextCheckSkipsRecoveredArticles()
    {
        var release = new Par2RepairServiceTests.FixtureRelease();
        await using var context = release.Ctx;
        release.DavItem.ReleaseDate = DateTimeOffset.UtcNow - TimeSpan.FromDays(2);
        release.Client.MarkDead(release.DataSegmentIds[2], release.DataSegmentIds[3]);
        var library = CreateLibrary(release.DavItem);
        var config = Configure(library.Directory, true, "delete");
        using var client = new TestStreamingClient(release.Client, config);
        using var service = new HealthCheckService(config, client, new WebsocketManager());

        await CheckAsync(service, release);

        Assert.NotNull(release.DavItem.RecoveryBlobId);
        Assert.True(File.Exists(library.Link));
        var repaired = Assert.Single(await context.HealthCheckResults
            .Where(x => x.DavItemId == release.DavItem.Id).ToListAsync());
        Assert.Equal(HealthCheckResult.RepairAction.Repaired, repaired.RepairStatus);

        await CheckAsync(service, release);

        Assert.True(await context.Items.AnyAsync(x => x.Id == release.DavItem.Id));
        Assert.True(await context.HealthCheckResults.AnyAsync(x =>
            x.DavItemId == release.DavItem.Id && x.Result == HealthCheckResult.HealthResult.Healthy));

        // If the recovery blob vanishes, the original missing articles must be checked again.
        BlobStore.Delete(release.DavItem.RecoveryBlobId!.Value);
        var getSegments = typeof(HealthCheckService)
            .GetMethod("GetAllSegments", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var segments = await (Task<List<string>>)getSegments.Invoke(service,
            [release.DavItem, release.DbClient, CancellationToken.None])!;
        Assert.Equal(release.DataSegmentIds, segments);
    }

    [Theory]
    [InlineData("mark-only", true, HealthCheckResult.RepairAction.ActionNeeded)]
    [InlineData("delete", false, HealthCheckResult.RepairAction.Deleted)]
    [InlineData("arr-research", false, HealthCheckResult.RepairAction.Deleted)]
    public async Task HealthCheck_InfeasibleRepair_UsesConfiguredFallback(
        string fallback, bool kept, HealthCheckResult.RepairAction action)
    {
        var release = new Par2RepairServiceTests.FixtureRelease();
        await using var context = release.Ctx;
        release.DavItem.ReleaseDate = DateTimeOffset.UtcNow - TimeSpan.FromDays(2);
        release.Client.MarkDead(release.DataSegmentIds[2..6]); // Eight slices, only six recovery slices.
        var library = CreateLibrary(release.DavItem);
        var config = Configure(library.Directory, true, fallback);
        using var client = new TestStreamingClient(release.Client, config);
        using var service = new HealthCheckService(config, client, new WebsocketManager());

        await CheckAsync(service, release);

        Assert.Equal(kept, await context.Items.AnyAsync(x => x.Id == release.DavItem.Id));
        Assert.Equal(kept, File.Exists(library.Link));
        var history = Assert.Single(await context.HealthCheckResults
            .Where(x => x.DavItemId == release.DavItem.Id).ToListAsync());
        Assert.Equal(action, history.RepairStatus);
    }

    [Fact]
    public async Task HealthCheck_Disabled_DoesNotAttemptPar2()
    {
        var release = new Par2RepairServiceTests.FixtureRelease();
        await using var context = release.Ctx;
        release.DavItem.ReleaseDate = DateTimeOffset.UtcNow - TimeSpan.FromDays(2);
        release.Client.MarkDead(release.DataSegmentIds[2]);
        var library = CreateLibrary(release.DavItem);
        var config = Configure(library.Directory, false, "mark-only");
        using var client = new TestStreamingClient(release.Client, config);
        using var service = new HealthCheckService(config, client, new WebsocketManager());

        await CheckAsync(service, release);

        Assert.Null(release.DavItem.RecoveryBlobId);
        Assert.False(await context.Items.AnyAsync(x => x.Id == release.DavItem.Id));
    }

    [Fact]
    public async Task ManualRepair_RequiresAuthenticationAndPost_ThenWritesOneHistoryRow()
    {
        var release = new Par2RepairServiceTests.FixtureRelease();
        await using var context = release.Ctx;
        release.Client.MarkDead(release.DataSegmentIds[2]);
        var config = new ConfigManager();
        using var client = new TestStreamingClient(release.Client, config);
        var http = new DefaultHttpContext();
        http.Request.Method = "POST";
        http.Request.QueryString = new QueryString($"?davItemId={release.DavItem.Id}");
        var controller = new RepairItemController(release.DbClient, client, config)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
        };

        Assert.IsType<UnauthorizedObjectResult>(await controller.HandleApiRequest());
        Environment.SetEnvironmentVariable("FRONTEND_BACKEND_API_KEY", "par2-test-key");
        http.Request.Headers["x-api-key"] = "par2-test-key";
        http.Request.Method = "GET";
        var rejected = Assert.IsType<StatusCodeResult>(await controller.HandleApiRequest());
        Assert.Equal(405, rejected.StatusCode);
        Assert.Null(release.DavItem.RecoveryBlobId);

        http.Request.Method = "POST";
        var response = Assert.IsType<OkObjectResult>(await controller.HandleApiRequest());
        Assert.Equal("Repaired", Assert.IsType<RepairItemResponse>(response.Value).Outcome);
        Assert.Single(await context.HealthCheckResults.Where(x => x.DavItemId == release.DavItem.Id).ToListAsync());
    }
}
