using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using NzbWebDAV.Statistics;

namespace NzbWebDAV.Api.Controllers.GetDashboardStatistics;

[ApiController]
[Route("api/dashboard-statistics")]
public sealed class GetDashboardStatisticsController(DashboardStatisticsStore store) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        var period = HttpContext.Request.Query["period"].FirstOrDefault() ?? "day";
        if (period is not ("day" or "week" or "month"))
            return BadRequest(new { error = "Period must be day, week or month." });
        var rawDate = HttpContext.Request.Query["date"].FirstOrDefault();
        var date = DateTime.UtcNow.Date;
        if (rawDate is not null && !DateTime.TryParseExact(rawDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out date))
            return BadRequest(new { error = "Date must use yyyy-MM-dd." });
        if (date.Year < 1970 || date.Year > 9998)
            return BadRequest(new { error = "Date is outside the supported range." });
        return Ok(await store.ReadAsync(period, date, HttpContext.RequestAborted));
    }
}
