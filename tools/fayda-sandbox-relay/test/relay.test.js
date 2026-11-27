'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');
const { buildRedirect, createRelay, parseArgs, DEFAULT_TARGET } = require('../relay');

test('buildRedirect keeps code and state on the deep link and drops anything else', () => {
  const url = buildRedirect(DEFAULT_TARGET, new URLSearchParams('code=abc&state=xyz&foo=bar'));
  assert.equal(url, 'weredaconnect://fayda-callback?code=abc&state=xyz');
});

test('buildRedirect forwards provider errors to a web callback page', () => {
  const url = new URL(buildRedirect('http://localhost:8080/fayda-callback.html',
    new URLSearchParams('error=consent_request_rejected&error_description=User%20cancelled&state=s1')));
  assert.equal(url.origin + url.pathname, 'http://localhost:8080/fayda-callback.html');
  assert.equal(url.searchParams.get('error'), 'consent_request_rejected');
  assert.equal(url.searchParams.get('error_description'), 'User cancelled');
  assert.equal(url.searchParams.get('state'), 's1');
  assert.equal(url.searchParams.has('code'), false);
});

async function withRelay(opts, fn) {
  const server = createRelay(opts);
  await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
  const base = `http://127.0.0.1:${server.address().port}`;
  try {
    await fn(base);
  } finally {
    await new Promise((resolve) => server.close(resolve));
  }
}

function get(url) {
  return new Promise((resolve, reject) => {
    http.get(url, (res) => {
      let body = '';
      res.on('data', (chunk) => { body += chunk; });
      res.on('end', () => resolve({ status: res.statusCode, headers: res.headers, body }));
    }).on('error', reject);
  });
}

test('GET /callback answers 302 to the target with the query, no caching, no referrer', async () => {
  await withRelay({}, async (base) => {
    const res = await get(`${base}/callback?code=c1&state=s1`);
    assert.equal(res.status, 302);
    assert.equal(res.headers.location, 'weredaconnect://fayda-callback?code=c1&state=s1');
    assert.equal(res.headers['cache-control'], 'no-store');
    assert.equal(res.headers['referrer-policy'], 'no-referrer');
    assert.match(res.body, /Open the app/);
  });
});

test('a web target is relayed the same way', async () => {
  await withRelay({ target: 'http://localhost:8080/fayda-callback.html' }, async (base) => {
    const res = await get(`${base}/callback?code=c1&state=s1`);
    assert.equal(res.headers.location, 'http://localhost:8080/fayda-callback.html?code=c1&state=s1');
  });
});

test('other paths are 404 and a callback without state is 400', async () => {
  await withRelay({}, async (base) => {
    assert.equal((await get(`${base}/other?code=c1&state=s1`)).status, 404);
    assert.equal((await get(`${base}/callback?code=c1`)).status, 400);
  });
});

test('the code and state are never logged', async () => {
  let logged = '';
  let logCount = 0;
  await withRelay({ log: (line) => { logged = line; logCount += 1; } }, async (base) => {
    await get(`${base}/callback?code=secret-code&state=state-77`);
  });
  assert.equal(logCount, 1);
  assert.doesNotMatch(logged, /secret-code|state-77/);
});

test('an unusable target is refused at start-up', () => {
  assert.throws(() => createRelay({ target: 'not a url' }));
});

test('parseArgs reads --port and --target and refuses unknown flags', () => {
  assert.deepEqual(
    parseArgs(['--port', '3001', '--target', 'https://dev-app.weredaconnect.com/fayda-callback.html']),
    { port: 3001, target: 'https://dev-app.weredaconnect.com/fayda-callback.html' },
  );
  assert.throws(() => parseArgs(['--bogus']));
});

test('only GET is relayed', async () => {
  await withRelay({}, async (base) => {
    const status = await new Promise((resolve, reject) => {
      const req = http.request(`${base}/callback?code=c1&state=s1`, { method: 'POST' }, (res) => {
        res.resume();
        res.on('end', () => resolve(res.statusCode));
      });
      req.on('error', reject);
      req.end();
    });
    assert.equal(status, 404);
  });
});

test('a target query parameter cannot steer the redirect', async () => {
  await withRelay({}, async (base) => {
    const res = await get(`${base}/callback?code=c1&state=s1&target=https%3A%2F%2Fevil.example%2F&redirect_uri=https%3A%2F%2Fevil.example%2F`);
    assert.equal(res.headers.location, 'weredaconnect://fayda-callback?code=c1&state=s1');
  });
});
