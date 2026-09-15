# Dashboard

`/dashboard` is the default landing page. It includes a live overview and a separate **Statistics history** section.

## Persistent history

The backend samples connections and queue length once per second even when no browser is open. Import and health records are reconciled every 15 seconds. Storage is isolated in:

```text
CONFIG_PATH/
├── db.sqlite                       # existing application database, unchanged
└── dashboard/
    ├── statistics-v1.sqlite         # dashboard-only archive
    ├── statistics-v1.sqlite-wal     # SQLite may create this while running
    └── statistics-v1.sqlite-shm     # SQLite may create this while running
```

`CONFIG_PATH` defaults to `/config`. The existing persistent config volume therefore also preserves dashboard history across container replacement. No schema changes or migrations are added to the application database. Returning to the same application version without these dashboard changes leaves the archive unused; restoring the dashboard resumes collection. This does not override unrelated upstream database migration restrictions.

All archive updates use a single transaction per batch: all buffered second samples, newly observed imports, health checks and the durable collection checkpoint commit together. Import/check IDs prevent duplicates during retries, restarts and overlap scans. An interrupted transaction rolls back. Disk or database failures are logged and retried without stopping the application; failed batches remain in memory and the dashboard indicates delayed collection or persistence. Corrupt files are not automatically replaced.

The archive contains counters, timestamps and record IDs; no filenames, provider credentials or NZB content. Data is retained without automatic expiry. File size will grow over time (up to 86,400 connection/queue samples per day plus one row per import/check). For a file-copy backup, stop the backend and copy the entire `dashboard` directory; do not copy only the SQLite main file while it is running in WAL mode.

## Reduced disk writes

Collection and persistence have different cadences:

- **Collect once per second** into memory, retaining each second's sample, including idle/zero values. No loss of sampling resolution or change to day/week/month aggregation.
- **Flush every 15 minutes by default**, using one SQLite transaction for the whole batch. In steady operation this is approximately 96 scheduled commits/day rather than 1,440 (about 93% fewer commits). This is a reduction in transaction frequency, not a measured reduction in physical bytes written by the filesystem.
- A **regular shutdown** attempts a final flush with a separate 10-second cancellation budget. An empty buffer causes no write.
- A buffer of **10,000 records** triggers an earlier flush, with at least one minute between attempts. This also applies to large first-run backfills. If storage remains unavailable, data is retained in memory and memory usage can grow; no samples are silently discarded.
- Schema initialization happens once per successful process startup. Collection, API reads and ID deduplication use read-only archive connections. Second samples already on disk are ignored, and an unchanged checkpoint is not rewritten.
- The existing SQLite WAL and synchronous durability settings are preserved. No periodic vacuum, full-file rewrite or forced checkpoint was added.

Configure the flush interval with an environment variable (seconds):

```text
DASHBOARD_STATS_FLUSH_SECONDS=900
```

The default is 900 seconds; values are clamped to 60–86,400 seconds (1 minute–24 hours). Connection and queue sampling remains once per second. Larger intervals reduce commits further while increasing the amount of unsaved data at risk and memory usage. Flush scheduling uses monotonic time, so system-clock changes do not postpone saves.

The API and dashboard merge the persisted archive with pending memory samples under the same lock used for flushes, avoiding double counts. The interface distinguishes **last collected** (`lastCapture`, used to detect stalled collection) from **last saved** (`lastSaved`, displayed in the footer) and shows the number of pending samples. Reading or refreshing the dashboard does not trigger persistence. Persisted period aggregates are cached in memory (up to eight date ranges, invalidated after each successful flush), then merged with pending samples. Second-by-second view refreshes therefore do not scan the full archive each second. Normal telemetry-only staging does not open the archive at all; the queue count is read from the application database. Events are reconciled separately every 15 seconds.

**Abrupt termination or power loss can lose the unflushed connection/queue samples**—normally up to about 15 minutes at the default interval, or longer if persistence is failing. Imports/checks can be recovered on restart only if they remain in the original application database. A regular shutdown flush is best-effort; a forced kill or expired shutdown timeout cannot guarantee it. Eliminating that loss window would require more frequent durable writes.

The archive path and schema remain unchanged, so existing saved dashboard statistics are preserved and the application database remains independent. Existing minute-resolution records remain individual observations; they are not expanded into invented second samples. Averages use the actual observed samples, so ranges spanning both sampling rates contain more observations from the new rate. Second-resolution storage can produce up to 60 times as many sample rows as the previous minute-resolution collector: batched commit frequency remains unchanged, but stored data volume increases. Missed ticks during database stalls, long backfills, persistence or shutdown are left as gaps rather than fabricated.

## Day, week and month

Use **Day / Week / Month** and **Date in period** to browse saved periods. The selection is preserved in the URL (`?period=week&date=2026-09-15`).

- **Day:** the selected UTC calendar day, with 24 hourly buckets.
- **Week:** Monday through Sunday containing the selected date, with seven daily buckets.
- **Month:** the selected calendar month, with one bucket per day, including leap days.
- All ranges are start-inclusive and end-exclusive. UTC avoids ambiguous daylight-saving boundaries; the date and timezone are explicitly shown.

Each period provides completed/failed imports, completed content size, average active connections and healthy-check percentage. The expandable table also includes sampled peak connections, average queue length, health-check counts and sample counts. Period connection averages are weighted by the number of valid connection samples. Unknown telemetry remains null and graph gaps remain empty rather than becoming zero.

The first capture imports all import/check records still available in the existing application database. Later captures scan since the last successful in-memory capture with a one-day overlap. After restart, scanning resumes from the last durable checkpoint. Previously archived entries remain available after the original history is cleared. Because import/check reconciliation polls every 15 seconds, records deleted before reconciliation cannot be recovered. Existing health aggregates may therefore contain checks whose individual records are no longer available for backfill. No connection or queue telemetry is reconstructed for time before collection started or while the backend was stopped. Sampling measures observed values, not every intermediate connection peak. History timestamps use the existing server-local `HistoryItem.CreatedAt` convention and are normalized to UTC; health timestamps already have an offset.

Imported size is NZB content size, **not network traffic**. Health percentage describes recorded checks, not the percentage of the whole library verified healthy.

## Implementation

- `DashboardStatisticsService`: hosted backend collector, independent of the frontend.
- `DashboardStatisticsStore`: separate SQLite store using the SQLite dependency already provided by Entity Framework; no additional packages.
- `/api/dashboard-statistics?period=day&date=YYYY-MM-DD`: authenticated API, using the existing API-key mechanism. Invalid periods/dates return HTTP 400.
- Live connection chart: existing `cxs` WebSocket state, sampled every second (120 points) for the last two minutes of the browser session. This remains separate from the durable second samples.
- Connections, queue count and the archive view refresh every second while visible. The archive uses a dedicated background request with no overlapping requests, a timeout, and cancellation on navigation/unmount; it does not reload the page. Recent import history and the health overview refresh every 15 seconds. The top overview still uses the latest 100 import records and the existing 30-day health summary; selected-period values are in **Statistics history**.

## Validation

Executed:

```sh
python3 -m unittest discover -s tests -v
```

Nine tests exercise the actual SQL extracted from the store: file reopening, idempotent replay, transaction rollback, period boundaries, missing versus zero connection telemetry, import/health aggregation, 15 minute samples committed together, replays that do not rewrite stored values, and 900 distinct second samples saved in one batch. They do not execute the C# service or React components.

Node/npm and the .NET SDK are unavailable in the implementation environment. Compilation, EF query translation, the C# buffer integration checks and browser integration still need verification in a configured environment:

```sh
# From the repository root (real store, temporary isolated archive)
dotnet run --project tests/DashboardBuffer/DashboardBuffer.csproj
# From backend/
dotnet build
# From frontend/
npm ci
npm run typecheck
npm run build
```

The C# integration executable checks that collection/read operations do not commit, pending second samples remain visible, cached reads do not double-count samples or confuse week/month ranges with the same start, empty flushes do not write, aggregates survive flush and restart, and a forced transaction failure retains the buffer for retry. It has been added but not executed in this environment.

Integration checks: verify the final flush on regular shutdown and the expected loss window on forced termination; restart the backend and verify that archive totals remain unchanged; clear original history after capture and verify archive totals remain; switch day/week/month across a year boundary and February; stop/restart collection and inspect graph gaps; test read-only/full storage; run the original application against the same config directory; check desktop/mobile navigation and date selection.

`docs/mockups/usenet-dashboard.png` is the original illustrative mockup with sample data. It predates the saved-history controls and is not a running-app screenshot.
