'use strict';

const crypto = require('node:crypto');

const b64url = (buf) => Buffer.from(buf).toString('base64url');

function randomToken(bytes = 32) {
  return b64url(crypto.randomBytes(bytes));
}

/** RFC 7636 S256: BASE64URL(SHA256(ASCII(code_verifier))). */
function pkceChallenge(verifier) {
  return b64url(crypto.createHash('sha256').update(verifier, 'ascii').digest());
}

function verifyPkce(verifier, challenge) {
  // RFC 7636: verifier is 43 to 128 chars of [A-Z a-z 0-9 - . _ ~].
  if (typeof verifier !== 'string' || !/^[A-Za-z0-9\-._~]{43,128}$/.test(verifier)) return false;
  if (typeof challenge !== 'string') return false;
  const a = Buffer.from(pkceChallenge(verifier));
  const b = Buffer.from(challenge);
  return a.length === b.length && crypto.timingSafeEqual(a, b);
}

/** Stable, non-reversible subject derived from a FIN. */
function subjectForFin(fin) {
  return b64url(crypto.createHash('sha256').update(`fayda-mock:${fin}`).digest()).slice(0, 32);
}

function generateSigningKeyPem() {
  const { privateKey } = crypto.generateKeyPairSync('rsa', { modulusLength: 2048 });
  return privateKey.export({ type: 'pkcs8', format: 'pem' });
}

/** Builds the signer (private key + public JWK with an RFC 7638 thumbprint kid). */
function loadSigningKey(pem) {
  const privateKey = crypto.createPrivateKey(pem);
  const { n, e } = crypto.createPublicKey(privateKey).export({ format: 'jwk' });
  const kid = b64url(crypto.createHash('sha256').update(JSON.stringify({ e, kty: 'RSA', n })).digest());
  return { privateKey, jwk: { kty: 'RSA', kid, use: 'sig', alg: 'RS256', n, e } };
}

function signJwt(payload, key) {
  const header = { alg: 'RS256', typ: 'JWT', kid: key.jwk.kid };
  const input = `${b64url(JSON.stringify(header))}.${b64url(JSON.stringify(payload))}`;
  const sig = crypto.sign('RSA-SHA256', Buffer.from(input), key.privateKey);
  return `${input}.${b64url(sig)}`;
}

/**
 * Verifies an RS256 JWT against a JWKS ({ keys: [...] }). Returns the payload
 * or throws. Only checks the signature; callers check claims.
 */
function verifyJwt(token, jwks) {
  const parts = String(token).split('.');
  if (parts.length !== 3) throw new Error('malformed jwt');
  const header = JSON.parse(Buffer.from(parts[0], 'base64url').toString());
  if (header.alg !== 'RS256') throw new Error('unsupported alg');
  const keys = (jwks && jwks.keys) || [];
  const jwk = keys.find((k) => !header.kid || k.kid === header.kid);
  if (!jwk) throw new Error('unknown kid');
  const publicKey = crypto.createPublicKey({ key: jwk, format: 'jwk' });
  const ok = crypto.verify('RSA-SHA256', Buffer.from(`${parts[0]}.${parts[1]}`), publicKey, Buffer.from(parts[2], 'base64url'));
  if (!ok) throw new Error('bad signature');
  return JSON.parse(Buffer.from(parts[1], 'base64url').toString());
}

module.exports = {
  randomToken, pkceChallenge, verifyPkce, subjectForFin,
  generateSigningKeyPem, loadSigningKey, signJwt, verifyJwt,
};
