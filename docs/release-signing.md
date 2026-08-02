# Release signing (Azure Trusted Signing)

Why: signed releases stop the SmartScreen "unknown publisher" warning (after a
short reputation ramp) and make in-app auto-update safe — the updater can
verify a downloaded exe is genuinely ours before swapping it in.

Cost: Basic tier, ~$9.99/month. The setup script and this doc get you from
nothing to "CI signs every release" with three manual steps that only the
account owner can perform.

## One-time setup

### Step 1 — Azure account (manual, ~10 min)

Create one at <https://portal.azure.com> (needs a credit card; you'll be on
pay-as-you-go — the only charge from this setup is the Trusted Signing Basic
tier). If you already have a subscription, skip ahead.

### Step 2 — Log in and run phase 1 (~5 min)

```powershell
az login                                          # opens the browser
powershell -File scripts/setup-trusted-signing.ps1
```

This creates the `eqdeeps-signing` resource group and Trusted Signing account
(East US), a GitHub OIDC app registration federated to this repo's `release`
environment (so CI signs with short-lived tokens — no secret to leak), the
minimal role assignment, and the repo secrets/variables CI will use.

### Step 3 — Identity validation (manual, then a wait)

This is Microsoft verifying you're a real person — it's what SmartScreen's
trust is anchored to, and no script can do it for you:

1. Portal → search "Trusted Signing accounts" → `eqdeeps-signing`.
2. Left menu → **Identity validations** → **New identity** → **Individual**.
3. Fill in your legal name/address; you'll be sent through a government-ID
   verification flow (Au10tix).
4. Wait for the validation to show **Completed** — typically 1–3 business
   days; watch for follow-up emails asking for more documentation.

> Individual (non-organization) validation availability varies by country.
> If the portal only offers "Organization", that's the blocker to raise.

### Step 4 — Run phase 2 (~1 min)

Open the completed identity validation, copy its ID (a GUID), then:

```powershell
powershell -File scripts/setup-trusted-signing.ps1 -IdentityValidationId <guid>
```

That creates the `eqdeeps-public` certificate profile. Setup done.

### Step 5 — Wire CI

Tell Claude the profile exists. The follow-up work (separate PRs): a signing
step in `release.yml` (sign `EQDeeps.Server.exe` between publish and zip,
under `environment: release`), then the in-app auto-updater (download →
verify our Authenticode signature → swap on restart), then delete the
SmartScreen note from the README once reputation settles.

## Ongoing

Nothing. Certificates are short-lived and rotate automatically inside the
service; CI authenticates per-run via OIDC. If you ever stop paying, releases
just go back to unsigned — the app keeps working.
