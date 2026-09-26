'use strict';

const { createApp, configFromEnv } = require('../src/app');
const { memoryStore } = require('../src/store');
const { memoryKeyProvider } = require('../src/keys');
const { randomToken, pkceChallenge } = require('../src/crypto');

const ISSUER = 'https://mock.example';
const REDIRECT = 'weredaconnect://fayda-callback';

function makeApp(envOverrides = {}, clock = { t: 1_800_000_000 }) {
  const config = configFromEnv({ CLIENT_ID: 'weredaconnect-test', CLIENT_SECRET: 's3cret', ...envOverrides });
  const getKey = memoryKeyProvider();
  const handle = createApp({ config, store: memoryStore(), getKey, now: () => clock.t });
  const call = (method, pathAndQuery, { body = '', headers = {} } = {}) => {
    const url = new URL(pathAndQuery, ISSUER);
    return handle({ method, path: url.pathname, query: url.searchParams, headers, body, origin: ISSUER });
  };
  return { call, getKey, clock, config };
}

function authorizeQuery(overrides = {}) {
  const verifier = overrides.verifier || randomToken(48);
  const params = {
    client_id: 'weredaconnect-test',
    redirect_uri: REDIRECT,
    response_type: 'code',
    scope: 'openid profile phone',
    state: 'st-1',
    nonce: 'n-1',
    code_challenge: pkceChallenge(verifier),
    code_challenge_method: 'S256',
    acr_values: 'mosip:idp:acr:generated-code',
    ...overrides,
  };
  delete params.verifier;
  for (const k of Object.keys(params)) if (params[k] === undefined) delete params[k];
  return { qs: new URLSearchParams(params).toString(), verifier, params };
}

/** Runs /authorize with auto mode and returns { code, state, verifier, params }. */
async function getCode(app, { fin = '1111111111111111', ...overrides } = {}) {
  const { qs, verifier, params } = authorizeQuery(overrides);
  const res = await app.call('GET', `/authorize?${qs}&auto=1&fin=${fin}`);
  const loc = new URL(res.headers.location);
  return { code: loc.searchParams.get('code'), state: loc.searchParams.get('state'), verifier, params, res };
}

function tokenBody(fields) {
  return new URLSearchParams({
    grant_type: 'authorization_code',
    redirect_uri: REDIRECT,
    client_id: 'weredaconnect-test',
    client_secret: 's3cret',
    ...fields,
  }).toString();
}

module.exports = { makeApp, authorizeQuery, getCode, tokenBody, ISSUER, REDIRECT };
