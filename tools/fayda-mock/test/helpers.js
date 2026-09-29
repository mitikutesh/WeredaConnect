'use strict';

const crypto = require('node:crypto');
const { createApp, configFromEnv, PATHS } = require('../src/app');
const { memoryStore } = require('../src/store');
const { memoryKeyProvider } = require('../src/keys');
const { randomToken, pkceChallenge, signJwt, loadSigningKey, generateSigningKeyPem } = require('../src/crypto');

const ISSUER = 'https://mock.example';
const REDIRECT = 'weredaconnect://fayda-callback';
const CLIENT_ID = 'weredaconnect-test';
// The relying party key the tests sign client assertions with (private_key_jwt).
const CLIENT_KEY = loadSigningKey(generateSigningKeyPem());
const CLIENT_JWKS_JSON = JSON.stringify({ keys: [CLIENT_KEY.jwk] });

function makeApp(envOverrides = {}, clock = { t: 1_800_000_000 }) {
  const config = configFromEnv({ CLIENT_ID, CLIENT_JWKS_JSON, ...envOverrides });
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
    client_id: CLIENT_ID,
    redirect_uri: REDIRECT,
    response_type: 'code',
    scope: 'openid profile phone',
    claims: JSON.stringify({ userinfo: { name: { essential: true }, phone_number: { essential: true } }, id_token: {} }),
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

/** A fresh private_key_jwt client assertion (new jti each call). */
function clientAssertion({ key = CLIENT_KEY, aud = `${ISSUER}${PATHS.token}`, exp = 1_900_000_000, ...claims } = {}) {
  return signJwt({ iss: CLIENT_ID, sub: CLIENT_ID, aud, exp, iat: 1_800_000_000, jti: crypto.randomUUID(), ...claims }, key);
}

function tokenBody(fields, assertion = {}) {
  return new URLSearchParams({
    grant_type: 'authorization_code',
    redirect_uri: REDIRECT,
    client_id: CLIENT_ID,
    client_assertion_type: 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer',
    client_assertion: clientAssertion(assertion),
    ...fields,
  }).toString();
}

/** Runs authorize + token and returns the parsed token response. */
async function exchange(app, opts, assertion) {
  const { code, verifier } = await getCode(app, opts);
  const res = await app.call('POST', PATHS.token, { body: tokenBody({ code, code_verifier: verifier }, assertion) });
  if (res.statusCode !== 200) throw new Error(`token ${res.statusCode}: ${res.body}`);
  return JSON.parse(res.body);
}

const jwksOf = async (app) => JSON.parse((await app.call('GET', PATHS.jwks)).body);

module.exports = {
  makeApp, authorizeQuery, getCode, tokenBody, clientAssertion, exchange, jwksOf,
  ISSUER, REDIRECT, CLIENT_ID, CLIENT_KEY, PATHS,
};
