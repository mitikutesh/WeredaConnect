'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { verifyJwt, subjectForFin } = require('../src/crypto');
const { userForSub, userForPhone, TEST_USERS } = require('../src/users');
const { makeApp, authorizeQuery, tokenBody, PATHS } = require('./helpers');

// What the WeredaConnect server puts on the authorize URL (mock mode only) when an
// account verifies again: the Fayda ID it holds, else the account phone.
const SUB = 'legacy-generated-sub_000000000001';
const ALL_CLAIMS = JSON.stringify({ userinfo: { name: {}, phone_number: {}, individual_id: {} }, id_token: {} });

/** Identity from the id_token, user data from the signed userinfo, as on eSignet. */
async function exchange(app, code, verifier) {
  const tok = await app.call('POST', PATHS.token, { body: tokenBody({ code, code_verifier: verifier }) });
  assert.equal(tok.statusCode, 200, tok.body);
  const body = JSON.parse(tok.body);
  const jwks = JSON.parse((await app.call('GET', PATHS.jwks)).body);
  const info = await app.call('GET', PATHS.userinfo, { headers: { authorization: `Bearer ${body.access_token}` } });
  assert.equal(info.statusCode, 200);
  return { idToken: verifyJwt(body.id_token, jwks), userinfo: verifyJwt(info.body, jwks) };
}

/** auto=1&fin=hint with the given authorize overrides (login_hint among them). */
async function autoHint(app, overrides) {
  const { qs, verifier } = authorizeQuery(overrides);
  const res = await app.call('GET', `${PATHS.authorize}?${qs}&auto=1&fin=hint`);
  return { res, verifier };
}

async function claimsForHint(app, overrides) {
  const { res, verifier } = await autoHint(app, overrides);
  assert.equal(res.statusCode, 302);
  const loc = new URL(res.headers.location);
  assert.equal(loc.searchParams.get('error'), null, res.headers.location);
  return exchange(app, loc.searchParams.get('code'), verifier);
}

test('userForSub: a seeded subject is that person, any other is honoured as a sub-only identity', () => {
  const sara = TEST_USERS.find((u) => u.name === 'Sara Haile');
  assert.equal(userForSub(subjectForFin(sara.fin)), sara);

  const other = userForSub(SUB);
  assert.equal(other.sub, SUB);
  assert.equal(other.fin, null);
  assert.equal(other.phone, null);
  assert.match(other.name, /^\S+ \S+$/);
  assert.equal(userForSub(SUB).name, other.name);
  assert.notEqual(userForSub('another-sub').sub, other.sub);

  assert.equal(userForSub(''), null);
  assert.equal(userForSub(undefined), null);
});

test('a sub hint signs the caller in as exactly that subject: stable name, no phone, no FIN claim', async () => {
  const app = makeApp();
  const first = await claimsForHint(app, { login_hint: `sub:${SUB}`, claims: ALL_CLAIMS });
  assert.equal(first.idToken.sub, SUB);
  assert.equal(first.userinfo.sub, SUB);
  assert.equal(first.userinfo.name, userForSub(SUB).name);
  assert.equal('phone_number' in first.userinfo, false);
  assert.equal('individual_id' in first.userinfo, false);

  const again = await claimsForHint(app, { login_hint: `sub:${SUB}`, claims: ALL_CLAIMS });
  assert.equal(again.idToken.sub, SUB);
  assert.equal(again.userinfo.name, first.userinfo.name);
});

test('a sub hint for a seeded identity returns that person, with the phone claim', async () => {
  const app = makeApp();
  const sara = TEST_USERS.find((u) => u.name === 'Sara Haile');
  const { idToken, userinfo } = await claimsForHint(app, { login_hint: `sub:${subjectForFin(sara.fin)}`, claims: ALL_CLAIMS });
  assert.equal(idToken.sub, subjectForFin(sara.fin));
  assert.equal(userinfo.name, 'Sara Haile');
  assert.equal(userinfo.phone_number, sara.phone);
  assert.equal(userinfo.individual_id, sara.fin);
});

test('a phone hint signs in as that phone\'s owner, carrying the phone', async () => {
  const app = makeApp();
  const { idToken, userinfo } = await claimsForHint(app, { login_hint: 'phone:0911223344' });
  assert.equal(idToken.sub, subjectForFin(userForPhone('+251911223344').fin));
  assert.equal(userinfo.phone_number, '+251911223344');
});

test('the picker offers the account identity first and preselected, and completes with it', async () => {
  const app = makeApp();
  const { qs, params, verifier } = authorizeQuery({ login_hint: `sub:${SUB}` });
  const pageRes = await app.call('GET', `${PATHS.authorize}?${qs}`);
  assert.equal(pageRes.statusCode, 200);
  const { body } = pageRes;

  const hintAt = body.indexOf('value="hint" checked');
  assert.ok(hintAt > 0, 'account identity option is checked');
  assert.ok(hintAt < body.indexOf('value="1111111111111111"'), 'account identity comes first');
  assert.doesNotMatch(body, /value="1111111111111111" checked/);
  assert.ok(body.includes(`Continue as ${userForSub(SUB).name}`));
  assert.ok(body.includes('FIN not kept by the mock'));
  assert.ok(body.includes(`name="login_hint" value="sub:${SUB}"`), 'the hint survives the form round trip');

  const post = await app.call('POST', '/authorize/complete', {
    body: new URLSearchParams({ ...params, otp: '123456', fin: 'hint' }).toString(),
  });
  assert.equal(post.statusCode, 302, post.body);
  const code = new URL(post.headers.location).searchParams.get('code');
  const { idToken, userinfo } = await exchange(app, code, verifier);
  assert.equal(idToken.sub, SUB);
  assert.equal(userinfo.sub, SUB);
});

test('a phone hint preselects "Identity for my phone number" with the number filled in', async () => {
  const app = makeApp();
  const { qs } = authorizeQuery({ login_hint: 'phone:0911223344' });
  const { body } = await app.call('GET', `${PATHS.authorize}?${qs}`);
  assert.match(body, /id="fin-phone" checked/);
  assert.ok(body.includes('value="+251911223344"'));
  assert.doesNotMatch(body, /value="hint"/);
  assert.doesNotMatch(body, /value="1111111111111111" checked/);
});

test('without a usable hint the picker is unchanged and fin=hint is refused', async () => {
  const app = makeApp();
  for (const loginHint of [undefined, 'nonsense', 'sub:has space', 'sub:', 'phone:123']) {
    const overrides = loginHint === undefined ? {} : { login_hint: loginHint };
    const { qs, params } = authorizeQuery(overrides);
    const { body } = await app.call('GET', `${PATHS.authorize}?${qs}`);
    assert.doesNotMatch(body, /value="hint"/, String(loginHint));
    assert.match(body, /value="1111111111111111" checked/, String(loginHint));
    assert.doesNotMatch(body, /id="fin-phone" checked/, String(loginHint));

    const { res } = await autoHint(app, overrides);
    assert.equal(res.statusCode, 302);
    const loc = new URL(res.headers.location);
    assert.equal(loc.searchParams.get('error'), 'invalid_request', String(loginHint));
    assert.equal(loc.searchParams.get('error_description'), 'login_hint is required for fin=hint');
    assert.equal(loc.searchParams.get('state'), 'st-1');

    const post = await app.call('POST', '/authorize/complete', {
      body: new URLSearchParams({ ...params, otp: '123456', fin: 'hint' }).toString(),
    });
    assert.equal(post.statusCode, 400, String(loginHint));
  }
});

test('the other choices still work beside a hint', async () => {
  const app = makeApp();
  const { qs, verifier } = authorizeQuery({ login_hint: `sub:${SUB}` });
  const res = await app.call('GET', `${PATHS.authorize}?${qs}&auto=1&fin=3333333333333333`);
  assert.equal(res.statusCode, 302);
  const code = new URL(res.headers.location).searchParams.get('code');
  const { idToken } = await exchange(app, code, verifier);
  assert.equal(idToken.sub, subjectForFin('3333333333333333'));
});
