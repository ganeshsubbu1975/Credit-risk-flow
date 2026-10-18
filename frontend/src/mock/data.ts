import type { AuditEvent, Chunk, Flag, LoanCase, Metrics, Step } from "../types";
import { buildPillars, decide, derive, money, pct } from "../logic";

// Ground truth from Financial_Metrics_Benchmark_4_Entities.pdf
export const PROFILES: { key: string; entity: string; metrics: Metrics; flags: Flag[] }[] = [
  {
    key: "A", entity: "Apex Mfg Inc.",
    metrics: { grossReceipts: 4920000, returns: 69800, netRevenue: 4850200, officerComp: 210000, depreciation: 115300, interest: 48250, taxableIncome: 446900, shortDebt: 350000, longDebt: 900000 },
    flags: [{ id: "TB-01", severity: "Low", rule: "Tax-to-bank variance", evidence: "Gross sales vs bank credits differ by -0.15% (minor)." }],
  },
  {
    key: "B", entity: "Nexus Logistics",
    metrics: { grossReceipts: 2350000, returns: 39200, netRevenue: 2310800, officerComp: 120000, depreciation: 45000, interest: 23100, taxableIncome: 157000, shortDebt: 240000, longDebt: 650000 },
    flags: [{ id: "TB-02", severity: "Low", rule: "Tax-to-bank variance", evidence: "Bank-summed deposits match Line 1c within tolerance." }],
  },
  {
    key: "C", entity: "Summit Healthcare",
    metrics: { grossReceipts: 6200000, returns: 79500, netRevenue: 6120500, officerComp: 250000, depreciation: 180000, interest: 42000, taxableIncome: 708000, shortDebt: 250000, longDebt: 900000 },
    flags: [],
  },
  {
    key: "D", entity: "Vantage Retail",
    metrics: { grossReceipts: 1910000, returns: 60000, netRevenue: 1850000, officerComp: 90000, depreciation: 35000, interest: 45000, taxableIncome: 15000, shortDebt: 275000, longDebt: 650000 },
    flags: [{ id: "CF-03", severity: "Medium", rule: "Cash-flow timing", evidence: "Taxable income is 0.8% of revenue while interest and debt are high; review liquidity." }],
  },
];

export function buildChunks(entity: string, m: Metrics): Chunk[] {
  const d = derive(m);
  const t = (id: string, page: number, text: string): Chunk => ({ id, page, text });
  return [
    t("c1", 1, `${entity}. Financial extraction for fiscal year 2024-2025 prepared from tax return and bank statements.`),
    t("c2", 1, `Gross receipts / sales (IRS Form 1120S Line 1a): ${money(m.grossReceipts)}.`),
    t("c3", 1, `Returns and allowances (Line 1b): ${money(m.returns)}.`),
    t("c4", 1, `Net revenue (Line 1c): ${money(m.netRevenue)}.`),
    t("c5", 1, `Officer compensation (Line 7): ${money(m.officerComp)}.`),
    t("c6", 2, `Depreciation and amortization (Line 14): ${money(m.depreciation)}.`),
    t("c7", 2, `Interest expense (Line 13): ${money(m.interest)}.`),
    t("c8", 2, `Taxable income / net (Line 21): ${money(m.taxableIncome)}.`),
    t("c9", 2, `Short-term debt, notes payable current (Balance Sheet Line 17d): ${money(m.shortDebt)}.`),
    t("c10", 2, `Long-term debt, mortgages and notes payable (Balance Sheet Line 19d): ${money(m.longDebt)}.`),
    t("c11", 3, `Calculated EBITDA = taxable income + interest + depreciation + officer compensation = ${money(d.ebitda)}. Total debt ${money(d.debt)}. Debt / EBITDA ratio ${d.leverage.toFixed(2)}x.`),
  ];
}

export const STEP_DEFS: Omit<Step, "status">[] = [
  { key: "upload", label: "Upload" },
  { key: "pdf", label: "PDF read" },
  { key: "ingest", label: "Ingest to vector DB", model: "Embedding model" },
  { key: "n1", label: "Node 1: Extraction & verification", model: "Small / fast model" },
  { key: "n2", label: "Node 2: Fraud & compliance", model: "Long-context model" },
  { key: "n3", label: "Node 3: Risk synthesis", model: "Reasoning model" },
  { key: "score", label: "Scorecard" },
  { key: "decision", label: "Decision model" },
  { key: "hitl", label: "Human review" },
  { key: "final", label: "Final decision" },
];

export const freshSteps = (): Step[] => STEP_DEFS.map((s) => ({ ...s, status: "pending" as const }));

export function seedCase(key: string, id: string, minsAgo: number, submittedBy = "Ethan Cole"): LoanCase {
  const p = PROFILES.find((x) => x.key === key)!;
  const d = derive(p.metrics);
  const { pillars, riskScore } = buildPillars(p.metrics);
  const dr = decide({ riskScore, complete: true, minConfidence: 0.93, reconciliationOk: true, flags: p.flags, llmRecommendation: riskScore < 30 ? "Approve" : "Escalate" });
  const when = new Date(Date.now() - minsAgo * 60000).toISOString();
  const steps: Step[] = freshSteps().map((s) => ({ ...s, status: "done" as const, latencyMs: 600 + Math.round(Math.random() * 1800), tokens: s.model ? 800 + Math.round(Math.random() * 3000) : undefined }));
  const awaiting = dr.decision === "Escalate";
  if (awaiting) { steps[8].status = "running"; steps[9].status = "pending"; }
  const c: LoanCase = {
    id, entity: p.entity, fileName: `${p.entity.replace(/[ .]/g, "_")}_FY2024-25.pdf`, submittedBy, submittedAt: when,
    status: awaiting ? "AwaitingReview" : dr.decision === "Approve" ? "Approved" : "Denied",
    steps, metrics: p.metrics, ebitda: d.ebitda, ebitdaExOfficer: d.ebitdaExOfficer, leverage: d.leverage,
    ebitdaMargin: d.ebitdaMargin, interestCover: d.interestCover, pillars, riskScore, flags: p.flags,
    llmRecommendation: riskScore < 30 ? "Approve" : "Escalate",
    rationale: `${p.entity} shows ${d.leverage.toFixed(2)}x leverage, an EBITDA margin of ${pct(d.ebitdaMargin)} and interest cover of ${d.interestCover.toFixed(1)}x. ` +
      (riskScore < 30 ? "Debt is well covered and no material compliance concerns were found." : "Leverage and thin profitability warrant an underwriter's review before a decision."),
    decisionResult: dr, humanDecision: null, chunks: buildChunks(p.entity, p.metrics), qa: [], audit: [],
  };
  const a = (step: string, detail: string, actor = "system"): AuditEvent => ({ at: when, actor, step, detail });
  c.audit = [a("Upload", `File ${c.fileName} accepted`, submittedBy), a("Ingest", `${c.chunks.length} chunks embedded`), a("Node 1", "Metrics extracted and reconciled"), a("Node 2", `${p.flags.length} flag(s) raised`), a("Node 3", `Recommendation: ${c.llmRecommendation}`), a("Scorecard", `Risk score ${riskScore}`), a("Decision model", `${dr.rule} fired: ${dr.decision} (${dr.reasonCodes.join(", ")})`)];
  if (!awaiting) c.audit.push(a("Final", `Decision recorded: ${dr.decision}`));
  return c;
}

export const seedCases = (): LoanCase[] => [
  seedCase("D", "LN-2026-0004", 12),
  seedCase("C", "LN-2026-0003", 95),
  seedCase("B", "LN-2026-0002", 240),
  seedCase("A", "LN-2026-0001", 410),
];

export const TREND_DAILY = [
  { label: "Mon", approved: 8, other: 3 }, { label: "Tue", approved: 11, other: 4 }, { label: "Wed", approved: 9, other: 6 },
  { label: "Thu", approved: 14, other: 5 }, { label: "Fri", approved: 12, other: 7 }, { label: "Sat", approved: 5, other: 2 }, { label: "Sun", approved: 7, other: 3 },
];
export const TREND_WEEKLY = [
  { label: "W1", approved: 46, other: 19 }, { label: "W2", approved: 52, other: 22 }, { label: "W3", approved: 49, other: 25 }, { label: "W4", approved: 58, other: 21 },
];
