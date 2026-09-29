'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const { verifyJwt, loadSigningKey, generateSigningKeyPem, subjectForFin } = require('../src/crypto');
const {
  makeApp, getCode, tokenBody, clientAssertion, exchange, jwksOf, authorizeQuery, ISSUER, CLIENT_ID, PATHS,
} = require('./helpers');

const bearer = (at) => ({ headers: { authorization: `Bearer ${at}` } });

test('id_token is RS256, verifies against the JWKS and carries only identity claims', async () => {
  const app = makeApp();
  const tok = await exchange(app, { nonce: 'nonce-xyz' });
  assert.equal(tok.token_type, 'Bearer');
  assert.equal(tok.expires_in, 300);

  const jwks = await jwksOf(app);
  assert.equal(jwks.keys[0].kty, 'RSA');
  const header = JSON.parse(Buffer.from(tok.id_token.split('.')[0], 'base64url').toString());
  assert.equal(header.alg, 'RS256');
  assert.equal(header.kid, jwks.keys[0].kid);

  const c = verifyJwt(tok.id_token, jwks);
  assert.equal(c.iss, ISSUER);
  assert.equal(c.aud, CLIENT_ID);
  assert.equal(c.nonce, 'nonce-xyz');
  assert.equal(c.exp - c.iat, 300);
  assert.equal(c.auth_time, c.iat);
  assert.equal(c.sub, subjectForFin('1111111111111111'));
  assert.equal(c.acr, 'mosip:idp:acr:generated-code');
  const expectedAtHash = crypto.createHash('sha256').update(tok.access_token).digest().subarray(0, 16).toString('base64url');
  assert.equal(c.at_hash, expectedAtHash);
  // eSignet releases user data only through userinfo.
  for (const k of ['name', 'given_name', 'family_name', 'phone_number', 'individual_id']) assert.equal(k in c, false, k);
});

test('sub is stable per FIN and differs across FINs', async () => {
  const app = makeApp();
  const jwks = await jwksOf(app);
  const a1 = verifyJwt((await exchange(app, { fin: '2222222222222222' })).id_token, jwks);
  const a2 = verifyJwt((await exchange(app, { fin: '2222222222222222' })).id_token, jwks);
  const b = verifyJwt((await exchange(app, { fin: '3333333333333333' })).id_token, jwks);
  assert.equal(a1.sub, a2.sub);
  assert.notEqual(a1.sub, b.sub);
  assert.doesNotMatch(a1.sub, /2222/);
});

test('ISSUER_URL_OVERRIDE sets iss and the token endpoint the assertion must name', async () => {
  const app = makeApp({ ISSUER_URL_OVERRIDE: 'https://fayda.dev.example/' });
  const tok = await exchange(app, {}, { aud: `https://fayda.dev.example${PATHS.token}` });
  assert.equal(verifyJwt(tok.id_token, await jwksOf(app)).iss, 'https://fayda.dev.example');
});

test('userinfo is a signed JWT with sub plus exactly the requested claims', async () => {
  const app = makeApp();
  const tok = await exchange(app, { fin: '3333333333333333' });
  const res = await app.call('GET', PATHS.userinfo, bearer(tok.access_token));
  assert.equal(res.statusCode, 200);
  assert.equal(res.headers['content-type'], 'application/jwt');
  const jwks = await jwksOf(app);
  const c = verifyJwt(res.body, jwks);
  assert.equal(c.sub, verifyJwt(tok.id_token, jwks).sub);
  assert.equal(c.name, 'Sara Haile');
  assert.equal(c.phone_number, '+251900100003');
  assert.equal('individual_id' in c, false);

  const bad = await app.call('GET', PATHS.userinfo, bearer('nope'));
  assert.equal(bad.statusCode, 401);
});

test('userinfo releases nothing but sub without a claims parameter', async () => {
  const app = makeApp();
  const tok = await exchange(app, { claims: undefined });
  const c = verifyJwt((await app.call('GET', PATHS.userinfo, bearer(tok.access_token))).body, await jwksOf(app));
  assert.ok(c.sub);
  assert.equal('name' in c, false);
  assert.equal('phone_number' in c, false);
});

test('userinfo can release individual_id when asked for it', async () => {
  const app = makeApp();
  const tok = await exchange(app, { claims: JSON.stringify({ userinfo: { individual_id: null, unknown_claim: null } }) });
  const c = verifyJwt((await app.call('GET', PATHS.userinfo, bearer(tok.access_token))).body, await jwksOf(app));
  assert.equal(c.individual_id, '1111111111111111');
  assert.equal('unknown_claim' in c, false);
});

test('user without a phone gets no phone_number claim', async () => {
  const app = makeApp();
  const tok = await exchange(app, { fin: '5555555555555555' });
  const c = verifyJwt((await app.call('GET', PATHS.userinfo, bearer(tok.access_token))).body, await jwksOf(app));
  assert.equal(c.name, 'No Phone');
  assert.equal('phone_number' in c, false);
});

test('a claims parameter that is not JSON is rejected at authorize', async () => {
  const app = makeApp();
  const { qs } = authorizeQuery({ claims: '{not json' });
  const res = await app.call('GET', `${PATHS.authorize}?${qs}`);
  assert.equal(new URL(res.headers.location).searchParams.get('error'), 'invalid_request');
});

test('a client assertion cannot be replayed', async () => {
  const app = makeApp();
  const assertion = clientAssertion();
  const one = await getCode(app);
  assert.equal((await app.call('POST', PATHS.token, { body: tokenBody({ code: one.code, code_verifier: one.verifier, client_assertion: assertion }) })).statusCode, 200);
  const two = await getCode(app);
  const replay = await app.call('POST', PATHS.token, { body: tokenBody({ code: two.code, code_verifier: two.verifier, client_assertion: assertion }) });
  assert.equal(replay.statusCode, 401);
  assert.match(JSON.parse(replay.body).error_description, /replayed/);
});

test('private_key_jwt rejects a wrong key, wrong aud, expired assertion, wrong iss and a client secret', async () => {
  const app = makeApp();
  const otherKey = loadSigningKey(generateSigningKeyPem());
  const cases = [
    [{}, { key: otherKey }],
    [{}, { aud: `${ISSUER}/token` }],
    [{}, { aud: ISSUER }],
    [{}, { exp: app.clock.t - 1 }],
    [{}, { iss: 'someone-else' }],
    [{ client_id: 'someone-else' }, {}],
    [{ client_assertion_type: '', client_secret: 'dev-fayda-mock-secret' }, {}],
  ];
  for (const [fields, assertion] of cases) {
    const { code, verifier } = await getCode(app);
    const res = await app.call('POST', PATHS.token, { body: tokenBody({ code, code_verifier: verifier, ...fields }, assertion) });
    assert.equal(res.statusCode, 401, JSON.stringify({ fields, assertion: Object.keys(assertion) }));
    assert.equal(JSON.parse(res.body).error, 'invalid_client');
  }
});

test('private_key_jwt is rejected when no client key is configured', async () => {
  const app = makeApp({ CLIENT_JWKS_JSON: '' });
  const { code, verifier } = await getCode(app);
  const res = await app.call('POST', PATHS.token, { body: tokenBody({ code, code_verifier: verifier }) });
  assert.equal(res.statusCode, 401);
});

test('an assertion signed by a key from getClientJwks is accepted', async () => {
  const { createApp, configFromEnv } = require('../src/app');
  const { memoryStore } = require('../src/store');
  const { memoryKeyProvider, clientJwksProvider } = require('../src/keys');
  const rpKey = memoryKeyProvider();
  const handle = createApp({
    config: configFromEnv({ CLIENT_ID }), store: memoryStore(), getKey: memoryKeyProvider(),
    getClientJwks: clientJwksProvider(rpKey), now: () => 1_800_000_000,
  });
  const call = (method, pq, opts = {}) => {
    const url = new URL(pq, ISSUER);
    return handle({ method, path: url.pathname, query: url.searchParams, headers: {}, body: opts.body || '', origin: ISSUER });
  };
  const { code, verifier } = await getCode({ call });
  const res = await call('POST', PATHS.token, { body: tokenBody({ code, code_verifier: verifier }, { key: await rpKey() }) });
  assert.equal(res.statusCode, 200, res.body);
});
