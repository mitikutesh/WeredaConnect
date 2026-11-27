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
 *   POST /payment/v1/token               -> { token, effectiveDate, expirationDate }
 *                                           (/apply/fabric-token kept as an alias)
 *   POST /payment/v1/merchant/preOrder   -> verifies our sig, returns { prepay_id }
 *   POST /payment/v1/merchant/queryOrder -> verifies our sig, answers the order's state from this
 *                                           store (FUP-430): WAIT_PAY, PAY_SUCCESS, PAY_FAILED or
 *                                           ORDER_CLOSED; a refusal for an order it never created
 *   GET  /payment/web/paygate?...        -> HTML "pay page" with Pay / Fail buttons
 *   POST /simulate/complete              -> fires an RSA-signed webhook to notify_url, in the
 *                                           documented flat shape (ESC-45): notify_time in ms,
 *                                           the transaction id signed as trans_id but sent as
 *                                           transId (as live Telebirr has been seen to do; set
 *                                           TELEBIRR_SIM_TRANS_ID_FIELD=trans_id for the doc sample).
 *                                           &notify=0 records the payment without a webhook.
 *   POST /simulate/expire                -> closes a pending checkout's window now (FUP-430)
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

/**
 * prepay_id -> { merchOrderId, amount, currency, notifyUrl, redirectUrl, title, appId, callbackInfo,
 *   status ("pending" | "completed" | "failed"), closesAt (epoch ms, end of the checkout window),
 *   paymentOrderId, transId, transEndTime (set once the payment is simulated) }
 */
const orders = new Map();
const fabricTokens = new Set();

// FUP-430: queryOrder's order_status for each state of a stored order.
const QUERY_STATUS = {
  pending: "WAIT_PAY",
  completed: "PAY_SUCCESS",
  failed: "PAY_FAILED",
  closed: "ORDER_CLOSED",
};

// FUP-430: the refusal for a merch_order_id this simulator never created. Telebirr's real code is
// undocumented (FUP-429 records it), so this is a placeholder the server reads as unknown, never
// as "not paid".
const UNKNOWN_ORDER_CODE = "SIM_ORDER_NOT_FOUND";

/** The checkout window of a preOrder's timeout_express ("120m"; 1 to 120 minutes, 120 by default). */
function checkoutWindowMs(timeoutExpress) {
  const m = /^(\d+)m$/.exec(String(timeoutExpress || ""));
  const minutes = m ? Math.min(Math.max(parseInt(m[1], 10), 1), 120) : 120;
  return minutes * 60 * 1000;
}

/** "completed", "failed", "closed" (still unpaid when its window ended) or "pending". */
function orderState(order) {
  if (order.status === "pending" && Date.now() >= order.closesAt) return "closed";
  return order.status;
}

/** The latest preOrder stored under this merch_order_id, or null. */
function findByMerchOrderId(merchOrderId) {
  let found = null;
  for (const order of orders.values()) {
    if (order.merchOrderId === merchOrderId) found = order;
  }
  return found;
}

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
  // Ethio Telecom's documented notify (Notify_Callback, "Step 7"): one flat object.
  const signed = {
    notify_url: order.notifyUrl,
    appid: order.appId || MERCHANT.merchantAppId,
    notify_time: nowMs(),
    merch_code: MERCHANT.merchantCode,
    merch_order_id: order.merchOrderId,
    // The ids queryOrder answers for the same payment (FUP-430).
    payment_order_id: order.paymentOrderId,
    total_amount: order.amount,
    trans_id: order.transId,
    trans_currency: order.currency,
    trade_status: success ? "Completed" : "Failure",
    trans_end_time: order.transEndTime,
  };
  if (order.callbackInfo) signed.callback_info = order.callbackInfo;
  const sign = C.signObject(signed, KEYS.telebirrPrivate);

  // Signed as trans_id, sent as transId unless told otherwise.
  const transIdField = process.env.TELEBIRR_SIM_TRANS_ID_FIELD || "transId";
  const notification = { ...signed };
  if (transIdField !== "trans_id") {
    notification[transIdField] = notification.trans_id;
    delete notification.trans_id;
  }
  notification.sign = sign;
  notification.sign_type = "SHA256WithRSA";

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
            `[sim] webhook -> ${order.notifyUrl} : ${r.statusCode} (${order.merchOrderId}, ${success ? "Completed" : "Failure"})`,
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

  // POST /payment/v1/token (documented path; /apply/fabric-token is the old simulator alias)
  if (route === "POST /payment/v1/token" || route === "POST /apply/fabric-token") {
    const appKey = req.headers["x-app-key"];
    if (appKey !== MERCHANT.fabricAppId) {
      return json(res, 401, { code: "40001", msg: "invalid X-APP-Key" });
    }
    const token = `Bearer ${nonce()}`;
    fabricTokens.add(token);
    const stamp = (d) => d.toISOString().replace(/[-:T]/g, "").slice(0, 14);
    const now = new Date();
    return json(res, 200, {
      token,
      effectiveDate: stamp(now),
      expirationDate: stamp(new Date(now.getTime() + 3600 * 1000)),
      expires_in: 3600,
    });
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
      appId: biz.appid,
      callbackInfo: biz.callback_info,
      status: "pending",
      closesAt: Date.now() + checkoutWindowMs(biz.timeout_express),
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

  // POST /payment/v1/merchant/queryOrder (FUP-430; Ethio Telecom's "Step 5: queryOrder", ESC-45 F5)
  if (route === "POST /payment/v1/merchant/queryOrder") {
    const auth = req.headers["authorization"];
    if (!auth || !fabricTokens.has(auth)) {
      return json(res, 401, { result: "FAIL", code: "40002", msg: "missing/invalid fabric token" });
    }
    let body;
    try {
      body = JSON.parse(await readBody(req));
    } catch {
      body = null;
    }
    if (!body || typeof body !== "object") {
      return json(res, 400, { result: "FAIL", code: "40003", msg: "bad json" });
    }

    // Verify OUR request signature (method payment.queryorder; biz_content appid, merch_code,
    // merch_order_id) with the merchant public key.
    if (!C.verifyObject(body, body.sign, KEYS.merchantPublic)) {
      return json(res, 200, {
        result: "FAIL",
        code: "60200099",
        msg: "Verify the sign field failed",
      });
    }

    const merchOrderId = (body.biz_content || {}).merch_order_id;
    const order = merchOrderId ? findByMerchOrderId(merchOrderId) : null;
    if (!order) {
      return json(res, 200, { result: "FAIL", code: UNKNOWN_ORDER_CODE, msg: "order does not exist" });
    }

    // The documented answer (order_status, as the sample and a live sandbox run show it). The
    // payment fields come with a simulated payment, the same ids its notify carried.
    const biz = {
      merch_order_id: order.merchOrderId,
      order_status: QUERY_STATUS[orderState(order)],
      total_amount: order.amount,
      trans_currency: order.currency,
    };
    if (order.paymentOrderId) {
      biz.payment_order_id = order.paymentOrderId;
      biz.trans_id = order.transId;
      biz.trans_time = order.transEndTime;
    }
    const response = {
      result: "SUCCESS",
      code: "0",
      msg: "success",
      nonce_str: nonce(),
      sign_type: "SHA256WithRSA",
      biz_content: biz,
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

  // POST /simulate/complete?prepay_id=..&result=success|fail[&notify=0]
  if (req.method === "POST" && url.pathname === "/simulate/complete") {
    const q = Object.fromEntries(url.searchParams.entries());
    const order = q.prepay_id && orders.get(q.prepay_id);
    if (!order) return json(res, 404, { ok: false, msg: "unknown prepay_id" });
    const state = orderState(order);
    if (state !== "pending") {
      return json(res, 409, { ok: false, result: state, msg: `already ${state}` });
    }
    const success = q.result !== "fail";
    order.status = success ? "completed" : "failed";
    order.paymentOrderId = `TB${crypto.randomBytes(6).toString("hex").toUpperCase()}`;
    order.transId = `TXN${crypto.randomBytes(6).toString("hex").toUpperCase()}`;
    order.transEndTime = nowMs();
    // notify=0 (FUP-430): a payment whose notify never arrives, so only queryOrder can tell.
    const notify = q.notify !== "0";
    const delivery = notify ? await fireWebhook(order, success) : null;
    return json(res, 200, {
      ok: true,
      result: order.status,
      webhookStatus: delivery ? delivery.status : null,
      redirectUrl: order.redirectUrl,
    });
  }

  // POST /simulate/expire?prepay_id=.. (FUP-430): end a pending checkout's window now, as if its
  // timeout_express had passed; queryOrder then answers ORDER_CLOSED and the checkout cannot be paid.
  // No notify is sent (whether Telebirr sends Expired is undocumented).
  if (req.method === "POST" && url.pathname === "/simulate/expire") {
    const q = Object.fromEntries(url.searchParams.entries());
    const order = q.prepay_id && orders.get(q.prepay_id);
    if (!order) return json(res, 404, { ok: false, msg: "unknown prepay_id" });
    const state = orderState(order);
    if (state !== "pending") {
      return json(res, 409, { ok: false, result: state, msg: `already ${state}` });
    }
    order.closesAt = Date.now();
    return json(res, 200, { ok: true, result: "closed" });
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
  if(!j.ok){out.textContent='Not paid: '+j.msg;return;}
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
<li><code>POST /payment/v1/token</code> (alias <code>/apply/fabric-token</code>)</li>
<li><code>POST /payment/v1/merchant/preOrder</code></li>
<li><code>POST /payment/v1/merchant/queryOrder</code>: the order's state (WAIT_PAY, PAY_SUCCESS, PAY_FAILED, ORDER_CLOSED)</li>
<li><code>GET  /payment/web/paygate</code> — the buyer pay page</li>
<li><code>POST /simulate/complete</code>: fires the signed webhook (<code>&amp;notify=0</code>: no webhook)</li>
<li><code>POST /simulate/expire</code>: closes a pending checkout's window now</li>
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
