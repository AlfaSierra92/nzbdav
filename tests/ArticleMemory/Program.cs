using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NzbWebDAV.Statistics;
using NzbWebDAV.Streams;
using UsenetSharp.Clients;
using UsenetSharp.Diagnostics;
using UsenetSharp.Models;
using UsenetSharp.Streams;

ArticleMemory.ConfigureLimit(null);
Assert(ArticleMemory.Snapshot().AllocatedBytes == 0, "Fresh process starts without article allocations");

// Count actual MemoryPool owner capacity, and return it exactly once under concurrent disposal.
var owner = ArticleMemory.TransportPool.Rent(5000);
var capacity = owner.Memory.Length;
Assert(ArticleMemory.Snapshot().TransportBytes == capacity, "Count pool capacity, not requested size");
Parallel.Invoke(owner.Dispose, owner.Dispose);
Assert(ArticleMemory.Snapshot().AllocatedBytes == 0, "Double disposal does not underflow");

var decoder = new YencStream(Stream.Null);
Assert(ArticleMemory.Snapshot().DecoderBytes >= 8192 + 512 + 1024, "Decoder buffers are included");
await decoder.DisposeAsync();
decoder.Dispose();
Assert(ArticleMemory.Snapshot().AllocatedBytes == 0, "Decoder async disposal returns every buffer");

var provider = new ProviderTelemetry("test", "test");
await using (var wrapper = new TelemetryYencStream(new EmptyDecodedStream(), provider, null))
    Assert(ArticleMemory.Snapshot().AllocatedBytes == 0, "Telemetry decorator rents no decoder buffers");
var headers = new UsenetYencHeader { FileName="test", FileSize=1, LineLength=128, PartNumber=1, TotalParts=1, PartSize=1, PartOffset=0 };
await using (var cached = new CachedYencStream(headers, new MemoryStream([1,2,3])))
    Assert(ArticleMemory.Snapshot().AllocatedBytes == 0, "Already-decoded disk/cache stream rents no decoder buffers");

// A constructor failing part-way through allocation must return earlier reservations and close its source.
ArticleMemory.ConfigureLimit(8500);
var source = new ObservedStream();
try { using var tooLarge = new YencStream(source); throw new Exception("Expected decoder budget rejection"); }
catch (ArticleMemoryLimitException) { }
Assert(source.WasDisposed && ArticleMemory.Snapshot().AllocatedBytes == 0, "Constructor failure cleans up source and reservations");
ArticleMemory.ConfigureLimit(null);

using (var probe = ArticleMemory.TransportPool.Rent(4096)) capacity = probe.Memory.Length;
ArticleMemory.ConfigureLimit(capacity * 2);
var owners = new ConcurrentBag<System.Buffers.IMemoryOwner<byte>>();
var rejected = 0;
Parallel.For(0, 32, _ =>
{
    try { owners.Add(ArticleMemory.TransportPool.Rent(4096)); }
    catch (ArticleMemoryLimitException) { Interlocked.Increment(ref rejected); }
});
Assert(owners.Count == 2 && rejected == 30, "Concurrent rents obey the global budget");
Assert(ArticleMemory.Snapshot().AllocatedBytes <= capacity * 2, "Budget is never exceeded by published owners");
foreach (var item in owners) item.Dispose();
ArticleMemory.ConfigureLimit(null);

// Exercise the real BODY and ARTICLE pipe implementations against a loopback NNTP server.
foreach (var article in new[] { false, true })
{
    var body = "=ybegin part=1 total=1 line=128 size=4 name=test.bin\r\n=ypart begin=1 end=4\r\n+,-.\r\n=yend size=4\r\n";
    await using var server = new NntpFixture(body, article);
    using var client = new UsenetClient();
    await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);
    var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    Stream stream = article
        ? (await client.ArticleAsync("test", _ => ready.TrySetResult(true), CancellationToken.None)).Stream!
        : (await client.BodyAsync("test", _ => ready.TrySetResult(true), CancellationToken.None)).Stream!;
    await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Assert(ArticleMemory.Snapshot().BufferedBytes == Encoding.Latin1.GetByteCount(body), "Unread pipe bytes include the complete prefetched article");
    var original = ArticleMemory.Snapshot().BufferedBytes;
    var buffer = new byte[3];
    Assert(await stream.ReadAsync(buffer.AsMemory()) == 3, "Pipe read succeeds");
    Assert(ArticleMemory.Snapshot().BufferedBytes == original - 3, "Consumption decreases unread data");
    await stream.DisposeAsync();
    stream.Dispose();
    Assert(ArticleMemory.Snapshot().AllocatedBytes == 0 && ArticleMemory.Snapshot().BufferedBytes == 0, "Both endpoints completed: all pipe buffers returned");
}

// Header parsing transfers data from the receive pipe into decoder buffers; disposal clears both.
await using (var server = new NntpFixture("=ybegin part=1 total=1 line=128 size=4 name=test.bin\r\n=ypart begin=1 end=4\r\n+,-.\r\n=yend size=4\r\n", false))
{
    using var client = new UsenetClient();
    await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);
    var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var body = await client.BodyAsync("test", _ => ready.TrySetResult(true), CancellationToken.None);
    await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await using (var stream = new YencStream(body.Stream!))
    {
        var parsed = await stream.GetYencHeadersAsync();
        Assert(parsed?.FileSize == 4 && ArticleMemory.Snapshot().BufferedBytes > 0, "Unread decoder data is measured after headers");
    }
    Assert(ArticleMemory.Snapshot().AllocatedBytes == 0 && ArticleMemory.Snapshot().BufferedBytes == 0, "Combined pipeline releases gauges");
}

// Refuse a growing pipe: readers must get the failure rather than silently truncated data.
ArticleMemory.ConfigureLimit(4096);
await using (var server = new NntpFixture(string.Concat(Enumerable.Repeat(new string('x', 100) + "\r\n", 200)), false))
{
    using var client = new UsenetClient();
    await client.ConnectAsync("127.0.0.1", server.Port, false, CancellationToken.None);
    var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var body = await client.BodyAsync("test", _ => ready.TrySetResult(true), CancellationToken.None);
    await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await using (var stream = body.Stream!)
    {
        try { await stream.CopyToAsync(Stream.Null); throw new Exception("Expected pipe budget failure"); }
        catch (ArticleMemoryLimitException) { }
    }
}
Assert(ArticleMemory.Snapshot().AllocatedBytes == 0 && ArticleMemory.Snapshot().BufferedBytes == 0, "Failed producer releases buffers after reader disposal");
ArticleMemory.ConfigureLimit(null);
Console.WriteLine("Article memory tests passed: pool ownership, decoder/wrapper lifetime, global cap, prefetch and producer failure.");

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
sealed class EmptyDecodedStream() : YencStream(Stream.Null, allocateBuffers: false)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromResult(0);
}
sealed class ObservedStream : MemoryStream
{
    public bool WasDisposed { get; private set; }
    protected override void Dispose(bool disposing) { WasDisposed = true; base.Dispose(disposing); }
}
sealed class NntpFixture : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _server;
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public NntpFixture(string body, bool article)
    {
        _listener.Start();
        _server = Serve(body, article);
    }
    private async Task Serve(string body, bool article)
    {
        try
        {
            using var peer = await _listener.AcceptTcpClientAsync(_cts.Token);
            using var reader = new StreamReader(peer.GetStream(), Encoding.Latin1, leaveOpen: true);
            await using var writer = new StreamWriter(peer.GetStream(), Encoding.Latin1, leaveOpen: true) { NewLine="\r\n", AutoFlush=true };
            await writer.WriteLineAsync("200 test server ready");
            var command = await reader.ReadLineAsync(_cts.Token);
            if (!(command?.StartsWith(article ? "ARTICLE " : "BODY ") ?? false)) throw new Exception("Unexpected test command");
            await writer.WriteAsync((article ? "220 article follows\r\nSubject: Test\r\n\r\n" : "222 body follows\r\n") + body + ".\r\n");
            await Task.Delay(Timeout.Infinite, _cts.Token);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _listener.Stop();
        try { await _server; }
        finally { _cts.Dispose(); }
    }
}
