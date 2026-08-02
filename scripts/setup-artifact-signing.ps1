# Provisions Azure Artifact Signing (formerly "Trusted Signing") for EQDeeps
# releases, in two phases:
#
#   Phase 1 (run after `az login`):
#     powershell -File scripts/setup-artifact-signing.ps1
#     Creates the resource group + signing account, the GitHub-Actions OIDC app
#     registration (federated to this repo's "release" environment, no
#     long-lived secret), the role assignments, and the repo secrets.
#     Then YOU complete identity validation in the portal — it can only be done
#     there, never from the CLI. See docs/release-signing.md.
#
#   Phase 2 (run once the validation shows Completed):
#     powershell -File scripts/setup-artifact-signing.ps1 -IdentityValidationId <guid>
#     Creates the certificate profile that CI signs with.
#
# Idempotent: safe to re-run either phase.
param(
    [string]$Region = "eastus",
    [string]$ResourceGroup = "default",
    [string]$AccountName = "moonchopper",
    [string]$ProfileName = "eqdeeps-public",
    [string]$Repo = "Moonchopper/EQDeeps",
    [string]$IdentityValidationId = ""
)

$ErrorActionPreference = "Stop"

# Region → signing endpoint (the value CI's signing action needs).
$endpoints = @{
    "brazilsouth"      = "https://brs.codesigning.azure.net"
    "centralus"        = "https://cus.codesigning.azure.net"
    "eastus"           = "https://eus.codesigning.azure.net"
    "japaneast"        = "https://jpe.codesigning.azure.net"
    "koreacentral"     = "https://krc.codesigning.azure.net"
    "northcentralus"   = "https://ncus.codesigning.azure.net"
    "northeurope"      = "https://neu.codesigning.azure.net"
    "polandcentral"    = "https://plc.codesigning.azure.net"
    "southcentralus"   = "https://scus.codesigning.azure.net"
    "switzerlandnorth" = "https://swn.codesigning.azure.net"
    "westcentralus"    = "https://wcus.codesigning.azure.net"
    "westeurope"       = "https://weu.codesigning.azure.net"
    "westus"           = "https://wus.codesigning.azure.net"
    "westus2"          = "https://wus2.codesigning.azure.net"
    "westus3"          = "https://wus3.codesigning.azure.net"
}
if (-not $endpoints.ContainsKey($Region)) {
    throw "Region '$Region' is not an Artifact Signing region. Pick one of: $($endpoints.Keys -join ', ')"
}

$account = az account show 2>$null | ConvertFrom-Json
if (-not $account) { throw "Not logged in. Run 'az login' first." }
Write-Host "Subscription: $($account.name) ($($account.id)) as $($account.user.name)"

Write-Host "== Ensuring artifact-signing CLI extension =="
az extension add --name artifact-signing --upgrade --only-show-errors

Write-Host "== Registering resource provider =="
az provider register --namespace Microsoft.CodeSigning --wait

if ($IdentityValidationId) {
    # ---- Phase 2: certificate profile --------------------------------------
    # No --include-street-address / --include-postal-code: those two flags would
    # publish a home address in every binary we ship. Public Trust always emits
    # CN, O, L, S and C regardless of the include* flags (those are private-trust
    # only), so the subject lands as:
    #   CN=<name>, O=<name>, L=<city>, S=<state>, C=<country>
    Write-Host "== Creating certificate profile '$ProfileName' =="
    az artifact-signing certificate-profile create `
        -g $ResourceGroup --account-name $AccountName -n $ProfileName `
        --profile-type PublicTrust --identity-validation-id $IdentityValidationId
    Write-Host ""
    Write-Host "Done. Certificate profile is live — tell Claude to wire release.yml."
    return
}

# ---- Phase 1: account + OIDC + repo secrets --------------------------------

# The certificate subject is sourced verbatim from the billing account, and the
# billing account type must match the identity validation type. Getting this
# wrong is not fixable in place: signing resources can't move between
# subscriptions, tenants, or resource groups, so a mismatch means tearing it all
# down and revalidating from scratch.
Write-Host "== Checking billing account =="
$billing = az billing account list --only-show-errors 2>$null | ConvertFrom-Json
if ($billing) {
    foreach ($b in $billing) {
        Write-Host "  $($b.displayName) — account type: $($b.accountType)"
        if ($b.accountType -ne "Individual") {
            Write-Warning "Account type is '$($b.accountType)', not 'Individual'. Individual identity validation needs an Individual billing account; this subscription can only do Organization validation (which requires 3+ years of verifiable business history)."
        }
        $region = $b.soldTo.region
        if ($region -and $region -cne $region.ToUpper()) {
            Write-Warning "Billing address region '$region' is not upper-case — it lands in the certificate subject verbatim. Fix it before validating."
        }
    }
} else {
    Write-Warning "Could not read the billing account. Confirm in the portal that its type matches the identity validation you intend to create."
}

Write-Host "== Resource group '$ResourceGroup' ($Region) =="
az group create -n $ResourceGroup -l $Region --only-show-errors | Out-Null

Write-Host "== Artifact Signing account '$AccountName' (Basic, ~`$9.99/mo) =="
az artifact-signing create -n $AccountName -g $ResourceGroup -l $Region --sku Basic

$accountId = az artifact-signing show -n $AccountName -g $ResourceGroup --query id -o tsv

Write-Host "== GitHub OIDC app registration (no client secret) =="
$appName = "eqdeeps-release-signing"
$appId = az ad app list --display-name $appName --query "[0].appId" -o tsv
if (-not $appId) {
    $appId = az ad app create --display-name $appName --query appId -o tsv
}
$sp = az ad sp show --id $appId 2>$null
if (-not $sp) {
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

Write-Host "== Role assignment: CI signs with the account, nothing more =="
az role assignment create --assignee $appId `
    --role "Artifact Signing Certificate Profile Signer" `
    --scope $accountId --only-show-errors | Out-Null

# Subscription Owner is NOT enough to create an identity validation — without
# these two the portal's "New identity" button stays greyed out. The Identity
# Verifier role requires at least Reader at subscription scope alongside it.
Write-Host "== Role assignment: you can create identity validations =="
$me = az ad signed-in-user show --query id -o tsv
az role assignment create --assignee $me --role "Reader" `
    --scope "/subscriptions/$($account.id)" --only-show-errors | Out-Null
az role assignment create --assignee $me --role "Artifact Signing Identity Verifier" `
    --scope $accountId --only-show-errors | Out-Null

Write-Host "== GitHub repo secrets/variables =="
gh secret set AZURE_TENANT_ID --repo $Repo --body $account.tenantId
gh secret set AZURE_CLIENT_ID --repo $Repo --body $appId
gh secret set AZURE_SUBSCRIPTION_ID --repo $Repo --body $account.id
gh variable set ARTIFACT_SIGNING_ENDPOINT --repo $Repo --body $endpoints[$Region]
gh variable set ARTIFACT_SIGNING_ACCOUNT --repo $Repo --body $AccountName
gh variable set ARTIFACT_SIGNING_PROFILE --repo $Repo --body $ProfileName

Write-Host ""
Write-Host "Phase 1 complete. Next (manual, portal): identity validation —"
Write-Host "see docs/release-signing.md, then re-run with -IdentityValidationId."
