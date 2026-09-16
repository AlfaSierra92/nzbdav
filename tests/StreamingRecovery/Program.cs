using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Models;
using NzbWebDAV.Exceptions;
using NzbWebDAV.Middlewares;
using NzbWebDAV.Streams;
using UsenetSharp.Models;
using UsenetSharp.Streams;

foreach (var bufferSize in new[] { 0, 2 })
{
    var client = DispatchProxy.Create<INntpClient, FakeClient>();
    var fake = (FakeClient)(object)client;
    await using var stream = new NzbFileStream(["segment"], 8, client, bufferSize);
    using var output = new MemoryStream();
    await stream.CopyToAsync(output, 3);
    Assert(output.ToArray().SequenceEqual(FakeClient.Data), "Recovery must neither duplicate nor skip bytes");
    Assert(fake.Opens == 2 && stream.Position == 8, "Timeout reopens the segment once");
    await stream.DisposeAsync();
    Assert(fake.Streams.All(x => x.Disposed), "Completed and failed segments are disposed");
}

// Failure while positioning the replacement must dispose it and restart from
// the same delivered position, even if bytes were consumed by the failed read.
{
    var client = DispatchProxy.Create<INntpClient, FakeClient>();
    var fake = (FakeClient)(object)client;
    fake.FailSeek = true;
    await using var stream = new NzbFileStream(["segment"], 8, client, 0);
    using var output = new MemoryStream();
    await stream.CopyToAsync(output, 3);
    Assert(output.ToArray().SequenceEqual(FakeClient.Data) && fake.Opens == 3, "Retry failed seek without losing bytes");
    await stream.DisposeAsync();
    Assert(fake.Streams.All(x => x.Disposed), "Failed seek stream is disposed");
}
{
    var client = DispatchProxy.Create<INntpClient, FakeClient>();
    var fake = (FakeClient)(object)client;
    fake.AlwaysFail = true;
    await using var stream = new NzbFileStream(["segment"], 8, client, 0);
    try { await stream.ReadAsync(new byte[3]); throw new Exception("Expected timeout"); }
    catch (TimeoutException) { }
    Assert(fake.Opens == 3 && stream.Position == 0, "Retry budget is bounded");
    await stream.DisposeAsync();
    Assert(fake.Streams.All(x => x.Disposed), "Exhausted retries release streams");
}
{
    var client = DispatchProxy.Create<INntpClient, FakeClient>();
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    await using var stream = new NzbFileStream(["segment"], 8, client, 0);
    try { await stream.ReadAsync(new byte[3], cts.Token); throw new Exception("Expected cancellation"); }
    catch (OperationCanceledException) { }
    Assert(((FakeClient)(object)client).Opens == 0, "Cancellation does not retry");
}
foreach (var started in new[] { false, true })
{
    var context = new DefaultHttpContext();
    var lifetime = new Lifetime();
    context.Features.Set<IHttpRequestLifetimeFeature>(lifetime);
    context.Features.Set<IHttpResponseFeature>(new ResponseFeature(started));
    await new ExceptionMiddleware(_ => throw new UsenetArticleNotFoundException("missing")).InvokeAsync(context);
    Assert(started ? lifetime.Aborted : context.Response.StatusCode == 404, "Abort started responses; preserve HTTP errors before headers");
}
Console.WriteLine("Streaming recovery tests passed.");
static void Assert(bool value, string message) { if (!value) throw new Exception(message); }

public class FakeClient : DispatchProxy
{
    public static readonly byte[] Data = [1, 2, 3, 4, 5, 6, 7, 8];
    public int Opens;
    public bool AlwaysFail, FailSeek;
    public List<FaultStream> Streams = [];
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method!.Name)
        {
            case "AcquireExclusiveConnectionAsync":
                return Task.FromResult(new UsenetExclusiveConnection(null));
            case "GetYencHeadersAsync":
                return Task.FromResult(new UsenetYencHeader { PartOffset = 0, PartSize = 8, FileSize = 8, FileName = "test", LineLength = 128, PartNumber = 1, TotalParts = 1 });
            case "DecodedBodyAsync":
                Opens++;
                var stream = new FaultStream(AlwaysFail || (FailSeek && Opens == 2) ? 0 : Opens == 1 ? 3 : -1);
                Streams.Add(stream);
                return Task.FromResult(new UsenetDecodedBodyResponse { Stream = stream, SegmentId = "segment", ResponseCode = 222, ResponseMessage = "body follows" });
            default: throw new NotSupportedException(method.Name);
        }
    }
}
public sealed class FaultStream(int failAt) : YencStream(Stream.Null, allocateBuffers: false)
{
    private int position;
    public bool Disposed;
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (failAt >= 0 && position >= failAt)
        {
            position++; // Simulate decoder consumption before the exception.
            throw new TimeoutException("Simulated NNTP timeout");
        }
        var count = Math.Min(Math.Min(buffer.Length, 3), FakeClient.Data.Length - position);
        FakeClient.Data.AsMemory(position, count).CopyTo(buffer);
        position += count;
        return ValueTask.FromResult(count);
    }
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
}
sealed class Lifetime : IHttpRequestLifetimeFeature
{
    public CancellationToken RequestAborted { get; set; }
    public bool Aborted;
    public void Abort() => Aborted = true;
}
sealed class ResponseFeature(bool started) : HttpResponseFeature
{
    public override bool HasStarted => started;
}
