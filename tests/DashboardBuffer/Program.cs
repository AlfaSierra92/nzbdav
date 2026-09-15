using Microsoft.Data.Sqlite;
using NzbWebDAV.Statistics;
using NzbWebDAV.Streams;
using UsenetSharp.Streams;

// Dependency-free integration checks against the real C# store. No application database is opened.
var directory = Path.Combine(Path.GetTempPath(), "dashboard-buffer-" + Guid.NewGuid());
var originalConfig = Environment.GetEnvironmentVariable("CONFIG_PATH");
var originalFlush = Environment.GetEnvironmentVariable("DASHBOARD_STATS_FLUSH_SECONDS");
Environment.SetEnvironmentVariable("CONFIG_PATH", directory);
Environment.SetEnvironmentVariable("DASHBOARD_STATS_FLUSH_SECONDS", null);
var ct = CancellationToken.None;
try
{
    var store = new DashboardStatisticsStore();
    Assert(store.FlushIntervalSeconds == 900, "Default interval must be 15 minutes");
    await store.InitializeAsync(ct);
    await using var observer = await store.OpenAsync(ct, readOnly: true);
    var version = await Version();
    var date = new DateTime(2026, 9, 15, 0, 0, 0, DateTimeKind.Utc);
    var start = new DateTimeOffset(date).ToUnixTimeSeconds();
    var imports = new[] { new ImportRecord("import", start, true, 1024) };
    var health = new[] { new HealthRecord("check", start, true) };
    for (var second = 0; second < 15; second++)
        await store.StageAsync(start + second, second, [second, 0, 40], imports, health, ct);
    var pending = await store.ReadAsync("day", date, ct);
    Assert(pending.PendingSamples == 15 && pending.LastSaved is null, "Samples must stay pending");
    Assert(pending.Buckets[0].Samples == 15 && pending.Buckets[0].AverageActive == 7, "Memory samples must be visible");
    Assert(pending.Buckets[0].Completed == 1 && pending.Buckets[0].HealthChecks == 1, "Pending replay must be deduplicated");
    var pendingAgain = await store.ReadAsync("day", date, ct);
    Assert(pendingAgain.Buckets[0].Samples == 15, "Cached base must not accumulate pending samples twice");
    var firstMonday = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    Assert((await store.ReadAsync("week", firstMonday, ct)).Buckets.Length == 7, "Week bucket count");
    Assert((await store.ReadAsync("month", firstMonday, ct)).Buckets.Length == 30, "Month must not reuse a week cache with the same start");
    Assert(await Version() == version, "Collecting and reading must not commit writes");
    Assert(await store.GetCheckpointAsync(ct) is null, "No durable checkpoint before flush");

    await store.FlushAsync(ct);
    var saved = await store.ReadAsync("day", date, ct);
    Assert(saved.PendingSamples == 0 && saved.LastSaved == start + 14, "Flush must advance durable status");
    Assert(saved.Buckets[0].AverageActive == 7 && saved.Buckets[0].Samples == 15, "Flush must preserve aggregates");
    var afterFlush = await Version();
    Assert(afterFlush != version, "Flush must commit");
    await store.FlushAsync(ct);
    Assert(await Version() == afterFlush, "Empty flush must not write");

    var reopened = new DashboardStatisticsStore();
    await reopened.InitializeAsync(ct);
    // Same-second restart: neither disk nor the merged view may double-count the existing second.
    await reopened.StageAsync(start + 14, 999, [999, 0, 1000], imports, health, ct);
    var replay = await reopened.ReadAsync("day", date, ct);
    Assert(replay.Buckets[0].Samples == 15 && replay.Buckets[0].AverageActive == 7, "Restart collision must retain the saved second");
    Assert(replay.Buckets[0].Completed == 1 && replay.Buckets[0].HealthChecks == 1, "Saved events must not appear twice");
    await reopened.StageAsync(start + 15, 15, null, [], [], ct);

    // Force a transaction error, then verify that all pending data survives for a later retry.
    await using (var writer = await store.OpenAsync(ct))
    {
        await using var command = writer.CreateCommand();
        command.CommandText = "CREATE TRIGGER fail_flush BEFORE INSERT ON samples BEGIN SELECT RAISE(ABORT, 'test failure'); END";
        await command.ExecuteNonQueryAsync(ct);
        var failed = false;
        try { await reopened.FlushAsync(ct); }
        catch (SqliteException) { failed = true; }
        Assert(failed, "Failure injection must fire");
        var retained = await reopened.ReadAsync("day", date, ct);
        Assert(retained.PendingSamples == 2 && retained.LastSaved == start + 14, "Failed flush must retain buffer and checkpoint");
        command.CommandText = "DROP TRIGGER fail_flush";
        await command.ExecuteNonQueryAsync(ct);
    }
    await reopened.FlushAsync(ct);
    var retried = await reopened.ReadAsync("day", date, ct);
    Assert(retried.PendingSamples == 0 && retried.Buckets[0].Samples == 16, "Retry must save every unique second");
    Assert(retried.Buckets[0].ConnectionSamples == 15, "Unknown connections must remain unknown");
    // Exercise real counter concurrency and stream instrumentation without network access.
    var provider = new ProviderTelemetry("test", "Test provider") { Configured = true };
    Parallel.For(0, 1000, _ => { provider.Article(2); provider.Bytes(3); });
    var counters = provider.Capture(1);
    Assert(counters.Articles == 1000 && counters.Bytes == 3000 && counters.OkMilliseconds == 2000, "Concurrent counters must not lose events");
    Assert(provider.Capture(1).Articles == 0, "Sampling must return deltas only");
    var tracker = new UsenetTelemetry();
    using (var activeRead = tracker.BeginRead("file", "client", "127.0.0.1", 100, 1000))
    {
        await using var tracked = new TelemetryYencStream(new FakeYencStream(), provider, activeRead);
        var readCount = await tracked.ReadAsync(new byte[32].AsMemory());
        activeRead.Sent(5);
        var frame = tracker.Capture();
        Assert(readCount == 8 && frame.Reads[0].Position == 105 && frame.ServedBytes == 5, "Decoded and served byte counts must be distinct");
        Assert(frame.Reads[0].Providers["test"] == 8, "Reads must be attributed to their provider");
    }
    Assert(tracker.Capture().Reads.Length == 0, "Disposed requests must disappear");
    provider.Capture(1);
    await using (var cancelled = new TelemetryYencStream(new FakeYencStream(new OperationCanceledException()), provider, null))
    {
        try { await cancelled.ReadAsync(new byte[32].AsMemory()); } catch (OperationCanceledException) { }
    }
    Assert(provider.Capture(1).Errors == 0, "Client cancellation must not count as provider failure");
    await using (var broken = new TelemetryYencStream(new FakeYencStream(new IOException("test")), provider, null))
    {
        for (var attempt=0; attempt<2; attempt++)
            try { await broken.ReadAsync(new byte[32].AsMemory()); } catch (IOException) { }
    }
    Assert(provider.Capture(1).Errors == 1, "Stream failure must be counted once");
    Console.WriteLine("Dashboard buffer integration checks passed.");

    async Task<long> Version()
    {
        await using var command = observer.CreateCommand();
        command.CommandText = "PRAGMA data_version";
        return (long)(await command.ExecuteScalarAsync(ct))!;
    }
}
finally
{
    SqliteConnection.ClearAllPools();
    Environment.SetEnvironmentVariable("CONFIG_PATH", originalConfig);
    Environment.SetEnvironmentVariable("DASHBOARD_STATS_FLUSH_SECONDS", originalFlush);
    if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

sealed class FakeYencStream(Exception? error = null) : YencStream(Stream.Null)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => error is null ? ValueTask.FromResult(Math.Min(8, buffer.Length)) : ValueTask.FromException<int>(error);
}
