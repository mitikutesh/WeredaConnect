# Fayda mock (eSignet OIDC provider)

A mock VeriFayda eSignet OpenID Connect provider for dev. The WeredaConnect
server talks to it exactly like real Fayda: eSignet paths under `/v1/esignet`,
authorization code with PKCE S256, `private_key_jwt` client authentication only,
an RS256 id_token with identity only, and a signed userinfo JWT that carries the
claims the `claims` parameter asked for. Zero npm dependencies. In Lambda it uses the AWS SDK v3 that
ships with the Lambda Node.js runtime (nodejs24.x).

Deployed by `infra/lib/constructs/fayda-mock-construct.ts`. Runbook:
`specs/features/fayda-mock-server.md`. Never deploy it to prod.

## Run locally

```bash
cd tools/fayda-mock
node server-local.js          # http://localhost:8788 (PORT to change)
npm test                      # node:test unit tests
npm run build                 # syntax check only, no compile step
```

In AWS the signing key lives in an SSM SecureString parameter that the Lambda creates on
first use; it is not owned by the stack, so it survives a stack deletion and can be removed
by hand when the mock is retired. Locally, codes live in memory and the signing key is regenerated on every
start, so tokens from a previous run no longer verify.

Quick check:

```bash
curl -s localhost:8788/v1/esignet/oidc/.well-known/openid-configuration
```

## Endpoints

| Method | Path | Notes |
|--------|------|-------|
| GET | `/v1/esignet/oidc/.well-known/openid-configuration` | Discovery (same fields as live Fayda; `jwks_uri` points at `/.well-known/jwks.json`, which answers 404 like on Fayda) |
| GET | `/v1/esignet/oauth/.well-known/jwks.json` | Public signing key |
| GET | `/authorize` | Test-user picker + OTP page. `auto=1&fin=<FIN>` (or `fin=new`) skips it, `simulate=denied` or `simulate=down` |
| POST | `/authorize/complete` | Form target of the picker, redirects with `code` + `state` |
| POST | `/v1/esignet/oauth/v2/token` | `authorization_code` only, PKCE required, `private_key_jwt` (aud = this URL, single-use jti) |
| GET | `/v1/esignet/oidc/userinfo` | `Authorization: Bearer <access_token>`. Signed JWT (`application/jwt`) with `sub` + requested claims |
| GET | `/health` | 200 |

The OTP is always `123456`. Codes are single use and expire after 5 minutes.
Any exchange attempt burns the code, even a failed one.

## Environment variables

| Name | Default | Purpose |
|------|---------|---------|
| `CLIENT_ID` | `weredaconnect-dev` | The only accepted client |
| `ALLOWED_REDIRECT_URIS` | `weredaconnect://fayda-callback` | Comma list, exact match |
| `REQUIRE_PKCE` | `true` | Set `false` to allow requests without `code_challenge` |
| `ISSUER_URL_OVERRIDE` | request origin | Fixes `iss` and the discovery URLs |
| `CLIENT_JWKS_JSON` | unset | Relying party public JWKS for `private_key_jwt` (local runs, tests) |
| `CLIENT_KEY_PARAM` | unset | SSM SecureString with the RP private key, the parameter the server signs with (`/{prefix}/fayda/client-private-key`). Missing or `PLACEHOLDER` means the mock writes a new base64 JWK there on its first discovery or authorize call; a usable key is kept. Its public half verifies client assertions |
| `CLIENT_KEY_FILE` | unset | Local stand-in for `CLIENT_KEY_PARAM`: a file with the same get-or-create behaviour (`server-local.js`, the server's full-flow test). With neither this, `CLIENT_KEY_PARAM` nor `CLIENT_JWKS_JSON`, every token request is rejected |
| `CODES_TABLE` | unset | DynamoDB table; unset means in-memory |
| `SIGNING_KEY_PARAM` | unset | SSM SecureString holding the PEM key; created on first cold start if missing. Unset means an in-memory key |

## Test users

| FIN | Name | phone_number |
|-----|------|--------------|
| 1111111111111111 | Abebe Kebede (farmer) | +251900100001 |
| 2222222222222222 | Chaltu Tolosa (trucker) | +251900100002 |
| 3333333333333333 | Sara Haile (buyer) | +251900100003 |
| 4444444444444444 | Linked User | +251900100004 |
| 5555555555555555 | No Phone | none |

`sub` is a stable hash of the FIN, not the FIN itself.

### Any other FIN (fresh sign-ups)

The server links each Fayda identity to one account (`FAYDA_004`), so the five
seeded FINs run out fast. The mock accepts **any** 16-digit FIN and derives a
synthetic identity from it: `sub` from the FIN hash as above, and a name
picked by a hash of the FIN, so the same FIN always returns the same person. Synthetic identities carry **no**
`phone_number` claim, so the server never treats the phone as Fayda-proven and
always sends the SMS OTP.

On the picker page choose **Generate new identity** (a random FIN) or **Type a
FIN**. For automation use `auto=1&fin=new` or `auto=1&fin=<any 16 digits>`.
Anything that is not exactly 16 digits is rejected.

### Identity for my phone number (accounts that will pay)

The server only links the Fayda identity of the account's owner: the Fayda ID
already on the account, else one whose `phone_number` equals the account phone
(`FAYDA_005`/`006`/`007` otherwise, see `specs/contracts/auth-api.md` 2.3.0).
A generated or typed identity has no phone and its FIN is never shown, so it can
neither link an account at payment time nor be used to verify again later.

**Identity for my phone number** acts like a real Fayda record: type the phone
(`+2519XXXXXXXX`, `2519...`, `09...` or `07...`) and the mock signs in as that
phone's owner. A seeded phone returns its seeded person; any other phone gets a
synthetic person whose FIN (`9` + 15 digits) is derived from the phone. The same
phone always returns the same person, with the `phone_number` claim set (in userinfo). Use it
for any account you will pay with: sign up with it, then pick it again with the
same phone whenever Fayda is asked for. Automation: `auto=1&phone=<number>`; a
malformed number redirects with `error=invalid_request`.
