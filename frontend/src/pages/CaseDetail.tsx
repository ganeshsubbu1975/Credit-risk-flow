import { useEffect, useRef, useState } from "react";
import { REAL, askQuestion, canReview, downloadRecord, reviewCase, useStore } from "../store";
import { Topbar, go } from "../components/Layout";
import { Icon } from "../components/Icon";
import { StatusPill, fmtDate } from "../components/bits";
import { money, pct } from "../logic";
import { REFUSALS } from "../mock/qa";
import type { LoanCase } from "../types";

type Tab = "progress" | "analysis" | "review" | "ask" | "record" | "audit";

export function CaseDetail({ id }: { id: string }) {
  const { cases } = useStore();
  const c = cases.find((x) => x.id === id);
  const [tab, setTab] = useState<Tab>("progress");
  if (!c) return <><Topbar title="Case not found" /><button className="btn ghost" onClick={() => go("/cases")}>Back to cases</button></>;

  const tabs: [Tab, string][] = [["progress", "Progress"], ["analysis", "Analysis"], ["review", "Underwriter review"], ["ask", "Ask the document"], ["record", "Decision record"], ["audit", "Audit"]];
  return (
    <>
      <Topbar title={<>{c.entity} <span style={{ color: "var(--muted)", fontWeight: 400, fontSize: 16 }}>{c.id}</span></>} />
      <div style={{ display: "flex", gap: 10, alignItems: "center", marginBottom: 16, flexWrap: "wrap" }}>
        <StatusPill s={c.status} />
        <span style={{ color: "var(--muted)" }}>{c.fileName} · submitted {fmtDate(c.submittedAt)} by {c.submittedBy}</span>
        {c.riskScore !== null && <span className="pill purple">Risk score {c.riskScore}</span>}
      </div>
      <div className="tabs" role="tablist">
        {tabs.map(([k, l]) => <button key={k} role="tab" aria-selected={tab === k} className={"tab" + (tab === k ? " on" : "")} onClick={() => setTab(k)}>{l}</button>)}
      </div>
      {tab === "progress" && <Progress c={c} />}
      {tab === "analysis" && <Analysis c={c} />}
      {tab === "review" && <Review c={c} />}
      {tab === "ask" && <Ask c={c} />}
      {tab === "record" && <Record c={c} />}
      {tab === "audit" && <Audit c={c} />}
    </>
  );
}

function Progress({ c }: { c: LoanCase }) {
  return (
    <div className="card">
      {c.steps.map((s, i) => (
        <div className="step" key={s.key}>
          <div className={"step-ico " + s.status}>{s.status === "done" ? <Icon name="check" size={14} /> : i + 1}</div>
          <div className="step-body">
            <div className="step-title"><span>{s.label}</span><span className="pill grey">{s.status}</span></div>
            <div className="step-meta">
              {s.summary ?? (s.status === "pending" ? "Waiting" : "")}
              {s.model && <> · {s.model}</>}
              {s.latencyMs !== undefined && <> · {(s.latencyMs / 1000).toFixed(1)}s</>}
              {s.tokens !== undefined && <> · {s.tokens.toLocaleString()} tokens</>}
            </div>
          </div>
        </div>
      ))}
    </div>
  );
}

function Analysis({ c }: { c: LoanCase }) {
  const m = c.metrics;
  if (c.riskScore === null) return <div className="card empty">Analysis appears once the scorecard step completes.</div>;
  return (
    <div className="two">
      <div className="card">
        <div className="card-head"><h3>Extracted metrics</h3></div>
        {([
          ["Gross receipts (L1a)", m.grossReceipts], ["Returns & allowances (L1b)", m.returns], ["Net revenue (L1c)", m.netRevenue],
          ["Officer compensation (L7)", m.officerComp], ["Depreciation & amortization (L14)", m.depreciation], ["Interest expense (L13)", m.interest],
          ["Taxable income (L21)", m.taxableIncome], ["Short-term debt (L17d)", m.shortDebt], ["Long-term debt (L19d)", m.longDebt],
        ] as [string, number][]).map(([k, v]) => <div className="kv" key={k}><span>{k}</span><b>{money(v)}</b></div>)}
      </div>
      <div style={{ display: "grid", gap: 16, alignContent: "start" }}>
        <div className="card">
          <div className="card-head"><h3>EBITDA and leverage</h3></div>
          <div className="kv"><span>EBITDA (with officer add-back)</span><b>{money(c.ebitda)}</b></div>
          <div className="kv"><span>EBITDA excluding officer comp</span><b>{money(c.ebitdaExOfficer)}</b></div>
          <div className="kv"><span>Debt / EBITDA</span><b>{c.leverage.toFixed(2)}x</b></div>
          <div className="kv"><span>EBITDA margin</span><b>{pct(c.ebitdaMargin)}</b></div>
          <div className="kv"><span>EBITDA / interest</span><b>{c.interestCover.toFixed(1)}x</b></div>
          <div className="note" style={{ marginTop: 10 }}>The benchmark formula adds officer compensation back, which is not standard EBITDA.</div>
        </div>
        <div className="card">
          <div className="card-head"><h3>Scorecard (creditworthiness)</h3></div>
          {c.pillars.map((p) => (
            <div key={p.name} style={{ marginBottom: 10 }}>
              <div className="kv" style={{ border: 0, padding: 0 }}><span>{p.name} · {Math.round(p.weight * 100)}%</span><b>{p.display}{p.score !== null && ` → ${p.score.toFixed(0)}`}</b></div>
              <div className="bar"><i style={{ width: (p.score ?? 0) + "%", background: p.score === null ? "var(--line)" : "var(--green)" }} /></div>
            </div>
          ))}
          <div className="note">Risk score = 100 − creditworthiness = <b>{c.riskScore}</b>. Unavailable pillars are excluded and weights renormalised.</div>
        </div>
        <div className="card">
          <div className="card-head"><h3>Compliance flags</h3></div>
          {c.flags.length === 0 ? <div className="empty">No flags raised</div> : c.flags.map((f) => (
            <div className="kv" key={f.id}><span><span className={"pill " + (f.severity === "Low" ? "grey" : "gold")}>{f.severity}</span> {f.rule}</span><b style={{ maxWidth: "55%", textAlign: "right", fontWeight: 500 }}>{f.evidence}</b></div>
          ))}
        </div>
      </div>
    </div>
  );
}

function Review({ c }: { c: LoanCase }) {
  const { user } = useStore();
  const [reason, setReason] = useState("");
  const [err, setErr] = useState("");
  const dr = c.decisionResult;
  const act = async (a: "Approve" | "Deny" | "Override") => {
    if (a === "Override" && reason.trim().length < 5) return setErr("An override needs a reason of at least 5 characters.");
    setErr("");
    try { await reviewCase(c.id, a, reason.trim()); } catch (e) { setErr(e instanceof Error ? e.message : "The decision could not be saved."); }
  };
  const allowed = canReview(c);
  return (
    <div className="two">
      <div className="card">
        <div className="card-head"><h3>AI reasoning</h3></div>
        {dr ? (
          <>
            <div className="kv"><span>AI recommendation</span><b>{c.llmRecommendation}</b></div>
            <div className="kv"><span>Decision model</span><b>{dr.modelVersion} · rule {dr.rule}</b></div>
            <div className="kv"><span>Outcome</span><b>{dr.decision}</b></div>
            <div className="kv"><span>Reason codes</span><b>{dr.reasonCodes.join(", ")}</b></div>
            <div className="note" style={{ marginTop: 12 }}>{c.rationale}<br />{dr.explanation}</div>
          </>
        ) : <div className="empty">Waiting for the decision model...</div>}
      </div>
      <div className="card">
        <div className="card-head"><h3>Your decision</h3></div>
        {c.humanDecision ? (
          <div className="note">{c.humanDecision.by} chose <b>{c.humanDecision.action}</b> (final: {c.humanDecision.finalDecision}) on {fmtDate(c.humanDecision.at)}.<br />Reason: {c.humanDecision.reason || "none given"}</div>
        ) : c.status !== "AwaitingReview" ? (
          <div className="note">This case did not need human review.</div>
        ) : (
          <>
            {!allowed && (
              <div className="note warn" style={{ marginBottom: 12 }}>
                {user.role !== "Underwriter" ? "Switch the role to Underwriter to act on this case." : "Separation of duties: you submitted this case, so another underwriter must decide."}
              </div>
            )}
            <textarea className="field" rows={4} placeholder="Reason (required for an override)" value={reason} onChange={(e) => setReason(e.target.value)} disabled={!allowed} />
            {err && <div className="err" role="alert">{err}</div>}
            <div style={{ display: "flex", gap: 10, marginTop: 12, flexWrap: "wrap" }}>
              <button className="btn green" disabled={!allowed} onClick={() => act("Approve")}>Approve</button>
              <button className="btn red" disabled={!allowed} onClick={() => act("Deny")}>Deny</button>
              <button className="btn ghost" disabled={!allowed} onClick={() => act("Override")}>Override AI</button>
            </div>
          </>
        )}
      </div>
    </div>
  );
}

const SUGGESTIONS = ["What is the reported net revenue?", "What is the total short-term debt?", "How much interest expense is reported?", "What is the officer compensation?"];

function Ask({ c }: { c: LoanCase }) {
  const [q, setQ] = useState("");
  const end = useRef<HTMLDivElement>(null);
  useEffect(() => { end.current?.scrollIntoView({ block: "end" }); }, [c.qa.length]);
  const ready = c.chunks.length > 0 || (c.chunkCount ?? 0) > 0;
  const send = async (text: string) => {
    if (!text.trim() || !ready) return;
    setQ("");
    try { await askQuestion(c.id, text.trim()); } catch (e) { alert(e instanceof Error ? e.message : "The question could not be sent."); }
  };
  return (
    <div className="card">
      <div className="card-head"><h3>Ask the document</h3><span className="badge-mode">Answers come only from {c.fileName}</span></div>
      {!ready ? <div className="empty">The document is still being loaded into the vector database.</div> : (
        <>
          <div className="note" style={{ marginBottom: 12 }}>
            This panel only reports what the loaded document says, with page citations. Ask for the "analyst commentary" to see the AI rationale. It cannot answer general questions, discuss other companies, or reveal scores and decisions.
          </div>
          <div className="chips">{SUGGESTIONS.map((s) => <button key={s} className="chip-btn" onClick={() => send(s)}>{s}</button>)}</div>
          <div className="chat" aria-live="polite">
            {c.qa.length === 0 && <div className="empty">No questions yet.</div>}
            {c.qa.map((m) => (
              <div key={m.id} style={{ display: "contents" }}>
                <div className="bubble q">{m.question}</div>
                <div className={"bubble a" + (m.refused ? " refused" : "")}>
                  {m.refused && m.reason && <div style={{ marginBottom: 4 }}><span className="pill gold">{REFUSALS[m.reason].label}</span></div>}
                  {m.answer}
                  {m.citations.map((ct) => <span className="cite" key={ct.chunkId}><b>{ct.page > 0 ? `Page ${ct.page}` : "Analyst commentary"}</b>{ct.page > 0 && <> · {ct.excerpt}</>}</span>)}
                </div>
              </div>
            ))}
            <div ref={end} />
          </div>
          <form className="ask" onSubmit={(e) => { e.preventDefault(); send(q); }}>
            <input className="field" placeholder="Ask about this document..." value={q} maxLength={500} onChange={(e) => setQ(e.target.value)} />
            <button className="btn primary" type="submit"><Icon name="send" size={16} />Ask</button>
          </form>
        </>
      )}
    </div>
  );
}

function download(name: string, data: unknown) {
  const url = URL.createObjectURL(new Blob([JSON.stringify(data, null, 2)], { type: "application/json" }));
  const a = document.createElement("a"); a.href = url; a.download = name; a.click(); URL.revokeObjectURL(url);
}

function Record({ c }: { c: LoanCase }) {
  const { user } = useStore();
  const final = c.humanDecision?.finalDecision ?? (c.status === "Approved" ? "Approve" : c.status === "Denied" ? "Deny" : null);
  if (!final) return <div className="card empty">The decision record is created once the case is finalised.</div>;
  const reasons = c.decisionResult?.reasonCodes ?? [];
  const applicant = { caseId: c.id, entity: c.entity, decision: final === "Approve" ? "Approved" : "Declined", decidedAt: c.humanDecision?.at ?? new Date().toISOString(), reasons: final === "Deny" ? reasons : undefined };
  const internal = { ...applicant, riskScore: c.riskScore, flags: c.flags, decisionModel: c.decisionResult, humanDecision: c.humanDecision, audit: c.audit };
  const canInternal = user.role !== "RiskOps";
  return (
    <div className="two">
      <div className="card">
        <div className="card-head"><h3>Decision</h3><StatusPill s={c.status} /></div>
        <div className="kv"><span>Outcome</span><b>{applicant.decision}</b></div>
        <div className="kv"><span>Decided by</span><b>{c.humanDecision ? c.humanDecision.by : "Decision model (automatic)"}</b></div>
        <div className="kv"><span>Reason codes</span><b>{reasons.join(", ")}</b></div>
        <div className="note" style={{ marginTop: 12 }}>{c.humanDecision?.reason || c.decisionResult?.explanation}</div>
      </div>
      <div className="card">
        <div className="card-head"><h3>Download</h3></div>
        <p style={{ color: "var(--muted)", lineHeight: 1.6, marginBottom: 12 }}>No email is sent by the system. Download the applicant copy and share it through your own channel. It never contains the internal score or fraud flags.</p>
        <div style={{ display: "flex", gap: 10, flexWrap: "wrap" }}>
          <button className="btn primary" onClick={() => (REAL ? void downloadRecord(c.id, "applicant") : download(`${c.id}-applicant.json`, applicant))}><Icon name="download" size={16} />Applicant copy</button>
          <button className="btn ghost" disabled={!canInternal} onClick={() => (REAL ? void downloadRecord(c.id, "internal") : download(`${c.id}-internal.json`, internal))}>Internal record</button>
        </div>
        {!canInternal && <div className="note" style={{ marginTop: 12 }}>The internal record is available to Underwriter and Auditor roles.</div>}
      </div>
    </div>
  );
}

function Audit({ c }: { c: LoanCase }) {
  return (
    <div className="card">
      <table>
        <thead><tr><th>Time</th><th>Actor</th><th>Step</th><th>Detail</th></tr></thead>
        <tbody>{c.audit.map((a, i) => <tr key={i}><td>{fmtDate(a.at)}</td><td>{a.actor}</td><td>{a.step}</td><td>{a.detail}</td></tr>)}</tbody>
      </table>
    </div>
  );
}
