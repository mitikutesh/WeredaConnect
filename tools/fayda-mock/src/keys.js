'use strict';

const crypto = require('node:crypto');
const { generateSigningKeyPem, loadSigningKey } = require('./crypto');

// Signing key providers. Each returns an async function that resolves to the
// signer from crypto.loadSigningKey, cached for the life of the process.

function memoryKeyProvider(pem) {
  const key = loadSigningKey(pem || generateSigningKeyPem());
  return async () => key;
}

/**
 * Reads the PEM from an SSM SecureString. If the parameter does not exist yet,
 * generates one and writes it with Overwrite:false. If another cold start won
 * the race (ParameterAlreadyExists), re-reads so both use the same key.
 */
function ssmKeyProvider(paramName, description = 'Fayda mock OIDC signing key (dev only, auto-generated)') {
  const { SSMClient, GetParameterCommand, PutParameterCommand } = require('@aws-sdk/client-ssm');
  const client = new SSMClient({});
  let pending = null;

  const read = async () => {
    const res = await client.send(new GetParameterCommand({ Name: paramName, WithDecryption: true }));
    return res.Parameter.Value;
  };

  const load = async () => {
    try {
      return loadSigningKey(await read());
    } catch (err) {
      if (err.name !== 'ParameterNotFound') throw err;
    }
    const pem = generateSigningKeyPem();
    try {
      await client.send(new PutParameterCommand({
        Name: paramName,
        Value: pem,
        Type: 'SecureString',
        Overwrite: false,
        Description: description,
      }));
      return loadSigningKey(pem);
    } catch (err) {
      if (err.name !== 'ParameterAlreadyExists') throw err;
      return loadSigningKey(await read());
    }
  };

  return () => {
    // Cache the promise; drop it on failure so the next request retries.
    if (!pending) pending = load().catch((err) => { pending = null; throw err; });
    return pending;
  };
}

// ---------- relying party (client) key ----------
//
// The WeredaConnect client key lives in the same parameter in mock and live mode,
// /{prefix}/fayda/client-private-key, in the same format Fayda's own sample app
// uses (PRIVATE_KEY_BASE64: a base64-encoded private JWK). The server signs its
// private_key_jwt assertions with it; the mock verifies them with its public
// half, which stands in for registering the public key at the Fayda partner portal.

/**
 * RSA private key given as base64 JWK, JWK JSON or PEM (the forms the server
 * accepts), as a signer whose public jwk keeps the stored kid. Throws otherwise,
 * for example on the PLACEHOLDER the seed script writes.
 */
function parseClientPrivateKey(value) {
  let text = String(value || '').trim();
  if (!text.startsWith('-----BEGIN') && !text.startsWith('{')) text = Buffer.from(text, 'base64').toString('utf8').trim();
  let privateKey;
  let kid;
  if (text.startsWith('-----BEGIN')) {
    privateKey = crypto.createPrivateKey(text);
  } else if (text.startsWith('{')) {
    const jwk = JSON.parse(text);
    if (jwk.kty !== 'RSA' || !jwk.d) throw new Error('not an RSA private JWK');
    privateKey = crypto.createPrivateKey({ key: jwk, format: 'jwk' });
    kid = jwk.kid;
  } else {
    throw new Error('not a PEM or JWK key');
  }
  const signer = loadSigningKey(privateKey.export({ type: 'pkcs8', format: 'pem' }));
  if (kid) signer.jwk.kid = kid;
  return signer;
}

/** A new RP key in the PRIVATE_KEY_BASE64 form: base64 of the private JWK with kid, use and alg. */
function generateClientKeyValue() {
  const pem = generateSigningKeyPem();
  const { kid } = loadSigningKey(pem).jwk;
  const jwk = crypto.createPrivateKey(pem).export({ format: 'jwk' });
  return Buffer.from(JSON.stringify({ ...jwk, kid, use: 'sig', alg: 'RS256' })).toString('base64');
}

/**
 * Get-or-create of the RP key in a parameter store ({ get, put }). A missing or
 * unusable value (the seeded PLACEHOLDER) is replaced with a new key; a usable
 * one, including a real Fayda key pasted in, is kept. Each call re-reads the
 * parameter; the app caches the result and re-reads only when an assertion
 * fails to verify (see clientJwksFor in app.js).
 */
function clientKeyProvider(paramStore) {
  return async () => {
    const current = await paramStore.get();
    if (current !== null) {
      try {
        return parseClientPrivateKey(current);
      } catch {
        // PLACEHOLDER or garbage: replace below.
      }
    }
    try {
      await paramStore.put(generateClientKeyValue(), current !== null);
    } catch (err) {
      if (err.name !== 'ParameterAlreadyExists') throw err;
    }
    // Re-read so concurrent cold starts settle on whichever write landed last.
    return parseClientPrivateKey(await paramStore.get());
  };
}

/** SSM SecureString adapter for clientKeyProvider. */
function ssmParamStore(paramName, description) {
  const { SSMClient, GetParameterCommand, PutParameterCommand } = require('@aws-sdk/client-ssm');
  const client = new SSMClient({});
  return {
    async get() {
      try {
        const res = await client.send(new GetParameterCommand({ Name: paramName, WithDecryption: true }));
        return res.Parameter.Value;
      } catch (err) {
        if (err.name === 'ParameterNotFound') return null;
        throw err;
      }
    },
    async put(value, overwrite) {
      await client.send(new PutParameterCommand({
        Name: paramName, Value: value, Type: 'SecureString', Overwrite: overwrite, Description: description,
      }));
    },
  };
}

/**
 * File adapter for clientKeyProvider: the local stand-in for the SSM parameter
 * (server-local.js, the server's full-flow test). A missing file reads as null.
 */
function fileParamStore(filePath) {
  const fs = require('node:fs');
  return {
    async get() {
      try {
        return fs.readFileSync(filePath, 'utf8');
      } catch (err) {
        if (err.code === 'ENOENT') return null;
        throw err;
      }
    },
    async put(value, overwrite) {
      try {
        fs.writeFileSync(filePath, value, { flag: overwrite ? 'w' : 'wx', mode: 0o600 });
      } catch (err) {
        if (err.code === 'EEXIST') { const e = new Error('exists'); e.name = 'ParameterAlreadyExists'; throw e; }
        throw err;
      }
    },
  };
}

/** The relying party's public JWKS for private_key_jwt, from a client key provider. */
function clientJwksProvider(getClientKey) {
  return async () => ({ keys: [(await getClientKey()).jwk] });
}

module.exports = {
  memoryKeyProvider, ssmKeyProvider, clientJwksProvider,
  clientKeyProvider, ssmParamStore, fileParamStore, parseClientPrivateKey, generateClientKeyValue,
};
