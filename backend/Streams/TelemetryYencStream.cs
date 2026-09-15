using NzbWebDAV.Extensions;
using NzbWebDAV.Statistics;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

// Delegate decoding unchanged; count decoded bytes actually read, never file-size estimates.
public sealed class TelemetryYencStream(YencStream inner, ProviderTelemetry telemetry, UsenetTelemetry.ReadSession? read)
    : YencStream(Null)
{
    private int _disposed;
    private int _failed;
    public override async ValueTask<UsenetYencHeader?> GetYencHeadersAsync(CancellationToken cancellationToken = default)
    {
        try { return await inner.GetYencHeadersAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (!error.IsCancellationException()) { RecordFailure(); throw; }
    }
    private void RecordFailure()
    {
        if (Interlocked.Exchange(ref _failed, 1) == 0) { telemetry.Error(); UsenetTelemetry.Shared.HardFailure(); }
    }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        try
        {
            var count = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count > 0) { telemetry.Bytes(count); read?.ProviderRead(telemetry.Id, count); }
            return count;
        }
        catch (Exception error) when (!error.IsCancellationException())
        {
            RecordFailure();
            throw;
        }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0) inner.Dispose();
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) await inner.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
