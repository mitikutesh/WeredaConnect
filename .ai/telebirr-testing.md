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
   Since escrow-protocol 1.18.0 (FUP-36) the server refuses `POST /escrow/initiate`
   with `422 ESCROW_039` unless the simulator's `baseUrl` host is loopback or
   `host.docker.internal`, so a tunnel-exposed simulator counts as live Telebirr
   and this option only works through the order checkout
   (`POST /escrow/order-checkout`).
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

Same gateway code; only config + a gated infra flag differ. **All the AWS wiring
is already built and gated OFF** behind `enableTelebirrLive` (default false), so
dev/staging keep the mock at zero extra cost until you deliberately flip it.

### ⚠️ Hard prerequisite: network reachability (source-IP allow-list)

Telebirr is **geo-restricted to Ethiopia**. The developer portal
(`developer.ethiotelecom.et`) requires an **Ethiopian proxy/VPN** to even browse,
and the sandbox gateway (`developerportal.ethiotelebirr.et` → `196.188.120.3:38443`)
is **unreachable from a non-Ethiopian IP** — TCP to `:38443` times out (verified
2026-07-07 from a European IP). This means:

- You **cannot** run the live fabric-token/preOrder from your laptop, even with
  valid credentials. Tier 2 (local simulator) is the only from-laptop live-code path.
- **Your backend's OUTBOUND calls to Telebirr must exit through an Ethiopian
  egress.** A registered AWS NAT IP in me-south-1/eu-north-1 is *not* Ethiopian,
  so an allow-list alone likely won't pass the geo-fence. Two options:
  1. **Ethiopian egress proxy (recommended, supported):** set `httpProxy` in the
     credentials secret (or `TELEBIRR_HTTP_PROXY` env) to an Ethiopian
     HTTP(S) proxy — `http[s]://[user:pass@]host:port`. The escrow/cron Lambdas
     then route Telebirr calls through it so Telebirr sees an Ethiopian source IP.
     The proxy must be publicly reachable from the Lambda (via the NAT) and exit
     in Ethiopia (commercial residential ET proxy, or a squid/tinyproxy on an
     Ethiopian VPS). Register the **proxy's** IP with Telebirr if they still
     require an allow-list.
  2. **Allow-list the NAT IP** — only if your Telebirr contact confirms they
     allow-list foreign IPs (i.e. the restriction is a per-IP allow-list, not a
     hard Ethiopia geo-fence). Then no proxy is needed.
- The **inbound webhook is unaffected** — Telebirr POSTs to your public HTTPS
  URL (outbound from Telebirr), which reaches any public endpoint regardless of
  geo-fencing. Only your outbound calls need the Ethiopian egress.

**Determine which path applies — `scripts/telebirr_reachability.sh`:** run it from
your laptop (expect unreachable), from your Ethiopian VPN (reachable → geo-fenced,
use `httpProxy`), or with `--proxy <candidate>` to validate an Ethiopian proxy
before putting it in the secret. Exit 0 = reachable, 1 = not.

### 📧 Onboarding request to send Telebirr (resolves both blockers)

The one external step that code can't do: send this to your Telebirr merchant
contact. Their answers resolve blocker #1 (their public key) and blocker #2 (the
allow-list / geo-fence question) in one round-trip.

> Subject: WeredaConnect (merchant 1658586294374402 / short code 200574) — sandbox integration questions
>
> Hello, we're integrating C2B H5 payments for the sandbox and need the following:
>
> 1. **Your RSA public key** for the sandbox, so we can verify your payment
>    notification (webhook) signatures.
> 2. **Source-IP policy for the payment gateway** (`developerportal.ethiotelebirr.et:38443`):
>    can you **allow-list a specific non-Ethiopian server IP** (our cloud egress),
>    or is the gateway reachable **only from Ethiopian networks**? If Ethiopia-only,
>    please confirm so — we'll route via an Ethiopian egress.
> 3. If you can allow-list: please allow-list our outbound IP **<EIP — from the
>    allocate-address step>**.
> 4. **The source IP(s) your notification service sends webhooks from**, so we can
>    allow-list your callbacks.
> 5. Confirmation of the exact **fabric-token and preOrder endpoint paths** and the
>    **signature padding** (PKCS#1 vs PSS) the sandbox expects.
>
> Our merchant public key (for you to register) is attached.

Attach the merchant public key (`openssl rsa -in merchant_private.pem -pubout`).
Their answers → drop their public key + `signPadding` into the secret; set
`telebirrAllowedIps`; and either register the NAT EIP (if #2 = allow-list) or set
`httpProxy` to an Ethiopian egress (if #2 = Ethiopia-only).

### What's already built (this branch)

- **`enableTelebirrLive` flag** (`infra/lib/config/environments.ts`; also
  `-c enableTelebirrLive=true` at deploy). When on, `infra/lib/stacks/api-stack.ts`
  provisions:
  - a **VPC + single t4g.nano NAT *instance* + Elastic IP** (NOT a NAT Gateway —
    per the "Never NAT Gateways" rule) giving the escrow Lambdas a stable outbound
    IP; free DynamoDB/S3 gateway endpoints keep that traffic off the NAT. The EIP
    is emitted as the `TelebirrNatEipOut` stack output.
  - the **public `POST /webhooks/telebirr` route** (no authorizer) + its Lambda.
  - the **`/wereda-connect-<env>/telebirr/credentials` secret** (placeholder
    values seeded so Lambdas cold-start; real values set out-of-band). Optional
    `telebirrNatEipAllocationId` reuses a pre-allocated EIP so you can register a
    stable IP with Telebirr before deploying.
  - `TELEBIRR_SECRET_ARN` / `TELEBIRR_NOTIFY_URL` (+ optional `TELEBIRR_ALLOWED_IPS`)
    on escrow + cron + webhook; `TELEBIRR_MOCK` is dropped so the real gateway runs.
- **Server**: the gateway routes OUTBOUND Telebirr calls through an optional
  **Ethiopian egress proxy** (`httpProxy` in the secret, or `TELEBIRR_HTTP_PROXY`
  env) — `TelebirrGatewayFactory.BuildHandler` (AOT-safe `SocketsHttpHandler` +
  `WebProxy`). Direct connection when unset.
- **Mobile**: the buyer flow opens the returned `toPayUrl` (Telebirr H5) via
  `url_launcher`, then polls `GET /escrow/{id}` (+ `weredaconnect://payment/complete`
  deep link / app-resume) until the webhook flips it to `escrowed`.
- **Mobile and web, order checkout (ESC-44)**: after the buyer's
  `payment_submitted` reaches the server, the order checkout screen calls
  `POST /escrow/order-checkout`, opens Telebirr (Android on its own, web from a
  link) and polls `GET /orders/{id}` until the webhook funds the order. Live
  builds (`--dart-define=TELEBIRR_LIVE=true`, and every `prod` build) show the
  Telebirr-only payment screen; demo builds keep the mock screen.

### Go-live sequence

1. **Register** at <https://developer.ethiotelecom.et/> and obtain: `fabricAppId`,
   `appSecret`, `merchantAppId`, `merchantCode`/`shortCode`, **Telebirr's RSA
   public key**, and their **notification source IPs**. Share YOUR merchant public
   key with Telebirr during onboarding — derive it from the private key:
   ```bash
   openssl rsa -in merchant_private.pem -pubout -out merchant_public.pem
   ```
2. **Allocate the outbound Elastic IP FIRST and give it to Telebirr** — so the
   allow-list request is in flight before you deploy (the sandbox is only
   reachable from the allow-listed IP, see above):
   ```bash
   aws ec2 allocate-address --domain vpc --region <region>
   #   → note "PublicIp" (give to Telebirr) and "AllocationId" (eipalloc-…)
   ```
   Then pin the NAT instance to that EIP at deploy via `telebirrNatEipAllocationId`
   (env config) or `-c telebirrNatEipAllocationId=eipalloc-…`. If you skip this,
   CDK allocates a fresh EIP on deploy and emits it as the `TelebirrNatEipOut`
   output — but then you must register it with Telebirr *after* the deploy.
3. **Deploy with the flag on** (staging first — me-south-1 is closest to Ethiopia;
   any region works once the IP is allow-listed):
   ```bash
   cdk deploy WeredaConnect-<env>-API \
     -c enableTelebirrLive=true \
     -c telebirrNatEipAllocationId=eipalloc-…   # from step 2 (omit to auto-create)
   ```
   Provisions the VPC + t4g.nano NAT instance (bound to your EIP) + public webhook
   route + credentials secret; escrow/cron switch off the mock. Confirm the
   `TelebirrNatEipOut` output matches the IP you gave Telebirr.
4. **Populate the secret** with real values (JSON shape below). Use `pss` for
   `signPadding`: Ethio Telecom's JS, Java, PHP and C# samples all sign with RSA-PSS
   (only the Python sample uses PKCS#1), and live notifications observed by third
   parties were PSS (ESC-45, `specs/decisions/telebirr-webhook-delivery-and-recovery.md`).
   `signPadding` only picks the padding of OUR requests; the webhook accepts a PSS or
   a PKCS#1 signature from Telebirr either way and logs which one verified.

   **dev/staging** (SSM Parameter Store — where non-prod secrets live):
   ```bash
   aws ssm put-parameter \
     --name /wereda-connect-<env>/telebirr/credentials \
     --type SecureString --overwrite \
     --value file://telebirr-credentials.<env>.json \
     --region <region>
   ```
   Note: SSM standard tier caps values at 4 KB — use an **RSA-2048** merchant key
   (a 4096-bit key pushes the JSON past the limit; `--tier Advanced` at $0.05/mo
   is the escape hatch). Never pass `--key-id`: the Lambdas' IAM only covers the
   default `aws/ssm` key.

   **prod** (Secrets Manager):
   ```bash
   aws secretsmanager put-secret-value \
     --secret-id /wereda-connect-<env>/telebirr/credentials \
     --secret-string file://telebirr-credentials.<env>.json \
     --region <region>
   ```
   ```json
   {
     "fabricAppId": "...", "appSecret": "...", "merchantAppId": "...",
     "merchantCode": "...", "shortCode": "...",
     "baseUrl": "https://developerportal.ethiotelebirr.et:38443/apiaccess/payment/gateway",
     "webUrl":  "https://developerportal.ethiotelebirr.et:38443/payment/web/paygate",
     "merchantPrivateKeyPem": "<your merchant private key, PKCS#8 PEM, newlines as \\n>",
     "telebirrPublicKeyPem":  "<Telebirr's sandbox public key, PEM, newlines as \\n>",
     "returnUrl": "weredaconnect://payment/complete",
     "signPadding": "pss",
     "httpProxy": "http://user:pass@et-proxy.example:8080"
   }
   ```
   `httpProxy` is the **Ethiopian egress** for outbound Telebirr calls (see the
   reachability note); leave it `""` if Telebirr allow-lists your NAT IP directly.
   It can also be supplied via the `TELEBIRR_HTTP_PROXY` env var (env wins).
   The `notify_url` is injected from `TELEBIRR_NOTIFY_URL` (the deployed webhook
   URL), so it need not be in the secret. Set `TELEBIRR_ALLOWED_IPS` (env config
   `telebirrAllowedIps`) to Telebirr's notification IPs to enable the webhook
   IP allow-list (signature verification is enforced regardless).
5. **Confirm the exact wire details** with the ESC-45 sandbox checklist
   (`specs/decisions/telebirr-webhook-delivery-and-recovery.md`): the token at
   `/payment/v1/token`, request timestamps in seconds, the flat notify body (`data`
   envelope, `transId` vs `trans_id`, `notify_time` unit) and the padding. Set
   `signPadding` to `pkcs1` only if preOrder answers "verify sign failed" with PSS.
6. **Build the app live** for that environment: set the GitHub environment
   variable `TELEBIRR_LIVE=true` on the environment (dev or staging, never at
   repo level) and redeploy the webapp (`deploy-webapp.yml`); for a manual web
   deploy run `TELEBIRR_LIVE=true scripts/deploy.sh <env>`; Android builds pass
   `--dart-define=TELEBIRR_LIVE=true`. Prod builds are live without it.
7. Pay with a Telebirr **test account**, confirm the webhook flips the escrow to
   `escrowed` (and an order checkout to `funded`), then switch base URLs to
   `https://app.ethiotelebirr.et:38443/…` for production.

> **Still not built** (out of scope for this prep): the payout/refund
> disbursement leg. Since ESC-02 the live gateway's release and refund fail
> closed with `PAYOUT_NOT_SUPPORTED` (no fake `SANDBOX-SIM-…` success) and the
> escrow goes to the manual payout queue (`GET /admin/payouts/manual`, admin
> page "Manual Payouts"), where an admin pays in the merchant portal and records
> the transaction id. Real B2B disbursement is still required before payouts
> run without an admin.

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
| App order checkout (ESC-44) | `mobile/test/features/order/presentation/bloc/order_checkout_bloc_test.dart`, `.../screens/order_checkout_screen_test.dart`; live web path with a faked gateway: `e2e/webapp/order-checkout.spec.ts` (`E2E_LIVE_CHECKOUT=1`, local `TELEBIRR_LIVE=true` build) |
