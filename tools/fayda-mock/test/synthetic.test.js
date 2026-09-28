'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { verifyJwt, subjectForFin } = require('../src/crypto');
const { findUser, generateFin, TEST_USERS } = require('../src/users');
const { makeApp, authorizeQuery, getCode, tokenBody } = require('./helpers');

async function claimsFor(app, fin) {
  const { code, verifier, res } = await getCode(app, { fin });
  assert.equal(res.statusCode, 302);
  const tok = await app.call('POST', '/token', { body: tokenBody({ code, code_verifier: verifier }) });
  assert.equal(tok.statusCode, 200);
  const jwks = JSON.parse((await app.call('GET', '/.well-known/jwks.json')).body);
  return verifyJwt(JSON.parse(tok.body).id_token, jwks);
}

test('any 16-digit FIN gets a stable synthetic identity with no phone_number', async () => {
  const app = makeApp();
  const a1 = await claimsFor(app, '9876543210123456');
  const a2 = await claimsFor(app, '9876543210123456');
  assert.equal(a1.sub, subjectForFin('9876543210123456'));
  assert.equal(a1.sub, a2.sub);
  assert.equal(a1.name, a2.name);
  assert.equal(a1.name, `${a1.given_name} ${a1.family_name}`);
  assert.ok(a1.given_name && a1.family_name);
  assert.equal('phone_number' in a1, false);

  const b = await claimsFor(app, '9876543210123457');
  assert.notEqual(b.sub, a1.sub);
});

test('seeded FINs keep their fixed identities and scenarios', () => {
  assert.equal(findUser('1111111111111111').name, 'Abebe Kebede');
  assert.equal(findUser('4444444444444444').phone, '+251900100004');
  assert.equal(findUser('5555555555555555').phone, null);
  assert.equal(findUser('5555555555555555').synthetic, undefined);
});

test('malformed FINs are rejected', async () => {
  for (const fin of ['', '123', '12345678901234567', 'abcdefghijklmnop', '1111 1111 1111 1111']) {
    assert.equal(findUser(fin), null, fin);
  }
  const app = makeApp();
  const { qs } = authorizeQuery();
  const res = await app.call('GET', `/authorize?${qs}&auto=1&fin=123`);
  assert.equal(new URL(res.headers.location).searchParams.get('error'), 'invalid_request');
});

test('generateFin returns fresh 16-digit FINs outside the seeded list', () => {
  const seen = new Set();
  for (let i = 0; i < 200; i++) {
    const fin = generateFin();
    assert.match(fin, /^[1-9]\d{15}$/);
    assert.equal(TEST_USERS.some((u) => u.fin === fin), false);
    seen.add(fin);
  }
  assert.ok(seen.size > 195);
});

test('auto=1&fin=new issues a code for a random synthetic identity', async () => {
  const app = makeApp();
  const c1 = await claimsFor(app, 'new');
  const c2 = await claimsFor(app, 'new');
  assert.notEqual(c1.sub, c2.sub);
  assert.equal('phone_number' in c1, false);
});

test('picker offers typed FIN and new identity, and completes with either', async () => {
  const app = makeApp();
  const { qs, params, verifier } = authorizeQuery();
  const pageRes = await app.call('GET', `/authorize?${qs}`);
  assert.match(pageRes.body, /name="custom_fin"/);
  assert.match(pageRes.body, /value="new"/);
  assert.match(pageRes.body, /pattern="\\d\{16\}"/);

  const post = (fields) => app.call('POST', '/authorize/complete', {
    body: new URLSearchParams({ ...params, otp: '123456', ...fields }).toString(),
  });

  const typed = await post({ fin: 'custom', custom_fin: '7777 7777 7777 0001' });
  assert.equal(typed.statusCode, 302);
  const code = new URL(typed.headers.location).searchParams.get('code');
  const tok = await app.call('POST', '/token', { body: tokenBody({ code, code_verifier: verifier }) });
  const jwks = JSON.parse((await app.call('GET', '/.well-known/jwks.json')).body);
  assert.equal(verifyJwt(JSON.parse(tok.body).id_token, jwks).sub, subjectForFin('7777777777770001'));

  assert.equal((await post({ fin: 'new' })).statusCode, 302);
  assert.equal((await post({ fin: 'custom', custom_fin: '12' })).statusCode, 400);
  assert.equal((await post({ fin: 'custom' })).statusCode, 400);
});

test('userinfo works for a synthetic identity', async () => {
  const app = makeApp();
  const { code, verifier } = await getCode(app, { fin: '8888888888880000' });
  const tok = JSON.parse((await app.call('POST', '/token', { body: tokenBody({ code, code_verifier: verifier }) })).body);
  const res = await app.call('GET', '/userinfo', { headers: { authorization: `Bearer ${tok.access_token}` } });
  assert.equal(res.statusCode, 200);
  const c = JSON.parse(res.body);
  assert.equal(c.name, findUser('8888888888880000').name);
  assert.equal('phone_number' in c, false);
});
