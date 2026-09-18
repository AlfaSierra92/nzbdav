using Microsoft.AspNetCore.Http;
using NzbWebDAV.Extensions;

namespace NzbWebDAV.Api.Controllers.RepairItem;

public class RepairItemRequest
{
    public Guid DavItemId { get; init; }
    public CancellationToken CancellationToken { get; init; }

    public RepairItemRequest(HttpContext context)
    {
        var davItemIdParam = context.GetQueryParam("davItemId");
        CancellationToken = context.RequestAborted;

        if (davItemIdParam is null)
            throw new BadHttpRequestException("Missing davItemId parameter");
        if (!Guid.TryParse(davItemIdParam, out var davItemId))
            throw new BadHttpRequestException("Invalid davItemId parameter");
        DavItemId = davItemId;
    }
}
