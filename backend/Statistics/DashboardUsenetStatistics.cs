using Microsoft.Data.Sqlite;

namespace NzbWebDAV.Statistics;

public sealed partial class DashboardStatisticsStore
{
    private readonly Dictionary<(long, string), TelemetryAggregate> _telemetry = new();
    private readonly Dictionary<(long, long, long), TelemetryAggregate[]> _telemetryCache = new();
    private readonly Queue<TelemetryFrame> _recentFrames = new();
    private TelemetryFrame? _liveTelemetry;

    public async Task CaptureTelemetryAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var frame = UsenetTelemetry.Shared.Capture();
            _liveTelemetry = frame;
            RecordArticleMemory(frame.Time, frame.ArticleMemory);
            _recentFrames.Enqueue(frame);
            while (_recentFrames.TryPeek(out var first) && first.Time <= frame.Time - 60) _recentFrames.Dequeue();
            var minute = frame.Time / 60 * 60;
            Add(new TelemetryAggregate { Time = minute, Provider = "", Name = "WebDAV", ServedBytes = frame.ServedBytes, HardFailures = frame.HardFailures, PeakBytesPerSecond=frame.Providers.Sum(p=>p.BytesPerSecond) });
            foreach (var provider in frame.Providers)
                Add(new TelemetryAggregate
                {
                    Time = minute, Provider = provider.Id, Name = provider.Name, Articles = provider.Articles,
                    Bytes = provider.Bytes, Misses = provider.Misses, Errors = provider.Errors, Retries = provider.Retries,
                    OkMilliseconds = provider.OkMilliseconds, OutageSeconds = provider.Outage ? frame.ElapsedSeconds : 0,
                    ObservedSeconds = frame.ElapsedSeconds, PeakBytesPerSecond = provider.BytesPerSecond,
                });
            void Add(TelemetryAggregate value)
            {
                var key = (value.Time, value.Provider);
                if (_telemetry.TryGetValue(key, out var current)) current.Add(value);
                else _telemetry[key] = value;
            }
        }
        finally { _gate.Release(); }
    }

    private async Task FlushTelemetryAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        foreach (var value in _telemetry.Values)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO telemetry_minutes VALUES ($time,$provider,$name,$articles,$bytes,$misses,$errors,$retries,$ok,$outage,$observed,$served,$peak,$hard)
                ON CONFLICT(time,provider) DO UPDATE SET name=excluded.name,
                    articles=articles+excluded.articles, bytes=bytes+excluded.bytes, misses=misses+excluded.misses,
                    errors=errors+excluded.errors, retries=retries+excluded.retries, ok_ms=ok_ms+excluded.ok_ms,
                    outage_seconds=outage_seconds+excluded.outage_seconds, observed_seconds=observed_seconds+excluded.observed_seconds,
                    served_bytes=served_bytes+excluded.served_bytes, peak_bps=MAX(peak_bps,excluded.peak_bps), hard_failures=hard_failures+excluded.hard_failures
                """;
            (string, object)[] parameters = [("$time",value.Time),("$provider",value.Provider),("$name",value.Name),
                ("$articles",value.Articles),("$bytes",value.Bytes),("$misses",value.Misses),("$errors",value.Errors),
                ("$retries",value.Retries),("$ok",value.OkMilliseconds),("$outage",value.OutageSeconds),
                ("$observed",value.ObservedSeconds),("$served",value.ServedBytes),("$peak",value.PeakBytesPerSecond),("$hard",value.HardFailures)];
            foreach (var (name, parameter) in parameters) command.Parameters.AddWithValue(name, parameter);
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    public async Task<object> ReadUsenetAsync(string range, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var end = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60 * 60 + 60;
            var start = range switch { "1h" => end-3600, "24h" => end-86400, "7d" => end-604800, "30d" => end-2592000, _ => 0 };
            var step = range is "1h" or "24h" ? 60L : range == "all" ? 86400L : 3600L;
            var key = (start, end, step);
            if (!_telemetryCache.TryGetValue(key, out var saved))
            {
                await using var connection = await OpenAsync(ct, readOnly: true);
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    SELECT time/$step*$step, provider, MAX(name), SUM(articles), SUM(bytes), SUM(misses), SUM(errors),
                        SUM(retries), SUM(ok_ms), SUM(outage_seconds), SUM(observed_seconds), SUM(served_bytes), MAX(peak_bps), SUM(hard_failures)
                    FROM telemetry_minutes WHERE time >= $start AND time < $end GROUP BY 1,2 ORDER BY 1
                    """;
                command.Parameters.AddWithValue("$step", step); command.Parameters.AddWithValue("$start", start); command.Parameters.AddWithValue("$end", end);
                await using var reader = await command.ExecuteReaderAsync(ct);
                var rows = new List<TelemetryAggregate>();
                while (await reader.ReadAsync(ct)) rows.Add(new TelemetryAggregate
                {
                    Time=reader.GetInt64(0), Provider=reader.GetString(1), Name=reader.GetString(2), Articles=reader.GetInt64(3),
                    Bytes=reader.GetInt64(4), Misses=reader.GetInt64(5), Errors=reader.GetInt64(6), Retries=reader.GetInt64(7),
                    OkMilliseconds=reader.GetDouble(8), OutageSeconds=reader.GetDouble(9), ObservedSeconds=reader.GetDouble(10),
                    ServedBytes=reader.GetInt64(11), PeakBytesPerSecond=reader.GetDouble(12), HardFailures=reader.GetInt64(13),
                });
                saved = rows.ToArray();
                if (_telemetryCache.Count >= 5) _telemetryCache.Clear();
                _telemetryCache[key] = saved;
            }
            var combined = saved.ToDictionary(item => (item.Time, item.Provider), item => item.Copy());
            foreach (var item in _telemetry.Values.Where(item => item.Time >= start && item.Time < end))
            {
                var bucket = item.Time / step * step;
                if (!combined.TryGetValue((bucket,item.Provider), out var value))
                    combined[(bucket,item.Provider)] = value = new TelemetryAggregate { Time=bucket, Provider=item.Provider, Name=item.Name };
                value.Add(item);
            }
            var total = new TelemetryAggregate();
            foreach (var item in combined.Values) total.Add(item);
            var providers = combined.Values.Where(item => item.Provider != "").GroupBy(item => item.Provider).Select(group =>
            {
                var sum = new TelemetryAggregate { Provider=group.Key, Name=group.First().Name };
                foreach (var value in group) sum.Add(value);
                return new { totals=sum, points=group.OrderBy(item => item.Time).ToArray() };
            }).ToArray();
            var points = combined.Values.GroupBy(item => item.Time).OrderBy(group => group.Key).Select(group =>
            {
                var sum = new TelemetryAggregate { Time=group.Key };
                foreach (var item in group) sum.Add(item);
                return sum;
            }).ToArray();
            var heatmap = points.GroupBy(item => item.Time / 3600 * 3600).Select(group => new { time=group.Key, articles=group.Sum(item=>item.Articles) }).ToArray();
            var memoryPoints = await ReadArticleMemoryAsync(start, end, step, ct);
            var memory = _liveTelemetry?.ArticleMemory;
            return new { range, start=range=="all" ? points.FirstOrDefault()?.Time ?? end : start, end, step,
                live=_liveTelemetry, errorsLastMinute=_recentFrames.Sum(frame=>frame.HardFailures),
                articlesLastMinute=_recentFrames.Sum(frame=>frame.Providers.Sum(provider=>provider.Articles)),
                totals=total, providers, points, heatmap, lastSaved=_lastSaved, collectionError=LastError is not null,
                articleRamBytes=memory?.AllocatedBytes, articleBufferedBytes=memory?.BufferedBytes,
                articleRamCapBytes=memory?.LimitBytes, articleRamTransportBytes=memory?.TransportBytes,
                articleRamDecoderBytes=memory?.DecoderBytes, articleRamPeakBytes=memory?.PeakAllocatedBytes,
                articleRamLimitRejections=memory?.LimitRejections, memoryPoints };
        }
        finally { _gate.Release(); }
    }
}

public sealed class TelemetryAggregate
{
    public long Time { get; set; }
    public string Provider { get; set; } = "";
    public string Name { get; set; } = "";
    public long HardFailures { get; set; }
    public long Articles { get; set; }
    public long Bytes { get; set; }
    public long Misses { get; set; }
    public long Errors { get; set; }
    public long Retries { get; set; }
    public double OkMilliseconds { get; set; }
    public double OutageSeconds { get; set; }
    public double ObservedSeconds { get; set; }
    public long ServedBytes { get; set; }
    public double PeakBytesPerSecond { get; set; }
    public TelemetryAggregate Copy() => (TelemetryAggregate)MemberwiseClone();
    public void Add(TelemetryAggregate other)
    {
        HardFailures+=other.HardFailures; Articles+=other.Articles; Bytes+=other.Bytes; Misses+=other.Misses; Errors+=other.Errors; Retries+=other.Retries;
        OkMilliseconds+=other.OkMilliseconds; OutageSeconds+=other.OutageSeconds; ObservedSeconds+=other.ObservedSeconds;
        ServedBytes+=other.ServedBytes; PeakBytesPerSecond=Math.Max(PeakBytesPerSecond,other.PeakBytesPerSecond);
    }
}
