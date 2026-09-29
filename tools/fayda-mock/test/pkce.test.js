'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { pkceChallenge, verifyPkce } = require('../src/crypto');
const { makeApp, getCode, tokenBody, PATHS } = require('./helpers');

test('S256 matches the RFC 7636 appendix B example', () => {
  const verifier = 'dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk';
  assert.equal(pkceChallenge(verifier), 'E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM');
  assert.equal(verifyPkce(verifier, 'E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM'), true);
});

test('verifyPkce rejects wrong, short and missing verifiers', () => {
  const challenge = pkceChallenge('a'.repeat(43));
  assert.equal(verifyPkce('b'.repeat(43), challenge), false);
  assert.equal(verifyPkce('a'.repeat(42), pkceChallenge('a'.repeat(42))), false);
  assert.equal(verifyPkce(undefined, challenge), false);
  assert.equal(verifyPkce('a'.repeat(43), undefined), false);
});

test('token endpoint rejects a wrong code_verifier', async () => {
  const app = makeApp();
  const { code } = await getCode(app);
  const res = await app.call('POST', PATHS.token, { body: tokenBody({ code, code_verifier: 'x'.repeat(50) }) });
  assert.equal(res.statusCode, 400);
  assert.equal(JSON.parse(res.body).error, 'invalid_grant');
});

test('authorize without code_challenge redirects with invalid_request when PKCE is required', async () => {
  const app = makeApp();
  const { qs } = require('./helpers').authorizeQuery({ code_challenge: undefined });
  const res = await app.call('GET', `/authorize?${qs}`);
  assert.equal(res.statusCode, 302);
  const loc = new URL(res.headers.location);
  assert.equal(loc.searchParams.get('error'), 'invalid_request');
  assert.equal(loc.searchParams.get('state'), 'st-1');
});

test('authorize with plain code_challenge_method is rejected', async () => {
  const app = makeApp();
  const { qs } = require('./helpers').authorizeQuery({ code_challenge_method: 'plain' });
  const res = await app.call('GET', `/authorize?${qs}`);
  assert.equal(new URL(res.headers.location).searchParams.get('error'), 'invalid_request');
});
