import { Children, isValidElement, useEffect, useState, type ReactNode } from "react";

type Props = { storageKey: string; labels: Record<string, string>; children: ReactNode; heading?: ReactNode; sidebar?: ReactNode; note?: ReactNode };

/** Reorders mounted sections without resetting their polling or local controls. */
export function SectionOrder({ storageKey, labels, children, heading, sidebar, note }: Props) {
    const sections = Children.toArray(children).filter(isValidElement);
    const defaults = Object.keys(labels);
    const [order, setOrder] = useState<string[]>(defaults);
    const [editing, setEditing] = useState(false);
    const [notice, setNotice] = useState("");
    useEffect(() => {
        try {
            const saved: unknown = JSON.parse(localStorage.getItem(storageKey) ?? "null");
            if (Array.isArray(saved)) {
                const valid = [...new Set(saved.filter((id): id is string => typeof id === "string" && Object.prototype.hasOwnProperty.call(labels, id)))];
                setOrder([...valid, ...Object.keys(labels).filter(id => !valid.includes(id))]);
            }
        } catch { /* Defaults remain usable when storage is unavailable. */ }
    }, [storageKey]);
    function save(next: string[], message: string) {
        setOrder(next);
        try { localStorage.setItem(storageKey, JSON.stringify(next)); setNotice(message); }
        catch { setNotice(`${message} Browser storage is unavailable; this order will last until you leave the page.`); }
    }
    function move(id: string, offset: number) {
        const next = [...order], from = next.indexOf(id), to = from + offset;
        if (to < 0 || to >= next.length) return;
        [next[from], next[to]] = [next[to], next[from]];
        save(next, `${labels[id]} moved to position ${to + 1} of ${next.length}.`);
    }
    return <div className="dash-section-order">
        <div className="dash-order-toolbar"><button type="button" className="dash-button" aria-expanded={editing} onClick={() => setEditing(!editing)}>{editing ? "Done" : "Reorder sections"}</button>{editing && <><span>Use the arrows to change order. Saved in this browser.</span><button type="button" className="dash-button" onClick={() => save(defaults, "Default order restored.")}>Reset order</button></>}</div>
        <span className={notice.includes("unavailable") ? "dash-warning" : "dash-order-status"} role="status">{notice}</span>
        {heading}
        <div className="dash-overview-layout">
        <div className="dash-overview-main">
        {order.map((id, index) => <div className="dash-ordered-section" key={id}>
            {editing && <div className="dash-order-controls"><strong>{labels[id]}</strong><span>{index + 1} / {order.length}</span><button type="button" className="dash-button" aria-label={`Move ${labels[id]} up`} disabled={index === 0} onClick={() => move(id, -1)}>↑</button><button type="button" className="dash-button" aria-label={`Move ${labels[id]} down`} disabled={index === order.length - 1} onClick={() => move(id, 1)}>↓</button></div>}
            {sections[defaults.indexOf(id)]}
        </div>)}
        {note}
        </div>
        {sidebar}
        </div>
    </div>;
}
