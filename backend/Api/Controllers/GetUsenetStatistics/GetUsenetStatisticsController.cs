using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Statistics;

namespace NzbWebDAV.Api.Controllers.GetUsenetStatistics;

[ApiController]
[Route("api/usenet-statistics")]
public sealed class GetUsenetStatisticsController(DashboardStatisticsStore store) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var range = HttpContext.Request.Query["range"].FirstOrDefault() ?? "24h";
        if (range is not ("1h" or "24h" or "7d" or "30d" or "all")) return BadRequest(new { error="Invalid range" });
        HttpContext.Response.Headers.CacheControl = "no-store";
        return Ok(await store.ReadUsenetAsync(range, HttpContext.RequestAborted));
    }
}
