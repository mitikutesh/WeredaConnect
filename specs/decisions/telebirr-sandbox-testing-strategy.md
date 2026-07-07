# Telebirr Sandbox — End-to-End Testing Strategy

## Version
1.0.0 | 2026-07-07

## Status
Proposed

## Purpose
Provide a concrete, staged way to exercise the **entire** WeredaConnect flow
(contact seller → buyer intent → matching → order → trip → delivery → escrow
payment → settlement/dispute) **without a real Telebirr merchant account**, and
to make the eventual switch to the live Telebirr sandbox a config-only change.

This decision record supersedes the Telebirr integration mechanics described in
`telebirr-sandbox-architecture.md` where the two conflict — see
[§5 Crypto Correction](#5-crypto-correction). The architecture doc's escrow
model (platform-managed escrow, Option A) still stands; only the wire protocol
was wrong.

---

## 1. Problem

- The escrow state machine (`EscrowService`) is complete and well-tested, but
  only against `MockTelebirrGateway`. `CreateTelebirrGateway()` throws for any
  non-mock path — the real gateway was never implemented.
- Existing coverage (`server/tests/.../EscrowFlowIntegrationTests.cs`,
  `scripts/e2e_test.py`) exercises most of the marketplace but **starts too
  late** on the payment side (no matching, no order/3-way-split, no
  `release → completed`, no real Telebirr wire path) and runs against **deployed
  AWS with the mock**.
- We have no Telebirr merchant credentials, so we cannot hit the live sandbox
  yet.

## 2. Goals

1. Test the whole app end-to-end today, with no external accounts.
2. Exercise the **real** Telebirr gateway code (signing, preOrder, H5 URL,
   webhook), not just the mock — so the live-sandbox switch carries no code risk.
3. Keep `MockTelebirrGateway` as the default; everything new is additive and
   gated behind `TELEBIRR_MOCK=false` + `TELEBIRR_BASE_URL`.

## 3. Decision — three independent tiers

### Tier 1 — Full-flow test with the mock (no account)
Close the coverage gaps so the complete flow is verified with `TELEBIRR_MOCK=true`:
contact-seller → buyer intent → intent↔crop/trip matching → order (with 3-way
split) → delivery → **escrow initiate → escrowed → confirm delivery → release →
completed**, plus dispute→refund and the auto-release/auto-refund cron.
Fast, deterministic, CI-friendly.

### Tier 2 — Local Telebirr sandbox simulator + real gateway (no account) ⭐
- `tools/telebirr-sandbox/` — a standalone local HTTP service that speaks the
  **real** Telebirr protocol and plays the role of Telebirr:
  - `POST /apply/fabric-token` → returns a fabric token
  - `POST /payment/v1/merchant/preOrder` → verifies the merchant's
    `SHA256WithRSA` signature, returns a `prepay_id`
  - `GET /payment/web/paygate?...` → renders a mock "pay page" with **Pay** and
    **Fail** buttons; clicking fires an **RSA-signed** notification to the
    merchant's `notify_url`
  - Holds its own RSA keypair (acts as "Telebirr"); verifies our request
    signatures with our public key.
- Server: real `TelebirrSandboxGateway`, `TelebirrSignatureService`,
  `TelebirrWebhookFunction`, `Models/TelebirrDtos.cs`, wired via
  `CreateTelebirrGateway()` when `TELEBIRR_MOCK=false`.
- Point the gateway at the simulator with
  `TELEBIRR_BASE_URL=http://localhost:8790`. Fully offline; exercises the genuine
  signing + webhook path.

### Tier 3 — Live developer.ethiotelebirr.et sandbox (needs account + Demo_Project)
Config-only switch: real credentials + Telebirr public key in Secrets Manager,
our RSA keypair, deploy the webhook route + NAT (static outbound IP for
whitelisting), set the sandbox base URLs. Same gateway code as Tier 2. The
`Demo_Project` sample and portal docs are used only to confirm exact field
names/quirks here.

## 4. Verified Telebirr protocol (from the OgBek/Telebirr SDK)

Base URLs:
- Sandbox API:  `https://developerportal.ethiotelebirr.et:38443/apiaccess/payment/gateway`
- Sandbox web:  `https://developerportal.ethiotelebirr.et:38443/payment/web/paygate`
- Prod API:     `https://app.ethiotelebirr.et:38443/apiaccess/payment/gateway`
- Prod web:     `https://app.ethiotelebirr.et:38443/payment/web/paygate`

Fabric token: `POST /apply/fabric-token` with `{ appSecret, X-APP-Key: fabricAppId }`.

PreOrder: `POST /payment/v1/merchant/preOrder`, headers `Authorization: <fabricToken>`,
`X-APP-Key: <fabricAppId>`. Body:
```json
{
  "nonce_str": "...", "method": "payment.preorder", "timestamp": "...",
  "version": "1.0",
  "biz_content": {
    "notify_url": "...", "appid": "<merchantAppId>", "merch_code": "<merchantCode>",
    "merch_order_id": "<our idempotency-key/outTradeNo>", "trade_type": "Checkout",
    "title": "...", "total_amount": "15000.00", "trans_currency": "ETB",
    "timeout_express": "120m", "business_type": "BuyGoods",
    "callback_info": "From web", "redirect_url": "<returnUrl>"
  },
  "sign_type": "SHA256WithRSA", "sign": "<base64 RSA sig>"
}
```
Response → `prepay_id`.

H5 checkout URL: sign **exactly five** fields
`{appid, merch_code, nonce_str, prepay_id, timestamp}`, then append
`sign_type=SHA256WithRSA&version=1.0&trade_type=Checkout&sign=<urlenc>`, prefixed
with the web URL. Never sign `version/trade_type/sign_type/redirect_url`.

Signing: RSA over the canonical string. Canonicalization = recursively
`ksort(SORT_STRING)`, drop `sign, sign_type, header, refund_info, openType,
raw_request`, drop nulls (keep empty strings), booleans → `'1'`/`'0'`, join
`key=value&key2=value2` (no URL-encoding), sign with SHA256 (RSA-PSS default,
salt 32; PKCS1 optional), output base64.

Webhook: RSA-verify with Telebirr's public key, timestamp within 300s, reject
duplicate `nonce_str`.

## 5. Crypto Correction

`telebirr-sandbox-architecture.md` v2.1.0 assumed:
- `h5token` auth → **wrong**; it's `fabric-token`.
- RSA+AES hybrid **encryption** of the whole payload → **wrong**; payloads are
  **signed** (SHA256WithRSA), not encrypted.
- secret-suffix SHA256 signing → **wrong**; it's RSA over a recursively-sorted
  canonical string.
- `toPayUrl` returned by `/create/order` → **wrong**; the merchant **builds** the
  H5 URL from a 5-field signature after `preOrder` returns `prepay_id`.

The `ITelebirrGateway` interface and the escrow state machine are unaffected —
only the gateway's wire implementation changes.

## 6. File inventory

New (server):
- `server/src/Services/ITelebirrSignatureService.cs`
- `server/src/Services/TelebirrSignatureService.cs`
- `server/src/Services/TelebirrSandboxGateway.cs`
- `server/src/Models/TelebirrDtos.cs`
- `server/src/Functions/TelebirrWebhookFunction.cs`
- tests under `server/tests/Services/` and `server/tests/Functions/`

New (tooling / tests):
- `tools/telebirr-sandbox/` — local simulator
- full-flow E2E harness (Tier 1) — extends `scripts/e2e_test.py`

Modified:
- `server/src/Functions/EscrowFunction.cs` + `EscrowCronFunction.cs`
  — `CreateTelebirrGateway()` builds the sandbox gateway when `TELEBIRR_MOCK=false`
- `server/src/AppJsonContext.cs` — register Telebirr DTOs
- `server/src/Models/EscrowDtos.cs` — add transient `ToPayUrl` to `HoldFundsResponse`

## 7. Config

| Var | Tier 2 (local sim) | Tier 3 (live sandbox) |
|---|---|---|
| `TELEBIRR_MOCK` | `false` | `false` |
| `TELEBIRR_BASE_URL` | `http://localhost:8790` | `https://developerportal.ethiotelebirr.et:38443/apiaccess/payment/gateway` |
| `TELEBIRR_WEB_URL` | `http://localhost:8790/payment/web/paygate` | `https://developerportal.ethiotelebirr.et:38443/payment/web/paygate` |
| credentials | generated locally by the simulator | Secrets Manager (`/wereda-connect-{env}/telebirr-credentials`) |

Rollback is always `TELEBIRR_MOCK=true` (no code change).

## 8. Open items
- Confirm exact field casing / any extra fields against the `Demo_Project` sample
  and portal docs before Tier 3 (SDK is our best reference until then).
- Trucker payout leg (3-way split) uses the same C2B-collect + platform-disburse
  model; B2B disbursement remains a logged intent in Tiers 1–2.
</content>
</invoke>
