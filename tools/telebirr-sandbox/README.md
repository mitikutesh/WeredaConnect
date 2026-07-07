# Telebirr Sandbox Simulator (local, zero-dependency)

Plays the role of Telebirr's H5/C2B payment gateway so the **entire** escrow
flow can be tested with **no real merchant account and no internet**. It speaks
the real Telebirr protocol (fabric-token → `preOrder` → H5 checkout URL →
RSA-signed webhook), so the same `TelebirrSandboxGateway` server code works
against this simulator *and* the live `developerportal.ethiotelebirr.et` sandbox
— only config differs.

## Run

```bash
cd tools/telebirr-sandbox
node server.js            # listens on http://127.0.0.1:8790
```

On first run it generates two RSA keypairs into `./keys` (the "merchant"
keypair = our server; the "Telebirr" keypair = the simulator) and writes
`./credentials.local.json` — the file the .NET gateway loads for local testing.
Both are git-ignored.

## Endpoints (mirror real Telebirr)

| Method | Path | Purpose |
|---|---|---|
| POST | `/apply/fabric-token` | Returns a fabric token (checks `X-APP-Key`) |
| POST | `/payment/v1/merchant/preOrder` | Verifies our `SHA256WithRSA` sign, returns `prepay_id` |
| GET | `/payment/web/paygate` | Buyer "pay page" with **Pay**/**Fail** buttons |
| POST | `/simulate/complete` | Fires the RSA-signed webhook to `notify_url` |
| GET | `/health`, `/` | status / help |

## Self-test

```bash
node server.js &          # start it
npm run selftest          # drives the full flow, verifies webhook signature
```

## Config (env)

| Var | Default | Notes |
|---|---|---|
| `PORT` | `8790` | listen port |
| `TELEBIRR_SIGN_PADDING` | `pss` | `pss` (real Telebirr) or `pkcs1` |
| `TELEBIRR_RETURN_URL` | `weredaconnect://payment/complete` | app deep link |

## How the server consumes it

Point the gateway at the simulator:

```
TELEBIRR_MOCK=false
TELEBIRR_BASE_URL=http://127.0.0.1:8790
TELEBIRR_WEB_URL=http://127.0.0.1:8790/payment/web/paygate
TELEBIRR_LOCAL_CREDENTIALS_FILE=tools/telebirr-sandbox/credentials.local.json
```

The credentials file carries the merchant private key (to sign our requests) and
the Telebirr public key (to verify webhooks) — mirroring what Secrets Manager
holds in AWS.

## Crypto

`telebirr-crypto.js` is the single source of truth for canonicalization:
recursively flatten top-level + `biz_content`, drop `sign/sign_type/header/
refund_info/openType/raw_request`, drop nulls (keep empty strings), booleans →
`"1"/"0"`, sort keys, join `key=value&…` (no URL-encoding), sign RSA-SHA256
(PSS salt 32 by default), base64. The C# `TelebirrSignatureService` implements
the identical rule.
