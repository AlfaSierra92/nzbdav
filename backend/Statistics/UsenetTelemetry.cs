using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace NzbWebDAV.Statistics;

// Hot paths only update counters in memory. The existing collector owns sampling and persistence.
public sealed class UsenetTelemetry
{
    public static UsenetTelemetry Shared { get; } = new();
    public static readonly AsyncLocal<ReadSession?> CurrentRead = new();
    private readonly ConcurrentDictionary<string, ProviderTelemetry> _providers = new();
    private readonly ConcurrentDictionary<string, ReadSession> _reads = new();
    private long _hardFailures, _lastHardFailures;
    public void HardFailure() => Interlocked.Increment(ref _hardFailures);
    private long _served;
    private long _lastServed;
    private long _lastTick = Stopwatch.GetTimestamp();
    public void ResetConfigured() { foreach (var provider in _providers.Values) provider.Configured = false; }
    public ProviderTelemetry Register(string host, int port, bool ssl, string user, Func<bool> outage, bool enabled)
    {
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{host.ToLowerInvariant()}|{port}|{ssl}|{user}")))[..16];
        var result = _providers.GetOrAdd(id, _ => new ProviderTelemetry(id, $"{host}:{port} · {id[..4]}"));
        result.IsOutage = outage;
        result.Configured = enabled;
        return result;
    }
    public ReadSession BeginRead(string name, string client, string address, long start, long? length)
    {
        var read = new ReadSession(this, name, client, address, start, length);
        _reads[read.Id] = read;
        return read;
    }
    public TelemetryFrame Capture()
    {
        var now = Stopwatch.GetTimestamp();
        var elapsed = Math.Max(.001, Stopwatch.GetElapsedTime(_lastTick, now).TotalSeconds);
        _lastTick = now;
        var served = Interlocked.Read(ref _served);
        var servedDelta = Math.Max(0, served - _lastServed);
        _lastServed = served;
        var hardFailures = Interlocked.Read(ref _hardFailures);
        var hardDelta = Math.Max(0, hardFailures - _lastHardFailures);
        _lastHardFailures = hardFailures;
        var providers = _providers.Values.Select(provider => provider.Capture(elapsed)).Where(p => p.Configured || p.Articles + p.Bytes + p.Errors + p.Misses + p.Retries > 0).ToArray();
        var reads = _reads.Values.Select(read => read.Snapshot(elapsed)).ToArray();
        return new TelemetryFrame(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), elapsed, providers, reads,
            servedDelta, servedDelta / elapsed, hardDelta);
    }
    public sealed class ReadSession : IDisposable
    {
        private readonly UsenetTelemetry _owner;
        private long _sent;
        private long _previousSent;
        private int _disposed;
        private readonly string _name, _client, _address;
        private readonly long _start;
        private readonly long? _length;
        private readonly ConcurrentDictionary<string, long> _providers = new();
        public string Id { get; } = Guid.NewGuid().ToString("N");
        internal ReadSession(UsenetTelemetry owner, string name, string client, string address, long start, long? length)
        { _owner = owner; _name = name; _client = client; _address = address; _start = start; _length = length; }
        public void Sent(int bytes) { Interlocked.Add(ref _sent, bytes); Interlocked.Add(ref _owner._served, bytes); }
        public void ProviderRead(string provider, int bytes) => _providers.AddOrUpdate(provider, bytes, (_, count) => count + bytes);
        internal ActiveRead Snapshot(double elapsed)
        {
            var sent = Interlocked.Read(ref _sent);
            var rate = Math.Max(0, sent - _previousSent) / elapsed;
            _previousSent = sent;
            return new ActiveRead(Id, _name, _client, _address, _start + sent, _length, sent, rate,
                _providers.ToDictionary(pair => pair.Key, pair => pair.Value));
        }
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) _owner._reads.TryRemove(Id, out _); }
    }
}

public sealed class ProviderTelemetry(string id, string name)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public volatile bool Configured;
    public Func<bool> IsOutage { get; set; } = () => false;
    private long _articles, _bytes, _misses, _errors, _retries, _okMicros;
    private long[] _last = new long[6];
    public void Article(double milliseconds) { Interlocked.Increment(ref _articles); Interlocked.Add(ref _okMicros, (long)(milliseconds * 1000)); }
    public void Bytes(int count) => Interlocked.Add(ref _bytes, count);
    public void Miss() => Interlocked.Increment(ref _misses);
    public void Error() => Interlocked.Increment(ref _errors);
    public void Retry() => Interlocked.Increment(ref _retries);
    public ProviderTick Capture(double elapsed)
    {
        long[] values = [Interlocked.Read(ref _articles), Interlocked.Read(ref _bytes), Interlocked.Read(ref _misses), Interlocked.Read(ref _errors), Interlocked.Read(ref _retries), Interlocked.Read(ref _okMicros)];
        var delta = values.Zip(_last, (current, previous) => Math.Max(0, current - previous)).ToArray();
        _last = values;
        return new ProviderTick(Id, Name, Configured, delta[0], delta[1], delta[2], delta[3], delta[4], delta[5] / 1000d,
            IsOutage(), delta[0] / elapsed, delta[1] / elapsed);
    }
}
public record ProviderTick(string Id, string Name, bool Configured, long Articles, long Bytes, long Misses, long Errors,
    long Retries, double OkMilliseconds, bool Outage, double ArticlesPerSecond, double BytesPerSecond);
public record ActiveRead(string Id, string Name, string Client, string Address, long Position, long? Length,
    long SentBytes, double BytesPerSecond, Dictionary<string, long> Providers);
public record TelemetryFrame(long Time, double ElapsedSeconds, ProviderTick[] Providers, ActiveRead[] Reads,
    long ServedBytes, double ServedBytesPerSecond, long HardFailures);
