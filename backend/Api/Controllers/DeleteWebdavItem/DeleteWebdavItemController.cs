using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NWebDav.Server.Stores;
using NzbWebDAV.WebDav;

namespace NzbWebDAV.Api.Controllers.DeleteWebdavItem;

[ApiController]
[Route("api/delete-webdav-item")]
public class DeleteWebdavItemController(DatabaseStore store) : BaseApiController
{
    protected override async Task<IActionResult> HandleRequest()
    {
        if (!HttpMethods.IsPost(Request.Method)) return StatusCode(405);
        var form = await Request.ReadFormAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        var directory = (form["directory"].FirstOrDefault() ?? "").Trim('/');
        var name = form["name"].FirstOrDefault() ?? "";
        // Only actual content can be removed; virtual views and system roots are protected.
        if (directory != "content" && !directory.StartsWith("content/", StringComparison.Ordinal))
            return StatusCode(403, new { error = "Only items inside content can be removed." });
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(['/', '\\', '\0']) >= 0
            || directory.Split('/').Any(x => x is "." or ".." || x.Contains('\\')))
            return BadRequest(new { error = "Invalid item path." });
        var parent = await store.GetItemAsync(directory, HttpContext.RequestAborted).ConfigureAwait(false);
        if (parent is not IStoreCollection collection)
            return NotFound(new { error = "Directory not found." });
        var status = (int)await collection.DeleteItemAsync(name, HttpContext.RequestAborted).ConfigureAwait(false);
        if (status >= 200 && status < 300) return Ok(new { status = true });
        return StatusCode(status, new { error = status == 403
            ? "Removal is forbidden. Check the read-only WebDAV setting."
            : "The item could not be removed." });
    }
}
