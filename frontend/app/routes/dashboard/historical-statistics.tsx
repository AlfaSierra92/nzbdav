import { useEffect, useState } from "react";
import { useSearchParams } from "react-router";
import type { DashboardStatistics } from "~/clients/backend-client.server";

type Props = { statistics: DashboardStatistics | null; period: string; date: string; onQueueUpdate: (queue: number | null) => void };
const formatBytes = (bytes: number) => {
    const unit = bytes > 0 ? Math.min(4, Math.floor(Math.log(bytes) / Math.log(1024))) : 0;
    return `${(bytes / 1024 ** unit).toFixed(unit ? 1 : 0)} ${["B", "KiB", "MiB", "GiB", "TiB"][unit]}`;
};
const formatDate = (time: number, hourly = false) => new Date(time * 1000).toLocaleString("en-GB", {
    timeZone: "UTC", ...(hourly ? { hour: "2-digit", minute: "2-digit" } : { day: "2-digit", month: "short" }),
});

export function HistoricalStatistics({ statistics: initialStatistics, period, date, onQueueUpdate }: Props) {
    const [statistics, setStatistics] = useState(initialStatistics);
    const [refreshFailed, setRefreshFailed] = useState(false);
    useEffect(() => { setStatistics(initialStatistics); }, [initialStatistics]);
    useEffect(() => {
        let disposed = false;
        let busy = false;
        let controller: AbortController | undefined;
        async function refresh() {
            if (busy || document.visibilityState !== "visible") return;
            busy = true;
            controller = new AbortController();
            const timeout = setTimeout(() => controller?.abort(), 10000);
            try {
                const query = new URLSearchParams({ period, date });
                const response = await fetch(`/api/dashboard-statistics?${query}`, { signal: controller.signal, cache: "no-store" });
                if (!response.ok) throw new Error("Statistics unavailable");
                const next: DashboardStatistics = await response.json();
                if (!disposed) {
                    setStatistics(next);
                    setRefreshFailed(false);
                    onQueueUpdate(next.currentQueue);
                }
            } catch {
                if (!disposed) { setRefreshFailed(true); onQueueUpdate(null); }
            } finally { clearTimeout(timeout); busy = false; }
        }
        void refresh();
        const timer = setInterval(() => void refresh(), 1000);
        return () => { disposed = true; clearInterval(timer); controller?.abort(); };
    }, [period, date, onQueueUpdate]);
    const [search, setSearch] = useSearchParams();
    const select = (key: string, value: string) => {
        const next = new URLSearchParams(search);
        next.set(key, value);
        setSearch(next, { preventScrollReset: true });
    };
    const buckets = statistics?.buckets ?? [];
    const completed = buckets.reduce((sum, bucket) => sum + bucket.completed, 0);
    const failed = buckets.reduce((sum, bucket) => sum + bucket.failed, 0);
    const imported = buckets.reduce((sum, bucket) => sum + bucket.importedBytes, 0);
    const checks = buckets.reduce((sum, bucket) => sum + bucket.healthChecks, 0);
    const healthy = buckets.reduce((sum, bucket) => sum + bucket.healthyChecks, 0);
    const connectionSamples = buckets.reduce((sum, bucket) => sum + bucket.connectionSamples, 0);
    const average = connectionSamples ? buckets.reduce((sum, bucket) => sum + (bucket.averageActive ?? 0) * bucket.connectionSamples, 0) / connectionSamples : null;
    const peak = Math.max(1, ...buckets.map(bucket => bucket.peakActive ?? 0));
    const stale = statistics?.lastCapture != null && Date.now() / 1000 - statistics.lastCapture > 10;
    return <section className="dash-panel dash-history" aria-labelledby="history-title">
        <div className="dash-panel-heading dash-history-heading">
            <div><h2 id="history-title">Statistics history</h2><p>Persistent history · calendar periods in UTC · weeks start on Monday</p></div>
            <div className="dash-history-controls">
                <div className="dash-periods" role="group" aria-label="History period">
                    {[["day", "Day"], ["week", "Week"], ["month", "Month"]].map(([value, label]) => <button key={value} type="button" aria-pressed={period === value} onClick={() => select("period", value)}>{label}</button>)}
                </div>
                <label className="dash-date">Date in period<input type="date" value={date} min="1970-01-01" max="9998-12-31" onChange={event => { if (event.target.value) select("date", event.target.value); }} /></label>
            </div>
        </div>
        {!statistics ? <div className="dash-warning" role="status">Statistics history is unavailable. The collector may still be starting; refresh to retry.</div> : <>
            {(statistics.collectionError || stale || refreshFailed) && <div className="dash-warning" role="status">Statistics collection is delayed. Showing available disk and memory data.</div>}
            <p className="dash-period-caption">{formatDate(statistics.start)} – {formatDate(statistics.end - 1)} · {new Date(statistics.start * 1000).getUTCFullYear()}</p>
            <div className="dash-saved-metrics">
                <div><span>Completed / failed</span><strong>{completed} <small>/ {failed}</small></strong></div>
                <div><span>Imported content</span><strong>{formatBytes(imported)}</strong></div>
                <div><span>Average connections</span><strong>{average === null ? "—" : average.toFixed(1)}</strong></div>
                <div><span>Healthy checks</span><strong>{checks ? `${Math.round(healthy / checks * 100)}%` : "—"}<small>{checks} recorded checks</small></strong></div>
            </div>
            <div className="dash-panel-heading"><h3>Active connections</h3><p>Average per {period === "day" ? "hour" : "day"} · peak scale {peak}</p></div>
            <div className="dash-history-bars" role="img" aria-label="Saved average active connections by time. Exact values are available in the table below.">
                {buckets.map(bucket => <div className={`dash-history-column ${bucket.averageActive === null ? "dash-no-sample" : ""}`} key={bucket.time} title={`${formatDate(bucket.time, period === "day")}: ${bucket.averageActive === null ? "No samples" : `${bucket.averageActive.toFixed(1)} average, ${bucket.peakActive} peak`}`}>
                    {bucket.averageActive !== null && <span style={{ height: `${Math.max(1, bucket.averageActive / peak * 100)}%` }} />}
                </div>)}
            </div>
            <div className="dash-axis"><span>{formatDate(statistics.start, period === "day")}</span><span>UTC · gaps indicate no samples</span><span>{formatDate(buckets[buckets.length - 1].time, period === "day")}</span></div>
            <details className="dash-history-details"><summary>View period details</summary><div className="dash-history-table"><table>
                <caption>Saved values for the selected period. A dash means no telemetry samples; import and health counts include recovered records.</caption>
                <thead><tr><th scope="col">Time (UTC)</th><th scope="col">Avg / peak connections</th><th scope="col">Avg queue</th><th scope="col">Completed</th><th scope="col">Failed</th><th scope="col">Content size</th><th scope="col">Healthy / checks</th><th scope="col">Samples</th></tr></thead>
                <tbody>{buckets.map(bucket => <tr key={bucket.time}><th scope="row">{formatDate(bucket.time, period === "day")}</th><td>{bucket.averageActive === null ? "—" : `${bucket.averageActive.toFixed(1)} / ${bucket.peakActive}`}</td><td>{bucket.averageQueue?.toFixed(1) ?? "—"}</td><td>{bucket.completed}</td><td>{bucket.failed}</td><td>{formatBytes(bucket.importedBytes)}</td><td>{bucket.healthyChecks} / {bucket.healthChecks}</td><td>{bucket.samples}</td></tr>)}</tbody>
            </table></div></details>
            <p className="dash-saved-note">Connections and queue sampled every second, including when this page is closed. Disk writes are batched every {Math.round(statistics.flushIntervalSeconds / 60)} minutes; {statistics.pendingSamples} samples are pending in memory and already included above. {statistics.lastSaved ? `Last saved: ${new Date(statistics.lastSaved * 1000).toISOString().replace("T", " ").replace(".000Z", " UTC")}.` : "Waiting for the first disk save."} Unsaved samples may be lost after an abrupt shutdown. Import and health totals include available recovered records; records deleted before collection cannot be recovered.</p>
        </>}
    </section>;
}
