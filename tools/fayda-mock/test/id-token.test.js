'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const { verifyJwt, signJwt, loadSigningKey, generateSigningKeyPem, subjectForFin } = require('../src/crypto');
const { makeApp, getCode, tokenBody, ISSUER } = require('./helpers');

async function exchange(app, opts) {
  const { code, verifier } = await getCode(app, opts);
  const res = await app.call('POST', '/token', { body: tokenBody({ code, code_verifier: verifier }) });
  assert.equal(res.statusCode, 200);
  return JSON.parse(res.body);
}

test('id_token is RS256, verifies against jwks_uri and carries the expected claims', async () => {
  const app = makeApp();
  const tok = await exchange(app, { nonce: 'nonce-xyz' });
  assert.equal(tok.token_type, 'Bearer');
  assert.equal(tok.expires_in, 300);

  const jwks = JSON.parse((await app.call('GET', '/.well-known/jwks.json')).body);
  assert.equal(jwks.keys[0].kty, 'RSA');
  assert.equal(jwks.keys[0].alg, 'RS256');
  const header = JSON.parse(Buffer.from(tok.id_token.split('.')[0], 'base64url').toString());
  assert.equal(header.alg, 'RS256');
  assert.equal(header.kid, jwks.keys[0].kid);

  const c = verifyJwt(tok.id_token, jwks);
  assert.equal(c.iss, ISSUER);
  assert.equal(c.aud, 'weredaconnect-test');
  assert.equal(c.nonce, 'nonce-xyz');
  assert.equal(c.exp - c.iat, 300);
  assert.equal(c.sub, subjectForFin('1111111111111111'));
  assert.equal(c.name, 'Abebe Kebede');
  assert.equal(c.given_name, 'Abebe');
  assert.equal(c.family_name, 'Kebede');
  assert.equal(c.phone_number, '+251900100001');
  assert.equal(c.acr, 'mosip:idp:acr:generated-code');
});

test('sub is stable per FIN and differs across FINs', async () => {
  const app = makeApp();
  const jwks = JSON.parse((await app.call('GET', '/.well-known/jwks.json')).body);
  const a1 = verifyJwt((await exchange(app, { fin: '2222222222222222' })).id_token, jwks);
  const a2 = verifyJwt((await exchange(app, { fin: '2222222222222222' })).id_token, jwks);
  const b = verifyJwt((await exchange(app, { fin: '3333333333333333' })).id_token, jwks);
  assert.equal(a1.sub, a2.sub);
  assert.notEqual(a1.sub, b.sub);
  assert.doesNotMatch(a1.sub, /2222/);
});

test('user without a phone gets no phone_number claim', async () => {
  const app = makeApp();
  const jwks = JSON.parse((await app.call('GET', '/.well-known/jwks.json')).body);
  const c = verifyJwt((await exchange(app, { fin: '5555555555555555' })).id_token, jwks);
  assert.equal('phone_number' in c, false);
});

test('ISSUER_URL_OVERRIDE sets iss', async () => {
  const app = makeApp({ ISSUER_URL_OVERRIDE: 'https://fayda.dev.example/' });
  const jwks = JSON.parse((await app.call('GET', '/.well-known/jwks.json')).body);
  assert.equal(verifyJwt((await exchange(app)).id_token, jwks).iss, 'https://fayda.dev.example');
});

test('userinfo returns the same claims for the access token', async () => {
  const app = makeApp();
  const tok = await exchange(app, { fin: '3333333333333333' });
  const res = await app.call('GET', '/userinfo', { headers: { authorization: `Bearer ${tok.access_token}` } });
  assert.equal(res.statusCode, 200);
  const c = JSON.parse(res.body);
  assert.equal(c.name, 'Sara Haile');
  assert.equal(c.phone_number, '+251900100003');
  const bad = await app.call('GET', '/userinfo', { headers: { authorization: 'Bearer nope' } });
  assert.equal(bad.statusCode, 401);
});

test('private_key_jwt client auth works with CLIENT_JWKS_JSON and rejects replay', async () => {
  const clientKey = loadSigningKey(generateSigningKeyPem());
  const app = makeApp({ CLIENT_JWKS_JSON: JSON.stringify({ keys: [clientKey.jwk] }) });
  const assertion = signJwt({
    iss: 'weredaconnect-test', sub: 'weredaconnect-test', aud: `${ISSUER}/token`,
    exp: app.clock.t + 60, jti: crypto.randomUUID(),
  }, clientKey);
  const body = (code, verifier) => new URLSearchParams({
    grant_type: 'authorization_code', code, code_verifier: verifier,
    redirect_uri: 'weredaconnect://fayda-callback', client_id: 'weredaconnect-test',
    client_assertion_type: 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer',
    client_assertion: assertion,
  }).toString();

  const one = await getCode(app);
  assert.equal((await app.call('POST', '/token', { body: body(one.code, one.verifier) })).statusCode, 200);
  const two = await getCode(app);
  const replay = await app.call('POST', '/token', { body: body(two.code, two.verifier) });
  assert.equal(replay.statusCode, 401);
});

test('private_key_jwt is rejected when CLIENT_JWKS_JSON is unset', async () => {
  const app = makeApp();
  const { code, verifier } = await getCode(app);
  const res = await app.call('POST', '/token', {
    body: new URLSearchParams({
      grant_type: 'authorization_code', code, code_verifier: verifier,
      redirect_uri: 'weredaconnect://fayda-callback',
      client_assertion_type: 'urn:ietf:params:oauth:client-assertion-type:jwt-bearer',
      client_assertion: 'a.b.c',
    }).toString(),
  });
  assert.equal(res.statusCode, 401);
});
