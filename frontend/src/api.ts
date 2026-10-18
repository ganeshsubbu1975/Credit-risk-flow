import type { LoanCase, QAItem, User } from "./types";

// Real API client for the ASP.NET Core backend. Used when VITE_API_MODE=real (see .env.example).
// Dev auth: the backend (Auth:Mode=Dev) trusts these two headers. For production, replace `authHeaders`
// with a bearer token from your OIDC library (MSAL / oidc-client).

const BASE = import.meta.env.VITE_API_BASE ?? "";

function authHeaders(user: User): Record<string, string> {
  return { "X-Demo-User": user.name, "X-Demo-Role": user.role };
}

async function call<T>(user: User, path: string, init: RequestInit = {}): Promise<T> {
  const res = await fetch(BASE + path, { ...init, headers: { ...authHeaders(user), ...(init.headers ?? {}) } });
  if (!res.ok) {
    let detail = res.statusText;
    try { const p = await res.json(); detail = p.detail ?? p.title ?? detail; } catch { /* no body */ }
    throw new Error(detail);
  }
  return (res.status === 204 ? undefined : await res.json()) as T;
}

/* eslint-disable @typescript-eslint/no-explicit-any */
// Maps the backend LoanState JSON onto the shape the UI already uses.
export function mapCase(s: any, qa: QAItem[] = []): LoanCase {
  const m = s.metrics;
  const d = s.scorecard?.derived;
  return {
    id: s.caseId,
    entity: s.entityName,
    fileName: s.fileName,
    submittedBy: s.submittedBy,
    submittedAt: s.submittedAt,
    status: s.status,
    steps: s.steps.map((x: any) => ({ key: x.key, label: x.label, model: x.model ?? undefined, status: x.status, summary: x.summary ?? undefined, latencyMs: x.latencyMs ?? undefined, tokens: x.tokens ?? undefined })),
    metrics: {
      grossReceipts: m?.grossReceipts ?? 0, returns: m?.returns ?? 0, netRevenue: m?.netRevenue ?? 0, officerComp: m?.officerComp ?? 0,
      depreciation: m?.depreciation ?? 0, interest: m?.interest ?? 0, taxableIncome: m?.taxableIncome ?? 0,
      shortDebt: m?.shortTermDebt ?? 0, longDebt: m?.longTermDebt ?? 0,
    },
    ebitda: d?.ebitda ?? 0,
    ebitdaExOfficer: d?.ebitdaExOfficer ?? 0,
    leverage: d?.leverage ?? 0,
    ebitdaMargin: d?.margin ?? 0,
    interestCover: d?.interestCover ?? 0,
    pillars: (s.scorecard?.pillars ?? []).map((p: any) => ({ name: p.name, weight: p.weight, display: p.display, score: p.score ?? null })),
    riskScore: s.scorecard?.riskScore ?? null,
    flags: s.flags ?? [],
    llmRecommendation: s.llmRecommendation?.outcome ?? null,
    rationale: s.llmRecommendation?.rationale ?? "",
    decisionResult: s.decision ? { decision: s.decision.decision, rule: s.decision.rule, reasonCodes: s.decision.reasonCodes, explanation: s.decision.explanation, modelVersion: s.decision.modelVersion } : null,
    humanDecision: s.human ? { action: s.human.action, finalDecision: s.human.final, reason: s.human.reason, by: s.human.by, at: s.human.at } : null,
    chunks: [],
    chunkCount: s.chunkCount ?? 0,
    qa,
    audit: [],
  };
}

function mapQa(e: any): QAItem {
  return {
    id: e.qaId, question: e.question, at: e.askedAt, refused: e.refused, reason: e.reason && e.reason !== "None" ? e.reason : undefined,
    answer: e.answer ?? undefined, citations: (e.citations ?? []).map((c: any) => ({ chunkId: c.chunkId, page: c.page, excerpt: c.excerpt })),
  };
}

export async function fetchCases(user: User): Promise<LoanCase[]> {
  const list = await call<any[]>(user, "/api/cases");
  return Promise.all(list.map((c) => fetchCase(user, c.caseId)));
}

export async function fetchCase(user: User, id: string): Promise<LoanCase> {
  const [state, qa, audit] = await Promise.all([
    call<any>(user, `/api/cases/${id}`),
    call<any[]>(user, `/api/cases/${id}/qa`).catch(() => []),
    call<any[]>(user, `/api/cases/${id}/audit`).catch(() => []), // 403 for Risk Ops
  ]);
  const c = mapCase(state, qa.map(mapQa));
  c.audit = audit.map((a) => ({ at: a.at, actor: a.actor, step: a.step, detail: a.summary }));
  return c;
}

export async function uploadCase(user: User, file: File): Promise<string> {
  const form = new FormData();
  form.append("file", file);
  const r = await call<{ caseId: string }>(user, "/api/cases", { method: "POST", body: form });
  return r.caseId;
}

export async function postReview(user: User, id: string, action: "Approve" | "Deny" | "Override", reason: string, finalDecision?: "Approve" | "Deny") {
  await call(user, `/api/cases/${id}/review`, {
    method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ action, reason, finalDecision }),
  });
}

export async function postQuestion(user: User, id: string, question: string) {
  await call(user, `/api/cases/${id}/qa`, { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ question }) });
}

export async function fetchRecord(user: User, id: string, view: "applicant" | "internal") {
  return call<unknown>(user, `/api/cases/${id}/record?view=${view}`);
}
