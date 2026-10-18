# Technical Design Document (TDD): Loan Underwriting Orchestrator

| | |
|---|---|
| Based on | `PRD_Loan_Underwriting_Orchestrator.md` (and `Business case.docx`) |
| Status | Draft for review |
| Platform | C# / .NET, Microsoft Foundry models, React front end |
| Deployment | On-premises by default (no Docker); Azure selectable with `Deployment:Mode` |

Requirement ids (FR-1 to FR-11) refer to the PRD. Items marked **[verify]** depend on product features or versions that must be confirmed before build (see section 17).

---

## 1. Purpose and scope
This document describes how the PRD is implemented: components, interfaces, data model, workflow, decision logic, guardrails, deployment, security, observability and test strategy. It covers v1: one digital entity PDF per case, three LLM nodes, a deterministic scorecard, a decision model, human review, a guarded document Q&A panel, and an on-prem / Azure deployment switch. Email is excluded.

## 2. Design principles
1. **LLMs advise, code decides.** The risk score and the final routing are deterministic and unit-tested. Model output feeds them but never overrides them.
2. **Ports and adapters.** Business logic depends on interfaces. `OnPrem` and `Azure` adapters are chosen at startup.
3. **Immutable state, append-only history.** Every step returns a new state record and writes an audit event.
4. **Fail safe.** Any error, missing data or low confidence leads to human review or an Error status, never an automatic approval.
5. **Least scope for Q&A.** Retrieval is filtered by case in the data layer; the answering model has no tools and no network.
6. **Foundry-only models.** No model or hosted API outside the Microsoft Foundry catalogue and Azure.

## 3. System context

```
 Risk Ops / Underwriter / Auditor
          │ HTTPS (OIDC)
          ▼
   React SPA (static) ──► ASP.NET Core API ──► Workflow engine (Agent Framework)
                               │  SignalR            │
                               │                     ├─► IChatClient (Foundry Local | Azure Foundry)
                               │                     ├─► IEmbeddingGenerator
                               │                     ├─► IPdfReader (PdfPig | Document Intelligence)
                               │                     └─► Scorecard + Decision model (pure C#)
                               ▼
        SQL Server 2025 / Azure SQL            IDocumentStore (file share | Blob)
        (cases, checkpoints, audit, vectors)
```

External actors: identity provider (AD FS or Entra ID). No email service, no internet dependency in OnPrem mode.

## 4. Solution structure

```
src/
  LoanUnderwriting.Abstractions/     interfaces, records, enums (no dependencies)
  LoanUnderwriting.Scoring/          ScorecardService, EbitdaCalculator
  LoanUnderwriting.Decisioning/      DecisionTable, PolicyParameters, DecisionEvaluator
  LoanUnderwriting.DocQA/            Chunker, IngestService, Retriever, GuardrailPipeline, QaService
  LoanUnderwriting.Workflow/         LoanState, executors, WorkflowFactory, CheckpointStore contracts
  LoanUnderwriting.Adapters.OnPrem/  Foundry Local, file share, SQL Server, PdfPig
  LoanUnderwriting.Adapters.Azure/   Foundry endpoints, Blob, Azure SQL, Document Intelligence, Key Vault
  LoanUnderwriting.Api/              minimal APIs, auth, SignalR hub, composition root, startup self-check
  LoanUnderwriting.Web/              React + TypeScript (Vite)
tests/
  *.UnitTests  *.IntegrationTests  *.GuardrailTests  *.E2E
deploy/
  onprem/ (deploy.ps1, install scripts, service and IIS templates)   azure/ (main.bicep)
```

Dependency rule: `Abstractions` ← `Scoring`, `Decisioning`, `DocQA`, `Workflow` ← `Adapters.*` ← `Api`. Scoring and Decisioning have no I/O and no model access.

## 5. Platform abstraction (FR-11)

### 5.1 Ports
```csharp
public interface IDocumentStore  { Task<DocumentRef> SaveAsync(Stream s, string name, CancellationToken ct);
                                   Task<Stream> OpenAsync(DocumentRef r, CancellationToken ct);
                                   Task DeleteAsync(DocumentRef r, CancellationToken ct); }
public interface IPdfReader      { Task<PdfContent> ReadAsync(Stream pdf, CancellationToken ct); }   // pages, text, tables
public interface IVectorStore    { Task UpsertAsync(Guid caseId, IReadOnlyList<ChunkEmbedding> c, CancellationToken ct);
                                   Task<IReadOnlyList<ScoredChunk>> SearchAsync(Guid caseId, ReadOnlyMemory<float> q, int k, CancellationToken ct);
                                   Task DeleteCaseAsync(Guid caseId, CancellationToken ct); }
public interface ICheckpointStore{ /* save/load workflow checkpoints per case */ }
public interface IAuditStore     { Task AppendAsync(AuditEvent e, CancellationToken ct);
                                   IAsyncEnumerable<AuditEvent> ReadAsync(Guid caseId); }
public interface ICaseRepository { /* create, get, list, update status */ }
// Models: Microsoft.Extensions.AI
//   IChatClient, IEmbeddingGenerator<string, Embedding<float>>
```
Notes: `IVectorStore.SearchAsync` takes `caseId` as a required argument so it is impossible to search without a case filter.

### 5.2 Adapter matrix
| Port | OnPrem | Azure |
|---|---|---|
| `IChatClient` | OpenAI-compatible client pointed at the Foundry Local endpoint | Azure AI Foundry deployment endpoint, `DefaultAzureCredential` |
| `IEmbeddingGenerator` | Foundry Local embedding model **[verify availability]** | Foundry embedding deployment |
| `IDocumentStore` | NTFS share, ACL-restricted service account, BitLocker/EFS | Azure Blob Storage |
| `IPdfReader` | PdfPig | Azure AI Document Intelligence |
| `ICaseRepository`, `ICheckpointStore`, `IAuditStore`, `IVectorStore` | SQL Server 2025 via EF Core and `Microsoft.Data.SqlClient` | Azure SQL (same code) |
| Identity | OIDC with AD FS | Entra ID |
| Secrets | DPAPI / certificate-protected config | Key Vault + managed identity |
| Realtime | ASP.NET Core SignalR (SQL Server backplane for multi-node) | Azure SignalR Service |
| Telemetry | OpenTelemetry to local collector / Windows Event Log | Application Insights |

### 5.3 Mode switch and startup self-check
- `Deployment:Mode` (env `LOAN_DEPLOY_MODE`) = `OnPrem` | `Azure`. Composition root registers the matching adapter set.
- Self-check at startup: logs the mode, adapters and model names; calls each configured model with a one-token probe; checks DB connectivity and vector support; checks the file store is writable. **It refuses to start** on any failure or on a mixed configuration (for example Azure adapters with on-prem model names).
- Mode is fixed at deployment time. Switching modes does not migrate data (separate runbook).

## 6. Model configuration (PRD 3.3)
`appsettings.json` (overridable by environment variables or Azure App Configuration):
```json
"Models": {
  "Extraction":  { "OnPrem": "<foundry-local-alias>", "Azure": "<foundry-deployment-name>" },
  "Compliance":  { "OnPrem": "<...>", "Azure": "<...>" },
  "Risk":        { "OnPrem": "<...>", "Azure": "<...>" },
  "ScopeCheck":  { "OnPrem": "<same as Extraction>", "Azure": "<same as Extraction>" },
  "QaAnswer":    { "OnPrem": "<same as Compliance>", "Azure": "<same as Compliance>" },
  "Embedding":   { "OnPrem": "<...>", "Azure": "<...>", "Dimensions": 1536 }
}
```
`IModelResolver.Get(ModelRole role)` returns the `IChatClient` for the active mode. Suggested defaults (verify against `foundry model list` and the Foundry catalogue): Phi-4-mini / `gpt-4o-mini` for extraction, Phi-4 / a GPT-4.1-class model for compliance and Q&A answers, gpt-oss-20b / an o-series model for risk synthesis. Claude is allowed in Azure mode only where Foundry offers it. Gemini is excluded.

Common call settings: temperature 0 for extraction, compliance and Q&A; structured outputs (JSON schema from C# records); per-call timeout; Polly pipeline (timeout, 3 retries with exponential backoff and jitter, circuit breaker).

## 7. Domain model

```csharp
public sealed record LoanState
{
    public Guid CaseId { get; init; }
    public DocumentRef Document { get; init; } = default!;
    public CaseStatus Status { get; init; }                  // Processing, AwaitingReview, Approved, Denied, Error
    public ExtractedMetrics? Metrics { get; init; }
    public ImmutableDictionary<string, double> FieldConfidence { get; init; } = ImmutableDictionary<string, double>.Empty;
    public Completeness? Completeness { get; init; }         // missing fields, retry count
    public ImmutableList<ComplianceFlag> Flags { get; init; } = ImmutableList<ComplianceFlag>.Empty;
    public Recommendation? LlmRecommendation { get; init; } // Approve | Deny | Escalate + rationale
    public ScorecardResult? Scorecard { get; init; }
    public DecisionResult? Decision { get; init; }
    public HumanDecision? Human { get; init; }
    public FinalDecision? Final { get; init; }
}
public sealed record ExtractedMetrics(decimal GrossReceipts, decimal Returns, decimal NetRevenue, decimal OfficerComp,
    decimal Depreciation, decimal Interest, decimal TaxableIncome, decimal ShortTermDebt, decimal LongTermDebt,
    decimal? BankCredits /* for variance, optional */, decimal? DebtService /* for DSCR, optional */);
public sealed record ComplianceFlag(string Id, Severity Severity, string Rule, string Evidence);   // Low..Critical
```
Money uses `decimal`. State is never mutated; executors return `state with { ... }`. Large artefacts (report text, chunks) are stored in the database and referenced from state.

## 8. Workflow design (FR-2, FR-6, FR-7)

### 8.1 Executors and edges
```
IngestExecutor ─► ExtractionExecutor ─► ComplianceExecutor ─► RiskExecutor ─► DecisionExecutor
                                                                                  │ (conditional edges on Route)
                                   ┌──────────────────────────────┬───────────────┴───────────────┐
                                   ▼ Approve                      ▼ Deny                          ▼ Escalate
                            FinalizeExecutor              FinalizeExecutor              HumanReviewExecutor (request port)
                                                                                                   │ resume
                                                                                                   ▼
                                                                                           FinalizeExecutor
```
`PdfRead` is part of `IngestExecutor`. Every executor: (1) loads or receives state, (2) does its work, (3) appends an `AuditEvent`, (4) returns new state, (5) triggers a checkpoint and a SignalR progress event.

| Executor | Work | Failure handling |
|---|---|---|
| `IngestExecutor` | Read PDF text/tables, chunk, embed, upsert vectors (idempotent by content hash); reject files with no text layer | Error status; case not processed |
| `ExtractionExecutor` | Node 1 prompt, parse to `ExtractedMetrics`, run reconciliation checks, compute confidence | One retry on missing fields; then mark incomplete |
| `ComplianceExecutor` | Node 2 prompt with rules; produce flags | Retry per Polly; on failure Error |
| `RiskExecutor` | Run `ScorecardService`; Node 3 writes narrative and recommendation | On model failure, recommendation = Escalate and narrative marked unavailable |
| `DecisionExecutor` | `DecisionEvaluator.Evaluate(input)` | Pure; exceptions become Error |
| `HumanReviewExecutor` | Request port; pause until response | Timeout reminders in UI (no email) |
| `FinalizeExecutor` | Write decision record, set final status | Idempotent |

### 8.2 Checkpointing and resume
- State and executor position are serialised (System.Text.Json) into `workflow_checkpoint` after each executor, in the same transaction as the audit event.
- On API start or on resume request, the workflow rehydrates from the latest checkpoint for the case. Steps are idempotent so a replay after a crash is safe.
- Human review: the workflow persists `AwaitingReview` and stops consuming resources. `POST /cases/{id}/review` loads the checkpoint, supplies the response to the request port and continues.
- **[verify]** Agent Framework workflow checkpoint and request-port APIs; if unsuitable, the same design is implemented with the Semantic Kernel Process Framework or a thin custom state machine behind `IWorkflowRunner`. The rest of the design does not change.

### 8.3 Concurrency and ordering
One active run per case (row-level lock on `cases.run_lock`). Different cases run in parallel, bounded by a configurable model-concurrency semaphore (important for single-GPU on-prem hosts).

## 9. Scoring and decision logic

### 9.1 Derived metrics (`EbitdaCalculator`)
- `EBITDA = TaxableIncome + Interest + Depreciation + OfficerComp` (benchmark formula). `EbitdaExOfficer = EBITDA − OfficerComp` is always reported alongside.
- `TotalDebt = ShortTermDebt + LongTermDebt`; `Leverage = TotalDebt / EBITDA`; `Margin = EBITDA / NetRevenue`; `InterestCover = EBITDA / Interest`.
- Reconciliation checks: `GrossReceipts − Returns = NetRevenue` (tolerance 0.5%); extracted EBITDA (if the document states one) equals recomputed EBITDA. Failure sets `ReconciliationOk = false`.

### 9.2 Scorecard (`ScorecardService`, FR-4)
Creditworthiness (0 to 100, higher is better), per pillar by linear interpolation inside the benchmark bands:

| Pillar (weight) | Band to score |
|---|---|
| Debt/EBITDA (40%) | <1.5x: 100→95; 1.5 to 2.5x: 94→80; 2.5 to 4.0x: 79→60; >4.0x: 59 falling 10 per 1.0x, floor 20 |
| DSCR (30%) | >10x: 95 to 100; 5 to 10x: 80 to 94; 2 to 5x: 60 to 79; <2x: below 60. Needs `DebtService`; else unavailable |
| EBITDA margin (20%) | >20%: 90→100 (at 30%); 15 to 20%: 80→89; 10 to 15%: 70→79; <10%: 59 falling to 0 |
| Data integrity (10%) | <1% variance: 95 to 100; >5%: penalised. Needs `BankCredits`; else unavailable |

`Creditworthiness = Σ(score × weight) / Σ(weight of available pillars)`; `RiskScore = round(100 − Creditworthiness, 1)`. The result lists which pillars were unavailable. Constants live in a versioned scoring policy.

Illustrative results (DSCR and integrity unavailable): A ≈ 9.0, B ≈ 21.7, C ≈ 6.3, D ≈ 44.

### 9.3 Decision model (`DecisionEvaluator`, FR-5)
Input record: `RiskScore, Complete, MinConfidence, ReconciliationOk, Flags, LlmRecommendation`. Ordered rules, first match wins:

| # | Condition | Decision | Reason code |
|---|---|---|---|
| R1 | not Complete | Escalate | `INCOMPLETE_DATA` |
| R2 | any Critical flag | Escalate | `CRITICAL_COMPLIANCE_FLAG` |
| R3 | `MinConfidence < 0.80` or not `ReconciliationOk` | Escalate | `LOW_CONFIDENCE` / `RECONCILIATION_FAILED` |
| R4 | `RiskScore > 70` | Deny | `HIGH_RISK_SCORE` |
| R5 | `RiskScore < 30` and highest flag < High and `LlmRecommendation != Deny` | Approve | `LOW_RISK_CLEAN` |
| R6 | otherwise | Escalate | `MEDIUM_RISK` or `MODEL_DISAGREEMENT` |

Output: `Decision, Route, RuleFired, ReasonCodes[], Explanation, ModelVersion`. Policy parameters (30, 70, 0.80, severity map) are loaded from the `policy_version` table (active row), stamped on every decision, and changed only through an approved, audited update by the Risk Policy Owner role. Boundary behaviour: 30 and 70 route to review.

## 10. Data design (SQL Server 2025 / Azure SQL)

```sql
CREATE TABLE cases (
  case_id UNIQUEIDENTIFIER PRIMARY KEY, entity_name NVARCHAR(200), file_name NVARCHAR(260),
  document_ref NVARCHAR(500), submitted_by NVARCHAR(200), submitted_at DATETIME2,
  status VARCHAR(20), risk_score DECIMAL(5,1) NULL, final_decision VARCHAR(10) NULL,
  run_lock DATETIME2 NULL, row_version ROWVERSION);

CREATE TABLE workflow_checkpoint (
  case_id UNIQUEIDENTIFIER, seq INT, step VARCHAR(40), state_json NVARCHAR(MAX), created_at DATETIME2,
  PRIMARY KEY (case_id, seq));

CREATE TABLE audit_event (            -- append-only: no UPDATE/DELETE granted to the app login
  event_id BIGINT IDENTITY PRIMARY KEY, case_id UNIQUEIDENTIFIER, at DATETIME2, actor NVARCHAR(200),
  step VARCHAR(60), model NVARCHAR(100) NULL, decision_model_version VARCHAR(20) NULL,
  input_hash CHAR(64) NULL, summary NVARCHAR(MAX), tokens INT NULL, latency_ms INT NULL);

CREATE TABLE document_chunks (
  chunk_id UNIQUEIDENTIFIER PRIMARY KEY, case_id UNIQUEIDENTIFIER NOT NULL, page INT, chunk_index INT,
  text NVARCHAR(MAX), content_hash CHAR(64), embedding VECTOR(1536) NOT NULL);   -- N = embedding dimension
CREATE INDEX ix_chunks_case ON document_chunks(case_id);

CREATE TABLE qa_log (
  qa_id UNIQUEIDENTIFIER PRIMARY KEY, case_id UNIQUEIDENTIFIER, asked_by NVARCHAR(200), asked_at DATETIME2,
  question NVARCHAR(1000), refused BIT, refusal_reason VARCHAR(20) NULL, answer NVARCHAR(MAX) NULL,
  citations_json NVARCHAR(MAX) NULL, chunk_ids_json NVARCHAR(MAX) NULL);

CREATE TABLE policy_version (
  version VARCHAR(20) PRIMARY KEY, is_active BIT, approve_below DECIMAL(5,1), deny_above DECIMAL(5,1),
  min_confidence DECIMAL(3,2), parameters_json NVARCHAR(MAX), approved_by NVARCHAR(200), approved_at DATETIME2);
```
Vector search (exact, per-case; no approximate index needed at a few hundred chunks per case):
```sql
SELECT TOP (@k) chunk_id, page, text,
       VECTOR_DISTANCE('cosine', embedding, CAST(@q AS VECTOR(1536))) AS distance
FROM document_chunks WHERE case_id = @caseId
ORDER BY distance;      -- @q is a JSON array string of floats
```
**[verify]** exact vector literal/parameter syntax and EF Core / `Microsoft.Data.SqlClient` vector support for the chosen .NET version.

Retention: chunks and documents are deleted with the case per the retention policy; `audit_event` and the decision record are kept per audit retention (open item). TDE enabled; the app login has no direct DELETE on `audit_event`.

## 11. API design

| Method and path | Role | Purpose |
|---|---|---|
| `POST /api/cases` (multipart PDF) | RiskOps | Validate, store, create case, start workflow; returns `caseId` |
| `GET /api/cases`, `GET /api/cases/{id}` | RiskOps, Underwriter, Auditor | List and detail (state projection) |
| `POST /api/cases/{id}/review` `{action, reason}` | Underwriter | Resume paused workflow; rejects if caller is the submitter |
| `GET /api/cases/{id}/record?view=applicant|internal` | RiskOps (applicant), Underwriter/Auditor (internal) | Decision record download (JSON/PDF) |
| `POST /api/cases/{id}/qa` `{question}` | RiskOps, Underwriter | Ask the document |
| `GET /api/cases/{id}/qa` | RiskOps, Underwriter, Auditor | Q&A history |
| `GET /api/cases/{id}/audit` | Underwriter, Auditor | Audit events |
| `GET /api/policy`, `PUT /api/policy` | PolicyOwner | Read and propose policy parameters |
| `GET /api/health` | Internal | Mode, adapters, model probes |
| SignalR `/hubs/cases` | Authenticated | Events: `StepStarted`, `StepCompleted`, `StatusChanged` (group per case) |

Conventions: JSON, problem+json errors, correlation id header, request size limit from config, anti-forgery not needed for bearer tokens. Upload validation: content type and magic bytes (`%PDF`), size, text-layer check, antivirus scan of the stored file.

## 12. Document Q&A design (FR-10)

### 12.1 Ingestion
Chunker: sentence-aware windows of about 400 tokens with 60-token overlap; keeps `page`, nearest heading and table row context; table rows are emitted as their own chunks ("Line 1c Net revenue: $X"). Embeds in batches; upserts keyed by `(case_id, content_hash)`; re-ingest replaces.

### 12.2 Query pipeline
```
question
  → G1 input checks      (length ≤ 500, rate limit, injection/jailbreak detection)
  → G2 decision-integrity (block: score, recommend, approve/deny, flags, override)
  → G3 scope check       (small model classifier + other-entity name check)
  → embed query → vector search WHERE case_id (top k=6)
  → G4 similarity gate   (best distance ≤ T else refuse NotFound; model not called)
  → grounded generation  (QaAnswer model, temperature 0, excerpts only, must cite chunk ids)
  → G5 output checks     (citations valid; every number appears in cited text; no uncited claims)
  → answer + citations, or refusal with reason; always written to qa_log and audit_event
```
Refusal reasons: `OutOfScope`, `NotFound`, `Blocked`, `Injection`. Parameters (k, T, chunk size, thresholds) live in policy configuration.

### 12.3 Guardrail implementation
- **Injection/jailbreak:** pattern rules plus a small-model classifier; in Azure mode also Azure AI Content Safety prompt shields. Retrieved chunks are wrapped as quoted data and the system prompt states they are untrusted.
- **Scope check:** classifier prompt returns `{in_scope: bool, category}`; the question must concern the loaded document's content. Names of other companies trigger `OutOfScope`.
- **Grounding check:** extract numbers and currency values from the answer; each must appear (normalised) in the cited chunk text; otherwise refuse. A second small-model "is each claim supported by the quotes" check is applied before display; failure refuses.
- **Isolation:** `IVectorStore.SearchAsync(caseId, …)` always applies `WHERE case_id = @caseId`; the Q&A service has no other data access; in OnPrem mode its host has no outbound network route (firewall rule); the model client has no tools or function-calling enabled.
- Answers never include the risk score, flags or recommendation, because those are neither in the chunks nor retrievable.

## 13. Front end design (React + TypeScript)
- Vite SPA, hash routing, no heavy UI library. The built look follows the reference dashboard (dark sidebar, rounded white panel, colour-topped KPI cards, pills, line chart, gauge, light/dark theme).
- Screens: Dashboard, New case (upload), Cases, Case detail (Progress, Analysis, Underwriter review, Ask the document, Decision record, Audit), Audit log, Settings, Help.
- State: a small store with an API client behind it. The prototype in `frontend/` runs on a mock API; the real implementation replaces `submitCase`, `reviewCase`, `askQuestion` with API calls and feeds step status from SignalR.
- Auth: OIDC (MSAL / oidc-client), bearer tokens to the API; role claims drive visible actions, and the API enforces them again.
- Accessibility and quality: keyboard operable controls, `aria-live` on the chat, error messages in `role="alert"`, no secrets in the client.

## 14. Security design
- **AuthN/Z:** OIDC; roles `RiskOps`, `Underwriter`, `Auditor`, `PolicyOwner`. Separation of duties enforced in `review` endpoint (`submitted_by ≠ caller`).
- **Data protection:** TLS 1.2+, SQL TDE, encrypted file share/Blob; keys per platform. PII/financial values redacted from logs; prompts and responses stored only in the audit tables, not in general logs.
- **Network:** OnPrem: model host and Q&A service restricted to the internal network; no internet egress needed. Azure: private endpoints and VNet integration, managed identity, no keys in config.
- **Upload safety:** type, magic bytes, size, text-layer check, antivirus; stored files named by GUID; no execution of document content.
- **LLM threat model:** prompt injection from PDF text (mitigated by data/instruction separation, structured outputs, deterministic scoring and decisions); data exfiltration through Q&A (no tools, no network, case filter); hallucinated numbers (recomputation and grounding checks); over-reliance (human review band, decision model).
- **Audit integrity:** append-only table, hash of inputs, decision-model version stamped; consider hash chaining of events **[decision]**.

## 15. Deployment and operations (FR-11)

### 15.1 OnPrem (no Docker)
`deploy.ps1 -Target OnPrem` (administrator, target Windows Server):
1. Install prerequisites: .NET hosting bundle, Foundry Local, optionally SQL Server 2025 Express.
2. Create the database, apply migrations (including the vector table) and seed `policy_version`.
3. Create the document folder share with restricted ACLs and enable encryption.
4. Publish the API (`dotnet publish`) as a Windows Service (or IIS site) under a dedicated service account; place the React build as static files.
5. Write protected configuration (`Deployment:Mode=OnPrem`, OIDC authority, model names, connection strings).
6. Download the configured models with Foundry Local (or import an offline package) and start the self-check.
Scaling: two or more API nodes behind IIS ARR / a load balancer; SignalR uses the SQL Server backplane; model host sized for the concurrency limit.
Developer machines: `dotnet run`, `npm run dev`, a local SQL Server 2025 instance, Foundry Local.

### 15.2 Azure
`deploy.ps1 -Target Azure` runs `main.bicep`: Foundry project and model deployments, App Service (API and web), Azure SQL, Blob Storage, Azure SignalR, Key Vault, Application Insights, private networking. Managed identity grants access to Foundry, SQL and Blob.

### 15.3 Operations
- Health endpoint and startup self-check; Windows Service auto-restart on failure.
- Backups: SQL full and log backups; documents backed up with the share; restore runbook tested quarterly.
- Observability: OpenTelemetry traces (API → workflow → model), metrics per step (latency, tokens, cost where applicable), dashboard for queue depth, error rate, refusal rate and override rate.
- Release: build in CI, run all tests, publish zip/MSI; model and policy changes are versioned and audited.

## 16. Test strategy
| Level | What | Tooling |
|---|---|---|
| Unit | Scorecard bands and renormalisation; EBITDA and reconciliation; every decision rule R1 to R6 with boundaries (29/30/70/71, confidence 0.79/0.80); chunker; number-grounding check | xUnit |
| Golden | Entities A to D from the benchmark: metrics, risk score, decision, route (A, B, C approve; D review) | xUnit with fixtures |
| Workflow | Mock `IChatClient`: each route, incompleteness retry, node failure → Error, AI/scorecard disagreement, resume after restart | xUnit, local SQL Server |
| Integration | EF Core and vector queries on SQL Server 2025; file store; PdfPig on benchmark PDFs; API auth and role rules | xUnit, local instance |
| Guardrail | Out-of-scope set (general knowledge, other companies, advice, code), injection and role-play prompts, score/decision requests, absent facts; cross-case isolation; injected instruction inside a PDF | Data-driven xUnit; target 100% refusal |
| Model evaluation | Extraction accuracy per mode on benchmark line items (≥98% Azure; agreed minimum on-prem); Q&A in-scope accuracy ≥95% with valid citation | Evaluation harness; tracked per release |
| E2E | Upload each benchmark PDF; stepper; HITL for D; override; record download; mode switch run in both modes | Playwright |
| Security | Role enforcement, separation of duties, upload abuse, network capture shows no outbound calls on-prem | Manual plus scripts |
| Performance | Latency per case and per Q&A on target hardware; concurrency limits | Load script |

## 17. Risks, assumptions and open items
| # | Item | Impact | Plan |
|---|---|---|---|
| 1 | Agent Framework workflow checkpoint/request-port features may not be GA | Orchestration redesign | Isolate behind `IWorkflowRunner`; fallback Semantic Kernel Process Framework or custom state machine |
| 2 | Foundry Local suitability for multi-user server hosting, hardware, licensing | On-prem capacity | Pilot on target hardware; define concurrency limit; evaluate Azure Local/Arc options |
| 3 | Embedding model availability in Foundry Local | Q&A on-prem blocked | Confirm early; decision needed if none (scope excludes non-Foundry models) |
| 4 | SQL Server 2025 vector syntax, edition support, EF/ADO.NET support | Q&A data layer | Spike in milestone 1; keep SQL behind `IVectorStore` |
| 5 | Small on-prem models extract less accurately | More human review | Code cross-checks, confidence rule R3, per-mode accuracy targets |
| 6 | Model catalogue names, region and quota for Azure | Deployment | Verify before build; names in config |
| 7 | DSCR and bank totals absent from benchmark input | Score uses fewer pillars | Renormalise and disclose; add inputs when available |
| 8 | Loan amount and exposure limits not modelled | Decision completeness | Policy owner to decide |
| 9 | Identity provider (AD FS vs other), approval limits | Access design | IT decision |
| 10 | Audit retention and data-residency rules | Storage and deletion | Compliance decision |
| 11 | No email: applicant is informed outside the system | Manual step | Accepted by decision; downloadable record provided |

## 18. Traceability (PRD to design)
| PRD | Design section |
|---|---|
| FR-1 Input | 11 (upload API), 13, 14 |
| FR-2 State and audit | 7, 8, 10 |
| FR-3 Nodes | 6, 8.1 |
| FR-4 Scorecard | 9.1, 9.2 |
| FR-5 Decision model | 9.3 |
| FR-6 Routing | 8.1, 9.3 |
| FR-7 HITL | 8.2, 11 |
| FR-8 Progress UI | 11 (SignalR), 13 |
| FR-9 Decision record (no email) | 11, 13 |
| FR-10 Document Q&A | 10, 12 |
| FR-11 Deployment mode | 5, 15 |
| NFRs | 14, 15.3, 16 |

## 19. Implementation plan (aligned with PRD milestones)
1. Abstractions, `LoanState`, scorecard and decision model with golden tests; vector spike on SQL Server 2025.
2. Workflow skeleton with stub executors, checkpoint store, routing, human-review pause/resume.
3. OnPrem adapters, Node 1 on benchmark PDFs, accuracy measurement.
4. Nodes 2 and 3, tuning of prompts and chunking.
5. API, SignalR, OIDC roles, React screens wired to the API.
6. Document Q&A: ingestion, retrieval, guardrails, panel, guardrail test set.
7. Azure adapters, Bicep, mode switch and self-check; repeat accuracy tests.
8. End-to-end tests in both modes, security hardening, performance test, runbooks.
