using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NzbWebDAV.Database;
using NzbWebDAV.Database.Models;
using NzbWebDAV.Statistics;
using NzbWebDAV.Websocket;
using Serilog;

namespace NzbWebDAV.Services;

public sealed class DashboardStatisticsService(DashboardStatisticsStore store, WebsocketManager websocket) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var initialized = false;
        long? checkpoint = null;
        var lastFlushAttempt = System.Diagnostics.Stopwatch.GetTimestamp();
        var lastEventsScan = System.Diagnostics.Stopwatch.GetTimestamp();
        var scanEvents = true;
        string? captureError = null;
        string? flushError = null;
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            do
            {
                try
                {
                    if (!initialized)
                    {
                        await store.InitializeAsync(stoppingToken);
                        checkpoint = await store.GetCheckpointAsync(stoppingToken);
                        initialized = true;
                    }
                    var now = DateTimeOffset.UtcNow;
                    // Advance the scan checkpoint in memory; the durable checkpoint moves only on flush.
                    var since = checkpoint.HasValue ? DateTimeOffset.FromUnixTimeSeconds(checkpoint.Value).AddDays(-1) : DateTimeOffset.MinValue;
                    await using var db = new DavDatabaseContext();
                    var imports = Array.Empty<ImportRecord>();
                    var checks = Array.Empty<HealthRecord>();
                    // Connections and queue are sampled every second. Reconcile event history separately
                    // to avoid scanning a day's imports/checks on every telemetry tick.
                    scanEvents = scanEvents || System.Diagnostics.Stopwatch.GetElapsedTime(lastEventsScan).TotalSeconds >= 15;
                    if (scanEvents)
                    {
                        var historyQuery = db.HistoryItems.AsNoTracking();
                        var healthQuery = db.HealthCheckResults.AsNoTracking();
                        if (checkpoint.HasValue)
                        {
                            var localSince = since.LocalDateTime;
                            historyQuery = historyQuery.Where(item => item.CreatedAt >= localSince);
                            healthQuery = healthQuery.Where(item => item.CreatedAt >= since);
                        }
                        var history = await historyQuery.Select(item => new { item.Id, item.CreatedAt, item.DownloadStatus, item.TotalSegmentBytes })
                            .ToListAsync(stoppingToken);
                        var health = await healthQuery.Select(item => new { item.Id, item.CreatedAt, item.Result }).ToListAsync(stoppingToken);
                        imports = history.Select(item => new ImportRecord(item.Id.ToString(),
                            new DateTimeOffset(DateTime.SpecifyKind(item.CreatedAt, DateTimeKind.Local)).ToUnixTimeSeconds(),
                            item.DownloadStatus == HistoryItem.DownloadStatusOption.Completed, item.TotalSegmentBytes)).ToArray();
                        checks = health.Select(item => new HealthRecord(item.Id.ToString(), item.CreatedAt.ToUnixTimeSeconds(),
                            item.Result == HealthCheckResult.HealthResult.Healthy)).ToArray();
                    }
                    var queue = await db.QueueItems.CountAsync(stoppingToken);
                    var pool = ParsePool(websocket.GetLastMessage(WebsocketTopic.UsenetConnections));
                    await store.StageAsync(now.ToUnixTimeSeconds(), queue, pool, imports, checks, stoppingToken);
                    if (scanEvents)
                    {
                        checkpoint = now.ToUnixTimeSeconds();
                        lastEventsScan = System.Diagnostics.Stopwatch.GetTimestamp();
                        scanEvents = false;
                    }
                    captureError = null;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception error)
                {
                    captureError = error.Message;
                    Log.Warning(error, "Dashboard statistics capture failed; retrying in one second");
                }

                // Use monotonic time: wall-clock corrections must not postpone persistence.
                var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(lastFlushAttempt).TotalSeconds;
                if (initialized && (elapsed >= store.FlushIntervalSeconds || (store.BufferLimitReached && elapsed >= 60)))
                {
                    lastFlushAttempt = System.Diagnostics.Stopwatch.GetTimestamp();
                    try { await store.FlushAsync(stoppingToken); flushError = null; }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                    catch (Exception error)
                    {
                        flushError = error.Message;
                        Log.Warning(error, "Dashboard statistics flush failed; retaining buffered data for retry");
                    }
                }
                store.LastError = captureError ?? flushError;
            } while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            // The normal stopping token is already cancelled. Allow a bounded final batch commit.
            using var finalFlush = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await store.FlushAsync(finalFlush.Token); }
            catch (Exception error) { Log.Warning(error, "Could not flush dashboard statistics during shutdown"); }
        }
    }

    private static int[]? ParsePool(string? message)
    {
        var parts = message?.Split('|');
        if (parts is not { Length: 6 }) return null;
        if (!int.TryParse(parts[3], out var live) || !int.TryParse(parts[4], out var max) ||
            !int.TryParse(parts[5], out var idle) || live < 0 || max < 0 || idle < 0 || idle > live) return null;
        return [live - idle, idle, max];
    }
}
