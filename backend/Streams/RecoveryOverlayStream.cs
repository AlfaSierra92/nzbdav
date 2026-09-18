using NzbWebDAV.Database.Models;

namespace NzbWebDAV.Streams;

/// <summary>
/// Serves a posted file whose dead articles were PAR2-repaired: reads inside a recovered
/// range come from the recovery blob's payload, everything else from the underlying
/// stream. The underlying stream must be seekable in original-file offsets and must not
/// contain the dead segments (they are filtered out at stream construction), so it is
/// always re-positioned past a covered range instead of reading through it.
/// </summary>
public class RecoveryOverlayStream(
    Stream underlying,
    long length,
    IReadOnlyList<DavFileRecovery.RecoveredRange> ranges,
    Stream payloadStream,
    long payloadStart
) : Stream
{
    private readonly DavFileRecovery.RecoveredRange[] _ranges =
        ranges.OrderBy(x => x.FileOffset).ToArray();

    private long _position;
    private bool _disposed;

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;

    public override long Position
    {
        get => _position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (buffer.IsEmpty || _position >= length) return 0;
        buffer = buffer[..(int)Math.Min(buffer.Length, length - _position)];

        var covering = FindCoveringRange(_position);
        int read;
        if (covering != null)
        {
            // serve from the recovery payload
            var rangeRemaining = covering.FileOffset + covering.Length - _position;
            var count = (int)Math.Min(buffer.Length, rangeRemaining);
            var payloadPosition = payloadStart + covering.PayloadOffset + (_position - covering.FileOffset);
            if (payloadStream.Position != payloadPosition) payloadStream.Seek(payloadPosition, SeekOrigin.Begin);
            read = await payloadStream.ReadAsync(buffer[..count], cancellationToken).ConfigureAwait(false);
        }
        else
        {
            // serve from the underlying stream, stopping at the next covered range
            var limit = NextRangeStart(_position) is { } nextStart
                ? (int)Math.Min(buffer.Length, nextStart - _position)
                : buffer.Length;
            if (underlying.Position != _position) underlying.Seek(_position, SeekOrigin.Begin);
            read = await underlying.ReadAsync(buffer[..limit], cancellationToken).ConfigureAwait(false);
        }

        _position += read;
        return read;
    }

    private DavFileRecovery.RecoveredRange? FindCoveringRange(long position)
        => _ranges.FirstOrDefault(x => position >= x.FileOffset && position < x.FileOffset + x.Length);

    private long? NextRangeStart(long position)
    {
        foreach (var range in _ranges)
            if (range.FileOffset > position)
                return range.FileOffset;
        return null;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => checked(_position + offset),
            SeekOrigin.End => checked(length + offset),
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };
        if (position < 0) throw new IOException("Cannot seek before the beginning of the file.");
        _position = position;
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;
        if (!disposing) return;
        underlying.Dispose();
        payloadStream.Dispose();
    }

    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await underlying.DisposeAsync().ConfigureAwait(false);
        await payloadStream.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }
}
