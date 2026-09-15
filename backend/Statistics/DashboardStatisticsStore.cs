using Microsoft.Data.Sqlite;
using NzbWebDAV.Database;

namespace NzbWebDAV.Statistics;

// Completely independent of DavDatabaseContext and its migrations.
public sealed partial class DashboardStatisticsStore
{
    public static string FilePath => Path.Join(DavDatabaseContext.ConfigPath, "dashboard", "statistics-v1.sqlite");
    public string? LastError { get; set; }
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SortedDictionary<long, SampleRecord> _samples = new();
    private readonly Dictionary<string, ImportRecord> _imports = new();
    private readonly Dictionary<string, HealthRecord> _checks = new();
    private readonly Dictionary<(long From, long To, long Step), StatisticsBucket[]> _diskCache = new();
    private int? _currentQueue;
    private long? _lastCapture;
    private long? _lastSaved;
    private bool _initialized;
    public int FlushIntervalSeconds { get; } = int.TryParse(
        Environment.GetEnvironmentVariable("DASHBOARD_STATS_FLUSH_SECONDS"), out var seconds)
        ? Math.Clamp(seconds, 60, 86400) : 900;
    public bool BufferLimitReached => _samples.Count + _imports.Count + _checks.Count + _telemetry.Count >= 10000;

    public async Task<SqliteConnection> OpenAsync(CancellationToken ct, bool readOnly = false)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = FilePath, DefaultTimeout = 5,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
        }.ToString());
        try { await connection.OpenAsync(ct); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        if (_initialized) return;
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        await using var connection = await OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS samples (
                time INTEGER PRIMARY KEY, active INTEGER, idle INTEGER, capacity INTEGER, queue INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS imports (
                id TEXT PRIMARY KEY, time INTEGER NOT NULL, completed INTEGER NOT NULL, bytes INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS imports_time ON imports(time);
            CREATE TABLE IF NOT EXISTS health (
                id TEXT PRIMARY KEY, time INTEGER NOT NULL, healthy INTEGER NOT NULL);
            CREATE INDEX IF NOT EXISTS health_time ON health(time);
            CREATE TABLE IF NOT EXISTS telemetry_minutes (
                time INTEGER NOT NULL, provider TEXT NOT NULL, name TEXT NOT NULL,
                articles INTEGER NOT NULL, bytes INTEGER NOT NULL, misses INTEGER NOT NULL, errors INTEGER NOT NULL,
                retries INTEGER NOT NULL, ok_ms REAL NOT NULL, outage_seconds REAL NOT NULL, observed_seconds REAL NOT NULL,
                served_bytes INTEGER NOT NULL, peak_bps REAL NOT NULL, hard_failures INTEGER NOT NULL, PRIMARY KEY(time,provider));
            CREATE TABLE IF NOT EXISTS metadata (key TEXT PRIMARY KEY, value INTEGER NOT NULL);
            """;
        await command.ExecuteNonQueryAsync(ct);
        _lastSaved = await GetCheckpointAsync(ct);
        _lastCapture = _lastSaved;
        _initialized = true;
    }

    public async Task<long?> GetCheckpointAsync(CancellationToken ct)
    {
        await using var connection = await OpenAsync(ct, readOnly: true);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM metadata WHERE key = 'lastCapture'";
        return await command.ExecuteScalarAsync(ct) is long value ? value : null;
    }

    // Called by the single collector. Reads archive IDs but never writes to disk.
    public async Task StageAsync(long capturedAt, int queue, int[]? pool,
        IReadOnlyList<ImportRecord> imports, IReadOnlyList<HealthRecord> checks, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (imports.Count == 0 && checks.Count == 0)
            {
                _samples.TryAdd(capturedAt, new SampleRecord(capturedAt, queue, pool));
                _lastCapture = capturedAt;
                _currentQueue = queue;
                return;
            }
            await using var connection = await OpenAsync(ct, readOnly: true);
            var existingImports = await ExistingIds("imports", imports.Select(item => item.Time));
            var existingChecks = await ExistingIds("health", checks.Select(item => item.Time));
            // Mutate only after both reads succeed. Keep every observed second, including zero activity.
            _samples.TryAdd(capturedAt, new SampleRecord(capturedAt, queue, pool));
            foreach (var item in imports)
                if (!existingImports.Contains(item.Id)) _imports.TryAdd(item.Id, item);
            foreach (var item in checks)
                if (!existingChecks.Contains(item.Id)) _checks.TryAdd(item.Id, item);
            _lastCapture = capturedAt;
            _currentQueue = queue;

            async Task<HashSet<string>> ExistingIds(string table, IEnumerable<long> times)
            {
                var since = times.Select(time => (long?)time).Min();
                var ids = new HashSet<string>();
                if (since is null) return ids;
                await using var command = connection.CreateCommand();
                // Table names are internal constants, never user input.
                command.CommandText = $"SELECT id FROM {table} WHERE time >= $since";
                command.Parameters.AddWithValue("$since", since.Value);
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) ids.Add(reader.GetString(0));
                return ids;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (_samples.Count == 0 && _imports.Count == 0 && _checks.Count == 0 && _telemetry.Count == 0) return;
            await using var connection = await OpenAsync(ct);
            using var transaction = connection.BeginTransaction();
            foreach (var sample in _samples.Values)
                await Execute("INSERT OR IGNORE INTO samples VALUES ($time, $active, $idle, $capacity, $queue)",
                    ("$time", sample.Time), ("$active", sample.Pool?[0]), ("$idle", sample.Pool?[1]),
                    ("$capacity", sample.Pool?[2]), ("$queue", sample.Queue));
            foreach (var item in _imports.Values)
                await Execute("INSERT OR IGNORE INTO imports VALUES ($id, $time, $completed, $bytes)",
                    ("$id", item.Id), ("$time", item.Time), ("$completed", item.Completed ? 1 : 0), ("$bytes", item.Bytes));
            foreach (var item in _checks.Values)
                await Execute("INSERT OR IGNORE INTO health VALUES ($id, $time, $healthy)",
                    ("$id", item.Id), ("$time", item.Time), ("$healthy", item.Healthy ? 1 : 0));
            if (_lastCapture is not null) await Execute("INSERT INTO metadata VALUES ('lastCapture', $time) ON CONFLICT(key) DO UPDATE SET value = excluded.value WHERE metadata.value < excluded.value", ("$time", _lastCapture));
            await FlushTelemetryAsync(connection, transaction, ct);
            transaction.Commit();
            _telemetry.Clear();
            _telemetryCache.Clear();
            _lastSaved = _lastCapture;
            _diskCache.Clear();
            // Clear only after a successful commit; failures leave the complete batch available for retry.
            _samples.Clear();
            _imports.Clear();
            _checks.Clear();

            async Task Execute(string sql, params (string Name, object? Value)[] parameters)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql;
                foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value ?? DBNull.Value);
                await command.ExecuteNonQueryAsync(ct);
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<StatisticsResponse> ReadAsync(string period, DateTime date, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try { return await ReadCoreAsync(period, date, ct); }
        finally { _gate.Release(); }
    }

    private async Task<StatisticsResponse> ReadCoreAsync(string period, DateTime date, CancellationToken ct)
    {
        var start = DateTime.SpecifyKind(date.Date, DateTimeKind.Utc);
        start = period switch
        {
            "week" => start.AddDays(-((int)start.DayOfWeek + 6) % 7),
            "month" => new DateTime(start.Year, start.Month, 1, 0, 0, 0, DateTimeKind.Utc),
            _ => start,
        };
        var end = period switch { "week" => start.AddDays(7), "month" => start.AddMonths(1), _ => start.AddDays(1) };
        var step = period == "day" ? 3600L : 86400L;
        var from = new DateTimeOffset(start).ToUnixTimeSeconds();
        var to = new DateTimeOffset(end).ToUnixTimeSeconds();
        var buckets = Enumerable.Range(0, (int)((to - from) / step))
            .Select(index => new StatisticsBucket { Time = from + index * step }).ToArray();
        var cacheKey = (from, to, step);
        if (!_diskCache.TryGetValue(cacheKey, out var cached))
        {
            await LoadPersisted();
            if (_diskCache.Count >= 8) _diskCache.Clear();
            _diskCache[cacheKey] = buckets.Select(bucket => bucket.Copy()).ToArray();
        }
        else buckets = cached.Select(bucket => bucket.Copy()).ToArray();
        // Merge pending samples while holding the gate so a flush cannot double-count them.
        // Skip a second already on disk (e.g. a restart in the same second).
        var savedSeconds = new HashSet<long>();
        var overlapping = _samples.Keys.Where(time => time >= from && time < to && time <= (_lastSaved ?? long.MinValue)).ToArray();
        if (overlapping.Length > 0)
        {
            await using var connection = await OpenAsync(ct, readOnly: true);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT time FROM samples WHERE time >= $from AND time <= $to";
            command.Parameters.AddWithValue("$from", overlapping.Min());
            command.Parameters.AddWithValue("$to", overlapping.Max());
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) savedSeconds.Add(reader.GetInt64(0));
        }
        foreach (var sample in _samples.Values.Where(item => item.Time >= from && item.Time < to))
        {
            if (savedSeconds.Contains(sample.Time)) continue;
            var bucket = buckets[(int)((sample.Time - from) / step)];
            bucket.AverageQueue = ((bucket.AverageQueue ?? 0) * bucket.Samples + sample.Queue) / (bucket.Samples + 1);
            bucket.Samples++;
            if (sample.Pool is not null)
            {
                bucket.AverageActive = ((bucket.AverageActive ?? 0) * bucket.ConnectionSamples + sample.Pool[0]) / (bucket.ConnectionSamples + 1);
                bucket.ConnectionSamples++;
                bucket.PeakActive = Math.Max(bucket.PeakActive ?? 0, sample.Pool[0]);
            }
        }
        foreach (var item in _imports.Values.Where(item => item.Time >= from && item.Time < to))
        {
            var bucket = buckets[(int)((item.Time - from) / step)];
            if (item.Completed) { bucket.Completed++; bucket.ImportedBytes += item.Bytes; }
            else bucket.Failed++;
        }
        foreach (var item in _checks.Values.Where(item => item.Time >= from && item.Time < to))
        {
            var bucket = buckets[(int)((item.Time - from) / step)];
            bucket.HealthChecks++;
            if (item.Healthy) bucket.HealthyChecks++;
        }
        return new StatisticsResponse(period, from, to, _lastCapture, LastError is not null, buckets,
            _lastSaved, _samples.Count, FlushIntervalSeconds, _currentQueue);

        async Task LoadPersisted()
        {
            await using var connection = await OpenAsync(ct, readOnly: true);
            // A consistent snapshot of all three tables while the collector commits new samples.
            using var transaction = connection.BeginTransaction(deferred: true);
            await Read("""
                SELECT (time - $from) / $step, COUNT(*), AVG(active), MAX(active), AVG(queue), COUNT(active)
                FROM samples WHERE time >= $from AND time < $to GROUP BY 1
                """, reader =>
            {
                var bucket = buckets[reader.GetInt32(0)];
                bucket.Samples = reader.GetInt32(1);
                bucket.AverageActive = reader.IsDBNull(2) ? null : reader.GetDouble(2);
                bucket.PeakActive = reader.IsDBNull(3) ? null : reader.GetInt32(3);
                bucket.AverageQueue = reader.GetDouble(4);
                bucket.ConnectionSamples = reader.GetInt32(5);
            });
            await Read("""
                SELECT (time - $from) / $step, SUM(completed), COUNT(*) - SUM(completed),
                       SUM(CASE WHEN completed = 1 THEN bytes ELSE 0 END)
                FROM imports WHERE time >= $from AND time < $to GROUP BY 1
                """, reader =>
            {
                var bucket = buckets[reader.GetInt32(0)];
                bucket.Completed = reader.GetInt32(1); bucket.Failed = reader.GetInt32(2);
                bucket.ImportedBytes = reader.GetInt64(3);
            });
            await Read("""
                SELECT (time - $from) / $step, COUNT(*), SUM(healthy)
                FROM health WHERE time >= $from AND time < $to GROUP BY 1
                """, reader =>
            {
                var bucket = buckets[reader.GetInt32(0)];
                bucket.HealthChecks = reader.GetInt32(1); bucket.HealthyChecks = reader.GetInt32(2);
            });
            transaction.Commit();
            async Task Read(string sql, Action<SqliteDataReader> apply)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql;
                command.Parameters.AddWithValue("$from", from);
                command.Parameters.AddWithValue("$to", to);
                command.Parameters.AddWithValue("$step", step);
                await using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) apply(reader);
            }
        }

    }
}

public record SampleRecord(long Time, int Queue, int[]? Pool);
public record ImportRecord(string Id, long Time, bool Completed, long Bytes);
public record HealthRecord(string Id, long Time, bool Healthy);
public record StatisticsResponse(string Period, long Start, long End, long? LastCapture,
    bool CollectionError, StatisticsBucket[] Buckets, long? LastSaved, int PendingSamples, int FlushIntervalSeconds, int? CurrentQueue);
public sealed class StatisticsBucket
{
    public StatisticsBucket Copy() => (StatisticsBucket)MemberwiseClone();
    public long Time { get; init; }
    public int Samples { get; set; }
    public int ConnectionSamples { get; set; }
    public double? AverageActive { get; set; }
    public int? PeakActive { get; set; }
    public double? AverageQueue { get; set; }
    public int Completed { get; set; }
    public int Failed { get; set; }
    public long ImportedBytes { get; set; }
    public int HealthChecks { get; set; }
    public int HealthyChecks { get; set; }
}
