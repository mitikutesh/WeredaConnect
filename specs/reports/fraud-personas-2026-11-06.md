# Adversarial personas: dev stack, 2026-11-06 (run `frd17-11062342`)

FRD-17. Script `scripts/e2e_fraud_personas.py`, API `https://vhhm8gh9c1.execute-api.eu-north-1.amazonaws.com`, started 2026-11-06 23:42:43Z, 149 HTTP calls. Expectations come from `specs/architecture/fraud-detection-architecture.md` (sections 2, 14 to 18) and the server code on `main` at the time of the run.

Observed signals are the rows `GET /admin/fraud-signals` returned for each persona account; the tier is computed from them with the scoring rules of section 14.2 (the score itself is never returned by the API) and cross-checked by the gates the persona met: a payment with a fresh password passes at LOW and MEDIUM, meets `AUTH_041` at HIGH and `FRAUD_ACCOUNT_HELD` at CRITICAL; a create meets `FRAUD_ACCOUNT_HELD` only at CRITICAL.

## Summary

| Persona | Accounts | Expected signals | Observed signals | Tier exp / obs | Challenge | Hold | Verdict |
|---|---|---|---|---|---|---|---|
| Listing spammer | spammer | FRAUD_DUPLICATE_LISTING, FRAUD_VELOCITY_EXCEEDED, (PRICE_ANOMALY) | FRAUD_DUPLICATE_LISTING, FRAUD_VELOCITY_EXCEEDED | MEDIUM / MEDIUM (35) | none (a farmer makes no payment); the duplicate and the 6th and 7th listing are refused; creates stay allowed below CRITICAL (the 7th listing meets FRAUD_VELOCITY_EXCEEDED, not FRAUD_ACCOUNT_HELD) | none (below CRITICAL) | DETECTED AND ENFORCED |
| Self-trader | self-farmer | none | none | LOW / LOW (0) | none | none | NOT DETECTED (no rule today) |
| Self-trader | self-buyer | none | none | LOW / LOW (0) | a fresh password passes; AUTH_040 once the sign-in is over 5 minutes old (baseline) | none | NOT DETECTED (no rule today) |
| IDOR prober | idor-buyer | IDOR_CREATE_CONFLICT, ROLE_FORBIDDEN, IMMUTABLE_FIELD_CHANGE | IDOR_CREATE_CONFLICT, IMMUTABLE_FIELD_CHANGE, ROLE_FORBIDDEN | HIGH / HIGH (90) | AUTH_041 on its payment at any amount, then AUTH_040 after the Fayda step-up while the password is stale (HIGH asks both, Fayda first) | none (HIGH holds nothing; it lengthens the payout delay only) | DETECTED AND ENFORCED |
| IDOR prober | idor-farmer | IDOR_CREATE_CONFLICT, ROLE_FORBIDDEN, IMMUTABLE_FIELD_CHANGE, FRAUD_DUPLICATE_LISTING, (PRICE_ANOMALY) | FRAUD_DUPLICATE_LISTING, IDOR_CREATE_CONFLICT, IMMUTABLE_FIELD_CHANGE, ROLE_FORBIDDEN | CRITICAL / CRITICAL (110) | FRAUD_ACCOUNT_HELD on its next crop create | the escrow of the clean buyer's paid order is held (risk_critical) by the auto-refund sweep once the farmer's answer deadline passes; check later with --recheck | DETECTED AND ENFORCED |
| GPS spoofer | gps-trucker | GPS_MOCKED, GPS_JUMP, IMPOSSIBLE_DELIVERY_TIME | GPS_JUMP, GPS_MOCKED, IMPOSSIBLE_DELIVERY_TIME | MEDIUM / MEDIUM (50) | none (a trucker makes no payment; trips stay allowed below CRITICAL) | none (below CRITICAL); the payout waits the new-payee delay as for any new account | DETECTED, NOT ENFORCED (flag only) |
| Confirm-then-dispute buyer | dispute-buyer | DISPUTE_RATE | DISPUTE_RATE | LOW / LOW (10) | AUTH_040 only when the password is stale; disputes under 10,000 ETB need no step-up | none (LOW); each disputed escrow is frozen by the dispute itself | DETECTED, NOT ENFORCED (flag only) |
| Payer mismatch | none | TelebirrWebhookFunction.PayerMatchesBuyerAsync (live where the Telebirr webhook is deployed): TELEBIRR_PAYER_MISMATCH weight 0 (logged, never scored) or TELEBIRR_PAYER_REFUSED weight 5 | n/a | n/a | n/a | n/a | NOT EXERCISABLE ON DEV |

Parenthesised signals are data dependent (the price rule needs 5 or more recent listings of the type with a spread).

## Listing spammer (`spammer`)

Threat: T1 listing spam, T2 duplicates, T3 price manipulation. What should catch it today: Rules 1 to 3 (live).

Plan:

- create 5 listings of 4 crop types, one teff at 999 ETB/kg (the price rule's range ends at 1,000)
- repost the first teff listing at a price 2 percent away (duplicate)
- create 2 more listings in the same hour (over the 5 per hour limit)

| Account | Action | Expected | Got | OK |
|---|---|---|---|---|
| spammer | create teff @ 40.0 ETB/kg | success | success | yes |
| spammer | create teff @ 40.8 ETB/kg | refused FRAUD_DUPLICATE_LISTING | FRAUD_DUPLICATE_LISTING: A similar listing already exists. Please update your existing listing instead. | yes |
| spammer | create wheat @ 22.0 ETB/kg | success | success | yes |
| spammer | create barley @ 26.0 ETB/kg | success | success | yes |
| spammer | create maize @ 18.0 ETB/kg | success | success | yes |
| spammer | create teff @ 999.0 ETB/kg | success | success | yes |
| spammer | create chickpea @ 60.0 ETB/kg | refused FRAUD_VELOCITY_EXCEEDED | FRAUD_VELOCITY_EXCEEDED: Maximum 5 listings per hour exceeded. | yes |
| spammer | create lentils @ 70.0 ETB/kg (8th listing, tier gate first) | refused FRAUD_VELOCITY_EXCEEDED, not FRAUD_ACCOUNT_HELD (below CRITICAL) | FRAUD_VELOCITY_EXCEEDED: Maximum 5 listings per hour exceeded. | yes |

**spammer** (`d0bc593c-4031-7098-39a0-b8a798302228`): signals FRAUD_DUPLICATE_LISTING, FRAUD_VELOCITY_EXCEEDED; score 35, tier MEDIUM (expected MEDIUM).
- `FRAUD_DUPLICATE_LISTING` (weight 20, Rule 1): FraudDetectionService.CheckListingCreationAsync, recorded by the SyncFunction crop create. seen.
- `FRAUD_VELOCITY_EXCEEDED` (weight 15, Rule 3): FraudDetectionService.CheckListingCreationAsync (5 listings per hour), SyncFunction crop create. seen.
- `PRICE_ANOMALY` (weight 10, Rule 2): FraudDetectionService.CheckListingCreationAsync (3 sigma over the last 50 listings of the type), flag only. not seen (data dependent).

Note: teff price sample a buyer sees (other farmers, up to 50): n=24, mean 216.6, sample deviation 322.0, max 1000.0 ETB/kg; 999 is within mean + 3 sigma (1182.6).

Verdict: **DETECTED AND ENFORCED**.

## Self-trader (`selftrade`)

Threat: wash trading for volume and reputation (FRD-15 relationship patterns). What should catch it today: none today: self-trading and same-device pairs are FRD-15 (planned, no producer).

Plan:

- one person, one device (shared X-Device-Id): the farmer account lists maize with a farm location
- the buyer account orders 50 kg (self-pickup) and pays
- the farmer accepts and shows the QR, the buyer confirms the pickup at the farm
- the buyer rates the farmer 5 stars
- after 5 minutes, the buyer pays a second order with the same sign-in: expected AUTH_040 (LOW, stale password)

| Account | Action | Expected | Got | OK |
|---|---|---|---|---|
| self-buyer | pay own crop (50 kg maize, fresh password) | accepted (LOW: AUTH_040 only for a stale password) | success | yes |
| self-buyer | order funded by the mock gateway | funded | funded | yes |
| self-buyer | collect at the farm (self-pickup, same device as the seller) | delivered | delivery 4889b21b delivered | yes |
| self-buyer | rate the seller (itself) 5 stars | accepted (nothing checks the pair) | success | yes |
| self-buyer | pay a second order with the 5-minute-old sign-in | refused AUTH_040 (LOW, stale password) | AUTH_040: Please sign in again to confirm this action. | yes |

**self-farmer** (`105c892c-a051-70aa-473c-befa5982c41c`): signals none; score 0, tier LOW (expected LOW).

**self-buyer** (`504c499c-30f1-7061-aba5-68ecc8def61e`): signals none; score 0, tier LOW (expected LOW).

Verdict: **NOT DETECTED (no rule today)**.

## IDOR prober (`idor`)

Threat: tampered client probing other users' rows (FRD-08). What should catch it today: FRD-08 reject signals and the FRD-04 tier gates (live).

Plan:

- buyer: create an order whose id is another buyer's order (create conflict)
- buyer: create a crop listing (role gate), cancel another buyer's order (no party), rewrite pricePerUnit on its own order (immutable), read another buyer's order over REST
- buyer: pay its own order: expected AUTH_041 (HIGH)
- buyer, after 5 minutes: Fayda step-up through the mock (its own identity), pay again with the old sign-in: expected AUTH_040 (HIGH asks Fayda, then a fresh password)
- farmer: list coffee, repost it (duplicate), create a listing on another farmer's listing id, create an order (role gate), move its own listing to another farmer (immutable)
- farmer: list again: expected FRAUD_ACCOUNT_HELD (CRITICAL)
- a clean buyer then pays the CRITICAL farmer's coffee listing (counterparty tier is not checked at order create, section 17 known gap); its escrow is due a risk_critical hold at settlement

| Account | Action | Expected | Got | OK |
|---|---|---|---|---|
| idor-buyer | create an order on another buyer's order id | conflict, nothing echoed | conflict serverData=no | yes |
| idor-buyer | create a crop listing as a buyer | refused CROP_UNAUTHORIZED | CROP_UNAUTHORIZED: Only farmers can create crop listings. | yes |
| idor-buyer | rewrite pricePerUnit on its own order to 1 ETB | refused, immutable | ORDER_VALIDATION: pricePerUnit is immutable and cannot be changed on update. | yes |
| idor-buyer | cancel another buyer's order | refused ORDER_UNAUTHORIZED (no new row: one per type per hour) | ORDER_UNAUTHORIZED: You can only update orders you are a participant in. | yes |
| idor-buyer | read another buyer's order over REST | 403 or 404, not a signal source (by decision) | HTTP 403 | yes |
| idor-buyer | pay its own order (20 kg teff, under 10,000 ETB) with a fresh password | refused AUTH_041 (HIGH) | AUTH_041: Confirm it's you with Fayda to continue with this payment. | yes |
| idor-farmer | repost its coffee listing | refused FRAUD_DUPLICATE_LISTING | FRAUD_DUPLICATE_LISTING: A similar listing already exists. Please update your existing listing instead. | yes |
| idor-farmer | create a listing on another farmer's listing id | conflict, nothing echoed | conflict serverData=no | yes |
| idor-farmer | create an order as a farmer | refused ORDER_UNAUTHORIZED | ORDER_UNAUTHORIZED: Only buyers can create orders. | yes |
| idor-farmer | move its own listing to another farmer | refused, immutable | CROP_VALIDATION: farmerId is immutable and cannot be changed on update. | yes |
| idor-farmer | list lentils after the probes | refused FRAUD_ACCOUNT_HELD (CRITICAL) | FRAUD_ACCOUNT_HELD: Your account is under review. Some actions are paused. Contact support. | yes |
| support-buyer | a clean buyer orders the CRITICAL farmer's coffee | accepted (section 17 known gap: the counterparty's tier is not checked) | success | yes |
| support-buyer | the clean buyer pays it (5 kg coffee) | funded; the money is protected by the settlement hold, not by a refusal | success, status=funded | yes |
| idor-buyer | Fayda step-up through the mock (its own identity) | linked | HTTP 200 faydaVerified=True | yes |
| idor-buyer | pay again with the 5-minute-old sign-in | refused AUTH_040 (HIGH: password next) | AUTH_040: Please sign in again to confirm this action. | yes |

**idor-buyer** (`d08c899c-4081-7091-d5af-f92525ed34bf`): signals IDOR_CREATE_CONFLICT, IMMUTABLE_FIELD_CHANGE, ROLE_FORBIDDEN; score 90, tier HIGH (expected HIGH).
- `IDOR_CREATE_CONFLICT` (weight 40, FRD-08): SyncFunction create conflict on a crop, trip, order or delivery id the caller does not own. seen.
- `ROLE_FORBIDDEN` (weight 25, FRD-08): SyncFunction.RefuseWithSignalAsync: role gates, caller id binding, rows the caller is no party to. seen.
- `IMMUTABLE_FIELD_CHANGE` (weight 25, FRD-08): SyncFunction.RejectImmutableFieldChanges (crop SIM-02, order SIM-06). seen.

**idor-farmer** (`e0fcc9bc-b061-70c5-86d5-d919e1db1d30`): signals FRAUD_DUPLICATE_LISTING, IDOR_CREATE_CONFLICT, IMMUTABLE_FIELD_CHANGE, ROLE_FORBIDDEN; score 110, tier CRITICAL (expected CRITICAL).
- `IDOR_CREATE_CONFLICT` (weight 40, FRD-08): SyncFunction create conflict on a crop, trip, order or delivery id the caller does not own. seen.
- `ROLE_FORBIDDEN` (weight 25, FRD-08): SyncFunction.RefuseWithSignalAsync: role gates, caller id binding, rows the caller is no party to. seen.
- `IMMUTABLE_FIELD_CHANGE` (weight 25, FRD-08): SyncFunction.RejectImmutableFieldChanges (crop SIM-02, order SIM-06). seen.
- `FRAUD_DUPLICATE_LISTING` (weight 20, Rule 1): FraudDetectionService.CheckListingCreationAsync, recorded by the SyncFunction crop create. seen.
- `PRICE_ANOMALY` (weight 10, Rule 2): FraudDetectionService.CheckListingCreationAsync (3 sigma over the last 50 listings of the type), flag only. not seen (data dependent).

Note: hold target: order `1543810b-f448-41d7-83e7-68f725fc806c`, escrow `escrow-order-1543810b-f448-41d7-83e7-68f725fc806c`, farmer deadline 2026-11-07T23:43:47.2862315Z; after it the order cron cancels it and the auto-refund sweep should write `held` (holdReason risk_critical), dev has FRAUD_HOLD_ENABLED on.

Verdict: **DETECTED AND ENFORCED**.

## GPS spoofer (`gps`)

Threat: T4 fake delivery to claim escrow (trucker). What should catch it today: LT-07 check-in signals and the FRD-09 delivery time rule (live); Rule 4 GPS_ANOMALY is written on the confirming buyer, never on the trucker.

Plan:

- list a trip Debre Birhan to Dire Dawa; carry a paid order of the support farmer's teff
- pick up at the farm with the farmer's QR
- send two check-ins: the first flagged mocked by the phone, the second 50 km away 30 s later
- the buyer confirms the drop-off at Dire Dawa minutes after the pickup (about 250 km)

| Account | Action | Expected | Got | OK |
|---|---|---|---|---|
| gps-trucker | pick up at the farm with the farmer's QR | success | success | yes |
| gps-trucker | check-in flagged mocked by the phone | accepted (flag only) | success | yes |
| gps-trucker | check-in 50 km away 30 s later (6,000 km/h) | accepted (flag only) | success | yes |

**gps-trucker** (`a02ce99c-b071-705c-754d-adb50fac436c`): signals GPS_JUMP, GPS_MOCKED, IMPOSSIBLE_DELIVERY_TIME; score 50, tier MEDIUM (expected MEDIUM).
- `GPS_MOCKED` (weight 25, LT-07): SyncFunction.CheckInFraudSignalsAsync (SyncFunction.CheckInEffects.cs), check-in flagged mocked. seen.
- `GPS_JUMP` (weight 10, LT-07): SyncFunction.CheckInFraudSignalsAsync, neighbouring check-ins over 1 km apart at over 130 km/h. seen.
- `IMPOSSIBLE_DELIVERY_TIME` (weight 15, FRD-09): TransactionPatternService.CheckDeliveryTime via SyncFunction.TryRecordDeliveryPatternSignalsAsync, on the delivery's trucker. seen.
- `GPS_ANOMALY` must not be on this account (Rule 4: FraudDetectionService.CheckGpsConsistency, recorded on the caller of the delivered update (the buyer)): absent.

Verdict: **DETECTED, NOT ENFORCED (flag only)**.

## Confirm-then-dispute buyer (`dispute`)

Threat: buyer griefing: keep the goods and claw the money back. What should catch it today: FRD-09 DISPUTE_RATE (live, 30 percent of 5 or more paid orders); the confirm-then-dispute pattern itself is FRD-15 (planned).

Plan:

- order 5 times from the support farmer and pay each (mock gateway)
- confirm 2 deliveries (the GPS spoofer's trucker delivery when that persona runs, and a self-pickup)
- dispute both right after confirming

| Account | Action | Expected | Got | OK |
|---|---|---|---|---|
| dispute-buyer | confirm the drop-off about 250 km away minutes after the pickup | accepted (the delivery time rule flags the trucker) | success | yes |
| dispute-buyer | pay order 2 (20 kg teff, fresh password) | accepted (LOW) | success | yes |
| dispute-buyer | pay order 3 (20 kg teff, fresh password) | accepted (LOW) | success | yes |
| dispute-buyer | pay order 4 (20 kg teff, fresh password) | accepted (LOW) | success | yes |
| dispute-buyer | pay order 5 (20 kg teff, fresh password) | accepted (LOW) | success | yes |
| dispute-buyer | confirm self-pickup of order 8ccb5dda | delivered | delivered | yes |
| dispute-buyer | dispute confirmed delivery 1 of 2 (escrow 27b4559d) | accepted (disputes are not tier-gated) | HTTP 200  | yes |
| dispute-buyer | dispute confirmed delivery 2 of 2 (escrow e81701af) | accepted (disputes are not tier-gated) | HTTP 200  | yes |

**dispute-buyer** (`a00ca94c-a061-70a5-2565-8364fbf7860c`): signals DISPUTE_RATE; score 10, tier LOW (expected LOW).
- `DISPUTE_RATE` (weight 10, FRD-09): TransactionPatternService.CheckDisputeRateAsync via EscrowFunction.HandleDisputeAsync. seen.

Verdict: **DETECTED, NOT ENFORCED (flag only)**.

## Payer mismatch (`payer`)

Threat: payment from a wallet that is not the buyer's (FRD-08). What should catch it today: TelebirrWebhookFunction.PayerMatchesBuyerAsync (live where the Telebirr webhook is deployed): TELEBIRR_PAYER_MISMATCH weight 0 (logged, never scored) or TELEBIRR_PAYER_REFUSED weight 5.

Plan:

- probe the Telebirr webhook route the detector lives on with an unsigned, empty notification

| Account | Action | Expected | Got | OK |
|---|---|---|---|---|
| - | POST an unsigned empty notification to /webhooks/telebirr | absent on dev (mock gateway, enableTelebirrLive off): 403 or 404; refused by the signature check where deployed | HTTP 404 {"message":"Not Found"} | yes |

Note: the payer check runs only on a verified Telebirr notification; dev funds orders through the mock gateway, which has no payer wallet, so no request can produce TELEBIRR_PAYER_MISMATCH or TELEBIRR_PAYER_REFUSED there.

Verdict: **NOT EXERCISABLE ON DEV**.

## Support accounts (counterparties, should stay clean)

- support-farmer (farmer, `00ac592c-a011-7085-c90c-7cdedf9c2335`): signals none, tier LOW.
- support-buyer (buyer, `c06c998c-00a1-707a-5581-2294cfd970fd`): signals none, tier LOW.

## Gates and holds: where they live

- `FRAUD_ACCOUNT_HELD`: RiskGatePolicy (FRD-04): CRITICAL refuses crop, trip, order create and payments.
- `AUTH_041`: StepUpPolicy.CheckPayment (FRD-04): HIGH asks a fresh Fayda login at any amount.
- `AUTH_040`: StepUpPolicy.CheckPayment: a fresh password for a payment up to 10,000 ETB (LOW and MEDIUM).
- `held`: SettlementRiskGate / PartySuspensionGate + EscrowService.HoldForReviewAsync in the EscrowCronFunction sweeps (FRD-03, FRD-04).

## Left on dev

- self-trade order `016c6773-7b68-4175-b8f6-ba7489375ebe` delivered, escrow auto-releases after the dispute window and payout delay
- support farmer teff listing `0a17148d-6d93-4248-909f-c61d13b7377d` (orders reference it)
- funded order `1543810b-f448-41d7-83e7-68f725fc806c` on the CRITICAL farmer's listing (hold target, left on purpose)
- GPS spoofer trip `4a3ce44b-1a78-4aa6-ae62-e8cadec0badb` with delivered delivery `e2610f1a-9110-4fef-abd4-33d266d1760e`
- disputed escrow `escrow-order-cfbb09df-480e-494f-8f67-657527b4559d` in the admin dispute queue
- disputed escrow `escrow-order-8ccb5dda-51ca-422d-8358-7dcce81701af` in the admin dispute queue

## Accounts

| Label | Role | User id |
|---|---|---|
| spammer | farmer | `d0bc593c-4031-7098-39a0-b8a798302228` |
| self-farmer | farmer | `105c892c-a051-70aa-473c-befa5982c41c` |
| self-buyer | buyer | `504c499c-30f1-7061-aba5-68ecc8def61e` |
| support-farmer | farmer | `00ac592c-a011-7085-c90c-7cdedf9c2335` |
| support-buyer | buyer | `c06c998c-00a1-707a-5581-2294cfd970fd` |
| idor-buyer | buyer | `d08c899c-4081-7091-d5af-f92525ed34bf` |
| idor-farmer | farmer | `e0fcc9bc-b061-70c5-86d5-d919e1db1d30` |
| dispute-buyer | buyer | `a00ca94c-a061-70a5-2565-8364fbf7860c` |
| gps-trucker | trucker | `a02ce99c-b071-705c-754d-adb50fac436c` |

Phones (from +25190017XXXX) and the random passwords are kept only in the `--state-file` when one was given.

Overall: every persona matched its expectation.


## Findings (analysis of this run)

1. **Self-trading is not detected (expected: FRD-15 is planned).** One person with a farmer and a buyer account on one device (one `X-Device-Id`) completed a paid order, a self-pickup at the farm and a 5-star rating of themselves. No signal, tier LOW. The only self-trade check today is `buyerId != farmerId` on the same account (`ESCROW_001`). The fake 5 stars count in the seller rating buyers see on crop cards (SIM-19). A self-trade that bought a whole listing would also mark it `sold`, which is the listing-conversion record the matcher reads as `farmerReliability`. Follow-up: FRD-15 (same device or IP on both sides, repeated pairs).
2. **Payer mismatch cannot be exercised on dev.** Dev funds orders through the mock gateway (no payer wallet) and has no `/webhooks/telebirr` route (404). The detector exists (`TelebirrWebhookFunction.PayerMatchesBuyerAsync`, covered by `TelebirrWebhookFunctionTests` `Payer_mismatch_*`), but by design `TELEBIRR_PAYER_MISMATCH` weighs 0 and `TELEBIRR_PAYER_REFUSED` 5. Even where it fires, it gives no tier change, no challenge and no hold. Exercising it end to end needs a live-gateway stack (`enableTelebirrLive`) and a signed sandbox notification.
3. **The GPS spoofer is detected but nothing is enforced.** `GPS_MOCKED`, `GPS_JUMP` and `IMPOSSIBLE_DELIVERY_TIME` put the trucker at MEDIUM (50). The spoofed delivery completes, trips stay allowed, and the payout waits 72 hours. That is the same 72 hours as for any new payee (`PayoutDelayPolicy` defaults: new 72, flagged 72), so being flagged changes nothing for a young account. Rule 4 (`GPS_ANOMALY`) cannot catch a trucker on a trucker delivery: it is written on whoever sends `delivered`, which is the buyer (ESC-33 plans to re-attribute it).
4. **The confirm-then-dispute buyer is detected only as a weak rate.** Disputing 2 of 5 paid orders right after confirming them gives `DISPUTE_RATE` (10, LOW). Nothing is gated: disputes are not tier-gated, and the pattern types are capped at 50 each, so disputes alone never pass MEDIUM. The confirm-then-dispute pattern itself is FRD-15 (planned).
5. **Rule 2 (price anomaly) misses an absurd price on dev.** A 999 ETB/kg teff listing (about 25 times the usual 40 ETB/kg) was not flagged. The teff sample a buyer sees holds earlier test listings up to 1,000 ETB/kg (n=24, mean 216.6, deviation 322.0), so 999 is inside mean + 3 sigma. A few outliers in a type's last 50 listings disable the rule for that type. Follow-up: a robust statistic (median and MAD), or an absolute per-type bound. On prod data the sample is cleaner, but a seller can still pollute it with their own listings first.
6. **IDOR probing in one hour stops at HIGH.** The three FRD-08 types give 90 in one hour, because the write limit keeps one row per user, type and hour (section 14.3), and `ROLE_FORBIDDEN` and `IMMUTABLE_FIELD_CHANGE` count once per entity. A buyer reaches CRITICAL only with probes in later hours (IDOR 40 per hour, uncapped) or a fourth hard type. The farmer prober reached CRITICAL (110) with a duplicate listing in the same hour. This is as specified (section 15). The gates behaved as section 17 says: `AUTH_041`, then `AUTH_040` after a Fayda step-up with a 5-minute-old sign-in, and `FRAUD_ACCOUNT_HELD` on the CRITICAL farmer's next listing.
7. **A buyer can still pay a CRITICAL farmer.** A clean buyer ordered and funded the CRITICAL farmer's coffee (section 17 known gap: the counterparty's tier is not checked at order create or payment). The money is protected only by the settlement hold, which happens at the next sweep after the farmer's answer deadline. Check it with `--recheck` after 2026-11-07T23:44Z (hold target `escrow-order-1543810b-f448-41d7-83e7-68f725fc806c`).
8. **No API returns the tier or the score.** The run recomputes them from `GET /admin/fraud-signals` with a replica of section 14.2, cross-checked by the gates. If the replica drifted from `RiskScoreCalculator`, only the gate checks would show it. FRD-07 (score and tier in Fraud Review) would remove the replica.

## Shakedown run `frd17-11062240` (an hour earlier)

The first run, at 22:40Z, found two script bugs: a rating id that is not `{orderId}:{rateeId}`, and a dispute idempotency key with five parts (`ESCROW_003`). Both were fixed. The rating and the two disputes were then sent by hand with the same accounts (the disputes gave `DISPUTE_RATE`, as here). Its signals and tiers were the same as this run's. It left on dev: the delivered self-trade order `88ac07f0-f3e1-4212-85f2-945afc941bcb`, the disputed escrows `escrow-order-6929f05c-a703-43a2-96b9-5874f9881445` and `escrow-order-bbc1c696-d559-4e46-8de3-19306fe9a413`, the GPS trip `fa7f4b9d-b9dd-4078-b6d5-dd738e452cbd`, and a second hold target, `escrow-order-4de391ef-b6fb-4c69-b383-1f370f0c3cce` (farmer deadline 2026-11-07T22:41Z). Its accounts: spammer `608ce90c-e051-70e1-5512-70ea8075e2c4`, self-farmer `203c79bc-f031-705e-9fc7-68be2947ff01`, self-buyer `30dcb93c-b081-7033-6f9b-07770645fe60`, support-farmer `20dc19dc-a081-705f-d027-1f3a2cc32c52`, support-buyer `a09cb96c-e091-704a-ebae-ed1770294c73`, idor-buyer `905c69cc-d041-7090-a559-baad4380c2a0`, idor-farmer `601c39ac-70f1-70e2-19a9-6ba561b3368f` (CRITICAL), dispute-buyer `809c499c-1001-70ab-07b9-1535ea49529a`, gps-trucker `50ccf9ec-8001-7095-4b22-4ede414dc584`.
