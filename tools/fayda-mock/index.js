'use strict';

// Lambda entry point (Function URL, payload format 2.0).
const { createApp, configFromEnv } = require('./src/app');
const { dynamoStore, memoryStore } = require('./src/store');
const { ssmKeyProvider, memoryKeyProvider } = require('./src/keys');

let handle = null;

function buildHandler(env = process.env) {
  return createApp({
    config: configFromEnv(env),
    store: env.CODES_TABLE ? dynamoStore(env.CODES_TABLE) : memoryStore(),
    getKey: env.SIGNING_KEY_PARAM ? ssmKeyProvider(env.SIGNING_KEY_PARAM) : memoryKeyProvider(),
  });
}

/** Converts a Function URL event into the app's request shape. */
function toRequest(event) {
  const headers = {};
  for (const [k, v] of Object.entries(event.headers || {})) headers[k.toLowerCase()] = v;
  const body = event.body
    ? (event.isBase64Encoded ? Buffer.from(event.body, 'base64').toString('utf8') : event.body)
    : '';
  const host = (event.requestContext && event.requestContext.domainName) || headers.host;
  return {
    method: event.requestContext.http.method,
    path: event.rawPath || '/',
    query: new URLSearchParams(event.rawQueryString || ''),
    headers,
    body,
    origin: `https://${host}`,
  };
}

exports.handler = async (event) => {
  if (!handle) handle = buildHandler();
  return handle(toRequest(event));
};

exports.toRequest = toRequest;
exports.buildHandler = buildHandler;
