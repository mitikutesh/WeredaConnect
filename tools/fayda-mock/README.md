# Fayda mock (eSignet OIDC provider)

A mock VeriFayda eSignet OpenID Connect provider for dev. The WeredaConnect
server talks to it exactly like real Fayda: discovery, authorization code with
PKCE S256, `client_secret_post` (or `private_key_jwt`), RS256 id_token checked
against the JWKS. Zero npm dependencies. In Lambda it uses the AWS SDK v3 that
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
curl -s localhost:8788/.well-known/openid-configuration
```

## Endpoints

| Method | Path | Notes |
|--------|------|-------|
| GET | `/.well-known/openid-configuration` | Discovery |
| GET | `/.well-known/jwks.json` | Public signing key |
| GET | `/authorize` | Test-user picker + OTP page. `auto=1&fin=<FIN>` (or `fin=new`) skips it, `simulate=denied` or `simulate=down` |
| POST | `/authorize/complete` | Form target of the picker, redirects with `code` + `state` |
| POST | `/token` | `authorization_code` only, PKCE required |
| GET | `/userinfo` | `Authorization: Bearer <access_token>` |
| GET | `/health` | 200 |

The OTP is always `123456`. Codes are single use and expire after 5 minutes.
Any exchange attempt burns the code, even a failed one.

## Environment variables

| Name | Default | Purpose |
|------|---------|---------|
| `CLIENT_ID` | `weredaconnect-dev` | The only accepted client |
| `CLIENT_SECRET` | `dev-fayda-mock-secret` | For `client_secret_post` |
| `ALLOWED_REDIRECT_URIS` | `weredaconnect://fayda-callback` | Comma list, exact match |
| `REQUIRE_PKCE` | `true` | Set `false` to allow requests without `code_challenge` |
| `ISSUER_URL_OVERRIDE` | request origin | Fixes `iss` and the discovery URLs |
| `CLIENT_JWKS_JSON` | unset | JWKS for `private_key_jwt`; unset means it is rejected |
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
synthetic identity from it: `sub` from the FIN hash as above, and a name,
`given_name` and `family_name` picked by a hash of the FIN, so the same FIN
always returns the same person. Synthetic identities carry **no**
`phone_number` claim, so the server never treats the phone as Fayda-proven and
always sends the SMS OTP.

On the picker page choose **Generate new identity** (a random FIN) or **Type a
FIN**. For automation use `auto=1&fin=new` or `auto=1&fin=<any 16 digits>`.
Anything that is not exactly 16 digits is rejected.
