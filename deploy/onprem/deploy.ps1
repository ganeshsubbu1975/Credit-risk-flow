<#
.SYNOPSIS
  Installs the Loan Underwriting API on a Windows Server as a Windows Service (no Docker).

.DESCRIPTION
  -Target OnPrem  publishes the API (and the React build when present), writes the production configuration,
                  creates the document folder with restricted permissions, installs and starts the Windows Service
                  and calls the health endpoint.
  -Target Azure   is not implemented yet (see TASKS.md, task T-31). The application itself supports Deployment:Mode=Azure
                  (models via a Foundry endpoint), but the Azure resources and the Blob / Document Intelligence adapters are open tasks.

  Run from an elevated PowerShell in the repository root. UNTESTED on a clean server: review before using in production.

.EXAMPLE
  .\deploy\onprem\deploy.ps1 -SqlConnection "Server=SQL01;Database=LoanUnderwriting;Integrated Security=True;TrustServerCertificate=True" `
      -ModelBaseUrl "http://localhost:5273/v1" -ExtractionModel "phi-4-mini" -ComplianceModel "phi-4" -RiskModel "gpt-oss-20b" `
      -EmbeddingModel "<embedding-model-id>" -EmbeddingDimensions 768 -OidcAuthority "https://adfs.contoso.com/adfs"
#>
[CmdletBinding()]
param(
  [ValidateSet("OnPrem", "Azure")] [string] $Target = "OnPrem",
  [string] $InstallDir = "C:\Apps\LoanUnderwriting",
  [string] $DocumentFolder = "D:\LoanUnderwriting\documents",
  [string] $ServiceName = "LoanUnderwriting",
  [string] $ServiceAccount = "",              # for example DOMAIN\svc-loan (leave empty for LocalSystem; a dedicated account is recommended)
  [string] $Url = "http://localhost:5000",
  [Parameter(Mandatory)] [string] $SqlConnection,
  [string] $ModelBaseUrl = "http://localhost:5273/v1",
  [Parameter(Mandatory)] [string] $ExtractionModel,
  [Parameter(Mandatory)] [string] $ComplianceModel,
  [Parameter(Mandatory)] [string] $RiskModel,
  [Parameter(Mandatory)] [string] $EmbeddingModel,
  [int] $EmbeddingDimensions = 768,
  [Parameter(Mandatory)] [string] $OidcAuthority,
  [string] $OidcAudience = "loan-underwriting-api",
  [switch] $SkipFrontend
)

$ErrorActionPreference = "Stop"

if ($Target -eq "Azure") {
  throw "Azure deployment is not implemented in this script yet (TASKS.md T-31). Set Deployment:Mode=Azure and the Models:Azure profile to run the API against Azure Foundry."
}

# --- prerequisites -------------------------------------------------------------------------
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) { throw "Run this script from an elevated (administrator) PowerShell." }
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) { throw ".NET SDK not found. Install the .NET 8 SDK first (see README)." }
$repo = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$api = Join-Path $repo "backend\src\LoanUnderwriting.Api\LoanUnderwriting.Api.csproj"

# --- stop an existing service so files can be replaced -------------------------------------
$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -eq "Running") { Stop-Service $ServiceName -Force; $svc.WaitForStatus("Stopped", "00:00:30") }

# --- publish ---------------------------------------------------------------------------------
Write-Host "Publishing API to $InstallDir ..."
dotnet publish $api -c Release -o $InstallDir --nologo | Out-Host
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

if (-not $SkipFrontend) {
  $fe = Join-Path $repo "frontend"
  if (Get-Command npm -ErrorAction SilentlyContinue) {
    Write-Host "Building the React app ..."
    Push-Location $fe
    try {
      npm install --no-audit --no-fund | Out-Host
      $env:VITE_API_MODE = "real"
      npm run build | Out-Host
      if ($LASTEXITCODE -ne 0) { throw "npm run build failed." }
    } finally { Pop-Location }
    $www = Join-Path $InstallDir "wwwroot"
    New-Item -ItemType Directory -Force $www | Out-Null
    Copy-Item (Join-Path $fe "dist\*") $www -Recurse -Force
  } else {
    Write-Warning "npm not found: the React app was not built. Install Node.js and re-run, or use -SkipFrontend."
  }
}

# --- document folder with restricted permissions -------------------------------------------
New-Item -ItemType Directory -Force $DocumentFolder | Out-Null
$account = if ($ServiceAccount) { $ServiceAccount } else { "NT AUTHORITY\SYSTEM" }
icacls $DocumentFolder /inheritance:r /grant:r "Administrators:(OI)(CI)F" "${account}:(OI)(CI)M" | Out-Null
Write-Host "Document folder: $DocumentFolder (restricted to Administrators and $account). Enable BitLocker/EFS on this volume for encryption at rest."

# --- production configuration (kept outside source control) --------------------------------
$config = [ordered]@{
  Deployment = @{ Mode = "OnPrem" }
  Database   = @{ ConnectionString = $SqlConnection; VectorProvider = "Native"; AutoCreateSchema = $true }
  Storage    = @{ DocumentFolder = $DocumentFolder }
  Models     = @{ OnPrem = @{
      Provider = "OpenAiCompatible"; BaseUrl = $ModelBaseUrl; AuthMode = "None"
      Models = @{ Extraction = $ExtractionModel; Compliance = $ComplianceModel; Risk = $RiskModel; ScopeCheck = $ExtractionModel; QaAnswer = $ComplianceModel }
      EmbeddingModel = $EmbeddingModel; EmbeddingDimensions = $EmbeddingDimensions } }
  SelfCheck  = @{ Enabled = $true; ProbeModels = $true }
  Auth       = @{ Mode = "Jwt"; Authority = $OidcAuthority; Audience = $OidcAudience; RoleClaim = "roles" }
  Cors       = @{ Origins = @($Url) }
  Kestrel    = @{ Endpoints = @{ Http = @{ Url = $Url } } }
}
$config | ConvertTo-Json -Depth 8 | Set-Content (Join-Path $InstallDir "appsettings.Production.json") -Encoding UTF8
icacls (Join-Path $InstallDir "appsettings.Production.json") /inheritance:r /grant:r "Administrators:F" "${account}:R" | Out-Null

# --- Windows Service -----------------------------------------------------------------------------
$exe = Join-Path $InstallDir "LoanUnderwriting.Api.exe"
if (-not $svc) {
  Write-Host "Creating service $ServiceName ..."
  if ($ServiceAccount) {
    $cred = Get-Credential -UserName $ServiceAccount -Message "Password for the service account"
    New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" -DisplayName "Loan Underwriting API" -StartupType Automatic -Credential $cred | Out-Null
  } else {
    New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" -DisplayName "Loan Underwriting API" -StartupType Automatic | Out-Null
  }
  sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
}
# The service runs in the Production environment.
Set-ItemProperty "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" -Name Environment -Type MultiString -Value @("ASPNETCORE_ENVIRONMENT=Production")

Start-Service $ServiceName
Write-Host "Waiting for the API to start (the startup self-check runs first) ..."
$ok = $false
for ($i = 0; $i -lt 40; $i++) {
  try { $h = Invoke-RestMethod "$Url/api/health" -TimeoutSec 3; $ok = $true; break } catch { Start-Sleep -Seconds 3 }
}
if ($ok) { Write-Host "Healthy. Mode: $($h.mode)"; $h.checks | Format-Table | Out-Host }
else { throw "The API did not become healthy. Check the Windows Event Log (source: $ServiceName) for the self-check result." }
