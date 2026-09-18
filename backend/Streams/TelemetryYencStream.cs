using NzbWebDAV.Extensions;
using NzbWebDAV.Statistics;
using NzbWebDAV.Utils;
using UsenetSharp.Models;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

// Delegate decoding unchanged; count decoded bytes actually read, never file-size estimates.
public sealed class TelemetryYencStream(YencStream inner, ProviderTelemetry telemetry, UsenetTelemetry.ReadSession? read)
    : YencStream(Null, allocateBuffers: false)
{
    private int _disposed;
    private int _failed;
    public override async ValueTask<UsenetYencHeader?> GetYencHeadersAsync(CancellationToken cancellationToken = default)
    {
        try { return await inner.GetYencHeadersAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (!error.IsCancellationException()) { RecordFailure(error); throw; }
    }
    private void RecordFailure(Exception error)
    {
        if (Interlocked.Exchange(ref _failed, 1) == 0)
        {
            telemetry.Error();
            UsenetTelemetry.Shared.HardFailure();
            ProviderErrorLogging.Warning(false, error, "Error reading NNTP article stream for provider {Provider}", telemetry.Name);
        }
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
            RecordFailure(error);
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
        try { if (Interlocked.Exchange(ref _disposed, 1) == 0) await inner.DisposeAsync().ConfigureAwait(false); }
        finally { base.Dispose(true); GC.SuppressFinalize(this); }
    }
}
