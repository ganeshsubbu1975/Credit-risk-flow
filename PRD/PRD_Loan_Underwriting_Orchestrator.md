# PRD: Multi-LLM Commercial Loan Underwriting Orchestrator (C# / Microsoft Foundry, On-Prem First, Azure Optional)

## Context
Risk Operations currently takes days to assess commercial loan applications from tax returns and bank statements. This PRD defines an automated system, built in C# (.NET) with Microsoft Foundry models, that extracts financials from an uploaded PDF, runs fraud/compliance checks, scores risk, applies a formal decision model, and routes the case to auto-approve, auto-deny, or a human underwriter. Source: `Business case.docx`. Test data: `Financial_Metrics_Benchmark_4_Entities.pdf` (Entities A-D) and `Financial Metrics Benchmark Dataset.pdf`.

**Two deployment modes, one codebase.** The solution runs fully **on-premises** by default, with no data leaving the organisation. A single setting, `Deployment:Mode = OnPrem | Azure` (env var `LOAN_DEPLOY_MODE`), switches the platform adapters and the model endpoints, and enables the Azure deployment path.

**Deviations from the business case**
- LangGraph is Python-only. The .NET equivalent is the **Microsoft Agent Framework workflow engine** (executors and edges, typed shared state, checkpointing, human-input request ports). Mapping in section 3.4.
- **Model scope is restricted to models supported by Azure and Microsoft Foundry.** Gemini 1.5 Pro (not available on Foundry) is removed. See section 3.3.
- **Email is excluded.** The business case's email of the final decision is replaced by an on-screen decision record that Risk Ops can download (FR-9).
- A formal **Decision Model** (section 4, FR-5) is added. The LLMs advise; the decision model decides.
- A **Document Q&A** panel (FR-10) is added: underwriters can ask questions answered only from the loaded PDF, retrieved from SQL Server's native vector type, behind strict scope guardrails.

## 1. Goals and Non-Goals
**Goals**
- Cut processing time from days to minutes.
- Enforce KYC/AML compliance and fraud flagging before any decision.
- Give a full audit trail, explainable decisions and a human override for medium-risk cases.
- Let underwriters ask questions about the loaded document and get cited answers drawn only from that document.
- Run completely on-prem, with Azure as a switchable deployment target, not a rewrite.

**Non-Goals (v1)**
- Loan pricing, term-sheet generation, or funds disbursement.
- Batch applications (one entity PDF per run).
- Scanned / image-only PDFs and OCR.
- Multi-region active-active deployment.
- Any model or LLM service outside the Microsoft Foundry catalogue and Azure.

## 2. Users
| Persona | Need |
|---|---|
| Risk Operations Officer | Submit an entity PDF, watch progress, see the outcome |
| Human Underwriter | Review AI reasoning on medium-risk cases, override, resume |
| Applicant company | Is informed of the decision by Risk Ops outside the system (no email from the system) |
| Compliance / Audit | Inspect audit log, decision-model versions and model usage |
| Risk Policy Owner | Maintain and approve decision-model parameters |

## 3. Architecture

### 3.1 Design principle: ports and adapters
The workflow, scoring and decision model are platform-neutral C# and depend only on interfaces. Each interface has an on-prem adapter and an Azure adapter, selected by `Deployment:Mode` at startup through DI.

| Port (interface) | OnPrem adapter | Azure adapter |
|---|---|---|
| `IChatClient` (`Microsoft.Extensions.AI`) | Foundry Local OpenAI-compatible endpoint (local runtime) | Azure AI Foundry deployment endpoint, `DefaultAzureCredential` |
| `IDocumentStore` | Encrypted NTFS file share (BitLocker / EFS, ACL-restricted service account) | Azure Blob Storage |
| `ICaseRepository`, `ICheckpointStore`, `IAuditStore` | SQL Server 2025 (EF Core; 2025 is required for the native `vector` type) | Azure SQL Database (same EF Core provider) |
| `IPdfReader` | PdfPig text and table extraction (native .NET library, no container) | Azure AI Document Intelligence (cloud) |
| `IVectorStore` | SQL Server 2025 native `VECTOR(n)` column and `VECTOR_DISTANCE`, in the same SQL Server instance (no second database engine, no Docker) | Azure SQL Database native `VECTOR(n)` type, same code |
| `IEmbeddingGenerator` (`Microsoft.Extensions.AI`) | Embedding model served by Foundry Local (to be confirmed, see open items) | Foundry embedding deployment, e.g. `text-embedding-3-small` |
| `ISecretProvider` | Windows DPAPI / certificate-protected configuration, or Windows Credential Manager for the service account | Azure Key Vault + managed identity |
| Identity | OIDC against Active Directory Federation Services (or on-prem Entra-compatible IdP) | Microsoft Entra ID |
| Realtime progress | ASP.NET Core SignalR (self-hosted, SQL Server backplane when more than one API node) | Azure SignalR Service |
| Telemetry | OpenTelemetry to the existing on-prem collector or log store, or local file/Windows Event Log exporters | Application Insights |

### 3.2 Technology summary
| Layer | Choice |
|---|---|
| Frontend | React (Vite, TypeScript), served by the API host or nginx; MSAL / OIDC sign-in |
| API | ASP.NET Core minimal APIs, .NET 10 LTS (or .NET 8 LTS) |
| Orchestration | Microsoft Agent Framework workflows (`Microsoft.Agents.AI.Workflows`) |
| Decision model | `LoanUnderwriting.Decisioning` library: versioned decision table plus deterministic scorecard (FR-4, FR-5) |
| On-prem hosting | **No Docker, containers or Kubernetes anywhere in the local/on-prem path.** The API runs as a Windows Service (Kestrel) or under IIS on Windows Server; React is built to static files served by the API or IIS; Foundry Local runs as a native process/service; SQL Server 2025 (Express or Developer for dev/pilot, Standard for production), which also stores document chunks and embeddings in its native vector type |
| Azure hosting | Azure App Service (code deploy, no container) for the API and web, provisioned by Bicep |
| IaC / CI-CD | PowerShell + MSI/zip packages and `dotnet publish` (on-prem), Bicep (Azure), GitHub Actions or Azure DevOps |
| Developer machine | `dotnet run` / `npm run dev`, a local SQL Server 2025 Developer/Express instance, Foundry Local; no Docker Desktop required |
| Document Q&A | `LoanUnderwriting.DocQA` library: chunking, ingestion into SQL Server vector columns, case-scoped retrieval, guardrail pipeline (FR-10) |

### 3.3 Models: only those supported by Azure and Microsoft Foundry
Every model call goes through `IChatClient`. Deployment names are set in `appsettings.json` (`Models:<Node>:<Mode>`), never hard-coded. At startup the API validates that each configured model is reachable and fails fast if not.

| Node | OnPrem (Foundry Local catalogue) | Azure (Foundry deployments) |
|---|---|---|
| 1 Extraction and Verification (fast, low cost) | A small Phi-family model, e.g. Phi-4-mini | `gpt-4o-mini` (benchmark baseline) or a current mini-tier GPT deployment |
| 2 Fraud and Compliance (long context) | Phi-4 (14B class); long documents are chunked and merged because local context is shorter | A long-context GPT-4.1-class deployment |
| 3 Risk Synthesis (reasoning) | A reasoning-capable catalogue model, e.g. gpt-oss-20b or a Phi reasoning model | An o-series reasoning deployment, or a Claude model offered in the Foundry catalogue (Azure mode only) |
| Q&A scope check (fast) | Same small Phi-family model as Node 1 | Same mini-tier deployment as Node 1 |
| Q&A answer (grounded) | Same model as Node 2 (Phi-4 class) | Same GPT-4.1-class deployment as Node 2 |
| Embeddings (ingestion and queries) | An embedding model from the Foundry Local catalogue, if one is offered (open item) | A Foundry embedding deployment, e.g. `text-embedding-3-small` |

- **Excluded:** Gemini (any version), and any model or hosted API outside the Foundry catalogue and Azure. Claude is allowed only because Foundry offers it, and only in Azure mode (it is not available for on-prem).
- **Verification required:** the exact model names above are suggestions. Confirm each against `foundry model list` (Foundry Local) and the Foundry catalogue for your region and quota before build.
- **Accuracy mitigation for small on-prem models:** structured-output schemas; arithmetic cross-checks (Gross - Returns = Net; EBITDA recomputed in code, not trusted from the LLM); a per-field confidence value; low confidence or failed reconciliation routes the case to human review (FR-5 rule R3). Benchmark accuracy is measured separately per mode.

### 3.4 LangGraph to .NET mapping
| Business-case concept | .NET implementation |
|---|---|
| `StateGraph` | Agent Framework `Workflow` built with `WorkflowBuilder` |
| Node | Executor class (`ExtractionExecutor`, `ComplianceExecutor`, `RiskExecutor`, `DecisionExecutor`, `IngestExecutor`, `ApproveExecutor`, `DenyExecutor`, `HumanReviewExecutor`, `FinalizeExecutor`) |
| Conditional edge | Conditional / switch edges on the decision model's `Route` output |
| Shared state | Immutable C# `record LoanState`; executors return new instances via `with` |
| Persistence | Checkpoints in SQL Server / Azure SQL via `ICheckpointStore` |
| Breakpoint / interrupt | `RequestPort` that pauses the workflow for human input; resumed by an API call |
| Audit history | Append-only `AuditEvent` table, written after every executor |

### 3.5 Flow
```
React UI -> ASP.NET Core API -> IDocumentStore (PDF) -> Workflow
 Upload -> IPdfReader -> Ingest (chunk + embed -> SQL Server vector table, tagged with caseId)
                      -> N1 Extract/Verify -> N2 Fraud/Compliance -> N3 Risk Synthesis
        -> Scorecard (deterministic) -> DECISION MODEL
              Approve ---------------------------------------> Finalize (decision record)
              Deny ------------------------------------------> Finalize
              Escalate -> HITL request port -> Underwriter -> Finalize

Document Q&A (side path, available once Ingest completes):
 UI question -> guardrails (input) -> vector search (WHERE case_id = current) -> grounded answer
             -> guardrails (output: citations + numeric check) -> answer with page citations, or refusal
```

## 4. Functional Requirements

### FR-1 Input screen (React)
- Input scope: **digital (text-based) PDFs only**; scanned or image-only PDFs are out of scope for v1 and OCR is not required. A PDF with no extractable text layer is rejected at upload with a clear message.
- Fields: PDF upload (single file, PDF only, size limit configurable, default 20 MB). There is no email field; the system sends no email.
- The browser uploads through the API, which stores the file via `IDocumentStore` and returns a `caseId`. The API starts the workflow with the document reference.
- Server-side validation of content type, magic bytes, size and malware scan (Defender for Storage in Azure; Microsoft Defender Antivirus on the file-share host on-prem).
- After upload the workflow's first step (`IngestExecutor`) chunks the PDF and loads it into the SQL Server vector table so that Document Q&A (FR-10) is available.

### FR-2 Workflow state and audit
- One shared `LoanState` record: `CaseId`, `DocumentRef`, `ExtractedMetrics`, `FieldConfidence`, `Completeness`, `ComplianceFlags`, `Scorecard`, `LlmRecommendation`, `DecisionResult`, `HumanDecision`, `FinalDecision`, `Status`.
- State is immutable: `record` types, `init`-only properties, `ImmutableList<T>`. Each executor returns a new state.
- History is a separate append-only `AuditEvent` stream. Each event holds case id, step, model name and mode, decision-model version, timestamp, input hash, output summary, token usage, latency, actor.
- Checkpoints are written after every executor; the audit log is exportable per case as JSON.

### FR-3 Nodes
| Node | Responsibility | Output |
|---|---|---|
| 1 Extraction and Verification | Extract gross/net revenue, returns, officer comp, D&A, interest, taxable income, short/long-term debt, EBITDA from tax return and bank statement; check completeness | Structured JSON metrics, `MissingFields`, per-field confidence (C# record schema, structured outputs) |
| 2 Fraud and Compliance | Check KYC/AML rules, tax-vs-bank variance, internal arithmetic consistency; flag anomalies | `ComplianceFlags[]` with severity (Low/Medium/High/Critical), rule id, evidence |
| 3 Risk Synthesis | Combine N1 and N2, write the risk narrative, produce an LLM recommendation | `LlmRecommendation` (Approve/Deny/Escalate), rationale |

- **EBITDA formula** (benchmark): Taxable Income (L21) + Interest (L13) + D&A (L14) + Officer Comp (L7). Officer comp is a non-standard add-back; reports also show EBITDA without it.
- **Completeness:** if required fields are missing, Node 1 retries once, then the case escalates rather than guessing.
- **Resilience:** Polly (timeout, exponential backoff, 3 tries, circuit breaker). A node failure sets the case to `Error`, which shows in the case list for Risk Ops; it never auto-approves.
- **Prompt-injection defence:** document text is passed as data, never as instructions; Content Safety shields in Azure mode and a pattern/heuristic filter on-prem.

### FR-4 Scorecard (risk score)
The benchmark PDF scores creditworthiness (higher = better). The business case routes on risk (higher = worse). **Risk Score = 100 - Creditworthiness Score.**

| Pillar | Weight | Bands |
|---|---|---|
| Debt/EBITDA | 40% | <1.5x: 95-100; 1.5-2.5x: 80-94; 2.5-4.0x: 60-79; >4.0x: <60 |
| DSCR | 30% | >10x: 95-100; 5-10x: 80-94; 2-5x: 60-79; <2x: <60 |
| EBITDA margin | 20% | >20%: 90-100; 15-20%: 80-89; 10-15%: 70-79; <10%: <60 |
| Data integrity (tax-to-bank variance) | 10% | <1%: 95-100; >5%: penalised |

- Computed by a deterministic C# `ScorecardService` (decimal arithmetic) from the extracted metrics. The LLM does not set the score.
- If DSCR or bank totals are absent, the pillar is marked unavailable and the weights are renormalised, and the report states this.
- Benchmark entities, inverted: A about 9.5, B about 21.4, C about 5.8, D about 47.9 (indicative; DSCR and integrity inputs are not in the PDF).

### FR-5 Decision Model
A formal, versioned, explainable decision model sits between scoring and routing. It follows a DMN-style decision table with an ordered, first-match hit policy, and is evaluated by deterministic C# code (`LoanUnderwriting.Decisioning`). The LLM recommendation is an input, never the decider.

**Inputs:** `RiskScore`, `Completeness`, `FieldConfidence` (minimum), `ReconciliationOk`, `ComplianceFlags` (highest severity), `LlmRecommendation`, `DataIntegrityAvailable`.

**Decision table (evaluated top to bottom, first match wins; thresholds are policy parameters):**
| Rule | Condition | Decision | Route | Reason code |
|---|---|---|---|---|
| R1 | Required fields still missing after retry | Escalate | Human review | `INCOMPLETE_DATA` |
| R2 | Any Critical compliance flag | Escalate | Human review (compliance officer); never auto-approve | `CRITICAL_COMPLIANCE_FLAG` |
| R3 | Minimum field confidence < 0.80, or arithmetic reconciliation failed | Escalate | Human review | `LOW_CONFIDENCE` / `RECONCILIATION_FAILED` |
| R4 | Risk score > 70 | Deny | Auto-Deny | `HIGH_RISK_SCORE` |
| R5 | Risk score < 30 and highest flag below High, and LLM recommendation is not Deny | Approve | Auto-Approve | `LOW_RISK_CLEAN` |
| R6 | Anything else, including scores 30-70 inclusive and LLM / scorecard disagreement | Escalate | Human review | `MEDIUM_RISK` / `MODEL_DISAGREEMENT` |

- **Output:** `Decision`, `Route`, `ReasonCodes[]`, `DecisionModelVersion`, `RuleFired`, plain-language explanation. Denials carry adverse-action reason codes for the applicant communication.
- **Governance:** parameters (30, 70, 0.80, severity mapping) live in a versioned policy file/table. Changes need Risk Policy Owner approval, are version-stamped, and every decision records the version used. Rules are unit-tested against a golden decision-table test suite.
- **Resolves the open item** on a Critical flag combined with a low score: rule R2 forces human review.

### FR-6 Routing
- Conditional edges follow `DecisionResult.Route`: Auto-Approve, Auto-Deny, Human Review.
- Scores of exactly 30 and 70 go to human review.
- Benchmark expectation: A, B and C auto-approve; D (about 47.9) goes to human review. D is the HITL test case.

### FR-7 Human-in-the-Loop
- For Escalate cases the workflow pauses at a `RequestPort` before the final decision; the checkpoint means a restart or redeploy does not lose the case.
- Underwriter screen (role `Underwriter`) shows extracted metrics with confidence, flags with evidence, AI rationale, scorecard breakdown, the decision-model rule fired with reason codes, and the full step history.
- Actions: **Approve**, **Deny**, or **Override** with a mandatory reason. The API posts the response to the paused workflow, which resumes from the checkpoint. The AI recommendation, decision-model result, human decision, user id and timestamp are appended to the audit stream.
- The submitter cannot act as underwriter on the same case (separation of duties).

### FR-8 Progress UI
- A stepper shows Upload, PDF read, Ingest to vector DB, Node 1, Node 2, Node 3, Scorecard, Decision model, HITL (if applicable), Final decision, with status pushed live over SignalR.
- Each step expands to show its output summary, model, latency and tokens.
- A case list shows status; reopening a case restores state from the database and checkpoint.

### FR-9 Final decision record (no email)
- **Email is excluded.** The system sends no email and has no mail/SMTP/Communication Services dependency in either mode.
- On the final decision the `FinalizeExecutor` stores a decision record and shows it on the case page: decision, plain-language reason, and for denials the adverse-action reason codes.
- Risk Ops can download the decision record as a PDF or JSON to pass to the applicant through their own channel. The downloadable applicant copy never includes the internal score or fraud flags; the internal view does.
- Finalisation is logged to the audit stream.

### FR-10 Document Q&A with guardrails
**Purpose.** An "Ask the document" panel on the case page (roles `RiskOps` and `Underwriter`) lets the user ask questions about the loaded PDF and get answers with page citations. The assistant knows nothing outside that one document.

**Ingestion (SQL Server native vector type).**
- `IngestExecutor` runs right after PDF read. It splits the text into chunks (about 300-500 tokens, with overlap, keeping page number, section heading and table context), embeds each chunk through `IEmbeddingGenerator`, and stores it via `IVectorStore`.
- SQL Server table `document_chunks(chunk_id, case_id, document_id, page, chunk_index, text, embedding VECTOR(N), content_hash)` with an index on `case_id`. Retrieval is an exact nearest-neighbour query: `SELECT TOP (k) ... WHERE case_id = @caseId ORDER BY VECTOR_DISTANCE('cosine', embedding, @queryVector)`. Because each case holds only a few hundred chunks and the filter narrows to one case, exact search is fast and no approximate (ANN) vector index is needed. `N` is the embedding model's dimension, set in config.
- Idempotent: re-running ingestion for the same document replaces its chunks. Deleting a case deletes its chunks; retention follows the audit policy.
- Q&A is enabled only when ingestion has completed for that case. The ingestion step appears in the progress stepper.

**Retrieval scope (hard limits).**
- Every query is `WHERE case_id = :currentCase` (enforced in the data layer, not by the prompt). No cross-case search, no search over other documents, the web, the benchmark data, or any other source.
- The answering model is given only the retrieved chunks and has no tools, plugins, browsing or function calling. In OnPrem mode the Q&A service has no outbound network access.
- Top-k retrieval (default 6) with a minimum similarity threshold. If nothing passes the threshold the answer is a refusal ("not found in the loaded document"); the model is not called.

**Guardrail pipeline (all layers must pass).**
1. *Input checks:* length limit, rate limit per user, prompt-injection and jailbreak detection (Azure AI Content Safety prompt shields in Azure mode; rule and classifier checks on-prem), and blocking of instructions such as "ignore previous instructions" or "act as".
2. *Scope check:* a small-model classifier decides whether the question is about the content of the loaded document. Out of scope includes general knowledge, news, markets, legal or tax advice, other companies or entities, writing code, opinions, and questions about the assistant itself. Out-of-scope questions get a fixed refusal and no retrieval. The retrieval similarity threshold is a second, independent scope check.
3. *Decision-integrity block:* the panel does not reveal or change the risk score, decision-model result, compliance analysis or the AI recommendation, and cannot approve or deny. It answers only what the document says.
4. *Grounded generation:* system prompt requires answering only from the supplied excerpts, quoting figures exactly, citing page and chunk, and replying "I can't find that in the loaded document" otherwise. Temperature 0.
5. *Output checks:* an answer without valid citations is replaced by a refusal. Every number in the answer must appear in the cited chunks (deterministic check). A grounding check rejects statements not supported by the cited text. Arithmetic or comparisons that go beyond the document (for example, forecasts) are refused; simple sums of figures stated in the document are allowed only if each input is cited.
6. *PII and safety:* output is scanned so it contains only content from the document; nothing is added from model memory.

**UI.**
- Chat panel with suggested questions (for example "What is the reported net revenue?"), the answer, and expandable citations showing the page number and the source excerpt; clicking a citation opens that page of the PDF.
- Refusals are shown in a distinct style with the reason category (out of scope, not found in document, blocked) and a reminder of what the panel can answer.
- History of questions for the case is shown; no memory is shared between cases.

**Audit.** Each question logs user, case, timestamp, scope-check result, retrieved chunk ids and scores, answer or refusal reason, model names and token usage in the append-only audit stream. Q&A never changes workflow state or the decision.

**API.** `POST /cases/{caseId}/qa` (question) returns `{answer, citations[], refused, reason}`; `GET /cases/{caseId}/qa` returns history.

### FR-11 Deployment mode switch
- `Deployment:Mode` selects adapters (section 3.1) and model endpoints (section 3.3). The same published .NET build runs in both modes.
- **On-prem delivery has no Docker.** `deploy.ps1 -Target OnPrem` (run as administrator on the target Windows Server) does the following:
  1. Installs prerequisites: .NET hosting bundle, Foundry Local, and optionally SQL Server 2025 Express.
  2. Creates the SQL Server database (including the vector table) and applies migrations.
  3. Creates the file-share folder with restricted ACLs.
  4. Publishes the API as a Windows Service (or an IIS site) running under a dedicated service account, with the React build as static files.
  5. Sets `Deployment:Mode=OnPrem` and OIDC and model settings in a protected config file.
  6. Downloads the configured Foundry Local models (or loads them from an offline package for air-gapped hosts) and runs the startup self-check.
- Azure delivery: `infra/azure/main.bicep` provisions the Foundry project and model deployments, App Service, Azure SQL (also holds the vectors), Blob Storage, SignalR, Key Vault and monitoring. `deploy.ps1 -Target Azure` runs it.
- A startup self-check prints the active mode, adapters and models, and refuses to start with a mixed or invalid configuration (for example Azure adapters with on-prem model names).
- Mode is a deployment-time setting; switching modes does not migrate existing cases (data migration is a separate runbook).

## 5. Non-Functional Requirements
- **Latency:** Azure mode, auto-routed cases under 3 minutes for a 20-page PDF. On-prem target depends on hardware and is measured in the pilot (expect slower on CPU-only hosts; GPU recommended for Phi-4 and the reasoning model).
- **Security:** encryption at rest (SQL TDE, encrypted object store/file share, optional customer-managed keys in Azure); TLS everywhere; no outbound internet required on-prem; in Azure, private endpoints and VNet integration, managed identity instead of keys; PII redacted in logs; uploads validated.
- **Data residency:** in OnPrem mode, no document, embedding or prompt leaves the network.
- **Q&A performance:** a typical question answered within 10 seconds in Azure mode; on-prem measured in the pilot.
- **Q&A isolation:** a question can never retrieve chunks from another case (verified by tests and enforced in the data layer).
- **Compliance:** audit retention per policy (open item); every decision explainable from stored artefacts, including decision-model version.
- **Reliability:** idempotent resume; crash mid-run resumes from the last checkpoint; at least 2 API nodes in production behind IIS / a load balancer (Windows Service auto-restart on failure); SQL backup and restore runbook.
- **Observability:** OpenTelemetry traces across API, workflow and model calls; per-node cost/latency/token metrics.
- **Cost control (Azure):** per-case token budgets, Foundry deployment quotas.

## 6. Success Metrics
- Median processing time under 5 minutes in Azure mode; on-prem target set after pilot.
- Extraction accuracy of at least 98% on benchmark line items in Azure mode; on-prem accuracy measured and a minimum agreed (small local models may score lower, compensated by FR-5 rule R3).
- 100% of cases carry a complete audit history including decision-model version.
- Human override rate, AI-vs-human disagreement rate and share of cases sent to review are tracked.
- Document Q&A: 100% refusal of the out-of-scope and injection test set, at least 95% of in-document questions answered correctly with a valid citation, zero cross-case leaks.

## 7. Proposed Solution Layout
```
src/
  LoanUnderwriting.Api/             minimal APIs, auth, SignalR hub, upload, HITL endpoints, mode self-check
  LoanUnderwriting.Workflow/        LoanState, executors, workflow builder, checkpoint contracts
  LoanUnderwriting.Scoring/         ScorecardService, EBITDA calculator
  LoanUnderwriting.Decisioning/     decision table, policy parameters, evaluator, reason codes
  LoanUnderwriting.DocQA/           chunker, ingestion, retrieval, guardrail pipeline, QA endpoints' logic
  LoanUnderwriting.Abstractions/    ports (IChatClient, IEmbeddingGenerator, IVectorStore, IDocumentStore, IPdfReader, ...)
  LoanUnderwriting.Adapters.OnPrem/ Foundry Local, file share, SQL Server 2025 (relational + vector), PdfPig
  LoanUnderwriting.Adapters.Azure/  Foundry endpoints, Blob, Azure SQL (relational + vector), Document Intelligence, Key Vault
  LoanUnderwriting.Web/             React app (UploadPage, CaseProgress, UnderwriterReview, AskDocumentPanel)
tests/
  *.UnitTests, *.IntegrationTests (local SQL Server 2025 instance, mocked IChatClient), decision-table golden tests, Q&A guardrail test set
deploy/
  onprem/ deploy.ps1, install scripts, service/IIS config templates      azure/ main.bicep
```
Existing assets to reuse: benchmark PDFs as fixtures; the EBITDA and leverage formulas from `Financial_Metrics_Benchmark_4_Entities.pdf`; `generate_entity_reports.py` as a reference for the metric derivations, to be ported to C#.

## 8. Milestones
1. Abstractions, `LoanState`, scorecard and decision model with golden tests (no LLM needed).
2. Workflow skeleton with stub executors, checkpoint store on SQL Server, routing, HITL pause/resume.
3. On-prem adapters: Foundry Local client, PDF reader, file-share store; Node 1 against benchmark PDFs with accuracy measurement.
4. Nodes 2 and 3 on on-prem models; tune chunking and prompts.
5. API, SignalR progress, OIDC roles, React screens.
6. Document Q&A: chunking and SQL Server vector ingestion, case-scoped retrieval, guardrail pipeline, Ask-the-document panel, guardrail test set.
7. Azure adapters, Bicep, and the mode switch with self-check; repeat accuracy tests on Azure models.
8. End-to-end tests in both modes, security hardening, load test, deployment runbooks.

## 9. Verification
- Unit: scorecard against Entities A-D; every decision-table rule R1-R6 with boundary cases (scores 29/30/70/71, confidence 0.79/0.80); EBITDA calculator.
- Workflow: with a mocked `IChatClient`, test each route, completeness failure, node error, and LLM / scorecard disagreement.
- Persistence: start an Escalate case, restart the Windows Service, resume, and confirm state and audit stream intact.
- Mode switch: run the same E2E suite with `Deployment:Mode=OnPrem` and `Azure`; confirm self-check rejects a mixed configuration; confirm no outbound calls in OnPrem mode (network capture).
- E2E: upload each benchmark PDF; confirm the stepper, the HITL screen for Entity D showing rule fired and reason codes, the override path, and the final decision record download (applicant copy excludes score and flags).
- Security: role enforcement, separation of duties, prompt-injection test documents, private-endpoint-only access (Azure).
- Model evaluation: per-mode extraction accuracy on the benchmark, tracked per release.
- Q&A in-scope: for each benchmark entity, ask questions with known answers (for example net revenue, total debt, interest expense, line numbers); confirm correct value, correct page citation and no extra information.
- Q&A out-of-scope: a fixed test set must all be refused with no retrieval or model call where applicable: general knowledge ("capital of France"), other companies, market or legal advice, requests to write code, "ignore your instructions" and role-play prompts, requests for the risk score or to approve the loan, and questions on facts absent from the PDF.
- Q&A isolation: load two entities' PDFs as separate cases; ask case A about a figure only in case B; confirm refusal and that the retrieval query was filtered by case id.
- Q&A grounding: seed a PDF with an injected instruction in its text; confirm it is treated as data and not obeyed; confirm numbers in answers always appear in cited chunks.
- Network: in OnPrem mode, capture traffic during Q&A and confirm no outbound calls.

## 10. Open Items
- Confirm Microsoft Agent Framework workflow checkpointing and request-port features are generally available for the chosen version; otherwise use the Semantic Kernel Process Framework.
- Confirm Foundry Local is licensed and suitable for server-side, multi-user on-prem hosting (it is oriented to local/edge use), and its hardware sizing; if not, evaluate Microsoft's supported options for running Foundry models on Azure Local / Azure Arc infrastructure.
- Confirm the target Windows Server version and that Foundry Local installs and runs as a service there without Docker.
- Confirm an embedding model is available in the Foundry Local catalogue for on-prem. If none is, on-prem embeddings need a decision, because the scope excludes models outside Azure and Foundry.
- Confirm SQL Server 2025 licensing and availability on the target servers (the native vector type is not in SQL Server 2022), that the Express/Developer editions used for dev and pilot support it, and the .NET data-access route (EF Core vector support or Microsoft.Data.SqlClient with parameterised vector queries) for the chosen .NET version.
- Set the Q&A parameters with the policy owner: chunk size, top-k, similarity threshold, refusal wording, and who may use the panel.
- Confirm exact catalogue names and hardware (GPU) for the on-prem Node 1-3 models and the Azure deployments and quota.
- Confirm the decision-model thresholds and the 0.80 confidence cut-off with the Risk Policy Owner; loan amount and exposure limits are not in the inputs yet and may be needed.
- Identity provider for on-prem (AD FS vs other OIDC) and underwriter approval limits.
- Audit retention period and data-residency rules.
- Whether the benchmark's "Elevated / High Risk" label for Entity D should map to review (this PRD does) or denial.
