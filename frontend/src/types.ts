export type Severity = "Low" | "Medium" | "High" | "Critical";
export type StepStatus = "pending" | "running" | "done" | "skipped" | "error";
export type CaseStatus = "Processing" | "AwaitingReview" | "Approved" | "Denied" | "Error";
export type Decision = "Approve" | "Deny" | "Escalate";
export type Role = "RiskOps" | "Underwriter" | "Auditor";

export interface Metrics {
  grossReceipts: number;
  returns: number;
  netRevenue: number;
  officerComp: number;
  depreciation: number;
  interest: number;
  taxableIncome: number;
  shortDebt: number;
  longDebt: number;
}

export interface Flag {
  id: string;
  severity: Severity;
  rule: string;
  evidence: string;
}

export interface Pillar {
  name: string;
  weight: number;
  display: string;
  score: number | null; // creditworthiness 0-100, null when unavailable
}

export interface Step {
  key: string;
  label: string;
  model?: string;
  status: StepStatus;
  summary?: string;
  latencyMs?: number;
  tokens?: number;
}

export interface Chunk {
  id: string;
  page: number;
  text: string;
}

export interface Citation {
  chunkId: string;
  page: number;
  excerpt: string;
}

export type RefusalReason = "OutOfScope" | "NotFound" | "Blocked" | "Injection";

export interface QAItem {
  id: string;
  question: string;
  answer?: string;
  citations: Citation[];
  refused: boolean;
  reason?: RefusalReason;
  at: string;
}

export interface AuditEvent {
  at: string;
  actor: string;
  step: string;
  detail: string;
}

export interface DecisionResult {
  decision: Decision;
  rule: string;
  reasonCodes: string[];
  explanation: string;
  modelVersion: string;
}

export interface HumanDecision {
  action: "Approve" | "Deny" | "Override";
  finalDecision: "Approve" | "Deny";
  reason: string;
  by: string;
  at: string;
}

export interface LoanCase {
  id: string;
  entity: string;
  fileName: string;
  submittedBy: string;
  submittedAt: string;
  status: CaseStatus;
  steps: Step[];
  metrics: Metrics;
  ebitda: number;
  ebitdaExOfficer: number;
  leverage: number;
  ebitdaMargin: number;
  interestCover: number;
  pillars: Pillar[];
  riskScore: number | null;
  flags: Flag[];
  llmRecommendation: Decision | null;
  rationale: string;
  decisionResult: DecisionResult | null;
  humanDecision: HumanDecision | null;
  chunks: Chunk[];
  /** Number of chunks in the vector store (real API). Q&A is available once this is above zero. */
  chunkCount?: number;
  qa: QAItem[];
  audit: AuditEvent[];
}

export interface User {
  name: string;
  role: Role;
}
