# Implementation Prompt — Telebirr Sandbox + Full-Flow E2E Testing

> Paste this into a fresh Claude Code session (or run via `/implement`) to build
> (or finish) the end-to-end testing solution. It is self-contained. Follow
> `CLAUDE.md` (completeness rules, AOT compliance, 3-role checks, mobile+web
> pairs). Do **not** commit, push, or deploy without explicit approval.

---

## Goal

Let a developer with **no real Telebirr merchant account** test the **entire**
WeredaConnect flow end-to-end: contact seller → buyer intent → intent↔crop/trip
matching → order (with 3-way farmer/trucker/commission split) → delivery →
escrow **initiate → escrowed → confirm delivery → release → completed**, plus
dispute→refund and the auto-release/refund cron. And exercise the **real**
Telebirr gateway wire path (fabric-token + RSA-SHA256 signing + `preOrder` + H5
URL + webhook), not just the mock — so switching to the live sandbox is a
config-only change.

Read first:
- `specs/decisions/telebirr-sandbox-testing-strategy.md` (the plan + tiers)
- `specs/decisions/telebirr-sandbox-architecture.md` (escrow model; **note its
  crypto section is wrong** — see below)
- `specs/contracts/escrow-protocol.md`
- `server/src/Services/EscrowService.cs`, `server/src/Functions/EscrowFunction.cs`,
  `server/src/Functions/EscrowCronFunction.cs`, `server/src/Services/MockTelebirrGateway.cs`
- `tools/telebirr-sandbox/` (the local simulator — ALREADY BUILT AND PASSING)
- `scripts/e2e_test.py` (existing marketplace E2E against deployed dev)

## Verified Telebirr protocol (source of truth: OgBek/Telebirr SDK)

The architecture doc assumed h5token + RSA+AES payload **encryption** +
secret-suffix SHA256. That is **WRONG**. The real protocol is:

- **Auth**: `POST /apply/fabric-token` (header `X-APP-Key: <fabricAppId>`, body
  `{appSecret}`) → `{ token }`. Send on later calls as
  `Authorization: <token>` + `X-APP-Key: <fabricAppId>`.
- **Create order**: `POST /payment/v1/merchant/preOrder` with body
  `{ nonce_str, method:"payment.preorder", timestamp, version:"1.0",
  biz_content:{ notify_url, appid, merch_code, merch_order_id, trade_type:"Checkout",
  title, total_amount:"NN.NN", trans_currency:"ETB", timeout_express:"120m",
  business_type:"BuyGoods", callback_info, redirect_url }, sign_type:"SHA256WithRSA",
  sign }` → response carries `biz_content.prepay_id`.
- **H5 checkout URL**: sign exactly 5 fields `{appid, merch_code, nonce_str,
  prepay_id, timestamp}`, then build
  `<web_url>?appid=..&merch_code=..&nonce_str=..&prepay_id=..&timestamp=..&sign_type=SHA256WithRSA&version=1.0&trade_type=Checkout&sign=<urlenc>`.
  Never sign `version/trade_type/sign_type/redirect_url`.
- **Signing**: RSA over a canonical string = recursively flatten top-level +
  `biz_content` into one map, drop `sign, sign_type, header, refund_info,
  openType, raw_request`, drop null (keep empty), booleans → `"1"/"0"`, sort keys
  ascending, join `key=value&…` (no URL-encoding). Padding **PSS** (SHA256, salt
  32, MGF1-SHA256) by default; PKCS1 selectable. Output base64.
- **Webhook**: Telebirr POSTs a notification signed the same way; verify with
  Telebirr's public key, enforce a 300s timestamp window and nonce-replay
  rejection. Notification `biz_content` carries `merch_order_id`,
  `payment_order_id`, `total_amount`, `trade_status` (`Completed`/`Failed`), …
- **Base URLs**: sandbox `https://developerportal.ethiotelebirr.et:38443/apiaccess/payment/gateway`
  and web `…/payment/web/paygate`; prod `https://app.ethiotelebirr.et:38443/…`.

The local simulator in `tools/telebirr-sandbox/` already implements exactly this
(see `telebirr-crypto.js`) and passes `npm run selftest`. Make the C# side
interop with it.

---

## Work items

### A. Server — real gateway, signing, DTOs, webhook (AOT-safe, no reflection)

1. `server/src/Models/TelebirrDtos.cs` — records for token/preOrder request &
   response and the webhook notification. Register **all** in
   `server/src/AppJsonContext.cs`. Use `System.Text.Json` source-gen only.
2. `server/src/Services/ITelebirrSignatureService.cs` +
   `TelebirrSignatureService.cs`:
   - `string BuildCanonicalString(IReadOnlyDictionary<string,object?> flat)` — the
     rule above (flatten biz_content, exclusions, sort, join).
   - `string Sign(canonical)` using `RSA` + `RSASignaturePadding.Pss`
     (SHA256) by default, `Pkcs1` selectable; base64.
   - `bool Verify(canonical, base64sig, telebirrPublicKey)`.
   - AOT-safe: `System.Security.Cryptography` only.
3. `server/src/Services/TelebirrConfig.cs` — load from a JSON string (local
   `credentials.local.json` shape *and* the Secrets Manager shape): fabricAppId,
   appSecret, merchantAppId, merchantCode, shortCode, merchantPrivateKeyPem,
   telebirrPublicKeyPem, baseUrl, webUrl, returnUrl, notifyUrl, allowedSourceIps,
   signPadding. Validate + fail loudly on missing required fields.
4. `server/src/Services/TelebirrSandboxGateway.cs : ITelebirrGateway`:
   - `HoldFundsAsync` → fabric-token, build+sign preOrder (`merch_order_id` =
     `request.TransactionRef`), POST preOrder, get `prepay_id`, build the signed
     H5 URL, return `HoldFundsResponse { Success=true, ToPayUrl=<h5url>,
     TelebirrRef = "PENDING-WEBHOOK-" + hash(TransactionRef) }`. On upstream
     failure return `Success=false, ErrorCode="TELEBIRR_*"`.
   - `ReleaseFundsAsync` / `RefundFundsAsync` → Phase-1 logged intents returning
     `SANDBOX-SIM-PAYOUT-/REFUND-<hash>` (B2B disbursement deferred).
   - `GetTransactionStatusAsync` → `"unknown"` (no polling endpoint).
   - Cache the fabric token for its TTL; generate nonces with a CSPRNG.
5. `server/src/Functions/TelebirrWebhookFunction.cs` (new Lambda,
   `POST /webhooks/telebirr`, **no** JWT authorizer):
   - Always return `200 OK` (never leak validation results).
   - Verify signature (Telebirr public key) → timestamp window (300s) →
     nonce-replay (reuse `IEscrowIdempotencyRepository`) → find escrow by
     `merch_order_id` → **verify `total_amount` matches escrow amount** → on
     `Completed` transition `initiated → escrowed` (set `telebirrRef =
     payment_order_id`), on `Failed` transition `initiated → cancelled`. Append
     audit entries. Idempotent.
6. Wire `CreateTelebirrGateway()` in `EscrowFunction.cs` **and**
   `EscrowCronFunction.cs`: when `TELEBIRR_MOCK != "true"`, build
   `TelebirrSandboxGateway` from `TELEBIRR_LOCAL_CREDENTIALS_FILE`/`_JSON` (local)
   or Secrets Manager (`TELEBIRR_SECRET_ARN`, AWS). Keep the mock as the default;
   existing behavior must be byte-identical when `TELEBIRR_MOCK=true`.

### B. Server — async escrow semantics (money code — careful, minimal, tested)

Real C2B is asynchronous: `preOrder` only yields a pay URL; funds aren't
confirmed until the webhook. Today `EscrowService.InitiateAsync` marks
`escrowed` as soon as `HoldFundsAsync` returns success — correct for the sync
mock, wrong for the async gateway.

- Add `string? ToPayUrl` to `HoldFundsResponse` (`server/src/Models/EscrowDtos.cs`).
- In `InitiateAsync`, when `holdResponse.ToPayUrl` is set: keep status
  `initiated`, store `telebirrRef` = the pending placeholder, return the escrow
  with `Status="initiated"` + `ToPayUrl` in `InitiateEscrowResponse` (add the
  field + register), and mark the idempotency record `processing` (not
  `completed`). Do **not** persist `ToPayUrl`. The webhook later flips to
  `escrowed`.
- When `ToPayUrl` is null (mock/sync path): behavior UNCHANGED.
- Cover both paths with tests; assert `GET /escrow/{id}` never contains
  `toPayUrl`.

### C. Server tests (xUnit + NSubstitute + FluentAssertions; **never Moq**)

- `TelebirrSignatureServiceTests` — canonical-string construction (incl.
  biz_content flattening + exclusions), sign→verify round-trip, PSS and PKCS1.
- `TelebirrSandboxGatewayTests` — inject a fake `HttpMessageHandler`; assert
  fabric-token + preOrder calls, headers, that the request body sign verifies,
  `ToPayUrl` shape, and error mapping.
- `TelebirrWebhookFunctionTests` — valid Completed→escrowed; Failed→cancelled;
  bad signature/expired timestamp/replayed nonce/amount-mismatch all return 200
  and do NOT transition.
- `EscrowService` async-initiate tests (item B).
- Cross-language interop check (optional but recommended): a test that verifies a
  signature produced by `tools/telebirr-sandbox/telebirr-crypto.js` for a known
  payload validates in `TelebirrSignatureService` and vice-versa.

### D. Infrastructure (CDK) — additive, gated

- Webhook route `POST /webhooks/telebirr` (no authorizer), reserved concurrency,
  route throttling.
- Secrets Manager secret `/${prefix}/telebirr-credentials`; env `TELEBIRR_MOCK`,
  `TELEBIRR_SECRET_ARN`, `TELEBIRR_BASE_URL`, `TELEBIRR_WEB_URL`.
- NAT/static-egress IP only needed for the live sandbox (Tier 3) — document, do
  not necessarily deploy for local testing.
- `npx cdk synth --quiet` must pass.

### E. Full-flow E2E test (Tier 1 + Tier 2)

Extend `scripts/e2e_test.py` (or add `scripts/e2e_full_flow.py`) to cover the
WHOLE flow and be parameterized by `--base-url` (deployed dev = mock) and a
`--telebirr-sim-url` (local simulator = real gateway path). Add the missing
pieces the current script skips:
- **Contact seller** (find the endpoint/field — phone reveal or messaging).
- **Buyer intent** creation + **matching** (intent↔crop/trip) — inspect
  `server/src/Functions/MatchFunction.cs` + `CropMatchStreamFunction.cs` and the
  matching contract; assert matches are produced.
- **Order** creation with the 3-way split — inspect
  `server/src/Functions/OrderFunction.cs` + `OrderDtos`; the escrow `orderId`
  path in `EscrowService.InitiateAsync` reconciles farmer+trucker+commission.
- Escrow **release → completed** and the **cron auto-release/auto-refund**
  (`/escrow-cron` action `auto_release_and_refund`, `reconciliation`).
- **Role visibility** asserts: farmer sees only own crops; trucker only own
  trips; buyer sees ALL crops + ALL trips.
- When `--telebirr-sim-url` is set: after `escrow/initiate`, GET the returned
  `toPayUrl`, POST the simulator's `/simulate/complete`, then poll
  `GET /escrow/{id}` until `escrowed` — proving the real gateway+webhook path.

Also consider a Flutter `integration_test` that drives the actual escrow screens
against a locally-running server + simulator (optional, heavier).

### F. Docs / runbook

`.ai/telebirr-testing.md` (or extend `TESTING.md`): how to (1) run the full flow
against deployed dev with the mock; (2) run locally with the simulator + real
gateway (start sim, set env, run server, run E2E); (3) onboard the live
`developer.ethiotelecom.et` sandbox — register, obtain fabricAppId/appSecret/
merchantAppId/merchantCode + Telebirr public key, generate our RSA keypair, load
Secrets Manager, deploy webhook+NAT, whitelist the egress IP, flip
`TELEBIRR_MOCK=false` + sandbox URLs. Note LocalStack lacks Cognito (auth uses
the real eu-north-1 pool) and how to work around it (seed test users / stub JWT
in local).

## Acceptance criteria

- `cd server && dotnet build && dotnet test` green (incl. new tests).
- `cd tools/telebirr-sandbox && node server.js & npm run selftest` green.
- With the simulator running and `TELEBIRR_MOCK=false`, the E2E reaches
  `escrowed` **via the webhook** (not synchronously) and completes settlement.
- `TELEBIRR_MOCK=true` path unchanged (all existing tests still pass).
- `flutter analyze && flutter test` green if any mobile code changed; run
  `flutter build web` if a platform-paired file changed.
- All 3 roles verified; no `dart:io`/Isar in shared/web paths; AOT rules honored
  (no Newtonsoft/Moq/reflection); 3 language files updated if UI strings changed.

## Constraints

- Keep `MockTelebirrGateway` the default; all new behavior gated behind
  `TELEBIRR_MOCK=false`.
- Money-handling code (`EscrowService`) changes must be minimal, preserve the
  sync path exactly, and be fully tested. Flag them for review.
- Confirm the exact live-sandbox field casing/quirks against the `Demo_Project`
  sample + portal before Tier 3 (the SDK is the reference until then).
