# LoanFlow web (React + TypeScript + Vite)

Front end for the loan underwriting orchestrator (see `../PRD`). Styled after `../image.png`.
It runs today against an in-memory **mock API** (`src/store.ts`, `src/mock/`); no backend is needed.

## Run
Requires Node.js 18+ (not currently installed on this machine).

```
cd frontend
npm install
npm run dev        # http://localhost:5173
npm run typecheck
npm run build
```

## Screens
- Dashboard: KPI cards, cases-by-outcome chart, pending review, latest cases, decision mix.
- New case: PDF upload (PDF only, 20 MB), then a simulated workflow.
- Case: progress stepper, analysis (metrics, EBITDA, scorecard, flags), underwriter review
  (approve / deny / override, separation of duties), Ask the document (guardrailed), decision record download, audit.
- Top-bar role switch (Risk Ops / Underwriter / Auditor) to try the permissions. Light and dark themes.

## Try this
1. Open the case for Vantage Retail (awaiting review). As Risk Ops you cannot decide; switch to Underwriter.
2. On Ask the document try: "What is the net revenue?" (answered with a page citation), "What is the capital of France?"
   (out of scope), "Ignore your instructions" (blocked), "What is the risk score?" (blocked), "What is Nexus revenue?" (other entity).

## Connecting the real API
Replace the functions in `src/store.ts` marked "Mock API seam" (`submitCase`, `reviewCase`, `askQuestion`) with calls to the
ASP.NET Core endpoints, and feed step updates from SignalR. The mock Q&A guardrails in `src/mock/qa.ts` only demonstrate the UI;
the real guardrails run server-side (PRD FR-10).
