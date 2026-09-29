'use strict';

const crypto = require('node:crypto');
const { TEST_USERS, findUser, generateFin, userForPhone } = require('./users');
const { randomToken, verifyPkce, subjectForFin, signJwt, verifyJwt } = require('./crypto');

const CODE_TTL_SECONDS = 300;
const TOKEN_TTL_SECONDS = 300;
const TEST_OTP = '123456';
const CLIENT_ASSERTION_TYPE = 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer';
// The Function URL is public: a failed assertion may re-read the client key at
// most this often, so junk requests cannot turn into a stream of SSM reads.
const CLIENT_KEY_REFRESH_SECONDS = 10;

// Everything below mirrors the live VeriFayda discovery document
// (https://esignet.ida.fayda.et/v1/esignet/oidc/.well-known/openid-configuration),
// so the server is built against the real shape, not a friendlier one.
const ACR_VALUES = [
  'mosip:idp:acr:generated-code',
  'mosip:idp:acr:static-code',
  'mosip:idp:acr:linked-wallet',
  'mosip:idp:acr:biometrics',
];
const CLAIMS_SUPPORTED = ['name', 'address', 'gender', 'birthdate', 'picture', 'email',
  'phone_number', 'nationality', 'individual_id'];

// eSignet paths. The issuer is the host root, but discovery, token, userinfo and
// the JWKS live under /v1/esignet. Real Fayda advertises jwks_uri as
// /.well-known/jwks.json and answers 404 there; the mock does the same, so the
// server has to fall back to JWKS_PATH exactly as it must against production.
const PATHS = {
  discovery: '/v1/esignet/oidc/.well-known/openid-configuration',
  authorize: '/authorize',
  token: '/v1/esignet/oauth/v2/token',
  userinfo: '/v1/esignet/oidc/userinfo',
  jwks: '/v1/esignet/oauth/.well-known/jwks.json',
  advertisedJwks: '/.well-known/jwks.json',
};

/** Reads config from env. Exported so tests can build one without env. */
function configFromEnv(env = process.env) {
  return {
    clientId: env.CLIENT_ID || 'weredaconnect-dev',
    allowedRedirectUris: (env.ALLOWED_REDIRECT_URIS || 'weredaconnect://fayda-callback')
      .split(',').map((s) => s.trim()).filter(Boolean),
    requirePkce: (env.REQUIRE_PKCE || 'true').toLowerCase() !== 'false',
    issuerOverride: (env.ISSUER_URL_OVERRIDE || '').replace(/\/+$/, ''),
    clientJwks: env.CLIENT_JWKS_JSON ? JSON.parse(env.CLIENT_JWKS_JSON) : null,
  };
}

/**
 * The userinfo claims an authorize request asks for: the keys of claims.userinfo
 * that Fayda supports. eSignet releases user data only through this parameter.
 * Returns null when the parameter is not valid JSON.
 */
function requestedClaims(claimsParam) {
  if (!claimsParam) return [];
  let parsed;
  try {
    parsed = JSON.parse(claimsParam);
  } catch {
    return null;
  }
  const userinfo = parsed && typeof parsed.userinfo === 'object' && parsed.userinfo ? parsed.userinfo : {};
  return Object.keys(userinfo).filter((k) => CLAIMS_SUPPORTED.includes(k));
}

/** OIDC Core 3.1.3.6: left half of SHA-256 of the access token, base64url. */
const atHash = (accessToken) =>
  crypto.createHash('sha256').update(accessToken, 'ascii').digest().subarray(0, 16).toString('base64url');

// ---------- response helpers ----------

const NO_STORE = { 'cache-control': 'no-store', pragma: 'no-cache' };

const json = (statusCode, body, extra = {}) => ({
  statusCode,
  headers: { 'content-type': 'application/json', ...extra },
  body: JSON.stringify(body),
});

const oauthError = (statusCode, error, description) =>
  json(statusCode, { error, error_description: description }, NO_STORE);

const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => (
  { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

const page = (statusCode, title, inner) => ({
  statusCode,
  headers: { 'content-type': 'text/html; charset=utf-8', ...NO_STORE },
  body: `<!doctype html><html><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<title>${esc(title)}</title>
<style>
body{font-family:system-ui,sans-serif;margin:0;padding:16px;max-width:480px;background:#f6f7f6;color:#1c2b2a}
h1{font-size:1.2rem}.banner{background:#fff3cd;padding:8px;border-radius:6px;font-size:.85rem}
label.user{display:block;background:#fff;border:1px solid #ccd;border-radius:8px;padding:12px;margin:8px 0}
small{color:#566}input[type=text]{font-size:1.1rem;padding:8px;width:10ch;letter-spacing:.2em}
input.fin{width:100%;box-sizing:border-box;margin-top:6px;letter-spacing:.1em}
button{margin-top:16px;width:100%;padding:12px;font-size:1rem;background:#0f766e;color:#fff;border:0;border-radius:8px}
</style></head><body>${inner}</body></html>`,
});

/**
 * The picker's fin choice: a seeded FIN, "custom" (typed FIN in customFin) or
 * "new" (a random FIN, so a fresh identity that no account holds yet).
 */
function resolveFin(choice, customFin) {
  if (choice === 'new') return generateFin();
  if (choice === 'custom') return String(customFin || '').replace(/\s+/g, '');
  return choice || '';
}

/** The identity the picker (or auto mode) chose; "phone" picks the phone's owner. */
function resolveUser(choice, customFin, phone) {
  if (choice === 'phone') return userForPhone(phone);
  return findUser(resolveFin(choice, customFin));
}

/** The user a code or access token was issued for (a phone identity keeps its phone). */
const issuedUser = (item) => {
  const user = findUser(item.fin);
  return item.phone ? { ...user, phone: item.phone } : user;
};

const errorPage = (statusCode, message) =>
  page(statusCode, 'Fayda mock error', `<h1>Fayda mock: request rejected</h1><p>${esc(message)}</p>`);

const redirect = (location) => ({ statusCode: 302, headers: { location, ...NO_STORE }, body: '' });

function redirectWith(redirectUri, params) {
  const url = new URL(redirectUri);
  for (const [k, v] of Object.entries(params)) if (v !== undefined && v !== null) url.searchParams.set(k, v);
  return redirect(url.toString());
}

// ---------- app ----------

/**
 * createApp({ config, store, getKey, getClientJwks, now }) returns handle(req).
 * getClientJwks resolves the relying party's public JWKS for private_key_jwt
 * (null when none is configured); it defaults to config.clientJwks.
 * req: { method, path, query: URLSearchParams, headers (lowercased), body: string, origin }
 * origin is the scheme+host the request arrived on; used as issuer unless
 * config.issuerOverride is set.
 */
function createApp({
  config, store, getKey, getClientJwks = async () => config.clientJwks, now = () => Math.floor(Date.now() / 1000),
}) {
  const issuerFor = (req) => config.issuerOverride || req.origin;

  // The RP public JWKS, cached. Re-read when an assertion does not verify (the key
  // was replaced), rate limited to one read per CLIENT_KEY_REFRESH_SECONDS.
  let clientJwks = null;
  let clientJwksReadAt = -Infinity;
  async function clientJwksFor({ refresh = false } = {}) {
    const stale = refresh && now() - clientJwksReadAt >= CLIENT_KEY_REFRESH_SECONDS;
    if (clientJwks && !stale) return clientJwks;
    clientJwksReadAt = now();
    clientJwks = await getClientJwks();
    return clientJwks;
  }

  /**
   * Stands in for client registration: makes sure the RP key exists before any
   * login can reach the token endpoint. On a fresh dev deploy this creates it (or
   * replaces the seeded PLACEHOLDER), so the server finds a usable key when it
   * signs its first client assertion. Cached after the first success. Failures
   * are logged, not shown: the token endpoint reports them.
   */
  const ensureClientKey = () => clientJwksFor().catch((err) => {
    console.error('fayda-mock: client key unavailable', err);
  });

  async function discovery(req) {
    await ensureClientKey();
    const iss = issuerFor(req);
    return json(200, {
      issuer: iss,
      authorization_endpoint: `${iss}${PATHS.authorize}`,
      token_endpoint: `${iss}${PATHS.token}`,
      userinfo_endpoint: `${iss}${PATHS.userinfo}`,
      jwks_uri: `${iss}${PATHS.advertisedJwks}`,
      scopes_supported: ['profile', 'email', 'phone'],
      response_types_supported: ['code'],
      response_modes_supported: ['query'],
      token_endpoint_auth_methods_supported: ['private_key_jwt'],
      token_endpoint_auth_signing_alg_values_supported: ['RS256'],
      userinfo_signing_alg_values_supported: ['RS256'],
      id_token_signing_alg_values_supported: ['RS256'],
      claim_types_supported: ['normal'],
      claims_parameter_supported: true,
      display_values_supported: ['page', 'popup', 'touch', 'wap'],
      subject_types_supported: ['pairwise'],
      claims_supported: CLAIMS_SUPPORTED,
      acr_values_supported: ACR_VALUES,
      request_parameter_supported: false,
      claims_locales_supported: ['en'],
      ui_locales_supported: ['en'],
    });
  }

  async function jwks() {
    const key = await getKey();
    return json(200, { keys: [key.jwk] });
  }

  /**
   * Validates authorize params. Returns { fatal } when the client or
   * redirect_uri is bad (never redirect then), { redirectError } when the
   * redirect_uri is trusted but something else is wrong, else { ok: params }.
   */
  function validateAuthorize(p) {
    if (p.client_id !== config.clientId) return { fatal: 'Unknown client_id.' };
    if (!p.redirect_uri || !config.allowedRedirectUris.includes(p.redirect_uri)) {
      return { fatal: 'redirect_uri is not registered for this client.' };
    }
    const bad = (d) => ({ redirectError: d });
    if (p.response_type !== 'code') return bad('response_type must be code');
    if (!String(p.scope || '').split(' ').includes('openid')) return bad('scope must include openid');
    if (!p.state) return bad('state is required');
    if (!p.nonce) return bad('nonce is required');
    if (config.requirePkce || p.code_challenge) {
      if (!p.code_challenge) return bad('code_challenge is required');
      if (p.code_challenge_method !== 'S256') return bad('code_challenge_method must be S256');
    }
    if (requestedClaims(p.claims) === null) return bad('claims must be a JSON object');
    return { ok: p };
  }

  const pickAcr = (acrValues) =>
    String(acrValues || '').split(' ').find((a) => ACR_VALUES.includes(a)) || ACR_VALUES[0];

  async function issueCode(p, user) {
    const code = randomToken();
    await store.putNew({
      code,
      kind: 'code',
      clientId: p.client_id,
      redirectUri: p.redirect_uri,
      codeChallenge: p.code_challenge || null,
      nonce: p.nonce,
      fin: user.fin,
      phone: user.phone || null,
      acr: pickAcr(p.acr_values),
      claims: requestedClaims(p.claims),
      expiresAt: now() + CODE_TTL_SECONDS,
    });
    return redirectWith(p.redirect_uri, { code, state: p.state });
  }

  const AUTH_FIELDS = ['client_id', 'redirect_uri', 'response_type', 'scope', 'state', 'nonce',
    'code_challenge', 'code_challenge_method', 'acr_values', 'claims'];
  const pickFields = (params) => Object.fromEntries(AUTH_FIELDS.map((k) => [k, params.get(k) || '']));

  async function authorize(req) {
    await ensureClientKey();
    const q = req.query;
    if (q.get('simulate') === 'down') {
      return page(503, 'Unavailable', '<h1>Fayda is temporarily unavailable</h1><p>Simulated outage.</p>');
    }
    const p = pickFields(q);
    const v = validateAuthorize(p);
    if (v.fatal) return errorPage(400, v.fatal);
    if (v.redirectError) {
      return redirectWith(p.redirect_uri, { error: 'invalid_request', error_description: v.redirectError, state: p.state || undefined });
    }
    if (q.get('simulate') === 'denied') {
      return redirectWith(p.redirect_uri, { error: 'access_denied', error_description: 'User denied consent', state: p.state });
    }
    if (q.get('auto') === '1') {
      // auto=1&phone=<number> signs in as that phone's owner (same phone, same person).
      const user = q.get('phone') !== null
        ? userForPhone(q.get('phone'))
        : findUser(resolveFin(q.get('fin'), q.get('custom_fin')));
      if (!user) {
        return redirectWith(p.redirect_uri, {
          error: 'invalid_request',
          error_description: q.get('phone') !== null ? 'phone must be an Ethiopian mobile number' : 'fin must be 16 digits',
          state: p.state,
        });
      }
      return issueCode(p, user);
    }

    const hidden = AUTH_FIELDS.map((k) => `<input type="hidden" name="${k}" value="${esc(p[k])}">`).join('');
    const users = TEST_USERS.map((u, i) => `<label class="user"><input type="radio" name="fin" value="${u.fin}"${i === 0 ? ' checked' : ''}>
 <b>${esc(u.name)}</b><br><small>FIN ${u.fin} &middot; ${esc(u.phone || 'no phone')} &middot; ${esc(u.hint)}</small></label>`).join('');
    const fresh = `<label class="user"><input type="radio" name="fin" value="phone" id="fin-phone">
 <b>Identity for my phone number</b><br><small>The Fayda record of this phone: same phone, same person every time.
 Use it for an account you will pay with (Fayda must match the account's phone).</small><br>
 <input type="tel" class="fin" name="phone" inputmode="tel" placeholder="+2519XXXXXXXX or 09XXXXXXXX"
 onfocus="document.getElementById('fin-phone').checked=true"></label>
<label class="user"><input type="radio" name="fin" value="new">
 <b>Generate new identity</b><br><small>Random FIN, synthetic name, no phone (server sends the SMS OTP).
 Its FIN is not shown, so it cannot sign in again later: verifying at payment or from the profile fails.</small></label>
<label class="user"><input type="radio" name="fin" value="custom" id="fin-custom">
 <b>Type a FIN</b><br><small>Any 16 digits. Same FIN, same identity. No phone claim.</small><br>
 <input type="text" class="fin" name="custom_fin" inputmode="numeric" maxlength="16" pattern="\\d{16}" placeholder="16 digits"
 onfocus="document.getElementById('fin-custom').checked=true"></label>`;
    return page(200, 'Fayda mock sign-in', `<div class="banner">MOCK Fayda eSignet. Test identities only.</div>
<h1>Choose a test identity</h1>
<form method="post" action="authorize/complete">${hidden}${users}${fresh}
<label>OTP (use ${TEST_OTP})<br><input type="text" name="otp" inputmode="numeric" autocomplete="one-time-code" maxlength="6" required></label>
<button type="submit">Continue</button></form>`);
  }

  async function authorizeComplete(req) {
    const form = new URLSearchParams(req.body || '');
    const p = pickFields(form);
    const v = validateAuthorize(p);
    if (v.fatal) return errorPage(400, v.fatal);
    if (v.redirectError) {
      return redirectWith(p.redirect_uri, { error: 'invalid_request', error_description: v.redirectError, state: p.state || undefined });
    }
    const user = resolveUser(form.get('fin'), form.get('custom_fin'), form.get('phone'));
    if (!user) {
      return errorPage(400, form.get('fin') === 'phone'
        ? 'Type an Ethiopian mobile number, like +251912345678 or 0912345678.'
        : 'Pick a test identity, or type a FIN of exactly 16 digits.');
    }
    if (form.get('otp') !== TEST_OTP) return errorPage(400, `Wrong OTP. The mock only accepts ${TEST_OTP}.`);
    return issueCode(p, user);
  }

  /**
   * Client authentication for the token endpoint: private_key_jwt only, as on
   * real Fayda. Returns null on success or an error response.
   */
  async function authenticateClient(form, req) {
    if (form.get('client_id') !== config.clientId) return oauthError(401, 'invalid_client', 'unknown client_id');
    if (form.get('client_assertion_type') !== CLIENT_ASSERTION_TYPE) {
      return oauthError(401, 'invalid_client', `client_assertion_type must be ${CLIENT_ASSERTION_TYPE}`);
    }
    let claims;
    try {
      let jwks = await clientJwksFor();
      if (!jwks) return oauthError(401, 'invalid_client', 'no client public key is configured for private_key_jwt');
      try {
        claims = verifyJwt(form.get('client_assertion'), jwks);
      } catch {
        // The stored key may have been replaced since it was cached: read it again once.
        jwks = await clientJwksFor({ refresh: true });
        claims = verifyJwt(form.get('client_assertion'), jwks || { keys: [] });
      }
    } catch (err) {
      return oauthError(401, 'invalid_client', `client_assertion rejected: ${err.message}`);
    }
    const aud = [].concat(claims.aud || []);
    if (claims.iss !== config.clientId || claims.sub !== config.clientId) return oauthError(401, 'invalid_client', 'client_assertion iss/sub must be the client_id');
    if (!aud.includes(`${issuerFor(req)}${PATHS.token}`)) return oauthError(401, 'invalid_client', 'client_assertion aud must be the token endpoint');
    if (typeof claims.exp !== 'number' || claims.exp <= now()) return oauthError(401, 'invalid_client', 'client_assertion expired');
    if (!claims.jti) return oauthError(401, 'invalid_client', 'client_assertion jti required');
    const fresh = await store.putNew({ code: `jti:${claims.jti}`, kind: 'jti', expiresAt: claims.exp });
    if (!fresh) return oauthError(401, 'invalid_client', 'client_assertion replayed');
    return null;
  }

  /** The claims a user record can release, keyed by the OIDC claim name. */
  function userClaims(user) {
    const all = { name: user.name, individual_id: user.fin, nationality: 'Ethiopian' };
    if (user.phone) all.phone_number = user.phone;
    return all;
  }

  async function token(req) {
    const form = new URLSearchParams(req.body || '');
    if (form.get('grant_type') !== 'authorization_code') {
      return oauthError(400, 'unsupported_grant_type', 'only authorization_code is supported');
    }
    const authErr = await authenticateClient(form, req);
    if (authErr) return authErr;

    const codeValue = form.get('code');
    if (!codeValue) return oauthError(400, 'invalid_request', 'code is required');
    // Consume first: any attempt, successful or not, burns the code (RFC 6749 4.1.2).
    const code = await store.consume(codeValue, now());
    if (!code || code.kind !== 'code') return oauthError(400, 'invalid_grant', 'code is invalid, expired or already used');
    if (code.clientId !== config.clientId) return oauthError(400, 'invalid_grant', 'code was issued to another client');
    if (form.get('redirect_uri') !== code.redirectUri) return oauthError(400, 'invalid_grant', 'redirect_uri does not match');
    if (code.codeChallenge || config.requirePkce) {
      if (!verifyPkce(form.get('code_verifier'), code.codeChallenge)) {
        return oauthError(400, 'invalid_grant', 'PKCE verification failed');
      }
    }

    const user = issuedUser(code);
    const iat = now();
    const accessToken = randomToken();
    // Like eSignet, the id_token proves who signed in and nothing more: user
    // data (name, phone) is released only by the userinfo endpoint.
    const idToken = signJwt({
      iss: issuerFor(req),
      sub: subjectForFin(user.fin),
      aud: config.clientId,
      iat,
      exp: iat + TOKEN_TTL_SECONDS,
      auth_time: iat,
      nonce: code.nonce,
      acr: code.acr,
      at_hash: atHash(accessToken),
    }, await getKey());

    await store.putNew({
      code: `at:${accessToken}`,
      kind: 'access_token',
      fin: user.fin,
      phone: code.phone || null,
      claims: code.claims || [],
      expiresAt: iat + TOKEN_TTL_SECONDS,
    });

    return json(200, {
      access_token: accessToken,
      id_token: idToken,
      token_type: 'Bearer',
      expires_in: TOKEN_TTL_SECONDS,
    }, NO_STORE);
  }

  /**
   * Signed userinfo (application/jwt), as eSignet returns it. Carries sub plus
   * only the claims the authorize request asked for in its claims parameter.
   */
  async function userinfo(req) {
    const m = /^Bearer\s+(\S+)$/i.exec(req.headers.authorization || '');
    const item = m ? await store.get(`at:${m[1]}`, now()) : null;
    if (!item || item.kind !== 'access_token') {
      return { ...oauthError(401, 'invalid_token', 'missing or expired access token'), headers: { 'content-type': 'application/json', 'www-authenticate': 'Bearer error="invalid_token"' } };
    }
    const user = issuedUser(item);
    const available = userClaims(user);
    const payload = { iss: issuerFor(req), aud: config.clientId, sub: subjectForFin(user.fin) };
    for (const k of item.claims || []) if (available[k] !== undefined) payload[k] = available[k];
    return {
      statusCode: 200,
      headers: { 'content-type': 'application/jwt', ...NO_STORE },
      body: signJwt(payload, await getKey()),
    };
  }

  return async function handle(req) {
    const route = `${req.method} ${req.path.replace(/\/+$/, '') || '/'}`;
    try {
      switch (route) {
        case `GET ${PATHS.discovery}`: return await discovery(req);
        case `GET ${PATHS.jwks}`: return await jwks();
        case `GET ${PATHS.authorize}`: return await authorize(req);
        case 'POST /authorize/complete': return await authorizeComplete(req);
        case `POST ${PATHS.token}`: return await token(req);
        case `GET ${PATHS.userinfo}`: return await userinfo(req);
        case 'GET /health': return json(200, { status: 'ok' });
        default: return json(404, { error: 'not_found' });
      }
    } catch (err) {
      console.error('fayda-mock error', route, err);
      return oauthError(500, 'server_error', 'internal error');
    }
  };
}

module.exports = { createApp, configFromEnv, TEST_OTP, ACR_VALUES, CLAIMS_SUPPORTED, PATHS };
