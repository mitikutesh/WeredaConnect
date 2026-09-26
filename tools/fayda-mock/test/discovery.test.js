'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { makeApp, ISSUER } = require('./helpers');
const { toRequest } = require('../index');

test('discovery document shape', async () => {
  const app = makeApp();
  const res = await app.call('GET', '/.well-known/openid-configuration');
  assert.equal(res.statusCode, 200);
  const d = JSON.parse(res.body);
  assert.equal(d.issuer, ISSUER);
  assert.equal(d.authorization_endpoint, `${ISSUER}/authorize`);
  assert.equal(d.token_endpoint, `${ISSUER}/token`);
  assert.equal(d.jwks_uri, `${ISSUER}/.well-known/jwks.json`);
  assert.equal(d.userinfo_endpoint, `${ISSUER}/userinfo`);
  assert.deepEqual(d.response_types_supported, ['code']);
  assert.deepEqual(d.scopes_supported, ['openid', 'profile', 'phone']);
  assert.deepEqual(d.id_token_signing_alg_values_supported, ['RS256']);
  assert.deepEqual(d.token_endpoint_auth_methods_supported, ['client_secret_post', 'private_key_jwt']);
  assert.deepEqual(d.code_challenge_methods_supported, ['S256']);
  assert.deepEqual(d.acr_values_supported, ['mosip:idp:acr:generated-code', 'mosip:idp:acr:biometrics']);
});

test('health and 404', async () => {
  const app = makeApp();
  assert.equal((await app.call('GET', '/health')).statusCode, 200);
  assert.equal((await app.call('GET', '/nope')).statusCode, 404);
});

test('Function URL event maps to a request with the https origin', () => {
  const req = toRequest({
    rawPath: '/token',
    rawQueryString: 'a=1',
    headers: { 'Content-Type': 'application/x-www-form-urlencoded' },
    body: Buffer.from('code=abc').toString('base64'),
    isBase64Encoded: true,
    requestContext: { domainName: 'abc.lambda-url.eu-north-1.on.aws', http: { method: 'POST' } },
  });
  assert.equal(req.origin, 'https://abc.lambda-url.eu-north-1.on.aws');
  assert.equal(req.body, 'code=abc');
  assert.equal(req.query.get('a'), '1');
  assert.equal(req.headers['content-type'], 'application/x-www-form-urlencoded');
});
