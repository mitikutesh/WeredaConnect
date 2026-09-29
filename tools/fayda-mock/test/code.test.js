'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { makeApp, authorizeQuery, getCode, tokenBody, REDIRECT, PATHS } = require('./helpers');

test('a code works once and is rejected on reuse', async () => {
  const app = makeApp();
  const { code, verifier, state } = await getCode(app);
  assert.equal(state, 'st-1');
  const first = await app.call('POST', PATHS.token, { body: tokenBody({ code, code_verifier: verifier }) });
  assert.equal(first.statusCode, 200);
  const second = await app.call('POST', PATHS.token, { body: tokenBody({ code, code_verifier: verifier }) });
  assert.equal(second.statusCode, 400);
  assert.equal(JSON.parse(second.body).error, 'invalid_grant');
});

test('a failed exchange still burns the code', async () => {
  const app = makeApp();
  const { code, verifier } = await getCode(app);
  await app.call('POST', PATHS.token, { body: tokenBody({ code, code_verifier: 'z'.repeat(60) }) });
  const retry = await app.call('POST', PATHS.token, { body: tokenBody({ code, code_verifier: verifier }) });
  assert.equal(retry.statusCode, 400);
});

test('an expired code is rejected', async () => {
  const app = makeApp();
  const { code, verifier } = await getCode(app);
  app.clock.t += 301;
  const res = await app.call('POST', PATHS.token, { body: tokenBody({ code, code_verifier: verifier }) });
  assert.equal(res.statusCode, 400);
});

test('redirect_uri must match the one used at authorize', async () => {
  const app = makeApp({ ALLOWED_REDIRECT_URIS: `${REDIRECT},https://app.example/cb` });
  const { code, verifier } = await getCode(app);
  const res = await app.call('POST', PATHS.token, { body: tokenBody({ code, code_verifier: verifier, redirect_uri: 'https://app.example/cb' }) });
  assert.equal(res.statusCode, 400);
});

test('a bad client assertion is 401 invalid_client and does not burn the code', async () => {
  const app = makeApp();
  const { code, verifier } = await getCode(app);
  const bad = await app.call('POST', PATHS.token, { body: tokenBody({ code, code_verifier: verifier }, { aud: 'https://elsewhere.example' }) });
  assert.equal(bad.statusCode, 401);
  assert.equal(JSON.parse(bad.body).error, 'invalid_client');
  const good = await app.call('POST', PATHS.token, { body: tokenBody({ code, code_verifier: verifier }) });
  assert.equal(good.statusCode, 200);
});

test('unknown client_id or unregistered redirect_uri renders an HTML error, no redirect', async () => {
  const app = makeApp();
  for (const o of [{ client_id: 'evil' }, { redirect_uri: 'https://evil.example/cb' }]) {
    const res = await app.call('GET', `/authorize?${authorizeQuery(o).qs}`);
    assert.equal(res.statusCode, 400);
    assert.match(res.headers['content-type'], /text\/html/);
    assert.equal(res.headers.location, undefined);
  }
});

test('interactive page lists users and completes with OTP 123456 only', async () => {
  const app = makeApp();
  const { qs, params, verifier } = authorizeQuery();
  const pageRes = await app.call('GET', `/authorize?${qs}`);
  assert.equal(pageRes.statusCode, 200);
  assert.match(pageRes.body, /Abebe Kebede/);
  assert.match(pageRes.body, /5555555555555555/);

  const form = (otp) => new URLSearchParams({ ...params, fin: '3333333333333333', otp }).toString();
  const wrong = await app.call('POST', '/authorize/complete', { body: form('000000') });
  assert.equal(wrong.statusCode, 400);

  const ok = await app.call('POST', '/authorize/complete', { body: form('123456') });
  assert.equal(ok.statusCode, 302);
  const loc = new URL(ok.headers.location);
  assert.equal(`${loc.protocol}//${loc.host}${loc.pathname}`, REDIRECT);
  const tok = await app.call('POST', PATHS.token, { body: tokenBody({ code: loc.searchParams.get('code'), code_verifier: verifier }) });
  assert.equal(tok.statusCode, 200);
});

test('simulate=denied and simulate=down', async () => {
  const app = makeApp();
  const { qs } = authorizeQuery();
  const denied = await app.call('GET', `/authorize?${qs}&simulate=denied`);
  assert.equal(new URL(denied.headers.location).searchParams.get('error'), 'access_denied');
  const down = await app.call('GET', `/authorize?${qs}&simulate=down`);
  assert.equal(down.statusCode, 503);
});
