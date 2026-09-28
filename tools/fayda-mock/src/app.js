'use strict';

const { TEST_USERS, findUser, generateFin } = require('./users');
const { randomToken, verifyPkce, subjectForFin, signJwt, verifyJwt } = require('./crypto');

const CODE_TTL_SECONDS = 300;
const TOKEN_TTL_SECONDS = 300;
const TEST_OTP = '123456';
const ACR_VALUES = ['mosip:idp:acr:generated-code', 'mosip:idp:acr:biometrics'];
const CLIENT_ASSERTION_TYPE = 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer';

/** Reads config from env. Exported so tests can build one without env. */
function configFromEnv(env = process.env) {
  return {
    clientId: env.CLIENT_ID || 'weredaconnect-dev',
    clientSecret: env.CLIENT_SECRET || 'dev-fayda-mock-secret',
    allowedRedirectUris: (env.ALLOWED_REDIRECT_URIS || 'weredaconnect://fayda-callback')
      .split(',').map((s) => s.trim()).filter(Boolean),
    requirePkce: (env.REQUIRE_PKCE || 'true').toLowerCase() !== 'false',
    issuerOverride: (env.ISSUER_URL_OVERRIDE || '').replace(/\/+$/, ''),
    clientJwks: env.CLIENT_JWKS_JSON ? JSON.parse(env.CLIENT_JWKS_JSON) : null,
  };
}

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
 * createApp({ config, store, getKey, now }) returns handle(req).
 * req: { method, path, query: URLSearchParams, headers (lowercased), body: string, origin }
 * origin is the scheme+host the request arrived on; used as issuer unless
 * config.issuerOverride is set.
 */
function createApp({ config, store, getKey, now = () => Math.floor(Date.now() / 1000) }) {
  const issuerFor = (req) => config.issuerOverride || req.origin;

  function discovery(req) {
    const iss = issuerFor(req);
    return json(200, {
      issuer: iss,
      authorization_endpoint: `${iss}/authorize`,
      token_endpoint: `${iss}/token`,
      jwks_uri: `${iss}/.well-known/jwks.json`,
      userinfo_endpoint: `${iss}/userinfo`,
      response_types_supported: ['code'],
      grant_types_supported: ['authorization_code'],
      subject_types_supported: ['public'],
      scopes_supported: ['openid', 'profile', 'phone'],
      id_token_signing_alg_values_supported: ['RS256'],
      token_endpoint_auth_methods_supported: ['client_secret_post', 'private_key_jwt'],
      token_endpoint_auth_signing_alg_values_supported: ['RS256'],
      code_challenge_methods_supported: ['S256'],
      acr_values_supported: ACR_VALUES,
      claims_supported: ['sub', 'name', 'given_name', 'family_name', 'phone_number', 'acr', 'nonce'],
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
    return { ok: p };
  }

  const pickAcr = (acrValues) =>
    String(acrValues || '').split(' ').find((a) => ACR_VALUES.includes(a)) || ACR_VALUES[0];

  async function issueCode(p, fin) {
    const code = randomToken();
    await store.putNew({
      code,
      kind: 'code',
      clientId: p.client_id,
      redirectUri: p.redirect_uri,
      codeChallenge: p.code_challenge || null,
      nonce: p.nonce,
      fin,
      acr: pickAcr(p.acr_values),
      expiresAt: now() + CODE_TTL_SECONDS,
    });
    return redirectWith(p.redirect_uri, { code, state: p.state });
  }

  const AUTH_FIELDS = ['client_id', 'redirect_uri', 'response_type', 'scope', 'state', 'nonce',
    'code_challenge', 'code_challenge_method', 'acr_values'];
  const pickFields = (params) => Object.fromEntries(AUTH_FIELDS.map((k) => [k, params.get(k) || '']));

  function authorize(req) {
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
      const user = findUser(resolveFin(q.get('fin'), q.get('custom_fin')));
      if (!user) return redirectWith(p.redirect_uri, { error: 'invalid_request', error_description: 'fin must be 16 digits', state: p.state });
      return issueCode(p, user.fin);
    }

    const hidden = AUTH_FIELDS.map((k) => `<input type="hidden" name="${k}" value="${esc(p[k])}">`).join('');
    const users = TEST_USERS.map((u, i) => `<label class="user"><input type="radio" name="fin" value="${u.fin}"${i === 0 ? ' checked' : ''}>
 <b>${esc(u.name)}</b><br><small>FIN ${u.fin} &middot; ${esc(u.phone || 'no phone')} &middot; ${esc(u.hint)}</small></label>`).join('');
    const fresh = `<label class="user"><input type="radio" name="fin" value="new">
 <b>Generate new identity</b><br><small>Random FIN, synthetic name, no phone (server sends the SMS OTP)</small></label>
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
    const user = findUser(resolveFin(form.get('fin'), form.get('custom_fin')));
    if (!user) return errorPage(400, 'Pick a test identity, or type a FIN of exactly 16 digits.');
    if (form.get('otp') !== TEST_OTP) return errorPage(400, `Wrong OTP. The mock only accepts ${TEST_OTP}.`);
    return issueCode(p, user.fin);
  }

  /** Client authentication for /token. Returns null on success or an error response. */
  async function authenticateClient(form, req) {
    const clientId = form.get('client_id');
    const assertionType = form.get('client_assertion_type');
    if (assertionType) {
      if (assertionType !== CLIENT_ASSERTION_TYPE) return oauthError(401, 'invalid_client', 'unsupported client_assertion_type');
      if (!config.clientJwks) return oauthError(401, 'invalid_client', 'private_key_jwt is not configured (CLIENT_JWKS_JSON unset)');
      let claims;
      try {
        claims = verifyJwt(form.get('client_assertion'), config.clientJwks);
      } catch (err) {
        return oauthError(401, 'invalid_client', `client_assertion rejected: ${err.message}`);
      }
      const iss = issuerFor(req);
      const aud = [].concat(claims.aud || []);
      if (claims.iss !== config.clientId || claims.sub !== config.clientId) return oauthError(401, 'invalid_client', 'client_assertion iss/sub must be the client_id');
      if (clientId && clientId !== config.clientId) return oauthError(401, 'invalid_client', 'client_id mismatch');
      if (!aud.includes(`${iss}/token`) && !aud.includes(iss)) return oauthError(401, 'invalid_client', 'client_assertion aud must be the token endpoint');
      if (typeof claims.exp !== 'number' || claims.exp <= now()) return oauthError(401, 'invalid_client', 'client_assertion expired');
      if (!claims.jti) return oauthError(401, 'invalid_client', 'client_assertion jti required');
      const fresh = await store.putNew({ code: `jti:${claims.jti}`, kind: 'jti', expiresAt: claims.exp });
      if (!fresh) return oauthError(401, 'invalid_client', 'client_assertion replayed');
      return null;
    }
    if (clientId !== config.clientId || form.get('client_secret') !== config.clientSecret) {
      return oauthError(401, 'invalid_client', 'bad client_id or client_secret');
    }
    return null;
  }

  function claimsFor(user) {
    const claims = {
      sub: subjectForFin(user.fin),
      name: user.name,
      given_name: user.givenName,
      family_name: user.familyName,
    };
    if (user.phone) claims.phone_number = user.phone;
    return claims;
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

    const user = findUser(code.fin);
    const iat = now();
    const claims = claimsFor(user);
    const idToken = signJwt({
      iss: issuerFor(req),
      aud: config.clientId,
      iat,
      exp: iat + TOKEN_TTL_SECONDS,
      nonce: code.nonce,
      acr: code.acr,
      ...claims,
    }, await getKey());

    const accessToken = randomToken();
    await store.putNew({ code: `at:${accessToken}`, kind: 'access_token', fin: user.fin, expiresAt: iat + TOKEN_TTL_SECONDS });

    return json(200, {
      access_token: accessToken,
      id_token: idToken,
      token_type: 'Bearer',
      expires_in: TOKEN_TTL_SECONDS,
    }, NO_STORE);
  }

  async function userinfo(req) {
    const m = /^Bearer\s+(\S+)$/i.exec(req.headers.authorization || '');
    const item = m ? await store.get(`at:${m[1]}`, now()) : null;
    if (!item || item.kind !== 'access_token') {
      return { ...oauthError(401, 'invalid_token', 'missing or expired access token'), headers: { 'content-type': 'application/json', 'www-authenticate': 'Bearer error="invalid_token"' } };
    }
    return json(200, claimsFor(findUser(item.fin)), NO_STORE);
  }

  return async function handle(req) {
    const route = `${req.method} ${req.path.replace(/\/+$/, '') || '/'}`;
    try {
      switch (route) {
        case 'GET /.well-known/openid-configuration': return discovery(req);
        case 'GET /.well-known/jwks.json': return await jwks();
        case 'GET /authorize': return authorize(req);
        case 'POST /authorize/complete': return await authorizeComplete(req);
        case 'POST /token': return await token(req);
        case 'GET /userinfo': return await userinfo(req);
        case 'GET /health': return json(200, { status: 'ok' });
        default: return json(404, { error: 'not_found' });
      }
    } catch (err) {
      console.error('fayda-mock error', route, err);
      return oauthError(500, 'server_error', 'internal error');
    }
  };
}

module.exports = { createApp, configFromEnv, TEST_OTP, ACR_VALUES };
