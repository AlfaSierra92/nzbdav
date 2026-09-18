using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
using NzbWebDAV.Api.Controllers.DeleteWebdavItem;
using NzbWebDAV.Api.SabControllers.RemoveFromHistory;

// These checks never open an application database or delete real content.
foreach (var directory in new[] { "", "/", "nzbs", ".ids", "completed-symlinks", "content-other" })
    await ExpectRejection(directory, "item", 403);
foreach (var name in new[] { "", " ", ".", "..", "../movie", "a/b", "a\\b", "a\0b" })
    await ExpectRejection("content/movies", name, 400);
foreach (var directory in new[] { "content/..", "content/./movies", "content/movies/../../nzbs", "content/a\\b" })
    await ExpectRejection(directory, "movie", 400);
await ExpectRejection("content/movies", "movie", 405, "GET");

var context = new DefaultHttpContext();
context.Request.QueryString = new QueryString("?failed_only=1&del_completed_files=1");
var request = await RemoveFromHistoryRequest.New(context);
Assert(request.FailedOnly && request.NzoIds.Count == 0, "Bulk failed removal requires no explicit IDs");
var id = Guid.NewGuid();
context = new DefaultHttpContext();
context.Request.QueryString = new QueryString($"?value={id}");
request = await RemoveFromHistoryRequest.New(context);
Assert(!request.FailedOnly && request.NzoIds.SequenceEqual(new[] { id }), "Existing individual deletion must remain supported");
Console.WriteLine("Manual removal request checks passed.");

static async Task ExpectRejection(string directory, string name, int expected, string method = "POST")
{
    var context = new DefaultHttpContext();
    context.Request.Method = method;
    context.Request.ContentType = "application/x-www-form-urlencoded";
    context.Request.Form = new FormCollection(new Dictionary<string, StringValues>
    {
        ["directory"] = directory,
        ["name"] = name
    });
    var controller = new TestController
    {
        ControllerContext = new ControllerContext { HttpContext = context }
    };
    var result = await controller.Execute();
    var status = result switch
    {
        ObjectResult value => value.StatusCode,
        StatusCodeResult value => value.StatusCode,
        _ => null
    };
    Assert(status == expected, $"Expected {expected} for {method} {directory}/{name}, got {status}");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

// A null store ensures rejected paths cannot reach the storage layer.
sealed class TestController() : DeleteWebdavItemController(null!)
{
    public Task<IActionResult> Execute() => HandleRequest();
}
