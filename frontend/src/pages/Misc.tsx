import { useState } from "react";
import { useStore, deleteAllDocuments } from "../store";
import { Topbar } from "../components/Layout";
import { fmtDate } from "../components/bits";
import { POLICY, DECISION_MODEL_VERSION } from "../logic";

export function AuditLog() {
  const { cases, user } = useStore();
  const rows = cases.flatMap((c) => c.audit.map((a) => ({ ...a, case: c.id }))).sort((a, b) => b.at.localeCompare(a.at));
  return (
    <>
      <Topbar title="Audit log" />
      <div className="card">
        {user.role === "RiskOps" && <div className="note" style={{ marginBottom: 12 }}>Full export is limited to the Auditor and Underwriter roles.</div>}
        <table>
          <thead><tr><th>Time</th><th>Case</th><th>Actor</th><th>Step</th><th>Detail</th></tr></thead>
          <tbody>
            {rows.slice(0, 80).map((a, i) => (
              <tr key={i}><td>{fmtDate(a.at)}</td><td>{a.case}</td><td>{a.actor}</td><td>{a.step}</td><td>{a.detail}</td></tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  );
}

export function Settings() {
  const { cases, user } = useStore();
  const [msg, setMsg] = useState("");
  const [busy, setBusy] = useState(false);
  const wipe = async () => {
    if (!window.confirm(`Permanently delete all ${cases.length} loaded document(s) and their cases? This cannot be undone.`)) return;
    setBusy(true);
    try {
      const r = await deleteAllDocuments();
      setMsg(`Deleted ${r.deleted} document(s).${r.skipped ? ` ${r.skipped} still processing and were skipped.` : ""}`);
    } catch (e) {
      setMsg(e instanceof Error ? e.message : "Delete failed");
    } finally { setBusy(false); }
  };
  return (
    <>
      <Topbar title="Settings" />
      {user.role === "Underwriter" && (
        <div className="card" style={{ marginBottom: 16 }}>
          <div className="card-head"><h3>Data management</h3></div>
          <div className="kv"><span>Loaded documents</span><b>{cases.length}</b></div>
          <div className="note" style={{ margin: "12px 0" }}>Deletes every loaded document together with its case, extracted text and Q&amp;A history. The audit log is kept.</div>
          <button className="btn" disabled={busy || cases.length === 0} onClick={wipe}>Delete all documents</button>
          {msg && <div className="note" style={{ marginTop: 12 }}>{msg}</div>}
        </div>
      )}
      <div className="two">
        <div className="card">
          <div className="card-head"><h3>Deployment</h3></div>
          <div className="kv"><span>Mode</span><b>Mock (no backend)</b></div>
          <div className="kv"><span>Configured modes</span><b>OnPrem | Azure</b></div>
          <div className="kv"><span>Models</span><b>Microsoft Foundry only</b></div>
          <div className="kv"><span>Email</span><b>Excluded</b></div>
        </div>
        <div className="card">
          <div className="card-head"><h3>Decision model {DECISION_MODEL_VERSION}</h3></div>
          <div className="kv"><span>Auto-approve below</span><b>{POLICY.approveBelow}</b></div>
          <div className="kv"><span>Auto-deny above</span><b>{POLICY.denyAbove}</b></div>
          <div className="kv"><span>Minimum extraction confidence</span><b>{POLICY.minConfidence}</b></div>
          <div className="note" style={{ marginTop: 12 }}>Thresholds are policy parameters; changes need Risk Policy Owner approval.</div>
        </div>
      </div>
    </>
  );
}

export function Help() {
  return (
    <>
      <Topbar title="Help center" />
      <div className="card">
        <p style={{ lineHeight: 1.7 }}>
          Upload a digital entity PDF from <b>New case</b>. Watch it move through extraction, compliance, risk synthesis, the scorecard and the decision model.
          Medium-risk cases pause for an underwriter, who can approve, deny or override. Use <b>Ask the document</b> on a case to query only that PDF.
          Switch the demo role in the top bar to try the Underwriter and Auditor views.
        </p>
      </div>
    </>
  );
}
