import { useSyncExternalStore } from "react";
import type { LoanCase, Role, Step, User, AuditEvent } from "./types";
import { PROFILES, buildChunks, freshSteps, seedCases } from "./mock/data";
import { buildPillars, decide, derive } from "./logic";
import { answerQuestion, REFUSALS } from "./mock/qa";
import * as api from "./api";

/** "real" talks to the ASP.NET Core backend (src/api.ts); otherwise the in-memory mock below is used. */
export const REAL = import.meta.env.VITE_API_MODE === "real";

// ---- Mock API seam -------------------------------------------------------
// Every function below is what the real ASP.NET Core API will provide
// (POST /cases, POST /cases/{id}/review, POST /cases/{id}/qa, SignalR progress).
// Swap these for fetch() calls when the backend exists; the UI does not change.

export interface AppState {
  cases: LoanCase[];
  user: User;
  theme: "light" | "dark";
  search: string;
}

let state: AppState = {
  cases: REAL ? [] : seedCases(),
  user: { name: "Ethan Cole", role: "RiskOps" },
  theme: (typeof localStorage !== "undefined" && localStorage.getItem("theme") === "dark") ? "dark" : "light",
  search: "",
};
const listeners = new Set<() => void>();
const set = (patch: Partial<AppState>) => { state = { ...state, ...patch }; listeners.forEach((l) => l()); };
const subscribe = (l: () => void) => { listeners.add(l); return () => listeners.delete(l); };
export const useStore = () => useSyncExternalStore(subscribe, () => state);

const patchCase = (id: string, fn: (c: LoanCase) => LoanCase) => set({ cases: state.cases.map((c) => (c.id === id ? fn(c) : c)) });
const audit = (actor: string, step: string, detail: string): AuditEvent => ({ at: new Date().toISOString(), actor, step, detail });
const setStep = (steps: Step[], key: string, patch: Partial<Step>) => steps.map((s) => (s.key === key ? { ...s, ...patch } : s));

export const setSearch = (search: string) => set({ search });
const DEMO_USERS: Record<Role, string> = { RiskOps: "Ethan Cole", Underwriter: "Priya Nair", Auditor: "Sam Reed" };
export const setRole = (role: Role) => {
  set({ user: { name: DEMO_USERS[role], role } });
  if (REAL) void refreshCases(); // visibility (for example the audit trail) depends on the role
};
export function toggleTheme() {
  const theme = state.theme === "light" ? "dark" : "light";
  try { localStorage.setItem("theme", theme); } catch { /* storage may be blocked */ }
  set({ theme });
}

// ---- Real API mode ---------------------------------------------------------
let pollTimer: ReturnType<typeof setTimeout> | undefined;
export const apiError = { message: "" };

export async function refreshCases() {
  try {
    const cases = await api.fetchCases(state.user);
    apiError.message = "";
    set({ cases });
  } catch (e) {
    apiError.message = e instanceof Error ? e.message : "API unavailable";
    set({});
  }
}

/** Polls while any case is processing (faster) or otherwise every few seconds. SignalR can replace this later. */
function startPolling() {
  const tick = async () => {
    await refreshCases();
    const busy = state.cases.some((c) => c.status === "Processing");
    pollTimer = setTimeout(tick, busy ? 1500 : 5000);
  };
  if (!pollTimer) void tick();
}
if (REAL) startPolling();

export async function submitCase(file: File, profileKey: string): Promise<string> {
  if (!REAL) return mockSubmitCase(file.name, profileKey);
  const id = await api.uploadCase(state.user, file);
  await refreshCases();
  return id;
}

export async function reviewCase(id: string, action: "Approve" | "Deny" | "Override", reason: string): Promise<void> {
  if (!REAL) return mockReviewCase(id, action, reason);
  const c = state.cases.find((x) => x.id === id);
  // Override flips the side the AI leaned to (the backend needs an explicit final decision).
  const aiSide = c?.llmRecommendation === "Deny" ? "Deny" : "Approve";
  const finalDecision = action === "Override" ? (aiSide === "Approve" ? "Deny" : "Approve") : undefined;
  await api.postReview(state.user, id, action, reason, finalDecision);
  await refreshCases();
}

export async function askQuestion(id: string, question: string): Promise<void> {
  if (!REAL) return mockAskQuestion(id, question);
  await api.postQuestion(state.user, id, question);
  await refreshCases();
}

/** Underwriter only. Returns how many cases were removed and how many were skipped because they were still processing. */
export async function deleteAllDocuments(): Promise<{ deleted: number; skipped: number }> {
  if (state.user.role !== "Underwriter") throw new Error("Only the Underwriter role can delete documents.");
  if (!REAL) {
    const keep = state.cases.filter((c) => c.status === "Processing");
    const deleted = state.cases.length - keep.length;
    set({ cases: keep });
    return { deleted, skipped: keep.length };
  }
  const r = await api.deleteAllCases(state.user);
  await refreshCases();
  return r;
}

export async function downloadRecord(id: string, view: "applicant" | "internal") {
  const data = await api.fetchRecord(state.user, id, view);
  const url = URL.createObjectURL(new Blob([JSON.stringify(data, null, 2)], { type: "application/json" }));
  const a = document.createElement("a"); a.href = url; a.download = `${id}-${view}.json`; a.click(); URL.revokeObjectURL(url);
}

// ---- Mock implementations ------------------------------------------------------
function mockSubmitCase(fileName: string, profileKey: string): string {
  const p = PROFILES.find((x) => x.key === profileKey)!;
  const id = `LN-2026-${String(state.cases.length + 1).padStart(4, "0")}`;
  const d = derive(p.metrics);
  const { pillars, riskScore } = buildPillars(p.metrics);
  const llm = riskScore < 30 ? "Approve" : riskScore > 70 ? "Deny" : "Escalate";
  const base: LoanCase = {
    id, entity: p.entity, fileName, submittedBy: state.user.name, submittedAt: new Date().toISOString(), status: "Processing",
    steps: setStep(freshSteps(), "upload", { status: "done", summary: `${fileName} accepted (digital PDF, text layer found)` }),
    metrics: p.metrics, ebitda: d.ebitda, ebitdaExOfficer: d.ebitdaExOfficer, leverage: d.leverage, ebitdaMargin: d.ebitdaMargin,
    interestCover: d.interestCover, pillars, riskScore: null, flags: [], llmRecommendation: null, rationale: "",
    decisionResult: null, humanDecision: null, chunks: [], qa: [], audit: [audit(state.user.name, "Upload", `File ${fileName} accepted`)],
  };
  set({ cases: [base, ...state.cases] });

  const script: { key: string; ms: number; summary: string; apply?: (c: LoanCase) => LoanCase }[] = [
    { key: "pdf", ms: 700, summary: "Text and tables extracted from 3 pages" },
    { key: "ingest", ms: 900, summary: "11 chunks embedded and stored for this case", apply: (c) => ({ ...c, chunks: buildChunks(p.entity, p.metrics) }) },
    { key: "n1", ms: 1300, summary: "9 metrics extracted; arithmetic reconciled; minimum confidence 0.93" },
    { key: "n2", ms: 1300, summary: `${p.flags.length} flag(s) raised`, apply: (c) => ({ ...c, flags: p.flags }) },
    { key: "n3", ms: 1500, summary: `Recommendation: ${llm}`, apply: (c) => ({ ...c, llmRecommendation: llm, rationale: `${p.entity}: leverage ${d.leverage.toFixed(2)}x, EBITDA margin ${(d.ebitdaMargin * 100).toFixed(1)}%, interest cover ${d.interestCover.toFixed(1)}x.` }) },
    { key: "score", ms: 600, summary: `Risk score ${riskScore} (DSCR and data integrity unavailable; weights renormalised)`, apply: (c) => ({ ...c, riskScore }) },
    { key: "decision", ms: 600, summary: "", apply: (c) => {
      const dr = decide({ riskScore, complete: true, minConfidence: 0.93, reconciliationOk: true, flags: p.flags, llmRecommendation: llm });
      return { ...c, decisionResult: dr, audit: [...c.audit, audit("system", "Decision model", `${dr.rule} fired: ${dr.decision} (${dr.reasonCodes.join(", ")})`)] };
    } },
  ];

  let t = 400;
  script.forEach((s, i) => {
    setTimeout(() => patchCase(id, (c) => ({ ...c, steps: setStep(c.steps, s.key, { status: "running" }) })), t);
    t += s.ms;
    setTimeout(() => patchCase(id, (c) => {
      let n = s.apply ? s.apply(c) : c;
      const summary = s.key === "decision" && n.decisionResult ? `${n.decisionResult.rule}: ${n.decisionResult.decision}. ${n.decisionResult.explanation}` : s.summary;
      n = { ...n, steps: setStep(n.steps, s.key, { status: "done", summary, latencyMs: s.ms, tokens: n.steps.find((x) => x.key === s.key)?.model ? 900 + i * 350 : undefined }) };
      if (s.key !== "decision") n = { ...n, audit: [...n.audit, audit("system", n.steps.find((x) => x.key === s.key)!.label, summary)] };
      if (s.key === "decision" && n.decisionResult) {
        const dec = n.decisionResult.decision;
        if (dec === "Escalate") return { ...n, status: "AwaitingReview", steps: setStep(n.steps, "hitl", { status: "running", summary: "Waiting for an underwriter" }) };
        return { ...n, status: dec === "Approve" ? "Approved" : "Denied", steps: setStep(setStep(n.steps, "hitl", { status: "skipped", summary: "Not required" }), "final", { status: "done", summary: `Auto-${dec === "Approve" ? "approved" : "denied"}` }) };
      }
      return n;
    }), t);
  });
  return id;
}

function mockReviewCase(id: string, action: "Approve" | "Deny" | "Override", reason: string) {
  const c = state.cases.find((x) => x.id === id);
  if (!c || c.status !== "AwaitingReview") return;
  const aiSide = c.llmRecommendation === "Deny" ? "Deny" : "Approve";
  const finalDecision: "Approve" | "Deny" = action === "Override" ? (aiSide === "Approve" ? "Deny" : "Approve") : action;
  const hd = { action, finalDecision, reason, by: state.user.name, at: new Date().toISOString() };
  patchCase(id, (x) => ({
    ...x, humanDecision: hd, status: finalDecision === "Approve" ? "Approved" : "Denied",
    steps: setStep(setStep(x.steps, "hitl", { status: "done", summary: `${hd.by}: ${action}${reason ? " - " + reason : ""}` }), "final", { status: "done", summary: `Final: ${finalDecision}` }),
    audit: [...x.audit, audit(hd.by, "Human review", `${action} (final ${finalDecision}). Reason: ${reason || "none given"}`), audit("system", "Final", `Decision record stored: ${finalDecision}`)],
  }));
}

function mockAskQuestion(id: string, question: string) {
  const c = state.cases.find((x) => x.id === id);
  if (!c) return;
  const r = answerQuestion(question, c.chunks, c.entity);
  const item = {
    id: crypto.randomUUID(), question, at: new Date().toISOString(), refused: r.refused, reason: r.reason,
    answer: r.refused ? REFUSALS[r.reason!].text : r.answer, citations: r.citations,
  };
  patchCase(id, (x) => ({ ...x, qa: [...x.qa, item], audit: [...x.audit, audit(state.user.name, "Document Q&A", r.refused ? `Refused (${r.reason}): ${question}` : `Answered from ${r.citations.map((q) => "p." + q.page).join(", ")}: ${question}`)] }));
}

export const canReview = (c: LoanCase) => state.user.role === "Underwriter" && c.status === "AwaitingReview" && c.submittedBy !== state.user.name;
