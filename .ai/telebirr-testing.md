# Telebirr + Full-Flow Testing Runbook

How to test the entire WeredaConnect flow (contact seller → intent → matching →
order → delivery → escrow → dispute/settlement) **without a real Telebirr
merchant account**, and how to graduate to the live Telebirr sandbox.

Three tiers, each independently runnable. See the design in
`specs/decisions/telebirr-sandbox-testing-strategy.md`.

---

## TL;DR

```bash
# 0. Server component tests (gateway signing, webhook, escrow) — fully local
cd server && dotnet test

# 1. Local Telebirr sandbox simulator self-test — fully local, no account
cd tools/telebirr-sandbox && node server.js &        # http://127.0.0.1:8790
npm run selftest

# 2. C# gateway ↔ live simulator (real HTTP + PSS signing)
TELEBIRR_SIM_URL=http://127.0.0.1:8790 \
TELEBIRR_CREDS_FILE="$(pwd)/tools/telebirr-sandbox/credentials.local.json" \
  dotnet test server --filter FullyQualifiedName~HoldFunds_against_live_simulator

# 3. Full marketplace→escrow flow against the deployed dev API (mock gateway)
python3 scripts/e2e_full_flow.py
```

---

## Tier 1 — Full app flow with the mock gateway (no account)

The deployed dev API runs `MockTelebirrGateway` (`TELEBIRR_MOCK=true`), so escrow
becomes `escrowed` synchronously. `scripts/e2e_full_flow.py` drives the whole
flow and checks role visibility, contact-seller, matching, the 3-way order
split, delivery, escrow, and dispute.

```bash
python3 scripts/e2e_full_flow.py                      # deployed dev
python3 scripts/e2e_full_flow.py --base-url <url>     # any deployment
```

Notes:
- It authenticates with the **ID token** (the API reads `custom:role` from the ID
  token, not the access token).
- It degrades honestly: features present in source but not on the target
  deployment are reported **SKIP** (e.g. `/match`, `buyer_intent` if a build
  predates PR #27); an un-deployed tightening (F-36 cross-resource 403) is a
  **WARN**. Only genuine breakage is a FAIL.
- Escrow `release → completed` and the auto-release/refund cron need system/admin
  or the scheduled Lambda and are covered by
  `server/tests/Functions/EscrowFlowIntegrationTests.cs`.

The older `scripts/e2e_test.py` (10 phases) also runs against deployed dev.

---

## Tier 2 — Local Telebirr sandbox simulator + real gateway (no account)

Exercises the **real** gateway code path (fabric-token + `SHA256WithRSA` signing
+ `preOrder` + built H5 URL + signed webhook) against a local simulator that
speaks the real Telebirr protocol. Fully offline.

### 2a. Start the simulator

```bash
cd tools/telebirr-sandbox
node server.js            # http://127.0.0.1:8790
npm run selftest         # optional: drives the whole H5 flow, verifies webhook sig
```

First run generates `keys/` (merchant + "Telebirr" RSA keypairs) and
`credentials.local.json` (the file the .NET gateway loads). Both are git-ignored.

### 2b. Prove the C# gateway talks to it

```bash
TELEBIRR_SIM_URL=http://127.0.0.1:8790 \
TELEBIRR_CREDS_FILE="$(git rev-parse --show-toplevel)/tools/telebirr-sandbox/credentials.local.json" \
  dotnet test server --filter FullyQualifiedName~HoldFunds_against_live_simulator
```

This acquires a fabric token, signs + posts a `preOrder`, and asserts the
returned `toPayUrl`. The signing scheme is proven byte-identical to the simulator
by `TelebirrSignatureServiceTests` (a Node-produced signature verifies in C#).

### 2c. Manual browser walk-through of the pay page

Open the `toPayUrl` (printed by the gateway / built from `webUrl` +
`prepay_id`) in a browser, or hit the simulator directly:

```bash
# The simulator renders a pay page with Pay / Fail buttons at:
#   http://127.0.0.1:8790/payment/web/paygate?...   (built by the gateway)
# Clicking "Pay" POSTs /simulate/complete which fires the signed webhook.
```

### 2d. Full server↔sim↔webhook loop

The server is Native-AOT Lambda (no local web host). To run the async
`initiate → toPayUrl → pay → webhook → escrowed` loop end-to-end you need the API
reachable by the simulator's webhook POST. Two options:

1. **Deploy dev with the sandbox gateway** and point `notify_url` at a
   publicly-reachable simulator (run it behind a tunnel), then:
   ```bash
   python3 scripts/e2e_full_flow.py --base-url <dev-url> --telebirr-sim-url <sim-url>
   ```
   The script fetches the `toPayUrl`, completes payment on the simulator, and
   polls `GET /escrow/{id}` until it flips to `escrowed`.
2. **Component tests** (no deployment): `TelebirrWebhookFunctionTests` feeds the
   handler a simulator-signed notification and asserts the `initiated → escrowed`
   transition, plus every rejection path (bad signature / stale timestamp /
   replay / amount mismatch).

### Config to switch the server off the mock

```
TELEBIRR_MOCK=false
TELEBIRR_LOCAL_CREDENTIALS_FILE=tools/telebirr-sandbox/credentials.local.json
TELEBIRR_NOTIFY_URL=<your server>/webhooks/telebirr
```

`CreateTelebirrGateway()` then builds `TelebirrSandboxGateway`. Rollback is
always `TELEBIRR_MOCK=true` (no code change).

### LocalStack / Cognito caveat

`server/docker-compose.localstack.yml` emulates DynamoDB/S3/Lambda/API-Gateway
but **not Cognito**. A fully-local API still needs a Cognito pool (the deployed
dev pool `eu-north-1_1LL9tzoxw`) for `/auth/*`. For pure escrow/webhook work the
component tests avoid this entirely.

---

## Tier 3 — Live developer.ethiotelebirr.et sandbox (needs registration)

Same gateway code; only config differs.

1. **Register** at <https://developer.ethiotelecom.et/> and obtain the merchant
   sandbox credentials: `fabricAppId`, `appSecret`, `merchantAppId`,
   `merchantCode`/`shortCode`, and **Telebirr's RSA public key**.
2. **Generate our RSA keypair** and share the public key with Telebirr during
   onboarding (used to verify our request signatures):
   ```bash
   openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:2048 -out merchant_private.pem
   openssl rsa -pubout -in merchant_private.pem -out merchant_public.pem
   ```
3. **Store credentials** in AWS Secrets Manager (`/wereda-connect-<env>/telebirr-credentials`)
   using the same JSON shape as `credentials.local.json`, with
   `baseUrl=https://developerportal.ethiotelebirr.et:38443/apiaccess/payment/gateway`
   and `webUrl=…/payment/web/paygate`.
4. **Deploy infra**: add the webhook route `POST /webhooks/telebirr` (no
   authorizer) + a NAT Gateway so the escrow Lambdas have a static outbound IP,
   and register that IP with Telebirr (source-IP allow-list).
5. **Set env**: `TELEBIRR_MOCK=false`, `TELEBIRR_SECRET_ARN=<arn>`,
   `TELEBIRR_NOTIFY_URL=<api>/webhooks/telebirr`, `TELEBIRR_ALLOWED_IPS=<telebirr IPs>`.
6. **Confirm the exact wire details** against the `Demo_Project` sample and the
   portal docs before go-live — specifically the fabric-token/preOrder **paths**
   and any field-casing quirks. The signing scheme (`SHA256WithRSA`, PSS default,
   canonical string) matches the OgBek SDK; verify it against the sandbox and
   flip `signPadding` if the portal requires PKCS1.
7. Test payment with a Telebirr **test account** on the sandbox, confirm the
   webhook flips the escrow to `escrowed`, then switch base URLs to
   `https://app.ethiotelebirr.et:38443/…` for production.

> `Demo_Project` lives under `~/Downloads`, which macOS privacy protection blocks
> from the CLI. Copy it into the repo (or grant the terminal Full-Disk / Downloads
> access) to diff its exact field names against this implementation before Tier 3.

---

## What's covered where

| Concern | Covered by |
|---|---|
| Escrow state machine, settlement, cron | `server/tests/Functions/EscrowFlowIntegrationTests.cs`, `EscrowServiceTests` |
| Telebirr signing (incl. Node↔C# interop) | `server/tests/Services/TelebirrSignatureServiceTests.cs` |
| Gateway preOrder/H5 build + live sim | `server/tests/Services/TelebirrSandboxGatewayTests.cs` |
| Webhook verify/timestamp/replay/amount + transition | `server/tests/Functions/TelebirrWebhookFunctionTests.cs` |
| Simulator protocol end-to-end | `tools/telebirr-sandbox/` (`npm run selftest`) |
| Whole marketplace flow + role visibility | `scripts/e2e_full_flow.py` |
