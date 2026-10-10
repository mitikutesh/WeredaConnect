"use strict";
/**
 * End-to-end self-test for the local Telebirr sandbox simulator.
 * Acts as the "merchant" (our server) and drives the full H5 flow:
 *   fabric-token -> preOrder (signed) -> paygate -> simulate/complete -> webhook.
 * Verifies the webhook signature with Telebirr's public key.
 *
 * Requires the simulator to be running (node server.js). Exits non-zero on failure.
 */
const http = require("http");
const fs = require("fs");
const path = require("path");
const crypto = require("crypto");
const C = require("./telebirr-crypto");

const SIM = process.env.TELEBIRR_SIM_URL || "http://127.0.0.1:8790";
const creds = JSON.parse(
  fs.readFileSync(path.join(__dirname, "credentials.local.json"), "utf8"),
);

let received = null;

function req(method, urlStr, { headers = {}, body } = {}) {
  const url = new URL(urlStr);
  return new Promise((resolve, reject) => {
    const r = http.request(
      url,
      { method, headers: { "Content-Type": "application/json", ...headers } },
      (res) => {
        let data = "";
        res.on("data", (c) => (data += c));
        res.on("end", () => resolve({ status: res.statusCode, body: data }));
      },
    );
    r.on("error", reject);
    if (body) r.write(body);
    r.end();
  });
}

function assert(cond, msg) {
  if (!cond) {
    console.error("FAIL:", msg);
    process.exit(1);
  }
  console.log("  ok:", msg);
}

async function main() {
  // 1. Start a webhook receiver.
  const receiver = http.createServer((rq, rs) => {
    let d = "";
    rq.on("data", (c) => (d += c));
    rq.on("end", () => {
      received = JSON.parse(d);
      rs.writeHead(200);
      rs.end("");
    });
  });
  await new Promise((r) => receiver.listen(0, "127.0.0.1", r));
  const notifyUrl = `http://127.0.0.1:${receiver.address().port}/webhook`;

  // 2. Fabric token (documented path).
  const tok = await req("POST", `${SIM}/payment/v1/token`, {
    headers: { "X-APP-Key": creds.fabricAppId },
    body: JSON.stringify({ appSecret: creds.appSecret }),
  });
  assert(tok.status === 200, "fabric-token 200");
  const token = JSON.parse(tok.body).token;
  assert(!!token, "fabric-token returned token");

  // 3. preOrder (signed with merchant private key).
  const nonce = crypto.randomBytes(12).toString("hex");
  // Request timestamps are epoch seconds; a merch_order_id is letters and digits only.
  const timestamp = Math.floor(Date.now() / 1000).toString();
  const merchOrderId = `WCOSELFTEST${timestamp}`;
  const body = {
    nonce_str: nonce,
    method: "payment.preorder",
    timestamp,
    version: "1.0",
    biz_content: {
      notify_url: notifyUrl,
      appid: creds.merchantAppId,
      merch_code: creds.merchantCode,
      merch_order_id: merchOrderId,
      trade_type: "Checkout",
      title: "WeredaConnect escrow test",
      total_amount: "15000.00",
      trans_currency: "ETB",
      timeout_express: "120m",
      business_type: "BuyGoods",
      callback_info: "From web",
      redirect_url: creds.returnUrl,
    },
    sign_type: "SHA256WithRSA",
  };
  body.sign = C.signObject(body, creds.merchantPrivateKeyPem);
  const pre = await req("POST", `${SIM}/payment/v1/merchant/preOrder`, {
    headers: { Authorization: token, "X-APP-Key": creds.fabricAppId },
    body: JSON.stringify(body),
  });
  assert(pre.status === 200, "preOrder 200");
  const preJson = JSON.parse(pre.body);
  assert(preJson.result === "SUCCESS", `preOrder SUCCESS (got ${preJson.code} ${preJson.msg || ""})`);
  const prepayId = preJson.biz_content.prepay_id;
  assert(!!prepayId, "preOrder returned prepay_id");

  // 4. Build + check H5 paygate (5-field signature).
  const h5 = {
    appid: creds.merchantAppId,
    merch_code: creds.merchantCode,
    nonce_str: nonce,
    prepay_id: prepayId,
    timestamp,
  };
  const h5sign = C.signObject(h5, creds.merchantPrivateKeyPem);
  const qs = new URLSearchParams({
    ...h5,
    sign_type: "SHA256WithRSA",
    version: "1.0",
    trade_type: "Checkout",
    sign: h5sign,
  }).toString();
  const page = await req("GET", `${SIM}/payment/web/paygate?${qs}`);
  assert(page.status === 200, "paygate 200");
  assert(!page.body.includes("did NOT verify"), "paygate accepted H5 signature");

  // 5. Complete payment -> webhook.
  const done = await req("POST", `${SIM}/simulate/complete?prepay_id=${encodeURIComponent(prepayId)}&result=success`);
  assert(done.status === 200, "simulate/complete 200");

  await new Promise((r) => setTimeout(r, 200));
  assert(received !== null, "webhook was delivered");
  // Documented flat shape (ESC-45): no biz_content, notify_time in ms.
  assert(received.biz_content === undefined, "webhook body is flat");
  assert(received.merch_order_id === merchOrderId, "webhook merch_order_id matches");
  assert(received.trade_status === "Completed", "webhook trade_status Completed");
  assert(/^\d{13}$/.test(received.notify_time), "webhook notify_time is epoch ms");
  // Telebirr signs the transaction id as trans_id; the simulator sends it as transId by default.
  const signedView = { ...received };
  if (signedView.transId !== undefined && signedView.trans_id === undefined) {
    signedView.trans_id = signedView.transId;
    delete signedView.transId;
  }
  const sigOk = C.verifyObject(signedView, received.sign, creds.telebirrPublicKeyPem);
  assert(sigOk, "webhook signature verifies with Telebirr public key (trans_id alias)");

  receiver.close();
  console.log("\nALL SIMULATOR SELF-TESTS PASSED");
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
