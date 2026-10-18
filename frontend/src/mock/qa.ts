import type { Chunk, Citation, RefusalReason } from "../types";

export interface QAResult {
  answer?: string;
  citations: Citation[];
  refused: boolean;
  reason?: RefusalReason;
}

const STOP = new Set("a an the of in on for to and or is are was were what which who how much many does do did me my us our your you it its this that with from by at as be can tell show give please about".split(" "));
const SYN: Record<string, string[]> = {
  revenue: ["sales", "receipts", "revenue"], sales: ["receipts", "revenue", "sales"], receipts: ["sales", "revenue"],
  debt: ["notes", "mortgages", "debt", "payable"], loan: ["debt", "notes"], borrowings: ["debt", "notes"],
  interest: ["interest"], depreciation: ["depreciation", "amortization"], income: ["income", "taxable"], profit: ["income", "taxable", "ebitda"],
  officer: ["officer", "compensation"], salary: ["officer", "compensation"], returns: ["returns", "allowances"],
  ebitda: ["ebitda"], leverage: ["ebitda", "debt", "ratio"], ratio: ["ratio", "ebitda"],
};
// Words that make a question "about a financial document" for the scope check.
const DOC_TERMS = new Set(["revenue", "sales", "receipts", "debt", "interest", "depreciation", "income", "taxable", "officer", "compensation", "returns", "allowances", "ebitda", "ratio", "line", "form", "page", "balance", "sheet", "notes", "mortgages", "payable", "amortization", "net", "gross", "total", "document", "pdf", "tax", "return", "statement", "fiscal", "year", "company", "entity", "profit", "leverage", "borrowings", "salary"]);

const INJECTION = /(ignore|disregard|forget).{0,25}(instruction|previous|above|prompt|rules)|system prompt|act as|pretend|role[- ]?play|jailbreak|developer mode|reveal your (prompt|instructions)/i;
const DECISION = /(risk score|recommend|should (we|i) (approve|lend|deny)|approve (this|the) (loan|case)|deny (this|the) (loan|case)|decision model|creditworth|override|compliance flag|fraud)/i;
const OTHER_ENTITY_CODE = /\bentity [a-d]\b/i;

const tok = (s: string) => s.toLowerCase().replace(/[^a-z0-9$.,\s]/g, " ").split(/\s+/).map((w) => w.replace(/[.,]+$/g, "")).filter((w) => w && !STOP.has(w));

export function answerQuestion(q: string, chunks: Chunk[], entity: string): QAResult {
  const none = { citations: [] as Citation[], refused: true };
  if (q.trim().length < 3 || q.length > 500) return { ...none, reason: "Blocked" };
  if (INJECTION.test(q)) return { ...none, reason: "Injection" };
  if (DECISION.test(q)) return { ...none, reason: "Blocked" };

  // Questions about any other company are outside this case's document.
  const lower = q.toLowerCase();
  const otherName = ["apex", "nexus", "summit", "vantage"].some((n) => lower.includes(n) && !entity.toLowerCase().includes(n));
  if (otherName || OTHER_ENTITY_CODE.test(q)) return { ...none, reason: "OutOfScope" };

  const words = tok(q);
  const orig = new Set(words);
  const syn = new Set<string>();
  words.forEach((w) => (SYN[w] ?? []).forEach((s) => { if (!orig.has(s)) syn.add(s); }));
  const expanded = new Set([...orig, ...syn]);

  // Scope check: at least one term must relate to the document's subject matter.
  const docVocab = new Set(chunks.flatMap((c) => tok(c.text)));
  const inScope = words.some((w) => DOC_TERMS.has(w) || docVocab.has(w));
  if (!inScope) return { ...none, reason: "OutOfScope" };

  // Retrieval scoped to this case's chunks only.
  const scored = chunks
    .map((c) => {
      const ct = new Set(tok(c.text));
      // Question words count double; synonyms count once.
      let hits = 0;
      orig.forEach((w) => { if (ct.has(w)) hits += 2; });
      syn.forEach((w) => { if (ct.has(w)) hits += 1; });
      return { c, score: Math.min(1, hits / Math.max(2, 2 * orig.size)) };
    })
    .filter((x) => x.score > 0)
    .sort((a, b) => b.score - a.score);

  if (!scored.length || scored[0].score < 0.34) return { ...none, reason: "NotFound" };
  const top = scored.slice(0, scored[0].score >= 0.9 ? 1 : 2).map((x) => x.c);
  return {
    refused: false,
    answer: top.map((c) => c.text).join(" "),
    citations: top.map((c) => ({ chunkId: c.id, page: c.page, excerpt: c.text })),
  };
}

export const REFUSALS: Record<RefusalReason, { label: string; text: string }> = {
  OutOfScope: { label: "Out of scope", text: "That question isn't about the loaded document, so I can't answer it." },
  NotFound: { label: "Not found in document", text: "I can't find that in the loaded document." },
  Blocked: { label: "Blocked", text: "I only report what the document says. I can't discuss scores, recommendations or decisions here." },
  Injection: { label: "Blocked", text: "That request tries to change how I work, so it was blocked." },
};
