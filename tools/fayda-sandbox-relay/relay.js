#!/usr/bin/env node
'use strict';

// Fayda sandbox callback relay (dev only).
//
// The shared VeriFayda sandbox test client has one registered redirect,
// http://localhost:3000/callback, so eSignet can only send the authorization code
// there. This server listens on that address on the developer's machine and answers
// with a redirect to the app's own Fayda callback, query string intact: the mobile
// deep link by default, or a web app callback page.
//
//   node relay.js                                                  -> weredaconnect://fayda-callback
//   node relay.js --target http://localhost:8080/fayda-callback.html
//   PORT=3000 TARGET=... node relay.js
//
// Android (emulator or USB device): `adb reverse tcp:3000 tcp:3000` makes the device's
// localhost:3000 reach this relay. The iOS simulator and desktop browsers reach it as is.
// The code and state are never logged. Runbook: specs/features/fayda-mock-server.md.

const http = require('node:http');

const DEFAULT_PORT = 3000;
const DEFAULT_PATH = '/callback';
const DEFAULT_TARGET = 'weredaconnect://fayda-callback';
// The OIDC callback parameters (OIDC Core 3.1.2.5 and 3.1.2.6); anything else is dropped.
const FORWARDED = ['code', 'state', 'error', 'error_description'];

/** The app callback URL for a sandbox callback: `target` plus the forwarded parameters. */
function buildRedirect(target, searchParams) {
  const out = new URL(target);
  for (const name of FORWARDED) {
    const value = searchParams.get(name);
    if (value !== null) out.searchParams.set(name, value);
  }
  return out.toString();
}

function escapeHtml(text) {
  return String(text).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

/** An http.Server that relays GET `path` to `target`; everything else is 404. */
function createRelay({ target = DEFAULT_TARGET, path = DEFAULT_PATH, log = () => {} } = {}) {
  new URL(target); // fail fast on an unusable target
  return http.createServer((req, res) => {
    const url = new URL(req.url, 'http://localhost');
    if (req.method !== 'GET' || url.pathname !== path) {
      res.writeHead(404, { 'content-type': 'text/plain; charset=utf-8', 'cache-control': 'no-store' });
      res.end('not found');
      return;
    }
    if (!url.searchParams.get('state')) {
      res.writeHead(400, { 'content-type': 'text/plain; charset=utf-8', 'cache-control': 'no-store' });
      res.end('missing state');
      return;
    }
    const location = buildRedirect(target, url.searchParams);
    const outcome = url.searchParams.has('code') ? 'code' : `error=${String(url.searchParams.get('error')).slice(0, 40)}`;
    log(`relayed ${outcome} to ${target}`);
    res.writeHead(302, {
      location,
      'cache-control': 'no-store',
      'referrer-policy': 'no-referrer',
      'content-type': 'text/html; charset=utf-8',
    });
    res.end(
      '<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="referrer" content="no-referrer">'
      + '<title>WeredaConnect</title></head><body><p>Returning to WeredaConnect... '
      + `<a href="${escapeHtml(location)}">Open the app</a> if nothing happens.</p></body></html>`,
    );
  });
}

function parseArgs(argv) {
  const opts = { port: Number(process.env.PORT || DEFAULT_PORT), target: process.env.TARGET || DEFAULT_TARGET };
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === '--port') opts.port = Number(argv[++i]);
    else if (argv[i] === '--target') opts.target = argv[++i];
    else throw new Error(`unknown argument ${argv[i]}`);
  }
  return opts;
}

if (require.main === module) {
  const { port, target } = parseArgs(process.argv.slice(2));
  createRelay({ target, log: (line) => console.log(`[fayda-sandbox-relay] ${line}`) })
    .listen(port, '127.0.0.1', () => console.log(`[fayda-sandbox-relay] http://localhost:${port}${DEFAULT_PATH} -> ${target}`));
}

module.exports = { buildRedirect, createRelay, parseArgs, DEFAULT_TARGET, DEFAULT_PORT, DEFAULT_PATH };
