import { useState } from "react";
import { useStore } from "../store";
import { Topbar, go } from "../components/Layout";
import { EntityCell, Kpi, StatusPill, fmtDate } from "../components/bits";
import { Gauge, LineChart } from "../components/Charts";
import { Icon } from "../components/Icon";
import { TREND_DAILY, TREND_WEEKLY } from "../mock/data";

export function Dashboard() {
  const { cases, user } = useStore();
  const [range, setRange] = useState<"daily" | "weekly">("daily");
  const approved = cases.filter((c) => c.status === "Approved").length;
  const denied = cases.filter((c) => c.status === "Denied").length;
  const pending = cases.filter((c) => c.status === "AwaitingReview");
  const processing = cases.filter((c) => c.status === "Processing").length;
  const decided = approved + denied;
  const first = user.name.split(" ")[0];

  return (
    <>
      <Topbar title={<>Welcome, {first} <span aria-hidden>👋</span></>} />
      <div className="kpis">
        <Kpi title="Total cases" value={cases.length} chip="+12%" foot="Increased from last month" color="var(--purple)" />
        <Kpi title="Auto-approved" value={approved} chip={decided ? Math.round((approved / decided) * 100) + "%" : "0%"} foot="Of decided cases" color="var(--orange)" />
        <Kpi title="Awaiting review" value={pending.length} foot={processing ? `${processing} still processing` : "Needs an underwriter"} color="var(--cyan)" />
        <Kpi title="Avg. processing time" value="2m 14s" chip="-96%" foot="Down from 2 days" color="var(--green)" />
      </div>

      <div className="grid2">
        <div className="card">
          <div className="card-head">
            <h3>Cases by outcome</h3>
            <div className="legend">
              <span><i style={{ background: "var(--green)" }} />Approved</span>
              <span><i style={{ background: "var(--gold)" }} />Review / Deny</span>
              <select className="select" value={range} onChange={(e) => setRange(e.target.value as "daily" | "weekly")} aria-label="Range">
                <option value="daily">Daily</option><option value="weekly">Weekly</option>
              </select>
            </div>
          </div>
          <LineChart data={range === "daily" ? TREND_DAILY : TREND_WEEKLY} />
        </div>

        <div className="card">
          <div className="card-head"><h3>Pending review</h3><button className="link" onClick={() => go("/cases")}>See all</button></div>
          <div className="stack">
            <div className="back" />
            <div className="front">
              {pending[0] ? (
                <>
                  <div style={{ fontSize: 12, color: "var(--muted)" }}>Next in queue</div>
                  <div style={{ fontWeight: 600, fontSize: 17, margin: "6px 0" }}>{pending[0].entity}</div>
                  <div style={{ fontSize: 12, color: "var(--muted)" }}>{pending[0].id} · risk {pending[0].riskScore ?? "..."}</div>
                  <div style={{ marginTop: 12 }}><StatusPill s={pending[0].status} /></div>
                </>
              ) : <div className="empty">No cases waiting</div>}
            </div>
          </div>
          <button className="btn" onClick={() => pending[0] ? go("/cases/" + pending[0].id) : go("/new")}><Icon name="plus" size={16} />{pending[0] ? "Open case" : "New case"}</button>
        </div>
      </div>

      <div className="grid2">
        <div className="card">
          <div className="card-head"><h3>Latest cases</h3><button className="link" onClick={() => go("/cases")}>See all</button></div>
          <table>
            <thead><tr><th>Entity</th><th>Submitted</th><th>Risk score</th><th>Status</th></tr></thead>
            <tbody>
              {cases.slice(0, 6).map((c) => (
                <tr key={c.id} className="click" onClick={() => go("/cases/" + c.id)}>
                  <td><EntityCell name={c.entity} /></td>
                  <td>{fmtDate(c.submittedAt)}</td>
                  <td>{c.riskScore ?? "..."}</td>
                  <td><StatusPill s={c.status} /></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
        <div className="card">
          <div className="card-head"><h3>Decision mix</h3><b>{cases.length}</b></div>
          <Gauge total={cases.length} caption="Cases" parts={[{ value: approved || 0.001, color: "var(--green)" }, { value: pending.length || 0.001, color: "var(--purple)" }, { value: denied || 0.001, color: "var(--gold)" }]} />
          <div className="legend" style={{ justifyContent: "center" }}>
            <span><i style={{ background: "var(--green)" }} />Approved {approved}</span>
            <span><i style={{ background: "var(--purple)" }} />Review {pending.length}</span>
            <span><i style={{ background: "var(--gold)" }} />Denied {denied}</span>
          </div>
        </div>
      </div>
    </>
  );
}
