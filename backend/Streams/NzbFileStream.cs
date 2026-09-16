using NzbWebDAV.Clients.Usenet;
using NzbWebDAV.Models;
using NzbWebDAV.Utils;
using Serilog;
using UsenetSharp.Streams;

namespace NzbWebDAV.Streams;

public class NzbFileStream(
    string[] fileSegmentIds,
    long fileSize,
    INntpClient usenetClient,
    int articleBufferSize
) : FastReadOnlyStream
{
    private long _position;
    private bool _disposed;
    private Stream? _innerStream;

    public override bool CanSeek => true;
    public override long Length => fileSize;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override void Flush()
    {
        _innerStream?.Flush();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (buffer.IsEmpty || _position >= fileSize) return 0;
        for (var retry = 0; ; retry++)
        {
            try
            {
                _innerStream ??= await GetFileStream(_position, cancellationToken).ConfigureAwait(false);
                var read = await _innerStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                _position += read;
                return read;
            }
            catch (TimeoutException) when (!cancellationToken.IsCancellationRequested)
            {
                // A failed read may have consumed decoder bytes, but none were handed to
                // the caller. Reopen at the last successfully delivered file position.
                var failedStream = _innerStream;
                _innerStream = null;
                if (failedStream != null) await failedStream.DisposeAsync().ConfigureAwait(false);
                if (retry >= 2) throw;
                Log.Warning("NNTP read timed out at file byte {Position}. Retrying ({Retry}/2).", _position, retry + 1);
                await Task.Delay(TimeSpan.FromMilliseconds(250 * (retry + 1)), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        var absoluteOffset = origin == SeekOrigin.Begin ? offset
            : origin == SeekOrigin.Current ? _position + offset
            : throw new InvalidOperationException("SeekOrigin must be Begin or Current.");
        if (_position == absoluteOffset) return _position;
        _position = absoluteOffset;
        _innerStream?.Dispose();
        _innerStream = null;
        return _position;
    }

    private async Task<InterpolationSearch.Result> SeekSegment(long byteOffset, CancellationToken ct)
    {
        return await InterpolationSearch.Find(
            byteOffset,
            new LongRange(0, fileSegmentIds.Length),
            new LongRange(0, fileSize),
            async (guess) =>
            {
                var header = await usenetClient.GetYencHeadersAsync(fileSegmentIds[guess], ct).ConfigureAwait(false);
                return new LongRange(header.PartOffset, header.PartOffset + header.PartSize);
            },
            ct
        ).ConfigureAwait(false);
    }

    private async Task<Stream> GetFileStream(long rangeStart, CancellationToken cancellationToken)
    {
        if (rangeStart == 0) return GetMultiSegmentStream(0, cancellationToken);
        var foundSegment = await SeekSegment(rangeStart, cancellationToken).ConfigureAwait(false);
        var stream = GetMultiSegmentStream(foundSegment.FoundIndex, cancellationToken);
        try
        {
            // Do not silently accept EOF while positioning a replacement stream.
            var remaining = rangeStart - foundSegment.FoundByteRange.StartInclusive;
            var scratch = new byte[1024];
            while (remaining > 0)
            {
                var read = await stream.ReadAsync(scratch.AsMemory(0, (int)Math.Min(remaining, scratch.Length)), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("Unexpected EOF while seeking within an NNTP segment.");
                remaining -= read;
            }
            return stream;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private Stream GetMultiSegmentStream(int firstSegmentIndex, CancellationToken cancellationToken)
    {
        var segmentIds = fileSegmentIds.AsMemory()[firstSegmentIndex..];
        return MultiSegmentStream.Create(segmentIds, usenetClient, articleBufferSize, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _innerStream?.Dispose();
        _disposed = true;
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        if (_innerStream != null) await _innerStream.DisposeAsync().ConfigureAwait(false);
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}
