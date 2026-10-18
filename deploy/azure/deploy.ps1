<#
.SYNOPSIS
  Deploys the Loan Underwriting Orchestrator to Azure: App Service (API + React SPA), Azure SQL, Blob Storage, Azure OpenAI.

.DESCRIPTION
  1. Creates the resource group and deploys main.bicep.
  2. Builds the React app into the API's wwwroot and publishes the API as a zip.
  3. Creates a database user for the web app's managed identity.
  4. Zip-deploys and calls /api/health.

  Prerequisites: Azure CLI (az login), .NET 8 SDK, Node 18+, PowerShell module SqlServer (Invoke-Sqlcmd).
  The signed-in user must be the Entra admin passed as -SqlAdminLogin (that is what lets the script create the database user).
  UNTESTED against a live subscription: review with 'az deployment group what-if' first.

.EXAMPLE
  .\deploy\azure\deploy.ps1 -ResourceGroup rg-loan-uw -NamePrefix loanuw -Location eastus2 `
      -SqlAdminLogin you@contoso.com -OidcAuthority "https://login.microsoftonline.com/<tenant-id>/v2.0"
#>
[CmdletBinding()]
param(
  [Parameter(Mandatory)] [string] $ResourceGroup,
  [Parameter(Mandatory)] [string] $NamePrefix,
  [string] $Location = "eastus2",
  [Parameter(Mandatory)] [string] $SqlAdminLogin,
  [Parameter(Mandatory)] [string] $OidcAuthority,
  [string] $OidcAudience = "loan-underwriting-api",
  [switch] $SkipOpenAi,   # subscription cannot create Azure OpenAI: deploy everything else, wire a model endpoint later
  [string] $OpenAiLocation = "",
  [switch] $SkipInfra
)
$ErrorActionPreference = "Continue"   # az writes warnings to stderr; failures are caught through $LASTEXITCODE checks
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$work = Join-Path $env:TEMP "loanuw-publish"

if (-not $SkipInfra) {
  if ((az group exists -n $ResourceGroup) -ne "true") { az group create -n $ResourceGroup -l $Location | Out-Null }
  $adminId = az ad user show --id $SqlAdminLogin --query id -o tsv
  az deployment group create -g $ResourceGroup -n main -f (Join-Path $PSScriptRoot "main.bicep") `
    -p namePrefix=$NamePrefix location=$Location sqlAdminLogin=$SqlAdminLogin sqlAdminObjectId=$adminId `
       oidcAuthority=$OidcAuthority oidcAudience=$OidcAudience deployOpenAi=$(if ($SkipOpenAi) {'false'} else {'true'}) `
       $(if ($OpenAiLocation) {"openAiLocation=$OpenAiLocation"}) | Out-Null
  if ($LASTEXITCODE) { throw "Bicep deployment failed" }
}
$out = az deployment group show -g $ResourceGroup -n main --query properties.outputs | ConvertFrom-Json
$app = $out.appName.value; $sqlServer = $out.sqlServer.value; $db = $out.sqlDatabase.value

# The API serves the SPA from wwwroot: one origin, no CORS needed.
Push-Location (Join-Path $root "frontend")
$env:VITE_API_MODE = "real"; $env:VITE_API_BASE = ""
npm ci; if ($LASTEXITCODE) { throw "npm ci failed" }
npm run build; if ($LASTEXITCODE) { throw "frontend build failed" }
Pop-Location
Copy-Item (Join-Path $root "frontend\dist\*") (Join-Path $root "backend\src\LoanUnderwriting.Api\wwwroot") -Recurse -Force

if (Test-Path $work) { Remove-Item $work -Recurse -Force }
dotnet publish (Join-Path $root "backend\src\LoanUnderwriting.Api") -c Release -o "$work\out"; if ($LASTEXITCODE) { throw "publish failed" }
# bsdtar writes forward-slash zip entries; Compress-Archive in Windows PowerShell 5.1 writes backslashes, which break on Linux App Service.
tar -a -cf "$work\app.zip" -C "$work\out" .

# The web app's managed identity needs read/write plus DDL rights: the app creates its schema at startup (Database:AutoCreateSchema).
$token = az account get-access-token --resource https://database.windows.net/ --query accessToken -o tsv
$sqlText = @"
IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = N'$app') CREATE USER [$app] FROM EXTERNAL PROVIDER;
ALTER ROLE db_datareader ADD MEMBER [$app];
ALTER ROLE db_datawriter ADD MEMBER [$app];
ALTER ROLE db_ddladmin ADD MEMBER [$app];
"@
# Temporary firewall rule for this machine so the user can be created; removed again below.
$myIp = (Invoke-RestMethod https://api.ipify.org).Trim()
$sqlName = $sqlServer.Split(".")[0]
az sql server firewall-rule create -g $ResourceGroup -s $sqlName -n deploy-temp --start-ip-address $myIp --end-ip-address $myIp | Out-Null
Start-Sleep 30
try { Invoke-Sqlcmd -ServerInstance $sqlServer -Database $db -AccessToken $token -Query $sqlText -ErrorAction Stop }
finally { az sql server firewall-rule delete -g $ResourceGroup -s $sqlName -n deploy-temp | Out-Null }

az webapp deploy -g $ResourceGroup -n $app --src-path "$work\app.zip" --type zip | Out-Null
$url = $out.appUrl.value
Start-Sleep 30
Invoke-RestMethod "$url/api/health" | ConvertTo-Json -Depth 4
Write-Host "Deployed: $url"
