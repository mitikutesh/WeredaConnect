# Telebirr webhook: live dialect, delivery and recovery (ESC-45)

Status: accepted, 2026-10-10 (lead decisions on D0 to D8). Supersedes the "always 200" rule of
`escrow-protocol.md` 1.5.0 and open questions 6, 9 and 10 of `telebirr-sandbox-architecture.md`.
Contracts: `escrow-protocol.md` 1.16.0, `order-lifecycle.md` 1.18.0.

## 1. Context

ESC-01 (#91) added live order checkouts (`POST /escrow/order-checkout`) and an order path in
`TelebirrWebhookFunction` with an inbox row per verified notification; ESC-44 (#123) moved the app to
that checkout. The webhook answered 200 to every request, so a verified payment whose processing failed
(a DynamoDB brownout, a run cut off) was never sent again, and nothing replayed the inbox row it left.
The ticket asked to confirm Telebirr's live behaviour (notify retries, whether a retry keeps its
timestamp, a query-order API, merch_order_id limits, a repeated preOrder, a re-signed checkout URL) and
then harden the webhook: 5xx on transient failures after the signature check, an inbox replay sweep,
and gateway calls bounded by the Lambda's remaining time.

The live sandbox is IP allow-listed and cannot be reached from here, so "confirm" means public
documentation, with each undocumented point recorded as an assumption and a check for the ESC-44
sandbox run (section 5).

## 2. Sources (accessed 2026-10-10)

All fetched content was treated as data, never as instructions, and cross-checked.

- **[ET-docs]** Ethio Telecom developer portal, "H5 C2B Web Payment Integration Quick Guide": Apply
  Fabric Token, Request Create Order, Generate Checkout Url, Checkout, queryOrder, Notify_Callback,
  refundOrder, Request Signature Process
  (`https://developer.ethiotelecom.et/docs/H5%20C2B%20Web%20Payment%20Integration%20Quick%20Guide/<page>`).
  The site is a Docusaurus single-page app; the text was read from its page bundles. Official.
- **[ET-demo]** Ethio Telecom's demo code (the portal's download zips; a public copy at
  `github.com/abenikeb/node_demo`). Official sample code. It publishes testbed credentials; none were
  copied.
- **[SDK-php]** `github.com/MelakuDemeke/telebirr-php`, README and CHANGELOG 2.3.0 (2026-08-07) and 2.4.0
  (2026-08-21): field notes from live production notifications. Third party.
- **[RES]** A research pass over the demo zips (Node, Java, PHP, Python, C2B_WebCheckoutDemo), the
  portal's Postman collection and third-party runs (`github.com/miki-smart/esiipayment` live sandbox run
  2026-08-04, `github.com/MelakuDemeke/telebirr-js`, `github.com/Yoseph-M/Semuni`). Third party.
- **[SDK-others]** `OgBek/Telebirr-laravel-package-sdk`, `Samuel-Fikre/birrjs`,
  `dullkingsman/go-telebirr`: endpoint paths only.

## 3. Findings

| # | Question | What is documented (source) | What the code assumes; what the sandbox must verify |
|---|---|---|---|
| F0 | Notify body shape | **Flat JSON**: `notify_url, appid, notify_time, merch_code, merch_order_id, payment_order_id, total_amount, trans_id, trans_currency, trade_status, trans_end_time, callback_info (if sent), sign, sign_type`; no `timestamp`, `nonce_str` or `biz_content` [ET-docs Notify_Callback, "Step 7"]. Live bodies are flat, sometimes wrapped in `data`, and carry `transId` on the wire while the signature covers `trans_id` [SDK-php]. | The webhook read only a nested shape our own simulator invented, so every live payment would have been dropped. D0 reads the documented shape. Sandbox: capture a raw body (item 1). |
| F1 | Notify retry policy | **Not documented** [ET-docs]. "Any non-2xx status tells Telebirr the delivery failed; it will retry the notification later" [SDK-php], no count or interval anywhere. | Retries are not relied on: the inbox sweep recovers without them. Sandbox item 2. |
| F2 | Does a retry keep its timestamp | The notify has no `timestamp`; only `notify_time` ("Notification sending time") and `trans_end_time`. Whether a retry gets a new `notify_time` and signature is **not documented**. | Worst case assumed: a retry keeps the original `notify_time` (D3). Sandbox item 2. |
| F3 | `notify_time` unit | **Contradictory**: the schema says "The unit is second", the sample is `'1670575472482'` (13 digits) [ET-docs]; live notifies send milliseconds [SDK-php, RES]. Requests use seconds [ET-docs, ET-demo]. | Detected by digit range, fail closed outside it (D0). Sandbox item 1. |
| F4 | Expected merchant answer | **Not documented**. The sample is headed "Example HTTP Response (200: Processed successful)" [ET-docs]; the official Node demo answers 201 with a JSON body [RES]; third parties answer 200 `{"success": true}` or `{"code":"0","msg":"success"}`. | 200 with `{"code":"0","msg":"success"}` (D2). Sandbox item 2. |
| F5 | Query-order API | **Exists**: `POST /payment/v1/merchant/queryOrder`, `method: payment.queryorder`, `biz_content {appid, merch_code, merch_order_id}`; answer `biz_content {merch_order_id, order_status, trade_status, payment_order_id, trans_time, trans_currency, total_amount, trans_id}`, statuses `PAY_SUCCESS, PAY_FAILED, WAIT_PAY, ORDER_CLOSED, PAYING, ACCEPTED, REFUNDING, REFUND_SUCCESS, REFUND_FAILED`; "can be used to query a payment in the event of an unsuccessful payment notification" [ET-docs]. The table names `trade_status`, the sample uses `order_status`; a live sandbox run saw `order_status: WAIT_PAY` [RES]. | Not used in ESC-45 (D7). If used later, `PAY_SUCCESS` means what `Completed` means on the notify, and `order_status` is read before `trade_status`. Sandbox item 3. |
| F6 | merch_order_id limits | **At most 64 characters, `^[A-Za-z0-9]+$`** [ET-docs Create Order, queryOrder and Notify schemas]; gateway error `49401024995` for other characters [SDK-php]. | Ours: `WCO`, 24 upper-case hex, 3-digit attempt: 30 characters, compliant (unit test). The `/escrow/initiate` path sends the client idempotency key (`{userId}:{entityType}:...`), which breaks the pattern: **that path is not usable live** (follow-up filed by the lead). |
| F7 | preOrder with a repeated merch_order_id | **Not documented**. | Never sent: one mapping per attempt, a retry gets the stored prepay_id. Sandbox item 4. |
| F8 | Re-signed checkout URL | The documented construction signs `appid, merch_code, nonce_str, prepay_id, timestamp` fresh and appends `&version=1.0&trade_type=Checkout` [ET-docs "Step 3"], which `BuildCheckoutUrl` does. Whether a second URL for one prepay_id is accepted is **not documented**. `timeout_express` is 1 to 120 minutes, 120 by default [ET-docs]. | Kept. Sandbox item 5; fallback if refused: store the first URL on the slot. |
| F9 | Token endpoint | `POST {baseUrl}/payment/v1/token`, answer `{effectiveDate, expirationDate (yyyyMMddHHmmss), token ("Bearer ...")}` [ET-docs, ET-demo]. | Ours was `/apply/fabric-token` (simulator path); fixed (D0). The token is cached 300 s (`expirationDate` is not read; reading it as UTC when it is local time would keep an expired token). |
| F10 | Request timestamp unit | Epoch **seconds** [ET-docs, ET-demo]. | Ours sent milliseconds; fixed (D0). Sandbox item 6. |
| F11 | Signature rule | Every field but `sign` and `sign_type` (the demo also skips `header, refund_info, openType, raw_request, wallet_reference_data`), `biz_content` flattened, sorted, `k=v&...` [ET-docs]. Inbound verification is not described. | Implemented; `wallet_reference_data` added to the exclusions. |
| F12 | Notify `trade_status` values | `Paying, Expired, Pending, Completed, Failure`; `Pending` is "Payment completed, awaiting order synchronization" [ET-docs]. | Only `Completed` funds. `Failure` and `Expired` count as failures; `Pending` and `Paying` never cancel an escrow (D0). |
| F13 | Payer phone in the notify | **None** in the notify or the queryOrder answer [ET-docs]. | `TELEBIRR_REQUIRE_PAYER_MATCH` must stay off. Sandbox item 8. |
| F14 | Refund API | **Exists**: `POST /payment/v1/merchant/refund`, `refund_request_no` unique, `actual_amount` at most the original [ET-docs]; `REFUND_DUPLICATED` guards a repeat; `queryRefund` is in the Postman collection [RES]. | Out of scope (ESC-02, ESC-20 follow-up). |
| F15 | Error answers | Not classified [ET-docs]. Seen: `49401024991` southbound unavailable (transient), `49401024995` parameter (permanent), `60200098` product not subscribed, HTTP 401 credentials or IP, `60320006` checkout page parameter (seen for every URL on a sandbox merchant, suspected enablement) [SDK-php, RES]. | Unchanged: a non-2xx preOrder is an unknown outcome. Follow-up: a 4xx with an `errorCode` body is a definite refusal. Sandbox item 10. |
| F16 | Timeouts, rate limits | **Not documented**. Official demos use 30 s [RES]. | D6. |
| F17 | Signature padding | The text says "SHA256WithRSA"; the official JS, Java, PHP and C# samples sign with **RSA-PSS** (only Python uses PKCS#1) [ET-docs, RES]; live notifies observed were PSS [SDK-php]. | Inbound verification accepts PSS or PKCS#1 and logs which one matched; outbound uses `signPadding` (runbook and seed now `pss`; the prod secret template is left untouched because editing it can regenerate the secret). Sandbox item 9. |
| F18 | B2C payout API | **Not in the public portal** [RES]. | Release and refund stay `PAYOUT_NOT_SUPPORTED` (ESC-02). |

## 4. Decisions

### D0. Read the documented dialect (in this PR, its own commit)
- **One map is verified and read** (validator item 1). The body is read with `JsonDocument` into one flat
  map: the top level, or `data` when the top level has no `merch_order_id` and no `biz_content` but
  `data` has one (then only `data` is read; `sign` from it, or from the top level when it has none). A
  `biz_content` object (our earlier simulator) is flattened one level. Any other object or array value,
  and any key seen twice, refuses the body; null is absent. The signature is checked over exactly that
  map, so a captured notification cannot be relabelled by an envelope.
- **`transId` alias** (item 2): when the map has `transId` and no `trans_id`, a copy with the key
  renamed is verified, and every field is then read from that copy.
- **Padding**: PSS or PKCS#1, against Telebirr's public key with SHA-256.
- **Freshness** on `notify_time` (else the earlier simulator's `timestamp`), seconds for 10 to 11 digits,
  milliseconds for 13 to 14, anything else refused.
- **Statuses**: `Failure` and `Expired` are failures; on the `/escrow/initiate` path only `Failed`,
  `Failure` and `Expired` cancel (item 7).
- **Gateway**: token at `/payment/v1/token`; request and checkout-URL timestamps in seconds; a failed token
  call is the definite `TELEBIRR_TOKEN` (preOrder never sent); `OperationCanceledException` is a timeout.
- **No spoofable log text** (item 3): field names of a body that did not verify are logged at most 20,
  each through `MoneyAlarmLog.LogId`.

### D1. Answers
Never 5xx before verification (signature, `notify_time`, merchant) or for a fault a retry cannot fix; 500
when a verified notification is not durably recorded or not applied for a reason a retry can fix.

| Case | Answer |
|---|---|
| Not allow-listed, empty, unparseable or refused body, bad signature, bad or missing `notify_time`, foreign merchant | 200 |
| Mapping read fails | 500 (`ORDER_PAYMENT_UNPROCESSED reason=mapping_lookup_*`) |
| Mapping row malformed | 200 (`ORDER_PAYMENT_UNPROCESSED reason=mapping_invalid`) |
| Inbox read or write fails | 500 (`ORDER_PAYMENT_UNPROCESSED`, nothing durable yet) |
| Less than 4 s left after the inbox write; apply throws; funding conflict | 500 (`ORDER_PAYMENT_REPLAY_PENDING`, row pending) |
| Orders not configured (deployment fault) | 200 (`ORDER_PAYMENT_UNPROCESSED`, row pending) |
| Any final verdict, an earlier refusal kept, a late redelivery of a handled payment | 200 |
| `/escrow/initiate` path, exception after verification | 500 (that path writes its dedupe key only after the escrow CAS) |

### D2. Bodies
200 `{"code":"0","msg":"success"}`; 500 `{"code":"1","msg":"error"}`; no detail either way (SEC-07).

### D3. Late window (lead approved)
Fresh: `|now - notify_time| <= 300 s`. On the order path a verified notification up to 48 h old is still
processed (logged `late=true`), because a retry may keep the original `notify_time`: the inbox key, the
status-conditional funding write, classification by `telebirrPaymentOrderId` and the attempt binding make
a replay harmless. A late redelivery of a notification whose inbox row is final changes nothing (no apply,
no hold, no alarm: validator item 4). Older than 48 h, or more than 300 s in the future, is refused and
alarmed (`reason=stale_notify_time`) only when the inbox has never seen it. The `/escrow/initiate` path
keeps 300 s. The window stays below the inbox row's 30-day TTL (unit test).

### D4. Inbox and replay sweep
- Every verified order notification is written before it is applied: key
  `webhook-inbox:{merch}:{payment}:{status}`, `escrowId`, `orderId`, status `received`, the verified
  payment fields (`notificationBiz`), and the replay queue attributes `inboxReplay = pending`,
  `inboxReplayAt` (receipt + 2 min), `replayAttempts`. A final outcome removes the queue attributes and the
  stored fields (sparse GSI `InboxReplayIndex` on the escrow idempotency table).
- The sweep is the webhook Lambda invoked by EventBridge every 5 minutes with
  `{"action":"webhook_inbox_replay"}`; it runs only without an API Gateway request context, before the IP
  allow-list, and takes no input from its event. Per run: due rows oldest first, at most 100, no claim with
  less than 5 s left.
- Each row is claimed with one conditional write (still pending, same replay time and attempt count),
  which also sets the next replay time (5, 10, 20, 40, then 60 minutes), then replayed like a redelivery.
  A replay racing a live redelivery funds once: the funding write is conditional on `payment_submitted`.
- After 8 attempts (about 4 h 20 min) the row moves to the `exhausted` partition with
  `ORDER_PAYMENT_UNPROCESSED reason=replay_exhausted` and an `order_payment_unprocessed` audit row, only
  if the conditional exhaust wins (a finalize that won is never undone: item 5); `MarkUnprocessed` is
  conditional on pending; a due row with every attempt used (a run that died on the 8th) is exhausted
  without a replay; a malformed or missing mapping is exhausted at once.
- A payment that applies after its row was exhausted logs "stop any manual refund".

### D5. Paging (lead approved)
Immediate page: no inbox row holds the payment (mapping read, inbox read or write), a malformed mapping,
orders not configured, `stale_notify_time`, every unmatched or duplicate verdict, and `replay_exhausted`.
No page for a failure after the inbox row exists (`ORDER_PAYMENT_REPLAY_PENDING`, also for a funding
conflict: item 6). New alarm `{prefix}-telebirr-inbox-replay-stalled`: no `TELEBIRR_INBOX_REPLAY_RUN`
line (one per run, none when the index query failed) for 30 minutes. The API-wide
`{prefix}-high-api-5xx-rate` alarm can page on webhook 500s when traffic is low; accepted (a 500 means
DynamoDB trouble on a money path), unchanged.

### D6. Gateway calls bounded by the remaining time
`GatewayDeadline` (an `AsyncLocal` deadline) is set per invocation by the escrow Lambda (remaining time
minus 5 s) and escrow-cron (minus 30 s). The live gateway sends every request through one bounded path:
with less than 2 s left nothing is sent (`TELEBIRR_NO_TIME`, definite, retried at once); otherwise the send
and the read are cancelled at the deadline (`TELEBIRR_TIMEOUT`, unknown outcome: the checkout slot becomes
`unknown` with its window and an `order_checkout_unknown` audit row before the run ends). Release and refund
make no HTTP call today; any future payout call must use the same path. The webhook bounds itself (D1:
4 s before applying; the sweep: 5 s before a claim).

### D7. queryOrder, merch_order_id, repeated preOrder, re-signed URL
queryOrder is not wired (the sweep replays our own verified notifications); reconciling `unknown` slots
and payments that never reached the inbox through it is a follow-up. The `WCO` id is kept. No repeated
preOrder is ever sent. The re-signed URL is kept until the sandbox says otherwise.

### D8. The 24 h race (lead requirement)
A payment can arrive about 22 h 10 min into the order's payment clock (checkouts open until 20 h, each
payable 130 min), and a late one later still, so a pending replay could otherwise meet the order cron's
24 h `payment_failed` and the buyer would have paid with no order.
- The webhook writes `paymentHoldUntil` (now + 4 h 47 min, the replay horizon) on the order for every
  verified `Completed` before the inbox row: only while `payment_submitted` in the mapping's payment
  attempt, extend-only, without moving `version` (the clients' sync token) or `updatedAt`. Each sweep
  claim extends it to the next replay + 15 min; nothing clears it (funding leaves `payment_submitted`; a
  refused payment's hold just lapses).
- The order cron reads the inbox's `pending` partition once per run (Query on `InboxReplayIndex` only)
  and skips those orders; if that read fails, it skips the payment-failed step for that run. It also skips
  orders whose hold is in the future, and its write is conditional on the hold (it closes "the cron read
  the order, then the hold landed").
- Not covered, all alarmed refund cases: the hold lapsing about an hour after a row is exhausted; a
  buyer's own cancel during the hold; a notification that first reaches us after the order already
  failed.

## 5. Sandbox checklist (ESC-44 run, must verify)

| # | Check | Expected | If different |
|---|---|---|---|
| 1 | Raw notify body | Flat (or `data`-wrapped); `trans_id` or `transId`; `notify_time` in ms; verifies under D0 (the log says which padding) | Adjust the parser before go-live; the single-map rule stays |
| 2 | Answer one notify with 500, log redeliveries for 24 h; then answer 200 | Retries arrive; whether `notify_time` and `sign` change is recorded; nothing after the 200 | If retries are re-signed fresh, D3's late window can shrink; if a 200 is retried, change the body |
| 3 | queryOrder for a paid, an unpaid and an expired order | Signed answer; `order_status` filled | Record the field before wiring the follow-up |
| 4 | preOrder twice with one merch_order_id | Error or the same prepay_id | Follow-up may resume `unknown` attempts by resending the id |
| 5 | First checkout URL, a re-signed URL after 10 min, one after 120 min | First two accepted, third expired | If refused, store the first URL on the slot |
| 6 | Token at `/payment/v1/token`, request timestamps in seconds | Accepted | Revert the unit or the path |
| 7 | A 30-character `WCO...` merch_order_id | Accepted | Shorten the id |
| 8 | Any payer field in the notify | None | Then decide on `TELEBIRR_REQUIRE_PAYER_MATCH` |
| 9 | Padding: which one the notify verified with; preOrder with PSS | PSS on both | Set `signPadding` to `pkcs1` for outbound only |
| 10 | The checkout page loads for our merchant | Loads | A `60320006` on every URL points at merchant enablement (ask Ethio Telecom) |

Must-verify D0 assumptions: flat body and `data` envelope (1), `transId` vs `trans_id` (1), `notify_time`
unit (1), token path and seconds (6), PSS (9).

## 6. Consequences

- A verified order payment that fails once is applied by a redelivery or by the sweep within minutes,
  with no human step; a human is paged only when nothing durable holds it or after 8 attempts.
- Runbook (`.ai/deployment.md`, Monitoring): read the `exhausted` partition, fix the cause, requeue a row
  (`inboxReplay = pending`, `replayAttempts = 0`, `inboxReplayAt = now`) or refund it after checking the
  order's `telebirrPaymentOrderId`.
- `notificationBiz` keeps the paying wallet's phone only if Telebirr ever sends one (the documented notify
  has none), on pending and exhausted rows only, for at most the row's 30-day TTL.
- A captured notification replayed more than 30 days later (its inbox row expired) and more than 48 h old
  logs one `stale_notify_time` alarm line per replay; it can never fund anything.
- Residual risk: D0 rests on the official documentation and one third-party field report, not on a body we
  captured; tolerance in parsing never accepts a body Telebirr did not sign.
- Follow-ups: queryOrder reconciliation for `unknown` slots and payments that never reached the inbox
  (F5); the live refund API for ESC-02 and ESC-20 (F14); a 4xx with an `errorCode` body as a definite
  preOrder refusal (F15); the `/escrow/initiate` merch_order_id (F6, filed by the lead);
  `AmazonDynamoDBConfig` timeouts for the webhook's client.
