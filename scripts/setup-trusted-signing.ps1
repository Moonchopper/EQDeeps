# Provisions Azure Trusted Signing for EQDeeps releases, two phases:
#
#   Phase 1 (run after `az login`):
#     powershell -File scripts/setup-trusted-signing.ps1
#     Creates the resource group + Trusted Signing account, the GitHub-Actions
#     OIDC app registration (federated to this repo's "release" environment,
#     no long-lived secret), the role assignment, and the repo secrets.
#     Then YOU complete identity validation in the portal (see
#     docs/release-signing.md) — Microsoft reviews a government ID; allow a
#     few business days.
#
#   Phase 2 (run once the validation shows Completed):
#     powershell -File scripts/setup-trusted-signing.ps1 -IdentityValidationId <guid>
#     Creates the certificate profile that CI signs with.
#
# Idempotent: safe to re-run either phase.
param(
    [string]$Region = "eastus",
    [string]$ResourceGroup = "eqdeeps-signing",
    [string]$AccountName = "eqdeeps-signing",
    [string]$ProfileName = "eqdeeps-public",
    [string]$Repo = "Moonchopper/EQDeeps",
    [string]$IdentityValidationId = ""
)

$ErrorActionPreference = "Stop"

# Region → Trusted Signing endpoint (the value CI's signing action needs).
$endpoints = @{
    "eastus"        = "https://eus.codesigning.azure.net"
    "westus2"       = "https://wus2.codesigning.azure.net"
    "westcentralus" = "https://wcus.codesigning.azure.net"
    "westus3"       = "https://wus3.codesigning.azure.net"
    "northeurope"   = "https://neu.codesigning.azure.net"
    "westeurope"    = "https://weu.codesigning.azure.net"
}
if (-not $endpoints.ContainsKey($Region)) {
    throw "Region '$Region' is not a Trusted Signing region. Pick one of: $($endpoints.Keys -join ', ')"
}

$account = az account show 2>$null | ConvertFrom-Json
if (-not $account) { throw "Not logged in. Run 'az login' first." }
Write-Host "Subscription: $($account.name) ($($account.id)) as $($account.user.name)"

Write-Host "== Ensuring trustedsigning CLI extension =="
az extension add --name trustedsigning --upgrade --only-show-errors

Write-Host "== Registering resource provider =="
az provider register --namespace Microsoft.CodeSigning --wait

if ($IdentityValidationId) {
    # ---- Phase 2: certificate profile --------------------------------------
    Write-Host "== Creating certificate profile '$ProfileName' =="
    az trustedsigning certificate-profile create `
        -g $ResourceGroup --account-name $AccountName -n $ProfileName `
        --profile-type PublicTrust --identity-validation-id $IdentityValidationId
    Write-Host ""
    Write-Host "Done. Certificate profile is live — tell Claude to wire release.yml."
    return
}

# ---- Phase 1: account + OIDC + repo secrets --------------------------------
Write-Host "== Resource group '$ResourceGroup' ($Region) =="
az group create -n $ResourceGroup -l $Region --only-show-errors | Out-Null

Write-Host "== Trusted Signing account '$AccountName' (Basic, ~`$9.99/mo) =="
az trustedsigning create -n $AccountName -g $ResourceGroup -l $Region --sku Basic

$accountId = az trustedsigning show -n $AccountName -g $ResourceGroup --query id -o tsv

Write-Host "== GitHub OIDC app registration (no client secret) =="
$appName = "eqdeeps-release-signing"
$appId = az ad app list --display-name $appName --query "[0].appId" -o tsv
if (-not $appId) {
    $appId = az ad app create --display-name $appName --query appId -o tsv
}
if (-not (az ad sp show --id $appId 2>$null)) {
    az ad sp create --id $appId | Out-Null
}

# Federated credential: tokens are only honored from this repo's "release"
# environment — release.yml's signing job must declare `environment: release`.
$fedName = "github-release-environment"
$existing = az ad app federated-credential list --id $appId --query "[?name=='$fedName'] | length(@)" -o tsv
if ($existing -eq "0") {
    $fed = @{
        name      = $fedName
        issuer    = "https://token.actions.githubusercontent.com"
        subject   = "repo:${Repo}:environment:release"
        audiences = @("api://AzureADTokenExchange")
    } | ConvertTo-Json -Compress
    $fedFile = Join-Path $env:TEMP "eqdeeps-fed.json"
    Set-Content -Path $fedFile -Value $fed -Encoding ascii
    az ad app federated-credential create --id $appId --parameters "@$fedFile"
    Remove-Item $fedFile
}

Write-Host "== Role assignment: sign with the account, nothing more =="
az role assignment create --assignee $appId `
    --role "Trusted Signing Certificate Profile Signer" `
    --scope $accountId --only-show-errors | Out-Null

Write-Host "== GitHub repo secrets/variables =="
gh secret set AZURE_TENANT_ID --repo $Repo --body $account.tenantId
gh secret set AZURE_CLIENT_ID --repo $Repo --body $appId
gh secret set AZURE_SUBSCRIPTION_ID --repo $Repo --body $account.id
gh variable set TRUSTED_SIGNING_ENDPOINT --repo $Repo --body $endpoints[$Region]
gh variable set TRUSTED_SIGNING_ACCOUNT --repo $Repo --body $AccountName
gh variable set TRUSTED_SIGNING_PROFILE --repo $Repo --body $ProfileName

Write-Host ""
Write-Host "Phase 1 complete. Next (manual, portal): identity validation —"
Write-Host "see docs/release-signing.md, then re-run with -IdentityValidationId."
