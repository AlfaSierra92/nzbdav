using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Config;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Services;

namespace NzbWebDAV.Api.Controllers.RepairItem;

/// <summary>
/// Runs a PAR2 repair against a single item on demand. Useful for retrying an item
/// that previously came back Infeasible -- for example once more recovery volumes
/// have been posted -- without waiting for its next scheduled health check.
/// </summary>
[ApiController]
[Route("api/repair-item")]
public class RepairItemController(
    DavDatabaseClient dbClient,
    UsenetStreamingClient usenetClient,
    ConfigManager configManager
) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        if (!HttpMethods.IsPost(Request.Method)) return StatusCode(405);
        var request = new RepairItemRequest(HttpContext);

        var davItem = await dbClient.Ctx.Items
            .FirstOrDefaultAsync(x => x.Id == request.DavItemId, request.CancellationToken)
            .ConfigureAwait(false);
        if (davItem == null)
            return NotFound(new RepairItemResponse { Status = false, Error = "No such item." });

        var result = await new Par2RepairService(usenetClient, configManager)
            .RepairAsync(davItem, dbClient, request.CancellationToken)
            .ConfigureAwait(false);

        // RepairAsync commits the recovery blob and RecoveryBlobId itself. The history
        // entry is the trigger's responsibility, so a manually repaired item records its
        // own -- the health check records one for the repairs it triggers.
        if (result.Outcome == Par2RepairOutcome.Repaired)
        {
            dbClient.Ctx.HealthCheckResults.Add(new HealthCheckResult
            {
                Id = Guid.NewGuid(),
                DavItemId = davItem.Id,
                Path = davItem.Path,
                CreatedAt = DateTimeOffset.UtcNow,
                Result = HealthCheckResult.HealthResult.Unhealthy,
                // Reuses the existing Repaired action rather than adding a PAR2-specific
                // one: the action is stored as an int and rendered by the health page, so
                // a new value would show up as an unknown status until the UI learns it.
                // The message says how the item was repaired.
                RepairStatus = HealthCheckResult.RepairAction.Repaired,
                Message = $"Manually repaired from PAR2 recovery data. {result.Message}",
            });
            await dbClient.Ctx.SaveChangesAsync(request.CancellationToken).ConfigureAwait(false);
        }

        return Ok(new RepairItemResponse
        {
            Outcome = result.Outcome.ToString(),
            Message = result.Message
        });
    }
}
