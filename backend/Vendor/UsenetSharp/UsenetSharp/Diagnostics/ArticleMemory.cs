using System.Buffers;
using System.Collections.Concurrent;

namespace UsenetSharp.Diagnostics;

/// <summary>Tracks buffers currently owned by article pipes and yEnc decoders, not process RSS or idle pool memory.</summary>
public static class ArticleMemory
{
    private static readonly object Gate = new();
    private static readonly ConcurrentDictionary<long, BufferedUsage> Usage = new();
    private static long _transport, _decoder, _peak, _rejections, _nextId;
    private static long? _limit;
    public static MemoryPool<byte> TransportPool { get; } = new TrackedPool();

    public static void ConfigureLimit(long? bytes)
    {
        if (bytes is <= 0) throw new ArgumentOutOfRangeException(nameof(bytes), "Use null for unlimited memory.");
        lock (Gate)
        {
            if (bytes.HasValue && bytes.Value < _transport + _decoder)
                throw new InvalidOperationException("Cannot lower the article memory limit below currently owned buffers.");
            _limit = bytes;
        }
    }

    public static ArticleMemorySnapshot Snapshot()
    {
        lock (Gate)
        {
            var allocated = _transport + _decoder;
            // Reader/writer cursors can move during sampling. Bound the instantaneous gauge by capacity.
            var buffered = Math.Min(allocated, Usage.Values.Sum(item => item.BufferedBytes));
            return new(allocated, buffered, _transport, _decoder, _peak, _limit, _rejections);
        }
    }

    private static void Reserve(int bytes, bool decoder)
    {
        lock (Gate)
        {
            if (_limit.HasValue && bytes > _limit.Value - _transport - _decoder)
            {
                _rejections++;
                throw new ArticleMemoryLimitException(_limit.Value);
            }
            if (decoder) _decoder += bytes; else _transport += bytes;
            _peak = Math.Max(_peak, _transport + _decoder);
        }
    }
    private static void Release(int bytes, bool decoder)
    {
        lock (Gate) { if (decoder) _decoder -= bytes; else _transport -= bytes; }
    }

    internal static byte[] RentDecoder(int size)
    {
        var array = ArrayPool<byte>.Shared.Rent(size);
        try { Reserve(array.Length, decoder: true); return array; }
        catch { ArrayPool<byte>.Shared.Return(array); throw; }
    }
    internal static void ReturnDecoder(byte[] array)
    {
        // Keep the reservation until the allocation has actually been returned to its pool.
        try { ArrayPool<byte>.Shared.Return(array); }
        finally { Release(array.Length, decoder: true); }
    }

    public static BufferedUsage TrackBufferedData()
    {
        var id = Interlocked.Increment(ref _nextId);
        var usage = new BufferedUsage(id);
        Usage[id] = usage;
        return usage;
    }
    public sealed class BufferedUsage : IDisposable
    {
        private readonly long _id;
        private long _buffered;
        private int _disposed;
        internal BufferedUsage(long id) => _id = id;
        public long BufferedBytes => Volatile.Read(ref _disposed) != 0 ? 0 : Math.Max(0, Interlocked.Read(ref _buffered));
        public void Add(long bytes) { if (Volatile.Read(ref _disposed) == 0) Interlocked.Add(ref _buffered, bytes); }
        public void Set(long bytes) { if (Volatile.Read(ref _disposed) == 0) Interlocked.Exchange(ref _buffered, Math.Max(0, bytes)); }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Usage.TryRemove(_id, out _);
            Interlocked.Exchange(ref _buffered, 0);
        }
    }

    private sealed class TrackedPool : MemoryPool<byte>
    {
        public override int MaxBufferSize => MemoryPool<byte>.Shared.MaxBufferSize;
        public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
        {
            var owner = MemoryPool<byte>.Shared.Rent(minBufferSize);
            var length = owner.Memory.Length;
            try { Reserve(length, decoder: false); return new TrackedOwner(owner, length); }
            catch { owner.Dispose(); throw; }
        }
        protected override void Dispose(bool disposing) { /* Shared pool lives for the process lifetime. */ }
    }
    private sealed class TrackedOwner(IMemoryOwner<byte> owner, int length) : IMemoryOwner<byte>
    {
        private IMemoryOwner<byte>? _owner = owner;
        public Memory<byte> Memory => _owner?.Memory ?? throw new ObjectDisposedException(nameof(TrackedOwner));
        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _owner, null);
            if (owner is null) return;
            try { owner.Dispose(); }
            finally { Release(length, decoder: false); }
        }
    }
}

public sealed record ArticleMemorySnapshot(long AllocatedBytes, long BufferedBytes, long TransportBytes,
    long DecoderBytes, long PeakAllocatedBytes, long? LimitBytes, long LimitRejections);

/// <summary>Fail promptly rather than waiting with a connection while another queued article holds the budget.</summary>
public sealed class ArticleMemoryLimitException(long limit) : IOException($"Article buffer memory limit reached ({limit} bytes).");
