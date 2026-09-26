'use strict';

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
function ssmKeyProvider(paramName) {
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
        Description: 'Fayda mock OIDC signing key (dev only, auto-generated)',
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

module.exports = { memoryKeyProvider, ssmKeyProvider };
