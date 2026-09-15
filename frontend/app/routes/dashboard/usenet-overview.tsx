import { useEffect, useState } from "react";
import "./usenet-overview.css";

type Aggregate = { time: number; provider: string; name: string; articles: number; bytes: number; misses: number; errors: number; retries: number; okMilliseconds: number; outageSeconds: number; observedSeconds: number; servedBytes: number; peakBytesPerSecond: number; hardFailures: number };
type ProviderTick = { id: string; name: string; configured: boolean; articlesPerSecond: number; bytesPerSecond: number; outage: boolean };
type ActiveRead = { id: string; name: string; client: string; address: string; position: number; length: number | null; sentBytes: number; bytesPerSecond: number; providers: Record<string, number> };
type Overview = { range: string; start: number; end: number; step: number; live: { time: number; providers: ProviderTick[]; reads: ActiveRead[]; servedBytesPerSecond: number } | null; totals: Aggregate; points: Aggregate[]; providers: { totals: Aggregate; points: Aggregate[] }[]; heatmap: { time: number; articles: number }[]; errorsLastMinute: number; articlesLastMinute: number; lastSaved: number | null; collectionError: boolean; articleRamBytes: number | null; articleRamCapBytes: number | null };
const n = (value: number) => value.toLocaleString("en-GB", { maximumFractionDigits: 1 });
const size = (bytes: number) => { const unit = bytes > 0 ? Math.min(4, Math.floor(Math.log(bytes) / Math.log(1000))) : 0; return `${n(bytes / 1000 ** unit)} ${["B", "KB", "MB", "GB", "TB"][unit]}`; };
const stamp = (time: number) => new Date(time * 1000).toLocaleString("en-GB", { timeZone: "UTC", month: "short", day: "2-digit", hour: "2-digit", minute: "2-digit" });

export function UsenetOverview() {
    const [range, setRange] = useState("24h");
    const [data, setData] = useState<Overview | null>(null);
    const [failed, setFailed] = useState(false);
    useEffect(() => {
        let disposed = false, busy = false;
        let controller: AbortController | undefined;
        async function refresh() {
            if (busy || document.visibilityState !== "visible") return;
            busy = true;
            controller = new AbortController();
            const timeout = setTimeout(() => controller?.abort(), 10000);
            try {
                const response = await fetch(`/api/usenet-statistics?range=${range}`, { signal: controller.signal, cache: "no-store" });
                if (!response.ok) throw new Error("Unavailable");
                const next: Overview = await response.json();
                if (!disposed) { setData(next); setFailed(false); }
            } catch { if (!disposed) setFailed(true); }
            finally { busy = false; clearTimeout(timeout); }
        }
        setData(null);
        void refresh();
        const timer = setInterval(() => void refresh(), 1000);
        return () => { disposed = true; clearInterval(timer); controller?.abort(); };
    }, [range]);
    const live = data?.live;
    const heatmapPeak = Math.max(1, ...(data?.heatmap ?? []).map(point => point.articles));
    const stale = failed || data?.collectionError || (live && Date.now() / 1000 - live.time > 10);
    const liveRate = live?.providers.reduce((sum, p) => sum + p.bytesPerSecond, 0) ?? 0;
    const articlesRate = live?.providers.reduce((sum, p) => sum + p.articlesPerSecond, 0) ?? 0;
    return <section className="usenet-overview" aria-label="Usenet overview">
        <div className="usenet-heading"><div><h2>Usenet overview</h2><p>Real traffic. Updated every second.</p></div><div className="dash-periods" role="group" aria-label="Usenet time range">{["1h", "24h", "7d", "30d", "all"].map(value => <button type="button" key={value} aria-pressed={range === value} onClick={() => setRange(value)}>{value === "all" ? "All" : value}</button>)}</div></div>
        {stale && <div className="dash-warning" role="status">Telemetry is delayed or unavailable. Values below may be stale.</div>}
        <div className="usenet-layout"><div className="usenet-main">
            <div className="usenet-kpis dash-panel">
                <Kpi label="Active reads" value={live ? n(live.reads.length) : "—"} note="WebDAV / preview requests" />
                <Kpi label="Articles / s" value={live ? n(articlesRate) : "—"} note={`${n(data?.articlesLastMinute ?? 0)} in the last 60 s`} />
                <Kpi label="Read throughput" value={live ? `${size(liveRate)}/s` : "—"} note="Decoded NNTP bytes read" />
                <Kpi label="Article RAM" value={data?.articleRamBytes != null ? size(data.articleRamBytes) : "—"} note="RAM / cap not exposed by NNTP library" />
                <Kpi label="Fetch errors" value={live ? n(data?.errorsLastMinute ?? 0) : "—"} note="Terminal failures · last 60 s" />
            </div>
            <section className="dash-panel"><div className="usenet-heading"><div><h2>Activity</h2><p>Articles per minute · {range === "all" ? "all recorded history" : `last ${range}`} · UTC</p></div><div className="usenet-totals">{[["ARTICLES", n(data?.totals.articles ?? 0)], ["MISSES", n(data?.totals.misses ?? 0)], ["ERRORS", n(data?.totals.hardFailures ?? 0)], ["SERVED", size(data?.totals.servedBytes ?? 0)]].map(([label, value]) => <span key={label}><small>{label}</small><strong>{value}</strong></span>)}</div></div>
                {data?.points.length ? <><ActivityChart data={data} /><div className="dash-axis"><span>{stamp(data.start)}</span><span>{stamp(data.end)} UTC</span></div><p className="usenet-note">Peak sampled read: {size(data.totals.peakBytesPerSecond)}/s · red marks: terminal errors. Hover over a point for details.</p></> : <div className="dash-empty">{data ? "No telemetry recorded in this period yet." : "Loading telemetry…"}</div>}
            </section>
            <section className="dash-panel"><div className="usenet-heading"><div><h2>Providers</h2><p>Per-provider activity in the selected period</p></div></div><div className="usenet-provider-table"><table><thead><tr>{["Provider", "Activity", "Outages", "Articles", "Read", "Share", "MB/s now", "Errors", "Retries", "Avg OK ms"].map(label => <th key={label} scope="col">{label}</th>)}</tr></thead><tbody>{data?.providers.map(({ totals: p, points }) => {
                    const now = live?.providers.find(provider => provider.id === p.provider);
                    const share = data?.totals.bytes ? p.bytes / data.totals.bytes * 100 : 0;
                    return <tr key={p.provider}><th scope="row"><span className={`usenet-dot ${now?.outage ? "bad" : ""}`} />{p.name}{!now?.configured && <small>historical</small>}</th><td><Spark points={points} field="articles" /></td><td title="Time observed with the circuit breaker open">{n(p.outageSeconds)} s</td><td>{n(p.articles)}</td><td>{size(p.bytes)}</td><td><div className="usenet-share"><span style={{ width: `${share}%` }} /><b>{n(share)}%</b></div></td><td>{now ? n(now.bytesPerSecond / 1e6) : "—"}</td><td title="Failed attempts, including subsequently recovered errors"><Spark points={points} field="errors" color="#fb7185" />{n(p.errors)}</td><td title="Retries on the same provider"><Spark points={points} field="retries" color="#e6b65c" />{n(p.retries)}</td><td title="Successful BODY/ARTICLE response latency, excluding pool wait and subsequent body reads">{p.articles ? n(p.okMilliseconds / p.articles) : "—"}</td></tr>;
                })}</tbody></table>{data?.providers.length === 0 && <div className="dash-empty">Provider activity will appear after the first sample.</div>}</div></section>
            <section className="dash-panel"><div className="usenet-heading"><div><h2>Activity heatmap</h2><p>Articles per {range === "all" ? "day" : "hour"} · UTC</p></div>{data?.heatmap.length ? <p>Peak: {stamp(data.heatmap.reduce((a,b) => a.articles >= b.articles ? a : b).time)}</p> : null}</div><div className="usenet-heatmap">{data?.heatmap.map(point => <span key={point.time} tabIndex={0} title={`${stamp(point.time)} UTC · ${n(point.articles)} articles`} style={{ background: `rgba(67, 218, 189, ${.08 + .92 * point.articles / heatmapPeak})` }} />)}</div><p className="usenet-note">Only observed intervals are shown; no data is reconstructed for downtime.</p></section>
        </div><aside className="dash-panel usenet-now"><div className="usenet-heading"><h2><span className="usenet-dot" />Right now</h2><span className="dash-badge">{live?.reads.length ?? 0} active</span></div>{live?.reads.map(read => <article key={read.id} className="usenet-read"><h3 title={read.name}>{read.name}</h3><p title={read.client}>{read.client.includes("Plex") ? "Plex" : read.client.includes("Jellyfin") ? "Jellyfin" : read.client.slice(0, 45) || "WebDAV client"} · {read.address}</p><small>{read.id.slice(0, 8)}</small><progress aria-label={`Read offset for ${read.name}`} max={read.length ?? Math.max(1, read.position)} value={read.length != null ? Math.min(read.position, read.length) : undefined} /><div className="usenet-read-size"><span>at {size(read.position)} / {read.length ? size(read.length) : "unknown"}</span><strong>{size(read.bytesPerSecond)}/s</strong></div><div className="usenet-read-providers">{Object.entries(read.providers).map(([id, bytes]) => <span key={id} title={`${size(bytes)} decoded bytes fetched for this request`}>{live?.providers.find(p=>p.id===id)?.name ?? id} · {size(bytes)}</span>)}</div></article>)}{!live?.reads.length && <div className="dash-empty">{live ? "No active reads." : "Waiting for live reads…"}</div>}<p className="usenet-note">Client details remain in memory. Progress is the current byte offset, including range requests.</p></aside></div>
        <p className="usenet-note">Sampled every second; minute totals persisted in batches with the existing 15-minute save interval. {data?.lastSaved ? `Last disk save: ${stamp(data.lastSaved)} UTC.` : "Waiting for first disk save."} NNTP bytes and bytes served to clients are measured separately.</p>
    </section>;
}
function Kpi({ label, value, note }: { label: string; value: string; note: string }) { return <div><span>{label}</span><strong>{value}</strong><small>{note}</small></div>; }
function Spark({ points, field, color = "#43dabd" }: { points: Aggregate[]; field: "articles" | "errors" | "retries"; color?: string }) {
    const compact = points.filter((_, i) => i % Math.max(1, Math.ceil(points.length / 80)) === 0);
    const max = Math.max(1, ...compact.map(p=>p[field]));
    return <svg className="usenet-spark" viewBox="0 0 100 25" role="img" aria-label={`${field} trend`}><polyline fill="none" stroke={color} strokeWidth="1" points={compact.map((p,i)=>`${i / Math.max(1,compact.length-1)*100},${24-p[field]/max*22}`).join(" ")} /></svg>;
}
function ActivityChart({ data }: { data: Overview }) {
    const max = Math.max(1, ...data.points.map(p=>p.articles * 60 / data.step));
    const x = (t: number) => (t-data.start) / Math.max(1,data.end-data.start)*900;
    const y = (p: Aggregate) => 175-p.articles*60/data.step/max*155;
    const path = data.points.map((p,i) => `${i===0 || p.time-data.points[i-1].time>data.step ? "M" : "L"}${x(p.time)},${y(p)}`).join(" ");
    return <div className="usenet-chart"><div className="usenet-y"><span>{n(max)}</span><span>{n(max/2)}</span><span>0</span></div><svg viewBox="0 0 900 190" role="img" aria-label={`Article activity, peak ${n(max)} per minute. Gaps indicate missing samples.`}>{[20,97,175].map(y=><line key={y} x1="0" x2="900" y1={y} y2={y} stroke="#263641" />)}<path d={path} stroke="#43dabd" strokeWidth="1.3" fill="none" />{data.points.map(p=><g key={p.time}><circle cx={x(p.time)} cy={y(p)} r="5" fill="transparent"><title>{stamp(p.time)} UTC: {n(p.articles)} articles, {n(p.misses)} misses, {n(p.hardFailures)} terminal errors, {size(p.servedBytes)} served</title></circle>{p.hardFailures>0 && <circle cx={x(p.time)} cy="175" r="2" fill="#fb7185"><title>{n(p.hardFailures)} terminal errors</title></circle>}</g>)}</svg></div>;
}
