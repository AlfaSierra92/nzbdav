using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Api.Controllers.GetLibraryStatistics;

[ApiController]
[Route("api/library-statistics")]
public sealed class GetLibraryStatisticsController(DavDatabaseClient dbClient) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        // Traverse the live content tree so deleted directories stop contributing
        // immediately, even while their descendants await background cleanup.
        var contentBytes = await dbClient.GetRecursiveSize(DavItem.ContentFolder.Id, HttpContext.RequestAborted)
            .ConfigureAwait(false);
        return Ok(new { contentBytes });
    }
}
