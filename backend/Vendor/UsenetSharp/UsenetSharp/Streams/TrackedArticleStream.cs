using UsenetSharp.Diagnostics;

namespace UsenetSharp.Streams;

internal sealed class TrackedArticleStream(Stream inner, ArticleMemory.BufferedUsage usage) : FastReadOnlyNonSeekableStream
{
    private int _disposed;
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var count = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        usage.Add(-count);
        return count;
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try { inner.Dispose(); }
            finally { usage.Dispose(); }
        }
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try { await inner.DisposeAsync().ConfigureAwait(false); }
            finally { usage.Dispose(); }
        }
        GC.SuppressFinalize(this);
    }
}
