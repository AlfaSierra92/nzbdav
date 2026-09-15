import { useEffect, useState } from "react";
import { Link, useNavigate, useRevalidator } from "react-router";
import type { Route } from "./+types/route";
import { backendClient } from "~/clients/backend-client.server";
import "./dashboard.css";
import { HistoricalStatistics } from "./historical-statistics";

export function meta() { return [{ title: "Dashboard · Nzb DAV" }]; }

export async function loader({ request }: Route.LoaderArgs) {
    const search = new URL(request.url).searchParams;
    const requestedPeriod = search.get("period") ?? "day";
    const period = ["day", "week", "month"].includes(requestedPeriod) ? requestedPeriod : "day";
    const requestedDate = search.get("date") ?? "";
    const date = /^\d{4}-\d{2}-\d{2}$/.test(requestedDate) ? requestedDate : new Date().toISOString().slice(0, 10);
    const [queue, history, health, statistics] = await Promise.allSettled([
        backendClient.getQueue(100), backendClient.getHistory(100), backendClient.getHealthCheckHistory(1),
        backendClient.getDashboardStatistics(period, date),
    ]);
    return {
        period, date,
        statistics: statistics.status === "fulfilled" ? statistics.value : null,
        queue: queue.status === "fulfilled" ? queue.value : null,
        history: history.status === "fulfilled" ? history.value : null,
        health: health.status === "fulfilled" ? health.value : null,
    };
}

type Connections = { live: number; max: number; idle: number };
function useConnections() {
    const [current, setCurrent] = useState<Connections | null>(null);
    const [samples, setSamples] = useState<number[]>([]);
    const [connected, setConnected] = useState(false);
    const navigate = useNavigate();
    useEffect(() => {
        let socket: WebSocket;
        let timer: ReturnType<typeof setTimeout>;
        let disposed = false;
        let latest: Connections | null = null;
        function connect() {
            socket = new WebSocket(window.location.origin.replace(/^http/, "ws"));
            socket.onopen = () => { setConnected(true); socket.send(JSON.stringify({ cxs: "state" })); };
            socket.onmessage = event => {
                try {
                    const data = JSON.parse(event.data);
                    if (data.Topic !== "cxs" || typeof data.Message !== "string") return;
                    const parts = data.Message.split("|").map(Number);
                    if (parts.length !== 6 || parts.some((n: number) => !Number.isFinite(n) || n < 0)) return;
                    const [, , , live, max, idle] = parts;
                    latest = { live, max, idle: Math.min(idle, live) };
                    setCurrent(latest);
                } catch { /* Ignore malformed messages; keep the last valid state. */ }
            };
            socket.onerror = () => socket.close();
            socket.onclose = event => {
                if (disposed) return;
                latest = null;
                setCurrent(null);
                setSamples([]);
                setConnected(false);
                if (event.code === 1008) { navigate("/login"); return; }
                timer = setTimeout(connect, 3000);
            };
        }
        connect();
        const sampling = setInterval(() => {
            if (latest) setSamples(previous => [...previous.slice(-119), latest!.live - latest!.idle]);
        }, 1000);
        return () => { disposed = true; clearTimeout(timer); clearInterval(sampling); socket.close(); };
    }, [navigate]);
    return { current, samples, connected };
}

const number = (value: number | undefined) => value === undefined ? "—" : value.toLocaleString();
const bytes = (value: number) => {
    if (!Number.isFinite(value) || value <= 0) return "0 B";
    const unit = Math.min(Math.floor(Math.log(value) / Math.log(1024)), 4);
    return `${(value / 1024 ** unit).toFixed(unit ? 1 : 0)} ${["B", "KiB", "MiB", "GiB", "TiB"][unit]}`;
};

export default function Dashboard({ loaderData: { queue, history, health, statistics, period, date } }: Route.ComponentProps) {
    const { current, samples, connected } = useConnections();
    const revalidator = useRevalidator();
    const [liveQueue, setLiveQueue] = useState<number | null>(null);
    useEffect(() => {
        const timer = setInterval(() => {
            if (document.visibilityState === "visible" && revalidator.state === "idle") void revalidator.revalidate();
        }, 15000);
        return () => clearInterval(timer);
    }, [revalidator]);
    const completed = history?.slots.filter(slot => slot.status === "Completed").length ?? 0;
    const failed = history?.slots.filter(slot => slot.status === "Failed").length ?? 0;
    const active = current ? current.live - current.idle : undefined;
    const utilization = current?.max ? Math.min(100, (current.live - current.idle) / current.max * 100) : 0;
    const chartMax = Math.max(1, current?.max ?? 0, ...samples);
    const points = samples.map((value, index) => `${index / 119 * 720},${170 - value / chartMax * 150}`).join(" ");
    const healthCount = health?.stats.reduce((sum, stat) => sum + stat.count, 0);
    const healthy = health?.stats.filter(stat => stat.result === 0).reduce((sum, stat) => sum + stat.count, 0) ?? 0;
    return <main className="dashboard">
        <header className="dash-heading">
            <div><div className="dash-eyebrow">YOUR USENET AT A GLANCE</div><h1>Dashboard<span>.</span></h1><p>Connections, activity and library health. All in one place.</p></div>
            <button className="dash-button" disabled={revalidator.state !== "idle"} onClick={() => void revalidator.revalidate()}>{revalidator.state === "idle" ? "↻ Refresh" : "Refreshing…"}</button>
        </header>
        {(!queue || !history || !health) && <div className="dash-warning" role="status">Some statistics are unavailable. Check your backend connection and refresh.</div>}
        <section className="dash-metrics" aria-label="Overview">
            <Metric label="Active connections" value={number(active)} caption={current ? `of ${current.max} available connections` : "Waiting for Usenet telemetry"} />
            <Metric label="In queue" value={number(liveQueue ?? queue?.noofslots)} caption="NZBs waiting or processing" />
            <Metric label="Successful imports" value={history?.slots.length ? `${Math.round(completed / history.slots.length * 100)}%` : "—"} caption={`Across ${history?.slots.length ?? 0} recent history entries`} />
            <Metric label="Imported size" value={history ? bytes(history.slots.filter(slot => slot.status === "Completed").reduce((sum, slot) => sum + slot.bytes, 0)) : "—"} caption="Completed NZBs in recent history" />
        </section>
        <HistoricalStatistics statistics={statistics} period={period} date={date} onQueueUpdate={setLiveQueue} />
        <div className="dash-grid">
            <section className="dash-panel dash-chart">
                <div className="dash-panel-heading"><div><h2>Connection activity</h2><p>Active connections · last 2 minutes in this session</p></div><span className={`dash-badge ${connected ? "" : "muted"}`}>{connected ? "● Live" : "○ Reconnecting"}</span></div>
                <div className="dash-chart-value">{number(active)} <span>active now</span></div>
                <div className="dash-plot">
                    {samples.length > 1 ? <svg viewBox="0 0 720 190" role="img" aria-label={`Active connections sampled every second. Scale: zero to ${chartMax}.`}>
                        <defs><linearGradient id="dash-fill" x1="0" y1="0" x2="0" y2="1"><stop stopColor="#4ee0bb" stopOpacity=".25"/><stop offset="1" stopColor="#4ee0bb" stopOpacity="0"/></linearGradient></defs>
                        {[20, 70, 120, 170].map(y => <line key={y} x1="0" x2="720" y1={y} y2={y} stroke="#24343f" strokeDasharray="4 5"/>)}
                        <polygon points={`0,170 ${points} ${(samples.length - 1) / 119 * 720},170`} fill="url(#dash-fill)"/>
                        <polyline points={points} fill="none" stroke="#4ee0bb" strokeWidth="3" strokeLinejoin="round"/>
                    </svg> : <div className="dash-empty">{current ? "Collecting connection samples…" : "Waiting for connection data…"}</div>}
                </div><div className="dash-axis"><span>Session samples · every second</span><span>Scale 0–{chartMax}</span></div>
            </section>
            <section className="dash-panel"><div className="dash-panel-heading"><div><h2>Connection pool</h2><p>Live Usenet capacity</p></div></div>
                <div className="dash-ring" style={{ background: `conic-gradient(#4ee0bb ${utilization}%, #23333f 0)` }}><div><strong>{current ? `${Math.round(utilization)}%` : "—"}</strong><span>in use</span></div></div>
                <div className="dash-pool"><span>Active <b>{number(active)}</b></span><span>Idle <b>{number(current?.idle)}</b></span><span>Limit <b>{number(current?.max)}</b></span></div>
            </section>
            <section className="dash-panel"><div className="dash-panel-heading"><div><h2>Recent activity</h2><p>{history ? `Latest ${history.slots.length} of ${history.noofslots} history entries` : "History unavailable"}</p></div><Link to="/queue">View all ↗</Link></div>
                <div className="dash-activity">{history?.slots.slice(0, 5).map(slot => <div className="dash-row" key={slot.nzo_id}><span className="dash-file" aria-hidden="true">▤</span><div className="dash-filename"><strong title={slot.name}>{slot.name}</strong><small>{slot.category || "uncategorized"} · {bytes(slot.bytes)}</small></div><span className={`dash-badge ${slot.status === "Completed" ? "" : "muted"}`}>{slot.status}</span></div>)}{history?.slots.length === 0 && <div className="dash-empty">No imports yet. <Link to="/queue">Add your first NZB →</Link></div>}</div>
            </section>
            <section className="dash-panel"><div className="dash-panel-heading"><div><h2>Library health</h2><p>Integrity checks · last 30 days</p></div><Link to="/health">Details ↗</Link></div>
                <div className="dash-health"><strong>{healthCount ? `${Math.round(healthy / healthCount * 100)}%` : "—"}</strong><span>healthy checks</span></div>
                <div className="dash-summary"><span>Checks recorded<b>{number(healthCount)}</b></span><span>Healthy<b>{health ? number(healthy) : "—"}</b></span><span>Failed recent imports<b>{history ? number(failed) : "—"}</b></span></div>
            </section>
        </div>
        <footer className="dash-footnote">Connections, queue and saved-history view refresh every second. Import and health data refresh every 15 seconds. Import statistics use the latest 100 history entries; imported size is NZB content size, not network traffic.</footer>
    </main>;
}

function Metric({ label, value, caption }: { label: string; value: string; caption: string }) {
    return <article className="dash-panel dash-metric"><h2>{label}</h2><strong>{value}</strong><p>{caption}</p></article>;
}
