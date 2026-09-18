using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Database.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Websocket;

namespace NzbWebDAV.Api.SabControllers.RemoveFromHistory;

public class RemoveFromHistoryController(
    HttpContext httpContext,
    DavDatabaseClient dbClient,
    ConfigManager configManager,
    WebsocketManager websocketManager
) : SabApiController.BaseController(httpContext, configManager)
{
    public async Task<RemoveFromHistoryResponse> RemoveFromHistory(RemoveFromHistoryRequest request)
    {
        var ids = request.FailedOnly
            ? await dbClient.Ctx.HistoryItems
                .Where(x => x.DownloadStatus == HistoryItem.DownloadStatusOption.Failed)
                .Select(x => x.Id).ToListAsync(request.CancellationToken).ConfigureAwait(false)
            : request.NzoIds;
        await dbClient.RemoveHistoryItemsAsync(ids, !request.FailedOnly && request.DeleteCompletedFiles, request.CancellationToken).ConfigureAwait(false);
        await dbClient.Ctx.SaveChangesAsync(request.CancellationToken).ConfigureAwait(false);
        if (ids.Count > 0)
            _ = websocketManager.SendMessage(WebsocketTopic.HistoryItemRemoved, string.Join(",", ids));
        return new RemoveFromHistoryResponse() { Status = true, RemovedIds = ids };
    }

    protected override async Task<IActionResult> Handle()
    {
        var request = await RemoveFromHistoryRequest.New(httpContext).ConfigureAwait(false);
        return Ok(await RemoveFromHistory(request).ConfigureAwait(false));
    }
}