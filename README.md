# Loan Underwriting Orchestrator

Multi-LLM commercial loan underwriting: upload an entity's digital PDF, extract financials, check compliance, score risk, apply a
decision model, route to auto-approve / auto-deny / human review, and ask questions about the loaded document (guarded).
Built with C# (.NET 8), SQL Server 2025 (native vector search), Microsoft Foundry models, and a React front end.
Runs on-premises by default (no Docker); Azure is selectable with one setting.

Design documents: `PRD/PRD_Loan_Underwriting_Orchestrator.md`, `PRD/TDD_Loan_Underwriting_Orchestrator.md`. Work list and status: `TASKS.md`.

```
backend/   .NET solution (Core, Infrastructure, Api, Tests)
frontend/  React + TypeScript (Vite)
samples/   four sample digital PDFs (generated)
deploy/    on-prem install script
PRD/       requirements and design documents
```

## 1. Prerequisites

| Need | Why | Check | Install (Windows) |
|---|---|---|---|
| .NET 8 SDK (or newer) | build and run the API | `dotnet --list-sdks` | `winget install Microsoft.DotNet.SDK.8` |
| SQL Server **2025** (Developer, Express or Standard) or Azure SQL | database and native `VECTOR` type. SQL Server 2022 and older lack it | `sqlcmd -S . -E -C -Q "SELECT @@VERSION"` shows 2025 / 17.x | Download SQL Server 2025 Developer from Microsoft; install the Database Engine |
| A SQL login that can create a database | the app creates its database and tables at start | `CREATE DATABASE` right (`dbcreator`), or create an empty database yourself and set `Database:AutoCreateSchema` to `false` after running the schema once | n/a |
| Node.js 18+ and npm | build or run the React app | `node -v` | `winget install OpenJS.NodeJS.LTS` |
| Foundry Local | on-prem models (only needed for real models, not for the offline demo) | `foundry --version` | `winget install Microsoft.FoundryLocal` (verify the package name for your version) |
| Python 3 with `reportlab` | only to regenerate the sample PDFs | `python -c "import reportlab"` | `pip install reportlab` |

Hardware for real on-prem models: a GPU is recommended for the larger models; sizing is measured in the pilot (see PRD NFRs).

## 2. Quick start (offline demo, no models needed)

This uses the built-in **stub** that reads the labelled sample PDFs. It is a test double, not a model, so it only understands the
sample format. Use it to see the whole flow end to end.

```powershell
# 1. Point the app at your SQL Server 2025 (Windows authentication shown)
$env:Database__ConnectionString = "Server=.;Database=LoanUnderwriting;Integrated Security=True;TrustServerCertificate=True"

# 2. Start the API (Development: stub models, dev authentication, listens on http://localhost:5000)
cd backend
dotnet run --project src/LoanUnderwriting.Api
```

Check it: `http://localhost:5000/api/health` should list Mode, Configuration, Database, Vector support and Document store as ok.
The first start creates the database and tables.

Try the API with the sample PDFs:
```powershell
cd ..
curl.exe -X POST -F "file=@samples/Vantage_Retail_FY2024-25.pdf;type=application/pdf" http://localhost:5000/api/cases
curl.exe http://localhost:5000/api/cases
```
(In Windows PowerShell 5.1 the quotes inside JSON must be escaped as shown; in PowerShell 7 or Git Bash plain quotes work.)
Expected: Apex, Nexus and Summit end **Approved**; Vantage ends **AwaitingReview**. In Development the headers `X-Demo-User` and
`X-Demo-Role` (RiskOps, Underwriter, Auditor) choose the identity, for example:
```powershell
curl.exe -X POST -H "Content-Type: application/json" -H "X-Demo-Role: Underwriter" -H "X-Demo-User: Priya Nair" `
  -d '{\"action\":\"Approve\",\"reason\":\"Reviewed\"}' http://localhost:5000/api/cases/<caseId>/review
```

### Front end
```powershell
cd frontend
npm install
copy .env.example .env.local      # VITE_API_MODE=real uses the API above; delete the file to use mock data
npm run dev                        # http://localhost:5173 (proxies /api to http://localhost:5000)
```
Use the role switch in the top bar: upload as **Risk Ops**, then switch to **Underwriter** to decide a case someone else submitted.
`npm run typecheck` and `npm run build` pass (Node.js 24). The UI itself has not been clicked through in a browser by the author, and real-API mode has not been tried against the running backend.

## 3. Using real models

### On-prem with Foundry Local
1. Install Foundry Local and list models: `foundry model list`.
2. Start a model so the local service runs, for example `foundry model run <model-id>`, and read the service address with
   `foundry service status`. The address and port can change between runs.
3. Choose models for the roles (see the table in PRD 3.3). Confirm that an **embedding** model exists in the catalogue; if none does, the
   document Q&A cannot run on-prem (open decision in the PRD).
4. Edit `backend/src/LoanUnderwriting.Api/appsettings.json` (or use environment variables) and remove the Development stub override:

```json
"Models": { "OnPrem": {
  "Provider": "OpenAiCompatible",
  "BaseUrl": "http://localhost:<port>/v1",
  "AuthMode": "None",
  "Models": { "Extraction": "<id>", "Compliance": "<id>", "Risk": "<id>", "ScopeCheck": "<id>", "QaAnswer": "<id>" },
  "EmbeddingModel": "<embedding id>",
  "EmbeddingDimensions": 768
} }
```
`EmbeddingDimensions` must equal the embedding model's output size. The schema's `VECTOR(n)` column is created with this number, so
set it **before the first start**; changing it later needs a new database or a migration.
5. Set `SelfCheck:ProbeModels` to `true` so the app calls every configured model at startup.
6. Tune `Qa:MaxDistance` (cosine distance above which a chunk is ignored). The default 0.6 suits real embeddings; the stub uses 0.85.

### Azure Foundry (option flag)
Set `Deployment:Mode` to `Azure` and fill `Models:Azure` (endpoint `https://<resource>.openai.azure.com/openai/v1`, deployment names, and
`AuthMode` `AzureAd` for managed identity / `az login`, or `ApiKey`). The model calls then go to Foundry. **Azure hosting is not built yet**:
Blob storage, Document Intelligence and the Bicep templates are open tasks (`TASKS.md` T-31); until then the document store stays on a
local folder. The startup self-check rejects mixed settings, for example Azure mode with the stub provider.

## 4. Configuration reference

All settings can be overridden with environment variables using `__` as the separator (for example `Database__ConnectionString`).

| Setting | Meaning | Default |
|---|---|---|
| `Deployment:Mode` | `OnPrem` or `Azure`; picks the model profile | `OnPrem` |
| `Database:ConnectionString` | SQL Server 2025 / Azure SQL | placeholder, must be set |
| `Database:VectorProvider` | `Native` (VECTOR type) or `JsonFallback` (older SQL Server, development only) | `Native` |
| `Database:AutoCreateSchema` | create database and tables at start | `true` |
| `Storage:DocumentFolder` | where uploaded PDFs are kept. Relative paths resolve against the application folder; **use an absolute path in production**, on an encrypted, access-restricted volume | `data/documents` |
| `Storage:MaxUploadMb` | upload limit | `20` |
| `Models:<Mode>:Provider` | `OpenAiCompatible` or `Stub` (demo and tests only) | `OpenAiCompatible` |
| `Models:<Mode>:BaseUrl`, `AuthMode`, `Models:{role}`, `EmbeddingModel`, `EmbeddingDimensions` | model endpoint and names per role (`Extraction`, `Compliance`, `Risk`, `ScopeCheck`, `QaAnswer`) | see `appsettings.json` |
| `Qa:TopK`, `Qa:MaxDistance` | retrieval size and relevance cut-off | `6`, `0.6` |
| `Workflow:MaxConcurrentRuns` | cases processed at once (keep low on one GPU) | `2` |
| `SelfCheck:Enabled`, `SelfCheck:ProbeModels` | startup checks; a failed check stops the app | `true`, `true` |
| `Auth:Mode` | `Jwt` (OIDC bearer tokens) or `Dev` (**trusts identity headers, development only**) | `Jwt` |
| `Auth:Authority`, `Auth:Audience`, `Auth:RoleClaim` | identity provider settings for `Jwt` | placeholders |
| `Cors:Origins` | allowed browser origins | `http://localhost:5173` |

Decision-model thresholds (approve below 30, deny above 70, minimum confidence 0.80) are stored in the `policy_version` table
(seeded with `DM-1.0.0`). Changing them is a controlled policy change; there is no edit endpoint yet.

### Identity (production)
Use `Auth:Mode` `Jwt` with your OIDC provider (AD FS or Entra ID). Create app roles/groups named `RiskOps`, `Underwriter`, `Auditor`
(and `PolicyOwner`) and make the provider put them in the token claim named by `Auth:RoleClaim`. Users appear by their `name` claim; the
separation-of-duties rule compares the submitter's name with the reviewer's. The React app still uses the demo role switch (T-37).

## 5. Run the tests
```powershell
cd backend
dotnet test                                    # 59 unit and pipeline tests; SQL tests are skipped without the variable below
$env:LOAN_TEST_SQL = "Server=.;Integrated Security=True;TrustServerCertificate=True"   # SQL Server 2025, no database name
dotnet test                                    # also runs the 3 SQL integration tests in a throwaway database
```

## 6. Install on a Windows Server (on-prem, no Docker)
From an elevated PowerShell in the repository root:
```powershell
.\deploy\onprem\deploy.ps1 -SqlConnection "Server=SQL01;Database=LoanUnderwriting;Integrated Security=True;TrustServerCertificate=True" `
  -ModelBaseUrl "http://localhost:<port>/v1" -ExtractionModel "<id>" -ComplianceModel "<id>" -RiskModel "<id>" `
  -EmbeddingModel "<id>" -EmbeddingDimensions 768 -OidcAuthority "https://<your-idp>/"
```
It publishes the API, builds and copies the React app, writes `appsettings.Production.json`, restricts the document folder, installs the
`LoanUnderwriting` Windows Service (auto-restart on failure) and checks `/api/health`. The service account needs rights on the database and
the document folder. This script has not been run on a clean server; review it first. Put the API behind HTTPS (IIS or a reverse proxy)
before real use.

## 7. Troubleshooting
| Symptom | Likely cause and fix |
|---|---|
| Startup stops with "Startup self-check failed" | Read the listed checks. Common: database not reachable, **SQL Server older than 2025** (vector type missing; use SQL Server 2025 or, for development only, `Database:VectorProvider=JsonFallback`), model endpoint unreachable, mixed mode settings |
| `Cannot open database` or permission error at first start | The login needs the right to create the database, or create it first and set `AutoCreateSchema` to `false` after the schema exists |
| "Embedding model returned N dimensions but M are configured" | Set `EmbeddingDimensions` to N before the first start (or recreate the `document_chunks` table) |
| 403 on an endpoint | Role check. Upload needs `RiskOps`, review needs `Underwriter` and not the submitter, Q&A needs `RiskOps` or `Underwriter` |
| Upload returns 422 | The PDF has no text layer (scanned) or cannot be read. Only digital PDFs are supported |
| Q&A refuses everything | The vector search found nothing within `Qa:MaxDistance`, or the scope check said out of scope. Check `qa_log` and tune the distance for your embedding model |
| Port 5000 busy | Change `applicationUrl` in `Properties/launchSettings.json` and the proxy target in `frontend/vite.config.ts` |

## 8. What is not finished
See `TASKS.md`. The main open items: Azure hosting (Bicep, Blob, Document Intelligence), Microsoft Agent Framework workflow engine, OIDC sign-in in
the React app, SignalR live updates, prompt tuning with real models, the on-prem network lock-down, and production runbooks.
