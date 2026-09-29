'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { verifyJwt, subjectForFin } = require('../src/crypto');
const { normalizePhone, userForPhone, TEST_USERS } = require('../src/users');
const { makeApp, authorizeQuery, tokenBody } = require('./helpers');

async function exchange(app, code, verifier) {
  const tok = await app.call('POST', '/token', { body: tokenBody({ code, code_verifier: verifier }) });
  assert.equal(tok.statusCode, 200);
  const body = JSON.parse(tok.body);
  const jwks = JSON.parse((await app.call('GET', '/.well-known/jwks.json')).body);
  return { claims: verifyJwt(body.id_token, jwks), accessToken: body.access_token };
}

async function claimsForPhone(app, phone) {
  const { qs, verifier } = authorizeQuery();
  const res = await app.call('GET', `/authorize?${qs}&auto=1&phone=${encodeURIComponent(phone)}`);
  assert.equal(res.statusCode, 302);
  const code = new URL(res.headers.location).searchParams.get('code');
  assert.ok(code, res.headers.location);
  return (await exchange(app, code, verifier)).claims;
}

test('normalizePhone accepts the usual Ethiopian mobile formats', () => {
  for (const input of ['+251912345678', '251912345678', '0912345678', '912345678', '+251 912 345 678', '0912-345-678']) {
    assert.equal(normalizePhone(input), '+251912345678', input);
  }
  assert.equal(normalizePhone('0712345678'), '+251712345678');
  for (const bad of ['', '12345', '+251812345678', '+25191234567', '+2519123456789', 'abc']) {
    assert.equal(normalizePhone(bad), null, bad);
  }
});

test('the same phone is always the same person, carrying that phone', async () => {
  const app = makeApp();
  const first = await claimsForPhone(app, '0912345678');
  const again = await claimsForPhone(app, '+251912345678');

  assert.equal(first.sub, again.sub);
  assert.equal(first.name, again.name);
  assert.equal(first.phone_number, '+251912345678');
  assert.equal(first.sub, subjectForFin(userForPhone('+251912345678').fin));

  const other = await claimsForPhone(app, '+251912345679');
  assert.notEqual(other.sub, first.sub);
});

test('a seeded phone returns its seeded person', async () => {
  const app = makeApp();
  const sara = TEST_USERS.find((u) => u.name === 'Sara Haile');
  const claims = await claimsForPhone(app, sara.phone);
  assert.equal(claims.sub, subjectForFin(sara.fin));
  assert.equal(claims.name, 'Sara Haile');
});

test('phone FINs are 16 digits and never a seeded FIN', () => {
  for (let i = 0; i < 200; i++) {
    const phone = `+2519${String(i).padStart(8, '0')}`;
    const user = userForPhone(phone);
    if (TEST_USERS.some((u) => u.phone === phone)) continue;
    assert.match(user.fin, /^9\d{15}$/);
    assert.equal(TEST_USERS.some((u) => u.fin === user.fin), false);
  }
});

test('auto mode with a bad phone redirects with invalid_request', async () => {
  const app = makeApp();
  const { qs } = authorizeQuery();
  const res = await app.call('GET', `/authorize?${qs}&auto=1&phone=12345`);
  const loc = new URL(res.headers.location);
  assert.equal(loc.searchParams.get('error'), 'invalid_request');
  assert.equal(loc.searchParams.get('code'), null);
});

test('picker offers the phone identity and completes with it', async () => {
  const app = makeApp();
  const { qs, params, verifier } = authorizeQuery();
  const pageRes = await app.call('GET', `/authorize?${qs}`);
  assert.match(pageRes.body, /value="phone"/);
  assert.match(pageRes.body, /name="phone"/);

  const post = (fields) => app.call('POST', '/authorize/complete', {
    body: new URLSearchParams({ ...params, otp: '123456', ...fields }).toString(),
  });

  const ok = await post({ fin: 'phone', phone: '0911223344' });
  assert.equal(ok.statusCode, 302);
  const code = new URL(ok.headers.location).searchParams.get('code');
  const { claims, accessToken } = await exchange(app, code, verifier);
  assert.equal(claims.phone_number, '+251911223344');

  const info = await app.call('GET', '/userinfo', { headers: { authorization: `Bearer ${accessToken}` } });
  assert.equal(JSON.parse(info.body).phone_number, '+251911223344');

  assert.equal((await post({ fin: 'phone', phone: '123' })).statusCode, 400);
  assert.equal((await post({ fin: 'phone' })).statusCode, 400);
});
