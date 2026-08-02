# Release signing (Azure Artifact Signing)

Why: signed releases stop the SmartScreen "unknown publisher" warning (after a
short reputation ramp) and make in-app auto-update safe — the updater can
verify a downloaded exe is genuinely ours before swapping it in.

Cost: Basic tier, ~$9.99/month. The setup script and this doc get you from
nothing to "CI signs every release" with two manual steps that only the account
owner can perform.

> The service was renamed from **Trusted Signing** to **Azure Artifact Signing**.
> Same service, same `Microsoft.CodeSigning` resource provider — but the CLI
> extension is now `artifact-signing` and the RBAC roles are `Artifact Signing
> Identity Verifier` / `Artifact Signing Certificate Profile Signer`. Older blog
> posts and the `trustedsigning` extension are stale.

## What ends up public

Signing publishes your validated legal identity. Anyone who downloads a release
can read the certificate subject from the exe's Properties → Digital Signatures,
and public scanners like VirusTotal index it permanently:

```
CN = Austin Culbertson
O  = Austin Culbertson
L  = Raleigh
S  = NC
C  = US
```

Individual certificates carry an `O=` too — it's your own name repeated, not an
organization, and it does not pick up the billing account's `companyName`.

Street address and postal code are opt-in via `--include-street-address` /
`--include-postal-code`; the script deliberately sets neither. Email and phone
are never included in the certificate under any setting.

There is no anonymous or pseudonymous option — CA/Browser Forum baseline
requirements force the subject to be the validated legal identity, so custom CN
and custom O aren't supported. Signing as a company instead requires
Organization validation, which needs **3+ years of verifiable business history**;
a newly formed LLC can't onboard.

## Before you start

These are the traps. None are fixable after the fact — signing resources can't
be migrated between subscriptions, tenants, or resource groups, so getting one
wrong means deleting everything and revalidating.

- **A paid subscription.** Free, trial, and sponsored subscriptions are rejected;
  account creation fails with a portal error. Pay-as-you-go or EA only.
- **Billing account type must match the validation type.** Individual validation
  requires a billing account whose Account Type is `Individual`. Check with
  `az billing account list` — phase 1 of the script also warns on this.
- **The billing account *is* the certificate.** The identity validation form is
  read-only and auto-populated from the billing account's "sold to" details.
  Legal name and address must match the government ID you'll verify with, and
  watch the casing — `region: "nc"` lands on the certificate as `S=nc`. Edit via
  Cost Management + Billing → Properties, or `az billing account update --sold-to`.
- **Individual validation is US/Canada only.** (Organizations get a wider list.)

## One-time setup

### Step 1 — Azure subscription (manual, ~10 min)

Sign in at <https://portal.azure.com> with the account you want to own this, then
Subscriptions → Add → **Pay-As-You-Go**. If the flow pushes you through Free
Trial first, upgrade to pay-as-you-go before continuing.

Keep this separate from any tenant you share with other people. A Microsoft
account's default directory (`<you>.onmicrosoft.com`) is a tenant of one and is
exactly right for this.

### Step 2 — Log in and run phase 1 (~5 min)

```powershell
az login --use-device-code                        # pick the right account explicitly
powershell -File scripts/setup-artifact-signing.ps1
```

Plain `az login` silently reuses whatever Microsoft session your browser already
has, which is an easy way to provision into the wrong tenant.

Phase 1 creates the `default` resource group and the `moonchopper` signing
account (East US), a GitHub OIDC app registration federated to this repo's
`release` environment (so CI signs with short-lived tokens — no secret to leak),
the role assignments, and the repo secrets/variables CI will use. It's
idempotent, so it's safe to run against resources you already made by hand.

> On a freshly created subscription, scoped `Microsoft.Authorization` calls can
> fail with `MissingSubscription` for a while — role *definition* lookups
> included, so it isn't the scope string. It clears once RBAC finishes
> propagating. Phase 1 treats these as non-fatal and prints the portal fallback
> (Access control (IAM) → Add role assignment) if a grant doesn't stick.

### Step 3 — Identity validation (manual, ~15 min)

Microsoft verifying you're a real person — it's what SmartScreen's trust is
anchored to, and it can only be done in the portal:

1. Portal → search "Artifact Signing accounts" → `moonchopper`.
2. Left menu → **Identity validations** → switch the dropdown from Organization
   to **Individual** → **New identity** → **Public**.
3. Select your billing account. The name and address fields populate read-only
   from it — if anything is wrong, fix the billing account and come back.
4. **Certificate subject preview** shows exactly what will be published. Read it.
5. Create. Status goes **In Progress**, then **Action Required** — click your
   name, then the verification link.
6. Complete the Microsoft Verified ID flow: email PIN, phone, then a QR-code
   handoff to your phone for government-ID capture (Au10tix) and a face check.
   You'll need the Microsoft Authenticator app.
7. Status changes to **Completed** a few minutes later.

> Sign in to the verification link with the **same email** as the validation
> request's primary email, or it fails with "You don't have permission to access
> this page".

If the **New identity** button is greyed out, the `Artifact Signing Identity
Verifier` role hasn't landed. Subscription **Owner does not imply it** — it must
be assigned explicitly, on the signing account or the resource group above it,
and it needs at least Reader at subscription scope alongside. Sign out and back
in after assigning; the portal caches the check.

### Step 4 — Run phase 2 (~1 min)

Open the completed identity validation, copy its ID (a GUID), then:

```powershell
powershell -File scripts/setup-artifact-signing.ps1 -IdentityValidationId <guid>
```

That creates the `eqdeeps-public` certificate profile. Setup done.

### If signing fails with AADSTS700213

`No matching federated identity record found for presented assertion subject`
means the federated credential's subject doesn't match what GitHub actually sent.
The Azure login step logs the presented claim — copy it verbatim into the
credential.

Most likely cause: GitHub emits **ID-qualified** subjects by default, e.g.

```
repo:Moonchopper@4328018/EQDeeps@1317763446:environment:release
```

rather than the `repo:<owner>/<name>:...` form most documentation shows. The
database IDs make the claim survive renames, so a renamed or recreated repo
can't impersonate this one. Phase 1 reads the correct prefix from
`repos/<repo>/actions/oidc/customization/sub` and repairs a stale subject on
re-run, so the fix is usually just running phase 1 again.

### Step 5 — Wire CI

Done. `release.yml` signs the app exe and the installer under
`environment: release`, and fails the build if either comes out unsigned or
untimestamped. Remaining follow-up: delete the SmartScreen note from the README
once reputation settles.

## Update signing (Ed25519) — required for auto-update

Authenticode proves *who* built a file. It does not prove that a given file is
the release the app cast is advertising, so auto-update (ADR-010) adds a second,
independent signature: NetSparkle's Ed25519 in `SecurityMode.Strict`, over both
the app cast and the installer. Both gates must pass before EQDeeps runs
anything it downloaded.

### One-time key generation

```
dotnet tool install --global NetSparkleUpdater.Tools.AppCastGenerator --version 2.9.0
netsparkle-generate-appcast --generate-keys
netsparkle-generate-appcast --export
```

`--export` prints the base64 keypair. Then:

1. **Private key** → repository secret `SPARKLE_PRIVATE_KEY` (Settings → Secrets
   and variables → Actions). The release workflow reads it as the
   `SPARKLE_PRIVATE_KEY` environment variable and refuses to publish without it.
2. **Public key** → the `PublicKey` constant in
   `src/EQDeeps.Server/Updates/UpdateService.cs`, replacing the
   `REPLACE_WITH_ED25519_PUBLIC_KEY` placeholder.

Back the private key up somewhere outside CI (a password manager). It is not
recoverable from the public key.

> **While the placeholder is still in place**, installed builds deliberately
> refuse to self-install and log a warning — they fall back to notify-only,
> exactly like the portable zip. That is the safe failure, but it does mean
> auto-update stays off until the key is wired in.

### Rotating or losing the key

Because the public key is compiled into every shipped copy, changing it means
already-installed copies will reject every future release: they verify against
the old key and see a mismatch. Recovery is a manual re-download by every user.
Treat `SPARKLE_PRIVATE_KEY` as release-critical, and rotate only alongside a
release that users are told to install by hand.

## Ongoing

Nothing month-to-month. Certificates are short-lived and rotate automatically
inside the service; CI authenticates per-run via OIDC. If you ever stop paying,
releases just go back to unsigned — the app keeps working.

One recurring item: **identity validation expires.** Microsoft emails reminders
starting 60 days out. Let it lapse and certificate renewal stops, which stops
signing until a new validation is created and attached to the profile.
