import type { DecisionResult, Flag, Metrics, Pillar } from "./types";

export const DECISION_MODEL_VERSION = "DM-1.0.0";
export const POLICY = { approveBelow: 30, denyAbove: 70, minConfidence: 0.8 };

export const money = (v: number) => "$" + Math.round(v).toLocaleString("en-US");
export const pct = (v: number, d = 1) => (v * 100).toFixed(d) + "%";

export function derive(m: Metrics) {
  const ebitda = m.taxableIncome + m.interest + m.depreciation + m.officerComp;
  const debt = m.shortDebt + m.longDebt;
  return {
    ebitda,
    ebitdaExOfficer: ebitda - m.officerComp,
    debt,
    leverage: debt / ebitda,
    ebitdaMargin: ebitda / m.netRevenue,
    interestCover: ebitda / m.interest,
  };
}

const lerp = (x: number, x0: number, x1: number, y0: number, y1: number) => y0 + ((x - x0) / (x1 - x0)) * (y1 - y0);

// Creditworthiness bands from the benchmark scoring criteria (higher = better).
export function leverageScore(x: number): number {
  if (x < 1.5) return lerp(x, 0, 1.5, 100, 95);
  if (x < 2.5) return lerp(x, 1.5, 2.5, 94, 80);
  if (x <= 4) return lerp(x, 2.5, 4, 79, 60);
  return Math.max(20, 59 - (x - 4) * 10);
}

export function marginScore(m: number): number {
  if (m > 0.2) return Math.min(100, lerp(m, 0.2, 0.3, 90, 100));
  if (m >= 0.15) return lerp(m, 0.15, 0.2, 80, 89);
  if (m >= 0.1) return lerp(m, 0.1, 0.15, 70, 79);
  return Math.max(0, 59 - (0.1 - m) * 300);
}

// DSCR and data integrity need inputs the benchmark does not provide; weights are renormalised.
export function buildPillars(m: Metrics): { pillars: Pillar[]; riskScore: number } {
  const d = derive(m);
  const pillars: Pillar[] = [
    { name: "Debt / EBITDA", weight: 0.4, display: d.leverage.toFixed(2) + "x", score: leverageScore(d.leverage) },
    { name: "Debt service coverage", weight: 0.3, display: "n/a", score: null },
    { name: "EBITDA margin", weight: 0.2, display: pct(d.ebitdaMargin), score: marginScore(d.ebitdaMargin) },
    { name: "Data integrity", weight: 0.1, display: "n/a", score: null },
  ];
  const avail = pillars.filter((p) => p.score !== null);
  const wsum = avail.reduce((s, p) => s + p.weight, 0);
  const cred = avail.reduce((s, p) => s + (p.score as number) * p.weight, 0) / wsum;
  return { pillars, riskScore: Math.round((100 - cred) * 10) / 10 };
}

export interface DecisionInput {
  riskScore: number;
  complete: boolean;
  minConfidence: number;
  reconciliationOk: boolean;
  flags: Flag[];
  llmRecommendation: "Approve" | "Deny" | "Escalate";
}

const order = ["Low", "Medium", "High", "Critical"];
const worst = (flags: Flag[]) => flags.reduce((w, f) => Math.max(w, order.indexOf(f.severity)), -1);

// Ordered decision table, first match wins (PRD FR-5).
export function decide(i: DecisionInput): DecisionResult {
  const mk = (decision: DecisionResult["decision"], rule: string, codes: string[], explanation: string): DecisionResult => ({
    decision, rule, reasonCodes: codes, explanation, modelVersion: DECISION_MODEL_VERSION,
  });
  if (!i.complete) return mk("Escalate", "R1", ["INCOMPLETE_DATA"], "Required fields are missing after retry.");
  if (worst(i.flags) >= 3) return mk("Escalate", "R2", ["CRITICAL_COMPLIANCE_FLAG"], "A critical compliance flag requires compliance review; never auto-approved.");
  if (i.minConfidence < POLICY.minConfidence || !i.reconciliationOk)
    return mk("Escalate", "R3", [i.reconciliationOk ? "LOW_CONFIDENCE" : "RECONCILIATION_FAILED"], "Extraction confidence or arithmetic reconciliation is below policy.");
  if (i.riskScore > POLICY.denyAbove) return mk("Deny", "R4", ["HIGH_RISK_SCORE"], `Risk score ${i.riskScore} is above ${POLICY.denyAbove}.`);
  if (i.riskScore < POLICY.approveBelow && worst(i.flags) < 2 && i.llmRecommendation !== "Deny")
    return mk("Approve", "R5", ["LOW_RISK_CLEAN"], `Risk score ${i.riskScore} is below ${POLICY.approveBelow} with no high-severity flags.`);
  const disagree = i.llmRecommendation === "Deny" && i.riskScore < POLICY.approveBelow;
  return mk("Escalate", "R6", [disagree ? "MODEL_DISAGREEMENT" : "MEDIUM_RISK"],
    disagree ? "AI recommendation disagrees with the scorecard." : `Risk score ${i.riskScore} is between ${POLICY.approveBelow} and ${POLICY.denyAbove}; human review required.`);
}
