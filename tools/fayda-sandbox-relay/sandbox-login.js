#!/usr/bin/env node
'use strict';

// Scripted sign-in at the VeriFayda sandbox (dev tooling, no dependencies).
//
// Drives the same calls the Fayda sign-in page makes (oauth-details, send-otp,
// authenticate, auth-code) for a documented test persona and prints the resulting
// authorization code together with the PKCE verifier and nonce it was minted with,
// as shell assignments. Feed them to the server's manual live check:
//
//   eval "$(node sandbox-login.js --fin 3126894653473958)"
//   cd ../../server && dotnet test --no-restore --filter FaydaSandboxLive
//
// The code is single use and short-lived, so run the test right away. The
// authorize parameters mirror FaydaOidcService (scope, acr_values, claims).
// Runbook: specs/features/fayda-mock-server.md, "Sandbox mode".

const crypto = require('node:crypto');

const DEFAULTS = {
  base: 'https://esignet.ida.fayda.et',
  clientId: 'aQ7xN2kLm9_Vp4RzT8cY-hW3sJ6FdE0uXbKqP1',
  redirect: 'http://localhost:3000/callback',
  fin: '3126894653473958',
  otp: '111111',
  scope: 'openid profile phone',
  acr: 'mosip:idp:acr:generated-code mosip:idp:acr:biometrics',
};

function parseArgs(argv) {
  const opts = { ...DEFAULTS };
  const names = { '--base': 'base', '--client-id': 'clientId', '--redirect': 'redirect', '--fin': 'fin', '--otp': 'otp', '--scope': 'scope', '--acr': 'acr' };
  for (let i = 0; i < argv.length; i++) {
    const key = names[argv[i]];
    if (!key) throw new Error(`unknown argument ${argv[i]}`);
    opts[key] = argv[++i];
  }
  return opts;
}

async function login(opts, fetchImpl = fetch) {
  const cookies = {};
  async function call(method, path, body, extra = {}) {
    const headers = { accept: 'application/json', ...extra };
    if (body) headers['content-type'] = 'application/json';
    if (Object.keys(cookies).length) headers.cookie = Object.entries(cookies).map(([k, v]) => `${k}=${v}`).join('; ');
    const res = await fetchImpl(opts.base + path, { method, headers, body: body ? JSON.stringify(body) : undefined, redirect: 'manual' });
    for (const c of (res.headers.getSetCookie?.() || [])) {
      const kv = c.split(';')[0]; const i = kv.indexOf('=');
      cookies[kv.slice(0, i).trim()] = kv.slice(i + 1).trim();
    }
    const text = await res.text();
    let json = null; try { json = JSON.parse(text); } catch { /* not json */ }
    if (json && Array.isArray(json.errors) && json.errors.length) {
      throw new Error(`${path}: ${json.errors.map((e) => e.errorCode).join(', ')}`);
    }
    if (!json || !json.response) throw new Error(`${path}: http ${res.status}`);
    return json.response;
  }
  const now = () => new Date().toISOString();

  await call('GET', '/v1/esignet/csrf/token').catch(() => null);
  const csrf = cookies['XSRF-TOKEN'] ? { 'X-XSRF-TOKEN': cookies['XSRF-TOKEN'] } : {};

  const verifier = crypto.randomBytes(32).toString('base64url');
  const challenge = crypto.createHash('sha256').update(verifier).digest('base64url');
  const nonce = crypto.randomBytes(32).toString('base64url');
  const state = crypto.randomBytes(32).toString('base64url');

  const details = await call('POST', '/v1/esignet/authorization/v3/oauth-details', {
    requestTime: now(),
    request: {
      clientId: opts.clientId, scope: opts.scope, responseType: 'code', redirectUri: opts.redirect,
      display: 'page', prompt: 'consent', acrValues: opts.acr, nonce, state, claimsLocales: 'en',
      claims: { userinfo: { name: { essential: true }, phone_number: { essential: true } }, id_token: {} },
      codeChallenge: challenge, codeChallengeMethod: 'S256',
    },
  }, csrf);
  const txn = details.transactionId;
  const auth = {
    ...csrf,
    'oauth-details-key': txn,
    'oauth-details-hash': crypto.createHash('sha256').update(JSON.stringify(details)).digest('base64url'),
  };

  // The sandbox personas receive the code by email only ("phone" is invalid_otp_channel).
  await call('POST', '/v1/esignet/authorization/send-otp', {
    requestTime: now(), request: { transactionId: txn, individualId: opts.fin, otpChannels: ['email'], captchaToken: '' },
  }, auth);
  await call('POST', '/v1/esignet/authorization/v3/authenticate', {
    requestTime: now(),
    request: { transactionId: txn, individualId: opts.fin, challengeList: [{ authFactorType: 'OTP', challenge: opts.otp, format: 'alpha-numeric' }], captchaToken: '' },
  }, auth);
  const issued = await call('POST', '/v1/esignet/authorization/auth-code', {
    requestTime: now(),
    request: { transactionId: txn, acceptedClaims: [...(details.essentialClaims || []), ...(details.voluntaryClaims || [])], permittedAuthorizeScopes: details.authorizeScopes || [] },
  }, auth);
  if (!issued.code) throw new Error('auth-code: no code issued');
  if (issued.state !== state) throw new Error('auth-code: state mismatch');
  return { code: issued.code, verifier, nonce, redirect: issued.redirectUri || opts.redirect, clientId: opts.clientId };
}

if (require.main === module) {
  login(parseArgs(process.argv.slice(2))).then((r) => {
    process.stdout.write(
      `export FAYDA_SANDBOX_CODE='${r.code}'\n`
      + `export FAYDA_SANDBOX_CODE_VERIFIER='${r.verifier}'\n`
      + `export FAYDA_SANDBOX_NONCE='${r.nonce}'\n`
      + `export FAYDA_SANDBOX_REDIRECT_URI='${r.redirect}'\n`
      + `export FAYDA_SANDBOX_CLIENT_ID='${r.clientId}'\n`,
    );
  }).catch((e) => {
    console.error(`[sandbox-login] ${e.message}`);
    process.exit(1);
  });
}

module.exports = { login, parseArgs, DEFAULTS };
