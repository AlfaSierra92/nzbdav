using System.Reflection;
using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Clients.Usenet.Connections;
using NzbWebDAV.Models;
using NzbWebDAV.Statistics;
using NzbWebDAV.Streams;
using UsenetSharp.Models;
using UsenetSharp.Streams;

Func<Exception>[] errors = [
    () => new OperationCanceledException(),
    () => new IOException("Socket closed"),
    () => new ObjectDisposedException("connection"),
    () => new IOException("Wrapped cancellation", new OperationCanceledException()),
    () => new TimeoutException("Provider timeout")
];

foreach (var error in errors)
foreach (var duringConnect in new[] { true, false })
{
    using var cts = new CancellationTokenSource();
    var telemetry = new ProviderTelemetry("test", "test");
    var breaker = new ProviderCircuitBreaker("test");
    // Cancellation must neither increment nor reset existing failures.
    breaker.RecordFailure();
    breaker.RecordFailure();
    var calls = 0;
    var client = DispatchProxy.Create<INntpClient, CommandProxy>();
    ((CommandProxy)(object)client).Run = () => { calls++; cts.Cancel(); throw error(); };
    await using var pool = new ConnectionPool<INntpClient>(1, _ =>
    {
        if (duringConnect) { calls++; cts.Cancel(); throw error(); }
        return ValueTask.FromResult(client);
    });
    using var providers = new MultiProviderNntpClient([
        new MultiConnectionNntpClient(pool, ProviderType.Pooled, breaker, "test", telemetry)
    ]);
    UsenetTelemetry.Shared.Capture();
    try { await providers.DecodedBodyAsync("segment", cts.Token); throw new Exception("Expected cancellation"); }
    catch (OperationCanceledException) { }
    var tick = telemetry.Capture(1);
    Assert(calls == 1 && tick.Errors == 0 && tick.Retries == 0, "Cancelled commands must not penalize or retry");
    Assert(!breaker.IsTripped, "Cancellation must not trip the breaker");
    breaker.RecordFailure();
    Assert(breaker.IsTripped, "Cancellation must not reset previous failures");
    Assert(UsenetTelemetry.Shared.Capture().HardFailures == 0, "Cancellation must not record a hard failure");
}

foreach (var error in errors)
foreach (var headers in new[] { false, true })
foreach (var cancellation in new[] { "read", "operation", "dispose", "none" })
{
    using var operation = new CancellationTokenSource();
    using var read = new CancellationTokenSource();
    var telemetry = new ProviderTelemetry("test", "test");
    using var stream = new TelemetryYencStream(new ErrorStream(error), telemetry, null, operation.Token);
    if (cancellation == "read") read.Cancel();
    if (cancellation == "operation") operation.Cancel();
    if (cancellation == "dispose") stream.Dispose();
    UsenetTelemetry.Shared.Capture();
    for (var attempt = 0; attempt < 2; attempt++)
    {
        try
        {
            if (headers) await stream.GetYencHeadersAsync(read.Token);
            else await stream.ReadAsync(new byte[1], read.Token);
            throw new Exception("Expected read error");
        }
        catch (Exception caught) when (caught.GetType() == error().GetType()) { }
    }
    var expected = cancellation == "none" && error() is not OperationCanceledException ? 1 : 0;
    Assert(telemetry.Capture(1).Errors == expected, "Count real stream errors once; ignore cancelled reads");
    Assert(UsenetTelemetry.Shared.Capture().HardFailures == expected, "Hard failures follow stream error accounting");
}

// A real timeout retains the existing retry and breaker behavior.
{
    var telemetry = new ProviderTelemetry("test", "test");
    var breaker = new ProviderCircuitBreaker("test");
    var calls = 0;
    await using var pool = new ConnectionPool<INntpClient>(1, _ =>
    {
        calls++;
        throw new TimeoutException("Provider unavailable");
    });
    using var provider = new MultiConnectionNntpClient(pool, ProviderType.Pooled, breaker, "test", telemetry);
    try { await provider.DecodedBodyAsync("segment", CancellationToken.None); throw new Exception("Expected timeout"); }
    catch (TimeoutException) { }
    var tick = telemetry.Capture(1);
    Assert(calls == 2 && tick.Errors == 2 && tick.Retries == 1, "Real failures retain retry accounting");
    breaker.RecordFailure();
    Assert(breaker.IsTripped, "Real failures still count towards the breaker threshold");
}
Console.WriteLine("NNTP cancellation tests passed.");

static void Assert(bool value, string message) { if (!value) throw new Exception(message); }

public class CommandProxy : DispatchProxy
{
    public Action Run = null!;
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == "Dispose") return null;
        Run();
        throw new InvalidOperationException("Expected command failure");
    }
}

sealed class ErrorStream(Func<Exception> error) : YencStream(Stream.Null, allocateBuffers: false)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => throw error();
    public override ValueTask<UsenetYencHeader?> GetYencHeadersAsync(CancellationToken cancellationToken = default) => throw error();
}
