# Implementation tasks

Derived from the PRD and TDD in `PRD/`. Status as of this build:

- **Done and verified**: built, and covered by tests or a live run on this machine.
- **Done, not verified**: code is written but could not be run here (no Node.js, no Foundry Local, no admin install).
- **Open**: not built.

Test evidence: 59 unit and pipeline tests pass; 3 SQL integration tests pass against SQL Server 2025 (`LOAN_TEST_SQL` set). A live run of the API on SQL Server 2025 with the four sample PDFs gave the expected outcomes (Entities A, B and C approved, Entity D waiting for review), and the review, role, separation-of-duties and Q&A endpoints behaved as designed.

## Foundation
| ID | Task | PRD | Status |
|---|---|---|---|
| T-01 | Solution: Core, Infrastructure, Api, Tests (TDD planned more projects; consolidated into three) | 7 | Done and verified |
| T-02 | Domain records and immutable `LoanState` | FR-2 | Done and verified |
| T-03 | Ports for models, embeddings, PDF, documents, vectors, repository, notifier | 3.1 | Done and verified |
| T-04 | Configuration options per mode (`Deployment:Mode`, model profiles) | FR-11 | Done and verified |

## Scoring and decisions
| ID | Task | PRD | Status |
|---|---|---|---|
| T-05 | EBITDA calculator and reconciliation checks | FR-3 | Done and verified |
| T-06 | Scorecard: risk = 100 - creditworthiness, unavailable pillars renormalised | FR-4 | Done and verified |
| T-07 | Decision model rules R1 to R6, versioned policy, reason codes | FR-5 | Done and verified (boundaries 29/30/70/71, 0.79/0.80) |
| T-08 | Deterministic compliance checks (tax-to-bank variance and others) | FR-3 | Done and verified |

## Workflow
| ID | Task | PRD | Status |
|---|---|---|---|
| T-09 | Workflow runner with checkpoint and audit after each step, concurrency lock, crash recovery | FR-2 | Done and verified. Custom runner behind a port, **not** the Microsoft Agent Framework (see T-30) |
| T-10 | Steps: ingest, Node 1, Node 2, scorecard, Node 3, decision, finalize | FR-3 | Done and verified (with the offline stub) |
| T-11 | Human-review pause and resume, separation of duties | FR-7 | Done and verified |
| T-12 | Failure handling: step errors end in `Error`, never an approval; model outage escalates | FR-3 | Done and verified |

## Data
| ID | Task | PRD | Status |
|---|---|---|---|
| T-13 | SQL schema (cases, checkpoints, append-only audit, chunks, Q&A log, policy) created at startup | TDD 10 | Done and verified on SQL Server 2025 |
| T-14 | Native `VECTOR` store with case-filtered exact cosine search | FR-10 | Done and verified on SQL Server 2025 |
| T-15 | JSON-fallback vector store for SQL Server without the vector type (dev only) | FR-10 | Done, not verified against a live database |
| T-16 | Revoke UPDATE/DELETE on `audit_event` from the application login (database permissions) | NFR | Open (DBA script) |

## Models and documents
| ID | Task | PRD | Status |
|---|---|---|---|
| T-17 | OpenAI-compatible chat and embeddings client (Foundry Local and Azure Foundry endpoints) | 3.3 | Done, not verified against a real endpoint |
| T-18 | Offline stub for models and embeddings (tests and demos only, not a model) | n/a | Done and verified |
| T-19 | PdfPig reader, digital PDFs only, rejects PDFs without text | FR-1 | Done and verified |
| T-20 | File document store with generated ids | 3.1 | Done and verified |
| T-21 | Prompt tuning and accuracy measurement on the benchmark with real models, per mode | 6 | Open (needs Foundry models) |

## Document Q&A
| ID | Task | PRD | Status |
|---|---|---|---|
| T-22 | Chunker, ingestion, case-scoped retrieval, similarity gate | FR-10 | Done and verified |
| T-23 | Guardrails: injection, decision-integrity, scope check, citations, number grounding | FR-10 | Done and verified (unit and pipeline tests) |
| T-24 | Q&A audit and log; isolation test across cases | FR-10 | Done and verified |
| T-25 | Real injection classifier and Azure Content Safety prompt shields (Azure mode) | FR-10 | Open |
| T-26 | Firewall rule blocking outbound traffic from the on-prem host, plus network-capture test | FR-10 | Open (infrastructure) |

## API and security
| ID | Task | PRD | Status |
|---|---|---|---|
| T-27 | Endpoints: upload, list, detail, review, record, Q&A, audit, policy, health | TDD 11 | Done and verified (live run) |
| T-28 | Roles: RiskOps, Underwriter, Auditor; Dev header auth and JWT bearer auth | FR-1 | Dev mode verified. JWT mode written, not verified against an identity provider |
| T-29 | SignalR hub and notifier | FR-8 | Done, not verified. The React app polls instead |
| T-30 | Replace the custom runner with Microsoft Agent Framework workflows | TDD 8.2 | Open (blocked on confirming its checkpoint and request-port features) |
| T-31 | Azure: Bicep, Blob store adapter, Document Intelligence adapter, App Service deploy | FR-11 | Partly done: Bicep (deploy/azure), BlobDocumentStore and deploy script exist, untested on a live subscription. Open: Document Intelligence adapter, SPA Entra sign-in (MSAL) |
| T-32 | Policy update endpoint and approval workflow for the Policy Owner | FR-5 | Open (read endpoint exists) |
| T-33 | OpenTelemetry traces and metrics | NFR | Open |
| T-34 | Hash-chained audit events (tamper evidence) | TDD 14 | Open (decision pending) |

## Front end
| ID | Task | PRD | Status |
|---|---|---|---|
| T-35 | React app: dashboard, upload, cases, case detail, review, Q&A, record, audit, themes | FR-1, 7, 8, 10 | Done. Type-check and production build pass; not clicked through in a browser |
| T-36 | Real API client and mapping (`VITE_API_MODE=real`) | FR-1 | Done. Compiles; not tried against the running API |
| T-37 | OIDC sign-in (MSAL or oidc-client) replacing the demo role switch | FR-1 | Open |
| T-38 | SignalR client for live progress | FR-8 | Open |
| T-39 | Playwright end-to-end tests | TDD 16 | Open |

## Deployment
| ID | Task | PRD | Status |
|---|---|---|---|
| T-40 | `deploy.ps1` for on-prem: publish, config, folder ACLs, Windows Service, health check | FR-11 | Done, not verified (needs an elevated install on a server) |
| T-41 | Startup self-check: mode consistency, database, vector support, storage, model probes | FR-11 | Done and verified (stub mode); model probes not verified |
| T-42 | Backup and restore runbook, load test, security test | NFR | Open |

## Decisions still needed (from the PRD)
Identity provider for on-prem, audit retention period, loan amount and exposure inputs, whether Entity D's "high risk" label should mean review or denial (built as review), whether a Foundry Local embedding model exists, and whether to hash-chain the audit log.
