'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const {
  clientKeyProvider, clientJwksProvider, parseClientPrivateKey, generateClientKeyValue, memoryKeyProvider,
} = require('../src/keys');
const { createApp, configFromEnv, PATHS } = require('../src/app');
const { memoryStore } = require('../src/store');
const { getCode, tokenBody, ISSUER, CLIENT_ID } = require('./helpers');

/** In-memory stand-in for the SSM parameter, with the same Overwrite semantics. */
function fakeParam(initial = null) {
  const p = { value: initial, puts: 0 };
  p.store = {
    get: async () => p.value,
    put: async (value, overwrite) => {
      if (p.value !== null && !overwrite) {
        const err = new Error('exists'); err.name = 'ParameterAlreadyExists'; throw err;
      }
      p.puts += 1;
      p.value = value;
    },
  };
  return p;
}

const decodeStored = (value) => JSON.parse(Buffer.from(value, 'base64').toString('utf8'));

test('a missing client key is created in the PRIVATE_KEY_BASE64 form', async () => {
  const param = fakeParam();
  const signer = await clientKeyProvider(param.store)();
  assert.equal(param.puts, 1);
  const jwk = decodeStored(param.value);
  assert.equal(jwk.kty, 'RSA');
  assert.ok(jwk.d && jwk.p && jwk.q && jwk.dp && jwk.dq && jwk.qi, 'private JWK');
  assert.equal(jwk.alg, 'RS256');
  assert.equal(jwk.kid, signer.jwk.kid);
  assert.equal('d' in signer.jwk, false, 'the JWKS side is public only');
});

test('the seeded PLACEHOLDER is replaced, a usable key is kept', async () => {
  const seeded = fakeParam('PLACEHOLDER');
  await clientKeyProvider(seeded.store)();
  assert.equal(seeded.puts, 1);
  assert.ok(parseClientPrivateKey(seeded.value));

  const existing = generateClientKeyValue();
  const kept = fakeParam(existing);
  const signer = await clientKeyProvider(kept.store)();
  assert.equal(kept.puts, 0);
  assert.equal(kept.value, existing);
  assert.equal(signer.jwk.kid, decodeStored(existing).kid);
});

test('a real key pasted as PEM or JWK JSON is accepted and kept', async () => {
  const { privateKey } = crypto.generateKeyPairSync('rsa', { modulusLength: 2048 });
  for (const value of [
    privateKey.export({ type: 'pkcs8', format: 'pem' }),
    JSON.stringify(privateKey.export({ format: 'jwk' })),
  ]) {
    const param = fakeParam(value);
    await clientKeyProvider(param.store)();
    assert.equal(param.puts, 0);
  }
});

test('parseClientPrivateKey rejects a public key and garbage', () => {
  const { publicKey } = crypto.generateKeyPairSync('rsa', { modulusLength: 2048 });
  assert.throws(() => parseClientPrivateKey(JSON.stringify(publicKey.export({ format: 'jwk' }))));
  assert.throws(() => parseClientPrivateKey('PLACEHOLDER'));
  assert.throws(() => parseClientPrivateKey(''));
});

test('each call re-reads the parameter, so a replaced key takes effect', async () => {
  const param = fakeParam(generateClientKeyValue());
  const provider = clientKeyProvider(param.store);
  const first = await provider();
  param.value = generateClientKeyValue();
  const second = await provider();
  assert.notEqual(first.jwk.n, second.jwk.n);
});

/** The mock as deployed: client key from a (fake) parameter, not CLIENT_JWKS_JSON. */
function deployedLikeApp(param) {
  const handle = createApp({
    config: configFromEnv({ CLIENT_ID }),
    store: memoryStore(),
    getKey: memoryKeyProvider(),
    getClientJwks: clientJwksProvider(clientKeyProvider(param.store)),
    now: () => 1_800_000_000,
  });
  const call = (method, pq, opts = {}) => {
    const url = new URL(pq, ISSUER);
    return handle({ method, path: url.pathname, query: url.searchParams, headers: {}, body: opts.body || '', origin: ISSUER });
  };
  return { call };
}

test('a fresh deploy: /authorize creates the key, and the server-side key then authenticates', async () => {
  const param = fakeParam('PLACEHOLDER');
  const app = deployedLikeApp(param);

  // The user reaches /authorize before the server ever signs an assertion.
  const { code, verifier } = await getCode(app);
  assert.equal(param.puts, 1, 'key created by the authorize step');

  // The server reads the same parameter and signs with it.
  const serverKey = parseClientPrivateKey(param.value);
  const res = await app.call('POST', PATHS.token, { body: tokenBody({ code, code_verifier: verifier }, { key: serverKey }) });
  assert.equal(res.statusCode, 200, res.body);
});

test('discovery also ensures the key exists', async () => {
  const param = fakeParam();
  const app = deployedLikeApp(param);
  assert.equal((await app.call('GET', PATHS.discovery)).statusCode, 200);
  assert.ok(parseClientPrivateKey(param.value));
});

test('an assertion signed by another key is rejected against the stored key', async () => {
  const param = fakeParam(generateClientKeyValue());
  const app = deployedLikeApp(param);
  const { code, verifier } = await getCode(app);
  const stranger = parseClientPrivateKey(generateClientKeyValue());
  const res = await app.call('POST', PATHS.token, { body: tokenBody({ code, code_verifier: verifier }, { key: stranger }) });
  assert.equal(res.statusCode, 401);
});

test('fileParamStore behaves like the SSM parameter (create once, overwrite on request)', async () => {
  const fs = require('node:fs');
  const os = require('node:os');
  const path = require('node:path');
  const { fileParamStore } = require('../src/keys');
  const file = path.join(fs.mkdtempSync(path.join(os.tmpdir(), 'fayda-mock-')), 'client-key');
  const store = fileParamStore(file);
  assert.equal(await store.get(), null);
  await store.put('one', false);
  await assert.rejects(store.put('two', false), { name: 'ParameterAlreadyExists' });
  await store.put('PLACEHOLDER', true);
  const signer = await clientKeyProvider(store)();
  assert.equal(parseClientPrivateKey(await store.get()).jwk.n, signer.jwk.n);
});

test('discovery and authorize read the client key once, not on every request', async () => {
  const param = fakeParam(generateClientKeyValue());
  let reads = 0;
  const get = param.store.get;
  param.store.get = async () => { reads += 1; return get(); };
  const app = deployedLikeApp(param);
  for (let i = 0; i < 5; i++) {
    await app.call('GET', PATHS.discovery);
    await getCode(app);
  }
  assert.equal(reads, 1);
});

test('a replaced client key is picked up on the next assertion, at most every 10 s', async () => {
  const param = fakeParam(generateClientKeyValue());
  const clock = { t: 1_800_000_000 };
  const handle = createApp({
    config: configFromEnv({ CLIENT_ID }), store: memoryStore(), getKey: memoryKeyProvider(),
    getClientJwks: clientJwksProvider(clientKeyProvider(param.store)), now: () => clock.t,
  });
  const call = (method, pq, opts = {}) => {
    const url = new URL(pq, ISSUER);
    return handle({ method, path: url.pathname, query: url.searchParams, headers: {}, body: opts.body || '', origin: ISSUER });
  };
  const exchange = async () => {
    const { code, verifier } = await getCode({ call });
    const key = parseClientPrivateKey(param.value);
    return call('POST', PATHS.token, { body: tokenBody({ code, code_verifier: verifier }, { key, exp: clock.t + 60 }) });
  };

  assert.equal((await exchange()).statusCode, 200);
  param.value = generateClientKeyValue();   // an operator (or another cold start) replaced it
  clock.t += 11;
  assert.equal((await exchange()).statusCode, 200);

  param.value = generateClientKeyValue();
  clock.t += 1;                            // inside the 10 s window: not re-read yet
  assert.equal((await exchange()).statusCode, 401);
  clock.t += 10;
  assert.equal((await exchange()).statusCode, 200);
});
