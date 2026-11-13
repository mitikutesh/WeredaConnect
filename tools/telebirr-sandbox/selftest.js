"use strict";
/**
 * End-to-end self-test for the local Telebirr sandbox simulator.
 * Acts as the "merchant" (our server) and drives the full H5 flow:
 *   fabric-token -> preOrder (signed) -> paygate -> simulate/complete -> webhook.
 * Verifies the webhook signature with Telebirr's public key.
 * Then queryOrder (FUP-430): WAIT_PAY before the payment, PAY_SUCCESS after it (the notify's ids),
 * ORDER_CLOSED after the window, PAY_FAILED, a payment with no notify, and the refusal for an
 * order the simulator never created.
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
let webhooks = 0;

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

/** preOrder signed with the merchant private key, exactly as the server sends it. */
function preOrder(token, { merchOrderId, notifyUrl, nonce, timestamp }) {
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
  return req("POST", `${SIM}/payment/v1/merchant/preOrder`, {
    headers: { Authorization: token, "X-APP-Key": creds.fabricAppId },
    body: JSON.stringify(body),
  });
}

/** A preOrder that must succeed; returns its prepay_id. */
async function openCheckout(token, merchOrderId, notifyUrl) {
  const timestamp = Math.floor(Date.now() / 1000).toString();
  const nonce = crypto.randomBytes(12).toString("hex");
  const pre = await preOrder(token, { merchOrderId, notifyUrl, nonce, timestamp });
  const preJson = JSON.parse(pre.body);
  if (pre.status !== 200 || preJson.result !== "SUCCESS") {
    assert(false, `preOrder for ${merchOrderId} (got ${pre.status} ${pre.body})`);
  }
  return preJson.biz_content.prepay_id;
}

/**
 * queryOrder as TelebirrSandboxGateway.QueryOrderAsync sends it (method payment.queryorder,
 * biz_content appid, merch_code, merch_order_id, signed). `tamper` changes the body after signing.
 */
async function queryOrder(token, merchOrderId, { tamper = false } = {}) {
  const body = {
    nonce_str: crypto.randomBytes(12).toString("hex"),
    method: "payment.queryorder",
    timestamp: Math.floor(Date.now() / 1000).toString(),
    version: "1.0",
    biz_content: {
      appid: creds.merchantAppId,
      merch_code: creds.merchantCode,
      merch_order_id: merchOrderId,
    },
    sign_type: "SHA256WithRSA",
  };
  body.sign = C.signObject(body, creds.merchantPrivateKeyPem);
  if (tamper) body.biz_content.merch_order_id = `${merchOrderId}X`;
  const headers = { "X-APP-Key": creds.fabricAppId };
  if (token) headers.Authorization = token;
  const r = await req("POST", `${SIM}/payment/v1/merchant/queryOrder`, {
    headers,
    body: JSON.stringify(body),
  });
  return { status: r.status, answer: JSON.parse(r.body) };
}

/** A SUCCESS queryOrder answer about `merchOrderId`, signed by "Telebirr". */
function assertAnswer(q, merchOrderId, orderStatus, label) {
  assert(q.status === 200, `${label}: queryOrder 200`);
  assert(q.answer.result === "SUCCESS", `${label}: queryOrder SUCCESS (got ${q.answer.code} ${q.answer.msg || ""})`);
  assert(q.answer.biz_content.merch_order_id === merchOrderId, `${label}: answer is about the order asked`);
  assert(q.answer.biz_content.order_status === orderStatus, `${label}: order_status ${orderStatus} (got ${q.answer.biz_content.order_status})`);
  assert(
    C.verifyObject(q.answer, q.answer.sign, creds.telebirrPublicKeyPem),
    `${label}: answer signature verifies with Telebirr public key`,
  );
}

async function main() {
  // 1. Start a webhook receiver.
  const receiver = http.createServer((rq, rs) => {
    let d = "";
    rq.on("data", (c) => (d += c));
    rq.on("end", () => {
      received = JSON.parse(d);
      webhooks++;
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
  const pre = await preOrder(token, { merchOrderId, notifyUrl, nonce, timestamp });
  assert(pre.status === 200, "preOrder 200");
  const preJson = JSON.parse(pre.body);
  assert(preJson.result === "SUCCESS", `preOrder SUCCESS (got ${preJson.code} ${preJson.msg || ""})`);
  const prepayId = preJson.biz_content.prepay_id;
  assert(!!prepayId, "preOrder returned prepay_id");

  // 3b. queryOrder before the payment (FUP-430): WAIT_PAY, no payment ids yet.
  const waiting = await queryOrder(token, merchOrderId);
  assertAnswer(waiting, merchOrderId, "WAIT_PAY", "before payment");
  assert(waiting.answer.biz_content.payment_order_id === undefined, "before payment: no payment_order_id");

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

  // 5b. queryOrder after the payment (FUP-430): PAY_SUCCESS with the notify's own ids.
  const paid = await queryOrder(token, merchOrderId);
  assertAnswer(paid, merchOrderId, "PAY_SUCCESS", "after payment");
  const paidBiz = paid.answer.biz_content;
  assert(paidBiz.payment_order_id === received.payment_order_id, "after payment: payment_order_id equals the notify's");
  assert(paidBiz.trans_id === signedView.trans_id, "after payment: trans_id equals the notify's");
  assert(paidBiz.total_amount === "15000.00" && paidBiz.trans_currency === "ETB", "after payment: amount and currency");

  // 6. The window ends unpaid (FUP-430): ORDER_CLOSED, and the checkout can no longer be paid.
  const closedId = `${merchOrderId}C`;
  const closedPrepay = await openCheckout(token, closedId, notifyUrl);
  const expired = await req("POST", `${SIM}/simulate/expire?prepay_id=${encodeURIComponent(closedPrepay)}`);
  assert(expired.status === 200, "simulate/expire 200");
  assertAnswer(await queryOrder(token, closedId), closedId, "ORDER_CLOSED", "after the window");
  const late = await req("POST", `${SIM}/simulate/complete?prepay_id=${encodeURIComponent(closedPrepay)}&result=success`);
  assert(late.status === 409, "a closed checkout cannot be paid (409)");

  // 7. A payment whose notify never arrives (notify=0): only queryOrder tells.
  const silentId = `${merchOrderId}N`;
  const silentPrepay = await openCheckout(token, silentId, notifyUrl);
  const before = webhooks;
  const silent = await req("POST", `${SIM}/simulate/complete?prepay_id=${encodeURIComponent(silentPrepay)}&result=success&notify=0`);
  assert(silent.status === 200, "simulate/complete notify=0 200");
  await new Promise((r) => setTimeout(r, 200));
  assert(webhooks === before, "notify=0 sent no webhook");
  const silentPaid = await queryOrder(token, silentId);
  assertAnswer(silentPaid, silentId, "PAY_SUCCESS", "paid with no notify");
  assert(!!silentPaid.answer.biz_content.payment_order_id, "paid with no notify: payment_order_id present");

  // 8. A failed payment: PAY_FAILED.
  const failedId = `${merchOrderId}F`;
  const failedPrepay = await openCheckout(token, failedId, notifyUrl);
  await req("POST", `${SIM}/simulate/complete?prepay_id=${encodeURIComponent(failedPrepay)}&result=fail&notify=0`);
  assertAnswer(await queryOrder(token, failedId), failedId, "PAY_FAILED", "failed payment");

  // 9. An order the simulator never created: a refusal (placeholder code until FUP-429), no biz_content.
  const never = await queryOrder(token, `WCONEVERCREATED${timestamp}`);
  assert(never.status === 200, "never created: queryOrder 200");
  assert(never.answer.result === "FAIL", "never created: result FAIL");
  assert(never.answer.code === "SIM_ORDER_NOT_FOUND", "never created: code SIM_ORDER_NOT_FOUND");
  assert(never.answer.biz_content === undefined, "never created: no biz_content");

  // 10. Refusals of the request itself.
  const tampered = await queryOrder(token, merchOrderId, { tamper: true });
  assert(tampered.answer.result === "FAIL" && tampered.answer.code === "60200099", "tampered queryOrder refused (60200099)");
  const noToken = await queryOrder(null, merchOrderId);
  assert(noToken.status === 401, "queryOrder without a fabric token 401");

  receiver.close();
  console.log("\nALL SIMULATOR SELF-TESTS PASSED");
}

main().catch((e) => {
  console.error(e);
  process.exit(1);
});
