using Microsoft.Data.Sqlite;
using UsenetSharp.Diagnostics;

namespace NzbWebDAV.Statistics;

public sealed partial class DashboardStatisticsStore
{
    private readonly Dictionary<long, ArticleMemoryMinute> _memoryMinutes = new();
    private readonly Dictionary<(long, long, long), ArticleMemoryMinute[]> _memoryCache = new();

    private void RecordArticleMemory(long time, ArticleMemorySnapshot memory)
    {
        var minute = time / 60 * 60;
        if (!_memoryMinutes.TryGetValue(minute, out var value))
            _memoryMinutes[minute] = value = new ArticleMemoryMinute { Time = minute };
        value.Samples++;
        value.AllocatedSum += memory.AllocatedBytes;
        value.BufferedSum += memory.BufferedBytes;
        value.PeakAllocated = Math.Max(value.PeakAllocated, memory.AllocatedBytes);
        value.PeakBuffered = Math.Max(value.PeakBuffered, memory.BufferedBytes);
    }

    private async Task FlushArticleMemoryAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken ct)
    {
        foreach (var item in _memoryMinutes.Values)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO article_memory_minutes VALUES ($time,$samples,$allocated,$buffered,$peakAllocated,$peakBuffered)
                ON CONFLICT(time) DO UPDATE SET samples=samples+excluded.samples,
                    allocated_sum=allocated_sum+excluded.allocated_sum, buffered_sum=buffered_sum+excluded.buffered_sum,
                    peak_allocated=MAX(peak_allocated,excluded.peak_allocated), peak_buffered=MAX(peak_buffered,excluded.peak_buffered)
                """;
            command.Parameters.AddWithValue("$time", item.Time);
            command.Parameters.AddWithValue("$samples", item.Samples);
            command.Parameters.AddWithValue("$allocated", item.AllocatedSum);
            command.Parameters.AddWithValue("$buffered", item.BufferedSum);
            command.Parameters.AddWithValue("$peakAllocated", item.PeakAllocated);
            command.Parameters.AddWithValue("$peakBuffered", item.PeakBuffered);
            await command.ExecuteNonQueryAsync(ct);
        }
    }

    private async Task<ArticleMemoryMinute[]> ReadArticleMemoryAsync(long start, long end, long step, CancellationToken ct)
    {
        var key = (start,end,step);
        if (!_memoryCache.TryGetValue(key, out var saved))
        {
            await using var connection = await OpenAsync(ct, readOnly: true);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT time/$step*$step, SUM(samples), SUM(allocated_sum), SUM(buffered_sum), MAX(peak_allocated), MAX(peak_buffered)
                FROM article_memory_minutes WHERE time >= $start AND time < $end GROUP BY 1 ORDER BY 1
                """;
            command.Parameters.AddWithValue("$start", start);
            command.Parameters.AddWithValue("$end", end);
            command.Parameters.AddWithValue("$step", step);
            await using var reader = await command.ExecuteReaderAsync(ct);
            var rows = new List<ArticleMemoryMinute>();
            while (await reader.ReadAsync(ct)) rows.Add(new ArticleMemoryMinute
            {
                Time=reader.GetInt64(0), Samples=reader.GetInt64(1), AllocatedSum=reader.GetInt64(2),
                BufferedSum=reader.GetInt64(3), PeakAllocated=reader.GetInt64(4), PeakBuffered=reader.GetInt64(5),
            });
            saved = rows.ToArray();
            if (_memoryCache.Count >= 5) _memoryCache.Clear();
            _memoryCache[key] = saved;
        }
        var combined = saved.ToDictionary(row => row.Time, row => row.Copy());
        foreach (var row in _memoryMinutes.Values.Where(row => row.Time >= start && row.Time < end))
        {
            var time = row.Time / step * step;
            if (!combined.TryGetValue(time, out var target)) combined[time] = target = new ArticleMemoryMinute { Time=time };
            target.Samples += row.Samples;
            target.AllocatedSum += row.AllocatedSum;
            target.BufferedSum += row.BufferedSum;
            target.PeakAllocated = Math.Max(target.PeakAllocated, row.PeakAllocated);
            target.PeakBuffered = Math.Max(target.PeakBuffered, row.PeakBuffered);
        }
        return combined.Values.OrderBy(row => row.Time).ToArray();
    }
}

public sealed class ArticleMemoryMinute
{
    public long Time { get; set; }
    public long Samples { get; set; }
    public long AllocatedSum { get; set; }
    public long BufferedSum { get; set; }
    public long PeakAllocated { get; set; }
    public long PeakBuffered { get; set; }
    public double? AverageAllocated => Samples == 0 ? null : (double)AllocatedSum / Samples;
    public double? AverageBuffered => Samples == 0 ? null : (double)BufferedSum / Samples;
    public ArticleMemoryMinute Copy() => (ArticleMemoryMinute)MemberwiseClone();
}
