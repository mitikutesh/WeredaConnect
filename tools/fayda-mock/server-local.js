'use strict';

// Local dev server wrapping the same app as the Lambda. In-memory codes and a
// throwaway signing key (regenerated on every start). private_key_jwt needs the
// RP key: CLIENT_KEY_FILE (a file standing in for the SSM parameter, created on
// first use in the same base64 JWK form) or CLIENT_JWKS_JSON (a public JWKS).
//   node server-local.js            -> http://localhost:8788
//   PORT=9000 node server-local.js
const http = require('node:http');
const { buildHandler } = require('./index');

const port = Number(process.env.PORT || 8788);
const env = { ...process.env };
delete env.CODES_TABLE;
delete env.SIGNING_KEY_PARAM;
delete env.CLIENT_KEY_PARAM;
env.ISSUER_URL_OVERRIDE = env.ISSUER_URL_OVERRIDE || `http://localhost:${port}`;
const handle = buildHandler(env);

http.createServer((req, res) => {
  const chunks = [];
  req.on('data', (c) => chunks.push(c));
  req.on('end', async () => {
    const url = new URL(req.url, `http://${req.headers.host}`);
    const out = await handle({
      method: req.method,
      path: url.pathname,
      query: url.searchParams,
      headers: req.headers,
      body: Buffer.concat(chunks).toString('utf8'),
      origin: `http://${req.headers.host}`,
    });
    res.writeHead(out.statusCode, out.headers);
    res.end(out.body);
  });
}).listen(port, () => console.log(`fayda-mock listening on ${env.ISSUER_URL_OVERRIDE}`));
