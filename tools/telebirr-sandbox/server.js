"use strict";
/**
 * Local Telebirr Sandbox Simulator
 * ================================
 * A zero-dependency HTTP service that plays the role of Telebirr's H5/C2B
 * payment gateway, so the whole escrow flow can be exercised end-to-end with NO
 * real Telebirr merchant account and NO internet.
 *
 * It speaks the REAL Telebirr protocol (see telebirr-crypto.js), so the same
 * `TelebirrSandboxGateway` server code works against this simulator AND the live
 * developerportal.ethiotelebirr.et sandbox — only config differs.
 *
 * Endpoints (mirror real Telebirr):
 *   POST /apply/fabric-token             -> { token }
 *   POST /payment/v1/merchant/preOrder   -> verifies our sig, returns { prepay_id }
 *   GET  /payment/web/paygate?...        -> HTML "pay page" with Pay / Fail buttons
 *   POST /simulate/complete              -> fires an RSA-signed webhook to notify_url
 *   GET  /health, GET /                  -> status / help
 *
 * On first run it generates two RSA keypairs (merchant + "Telebirr") into
 * ./keys and writes ./credentials.local.json for the .NET gateway to load.
 *
 * Usage:  node server.js            (PORT defaults to 8790)
 */
const http = require("http");
const fs = require("fs");
const path = require("path");
const crypto = require("crypto");
const C = require("./telebirr-crypto");

const PORT = parseInt(process.env.PORT || "8790", 10);
const HOST = process.env.HOST || "127.0.0.1";
const PUBLIC_BASE =
  process.env.TELEBIRR_SIM_PUBLIC_BASE || `http://${HOST}:${PORT}`;
const KEYS_DIR = path.join(__dirname, "keys");
const CREDS_FILE = path.join(__dirname, "credentials.local.json");

// Fixed sandbox merchant identity (arbitrary but stable for local dev).
const MERCHANT = {
  fabricAppId: "local-fabric-app-id",
  appSecret: "local-app-secret",
  merchantAppId: "local-merchant-app-id",
  merchantCode: "220100",
  shortCode: "220100",
};

// ── Key + credential bootstrap ─────────────────────────────────────────────

function loadOrCreateKeys() {
  const files = {
    merchantPrivate: path.join(KEYS_DIR, "merchant_private.pem"),
    merchantPublic: path.join(KEYS_DIR, "merchant_public.pem"),
    telebirrPrivate: path.join(KEYS_DIR, "telebirr_private.pem"),
    telebirrPublic: path.join(KEYS_DIR, "telebirr_public.pem"),
  };

  if (!Object.values(files).every((f) => fs.existsSync(f))) {
    fs.mkdirSync(KEYS_DIR, { recursive: true });
    const merchant = C.generateKeyPair();
    const telebirr = C.generateKeyPair();
    fs.writeFileSync(files.merchantPrivate, merchant.privateKey);
    fs.writeFileSync(files.merchantPublic, merchant.publicKey);
    fs.writeFileSync(files.telebirrPrivate, telebirr.privateKey);
    fs.writeFileSync(files.telebirrPublic, telebirr.publicKey);
    console.log("[sim] Generated fresh RSA keypairs in ./keys");
  }

  const keys = Object.fromEntries(
    Object.entries(files).map(([k, f]) => [k, fs.readFileSync(f, "utf8")]),
  );

  // Credentials the .NET gateway consumes (matches TelebirrConfig shape).
  const creds = {
    fabricAppId: MERCHANT.fabricAppId,
    appSecret: MERCHANT.appSecret,
    merchantAppId: MERCHANT.merchantAppId,
    merchantCode: MERCHANT.merchantCode,
    shortCode: MERCHANT.shortCode,
    baseUrl: PUBLIC_BASE,
    webUrl: `${PUBLIC_BASE}/payment/web/paygate`,
    merchantPrivateKeyPem: keys.merchantPrivate,
    telebirrPublicKeyPem: keys.telebirrPublic,
    signPadding: process.env.TELEBIRR_SIGN_PADDING || "pss",
    returnUrl: process.env.TELEBIRR_RETURN_URL || "weredaconnect://payment/complete",
  };
  fs.writeFileSync(CREDS_FILE, JSON.stringify(creds, null, 2));
  return keys;
}

const KEYS = loadOrCreateKeys();

// ── In-memory order store ──────────────────────────────────────────────────

/** prepay_id -> { merchOrderId, amount, currency, notifyUrl, redirectUrl, title, status } */
const orders = new Map();
const fabricTokens = new Set();

// ── HTTP helpers ───────────────────────────────────────────────────────────

function readBody(req) {
  return new Promise((resolve) => {
    let data = "";
    req.on("data", (c) => (data += c));
    req.on("end", () => resolve(data));
  });
}

function json(res, status, obj) {
  const body = JSON.stringify(obj);
  res.writeHead(status, { "Content-Type": "application/json" });
  res.end(body);
}

function html(res, status, body) {
  res.writeHead(status, { "Content-Type": "text/html; charset=utf-8" });
  res.end(body);
}

function nonce() {
  return crypto.randomBytes(16).toString("hex");
}

function nowMs() {
  return Date.now().toString();
}

// ── Webhook delivery ───────────────────────────────────────────────────────

async function fireWebhook(order, success) {
  const notification = {
    method: "payment.notify",
    nonce_str: nonce(),
    timestamp: nowMs(),
    notify_time: new Date().toISOString(),
    sign_type: "SHA256WithRSA",
    biz_content: {
      merch_order_id: order.merchOrderId,
      payment_order_id: `TB${crypto.randomBytes(6).toString("hex").toUpperCase()}`,
      total_amount: order.amount,
      trans_currency: order.currency,
      trade_status: success ? "Completed" : "Failed",
      merch_code: MERCHANT.merchantCode,
      trans_id: `TXN${crypto.randomBytes(6).toString("hex").toUpperCase()}`,
    },
  };
  notification.sign = C.signObject(notification, KEYS.telebirrPrivate);

  const url = new URL(order.notifyUrl);
  const payload = JSON.stringify(notification);
  const lib = url.protocol === "https:" ? require("https") : http;

  return new Promise((resolve) => {
    const req = lib.request(
      url,
      {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
          "Content-Length": Buffer.byteLength(payload),
        },
        timeout: 8000,
      },
      (r) => {
        let out = "";
        r.on("data", (c) => (out += c));
        r.on("end", () => {
          console.log(
            `[sim] webhook -> ${order.notifyUrl} : ${r.statusCode} (${order.merchOrderId}, ${success ? "Completed" : "Failed"})`,
          );
          resolve({ status: r.statusCode, body: out });
        });
      },
    );
    req.on("error", (e) => {
      console.log(`[sim] webhook delivery FAILED to ${order.notifyUrl}: ${e.message}`);
      resolve({ status: 0, body: e.message });
    });
    req.on("timeout", () => {
      req.destroy();
      resolve({ status: 0, body: "timeout" });
    });
    req.write(payload);
    req.end();
  });
}

// ── Routes ─────────────────────────────────────────────────────────────────

async function handle(req, res) {
  const url = new URL(req.url, PUBLIC_BASE);
  const route = `${req.method} ${url.pathname}`;

  // POST /apply/fabric-token
  if (route === "POST /apply/fabric-token") {
    const appKey = req.headers["x-app-key"];
    if (appKey !== MERCHANT.fabricAppId) {
      return json(res, 401, { code: "40001", msg: "invalid X-APP-Key" });
    }
    const token = `FABRIC-${nonce()}`;
    fabricTokens.add(token);
    return json(res, 200, { token, expires_in: 7200 });
  }

  // POST /payment/v1/merchant/preOrder
  if (route === "POST /payment/v1/merchant/preOrder") {
    const auth = req.headers["authorization"];
    if (!auth || !fabricTokens.has(auth)) {
      return json(res, 401, { result: "FAIL", code: "40002", msg: "missing/invalid fabric token" });
    }
    const raw = await readBody(req);
    let body;
    try {
      body = JSON.parse(raw);
    } catch {
      return json(res, 400, { result: "FAIL", code: "40003", msg: "bad json" });
    }

    // Verify OUR request signature with the merchant public key.
    const okSig = C.verifyObject(body, body.sign, KEYS.merchantPublic);
    if (!okSig) {
      return json(res, 200, {
        result: "FAIL",
        code: "60200099",
        msg: "Verify the sign field failed",
      });
    }

    const biz = body.biz_content || {};
    const prepayId = `PREPAY-${nonce()}`;
    orders.set(prepayId, {
      merchOrderId: biz.merch_order_id,
      amount: biz.total_amount,
      currency: biz.trans_currency || "ETB",
      notifyUrl: biz.notify_url,
      redirectUrl: biz.redirect_url,
      title: biz.title || "WeredaConnect payment",
      status: "pending",
    });

    const response = {
      result: "SUCCESS",
      code: "0",
      msg: "success",
      nonce_str: nonce(),
      sign_type: "SHA256WithRSA",
      biz_content: { prepay_id: prepayId, merch_order_id: biz.merch_order_id },
    };
    response.sign = C.signObject(response, KEYS.telebirrPrivate);
    return json(res, 200, response);
  }

  // GET /payment/web/paygate?appid=..&prepay_id=..&sign=..
  if (req.method === "GET" && url.pathname === "/payment/web/paygate") {
    const q = Object.fromEntries(url.searchParams.entries());
    const prepayId = q.prepay_id;
    const order = prepayId && orders.get(prepayId);
    if (!order) {
      return html(res, 404, `<h2>Unknown or expired prepay_id</h2>`);
    }
    // Verify the 5-field H5 signature with the merchant public key.
    const h5Fields = {
      appid: q.appid,
      merch_code: q.merch_code,
      nonce_str: q.nonce_str,
      prepay_id: q.prepay_id,
      timestamp: q.timestamp,
    };
    const okSig = C.verifyObject(h5Fields, q.sign, KEYS.merchantPublic);
    return html(res, 200, payPage(prepayId, order, okSig));
  }

  // POST /simulate/complete?prepay_id=..&result=success|fail
  if (req.method === "POST" && url.pathname === "/simulate/complete") {
    const q = Object.fromEntries(url.searchParams.entries());
    const order = q.prepay_id && orders.get(q.prepay_id);
    if (!order) return json(res, 404, { ok: false, msg: "unknown prepay_id" });
    if (order.status !== "pending") {
      return json(res, 409, { ok: false, msg: `already ${order.status}` });
    }
    const success = q.result !== "fail";
    order.status = success ? "completed" : "failed";
    const delivery = await fireWebhook(order, success);
    return json(res, 200, {
      ok: true,
      result: order.status,
      webhookStatus: delivery.status,
      redirectUrl: order.redirectUrl,
    });
  }

  if (route === "GET /health") {
    return json(res, 200, { status: "ok", orders: orders.size });
  }

  if (route === "GET /") {
    return html(res, 200, helpPage());
  }

  return json(res, 404, { code: "404", msg: `no route for ${route}` });
}

// ── HTML ───────────────────────────────────────────────────────────────────

function payPage(prepayId, order, okSig) {
  const warn = okSig
    ? ""
    : `<p style="color:#b00">⚠ merchant signature did NOT verify (error 60200099 in production)</p>`;
  return `<!doctype html><html><head><meta charset="utf-8">
<title>Telebirr Sandbox — Pay</title>
<style>body{font-family:system-ui;max-width:520px;margin:40px auto;padding:0 16px}
.card{border:1px solid #ddd;border-radius:12px;padding:24px}
.amt{font-size:34px;font-weight:700}.muted{color:#666}
button{font-size:16px;padding:12px 20px;border-radius:8px;border:0;cursor:pointer;margin-right:8px}
.pay{background:#0a7d3b;color:#fff}.fail{background:#eee}
#out{margin-top:16px;white-space:pre-wrap}</style></head>
<body><div class="card">
<div class="muted">Telebirr Sandbox (local simulator)</div>
<h2>${escapeHtml(order.title)}</h2>
<div class="amt">${escapeHtml(order.amount)} ${escapeHtml(order.currency)}</div>
<p class="muted">Order: ${escapeHtml(order.merchOrderId || "?")}<br>prepay_id: ${escapeHtml(prepayId)}</p>
${warn}
<button class="pay" onclick="done('success')">Pay ✅</button>
<button class="fail" onclick="done('fail')">Cancel / Fail ❌</button>
<div id="out"></div></div>
<script>
async function done(result){
  const out=document.getElementById('out');
  out.textContent='Processing...';
  const r=await fetch('/simulate/complete?prepay_id=${encodeURIComponent(prepayId)}&result='+result,{method:'POST'});
  const j=await r.json();
  out.textContent='Result: '+j.result+'\\nWebhook delivered (HTTP '+j.webhookStatus+') to your server.'+
    (j.redirectUrl?('\\nApp redirect: '+j.redirectUrl):'');
}
</script></body></html>`;
}

function helpPage() {
  return `<!doctype html><html><head><meta charset="utf-8"><title>Telebirr Sandbox</title>
<style>body{font-family:system-ui;max-width:640px;margin:40px auto;padding:0 16px}code{background:#f4f4f4;padding:2px 6px;border-radius:4px}</style></head>
<body><h1>Telebirr Sandbox Simulator</h1>
<p>Playing the role of Telebirr's H5/C2B gateway at <code>${PUBLIC_BASE}</code>.</p>
<ul>
<li><code>POST /apply/fabric-token</code></li>
<li><code>POST /payment/v1/merchant/preOrder</code></li>
<li><code>GET  /payment/web/paygate</code> — the buyer pay page</li>
<li><code>POST /simulate/complete</code> — fires the signed webhook</li>
</ul>
<p>Credentials for the .NET gateway were written to <code>credentials.local.json</code>.</p>
</body></html>`;
}

function escapeHtml(s) {
  return String(s == null ? "" : s).replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c],
  );
}

// ── Boot ───────────────────────────────────────────────────────────────────

http
  .createServer((req, res) =>
    handle(req, res).catch((e) => {
      console.error("[sim] error:", e);
      json(res, 500, { code: "500", msg: String(e && e.message) });
    }),
  )
  .listen(PORT, HOST, () => {
    console.log(`[sim] Telebirr sandbox listening on ${PUBLIC_BASE}`);
    console.log(`[sim] credentials -> ${CREDS_FILE}`);
    console.log(`[sim] sign padding: ${process.env.TELEBIRR_SIGN_PADDING || "pss"}`);
  });
