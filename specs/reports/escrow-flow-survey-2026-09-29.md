# Escrow and Order Flow Code Survey (2026-09-29)

**What ran.** Four read-only code surveys on `fix/payment-fayda-stepup-pickup-qr` at `bc06971`:
1. server order, trip and delivery lifecycle
2. escrow money path
3. ratings, reliability, fraud, users and admin
4. mobile and web client flows

They ground the design in `specs/features/three-party-escrow-commitments-reputation.md`. Every finding below names the EPIC ESC task in `TASK.md` that owns it. No code was changed.

**How to read it.**
- Findings marked **verified** were re-read by hand on 2026-09-29. The rest come from the surveys; re-check each one before fixing it, and expect line numbers to drift.
- Paths are relative to `server/src`, `mobile/lib` or `admin/src` unless written in full.

## 1. Money path

| # | Finding | Evidence | Task |
|---|---|---|---|
| 1.1 | **Live payments never fund an order.** `CreateFromFundedOrderAsync` is called only from the mock auto-fund and from the link-time self-heal. The webhook resolves escrows only through the `/escrow/initiate` idempotency map. **verified** | `Functions/SyncFunction.cs:5409, 5672-5724`; `Functions/TelebirrWebhookFunction.cs:160-224` | ESC-01 |
| 1.2 | **Live payouts and refunds are fake.** Live release and refund are stubs that return `Success = true` with a `SANDBOX-SIM-*` reference. The status check always returns `unknown`. This gateway is used whenever `enableTelebirrLive` is set. **verified** | `Services/TelebirrSandboxGateway.cs:146-170`; `infra/lib/stacks/api-stack.ts:1065, 1786-1804` | ESC-02 |
| 1.3 | **Dispute verdicts can never settle.** The settlement lock requires `attribute_not_exists(settlementLock) AND (status = escrowed OR status = disputed)`, so a `dispute_resolved` escrow never qualifies. The tests mock the lock to always succeed. **verified** | `Data/EscrowRepository.cs:141-142`; `server/tests/Functions/EscrowCronFunctionTests.cs:30-33, 917-941` | ESC-03 |
| 1.4 | **An unpaid trucker leg is never retried.** `truckerPayoutPending` is written but never read, and the escrow still moves to `completed`. When the trucker's identity check blocks the payout, the reason is lost (ErrorCode is null). **verified** | `Services/EscrowService.cs:1130-1166, 1201-1206` | ESC-04 |
| 1.5 | **Order escrows are extended forever.** For orders from `accepted` to `disbursing`, every hourly run pushes `expiresAt` out 48h, with no cap and no audit. **verified** | `Functions/EscrowCronFunction.cs:44-47, 59, 460-470` | ESC-06, ESC-16 |
| 1.6 | **Double payout on retry.** A gateway failure or timeout releases the lock and deletes the idempotency key. The next sweep then pays again unless the provider dedupes the reference, and neither gateway does (the mock returns a fresh reference on every call). | `Services/EscrowService.cs:1100-1110, 1624-1634`; `Services/MockTelebirrGateway.cs:42-94` | ESC-05, ESC-20 |
| 1.7 | **Crash windows leave settlements half done.** Three gaps: a failure after the farmer is paid but before `released` is written (only 4 retries); an exception in the trucker leg; the refund status write, which is attempted once with no recovery sweep. Recovery handles only `released`. | `Services/EscrowService.cs:1120-1129, 1177-1194, 1641`; `Functions/EscrowCronFunction.cs:634-667` | ESC-05 |
| 1.8 | **Refunds are always all or nothing.** Refund always returns the full `amount`, including commission and transport. `RefundFundsRequest.Amount` exists but is never used for a partial refund, and the refund `Reason` is never stored. | `Services/EscrowService.cs:1586, 1616-1622, 1660`; `Models/EscrowDtos.cs:162-177` | ESC-20 |
| 1.9 | **Admin dispute resolution moves no money.** Resolve accepts any `Resolution` string, ignores `Action` and `Notes`, and touches neither the order nor the delivery. `partial_refund` only logs a warning every hour. The admin UI offers only `farmer_wins` and `buyer_wins`. Today's workaround is to release or refund while the escrow is still `disputed`. | `Functions/AdminFunction.cs:346-469`; `Functions/EscrowCronFunction.cs:309-312`; `admin/src/pages/DisputesPage.tsx:20-31` | ESC-03, ESC-20, ESC-24 |
| 1.10 | **Sweeps starve and spam the audit log.** Sweeps read the oldest 100 rows of `StatusExpiresIndex` with no cursor. Rows skipped without being changed stay at the front forever. A blocked release writes `release_blocked_kyc` every hour. | `Data/EscrowRepository.cs:373-435`; `Functions/EscrowCronFunction.cs:328-539` | ESC-10 |
| 1.11 | **Escrows are refunded mid-transit.** Escrows created by `/escrow/initiate` have no `source`, so they are refunded at 7 days with no order or delivery check, even while the goods are on the road. The cron also builds `EscrowService` without the order and delivery repositories. | `Functions/EscrowCronFunction.cs:115, 402-539` | ESC-10 |
| 1.12 | **Reconciliation barely runs.** It covers only `initiated` escrows. With the sandbox gateway the status is always `unknown`, so it always skips. Payouts and refunds are never reconciled. The contract says 30 minutes for `initiated`; the code uses 150. | `Functions/EscrowCronFunction.cs:677-796` | ESC-05, ESC-20 |
| 1.13 | **Escrow writes are barely guarded or audited.** `version` is written but never checked; transitions only check `status`. No escrow path writes transaction-audit rows. These are also unaudited: confirmation stamping, the 48h extension, lock acquire and release, and the cron's order cancel. | `Data/EscrowRepository.cs:115-120`; `Helpers/TransactionAuditWriter.cs` | ESC-20 |
| 1.14 | **Delivery confirmation stamping is silent.** `StampEscrowDeliveryConfirmedAsync` writes no audit and no version bump, and a failure is only logged. A dispute raised before delivery is allowed and gets a window of now + 48h. | `Functions/SyncFunction.cs:4835-4897`; `Services/EscrowService.cs:1298-1310, 1363-1367` | ESC-20 |
| 1.15 | **Double-charge guard gaps.** The list of "active" statuses skips `dispute_resolved`. Concurrent initiates are guarded only by non-atomic reads. | `Services/EscrowService.cs:91-94, 218, 554-556` | ESC-20 |
| 1.16 | **Release doesn't match its contract.** The trigger is validated but not stored. `request.DeliveryId` is never used, although the contract says it must match. The response says `completed`; the contract example says `released`. | `Services/EscrowService.cs:977-1228`; `specs/contracts/escrow-protocol.md:313, 323` | ESC-14 |
| 1.17 | **SSM timing settings are ignored.** The dispute-window and escrow-timeout parameters are passed to the Lambdas but never read. 48h and 7 days are hardcoded in several places. | `infra/lib/stacks/api-stack.ts:687-700`; `Services/EscrowService.cs:60-61`; `Functions/SyncFunction.cs:3888, 4872` | ESC-16 |
| 1.18 | **Commission rules disagree.** The server charges 5% of crop cost (SSM, default 0.05). The contract and `specs/transaction-flow.md` say 2.5% of the amount with a 5 ETB floor. The order-funded escrow falls back to the 2.5% rule when the split is missing. | `Functions/SyncFunction.cs:5062-5103, 6265-6301`; `Services/EscrowService.cs:694-708`; `specs/contracts/escrow-protocol.md:927-937` | ESC-14 (SIM-30) |
| 1.19 | **Monitoring can't see settlement failures.** The cron catches errors per item, and the alarm counts only Lambda errors. Only `ORDER_ESCROW_CREATE_FAILED` has a log alarm; `ORDER_ESCROW_LINK_FAILED` has none. | `infra/lib/stacks/monitoring-stack.ts:186, 465-493` | ESC-13 |
| 1.20 | **Clients don't know `dispute_resolved`.** It is missing from the escrow status lists on both platforms. Mobile uses a strict name lookup that likely throws; web shows an unknown status as `initiated`. | `core/database/schemas/payment.dart:122-142`; `core/models/web/payment.dart:50-58`; `features/escrow_payment/data/repositories/escrow_repository.dart:548, 572, 618`; `web_escrow_repository.dart:289-293` | ESC-22 |

## 2. Order lifecycle

| # | Finding | Evidence | Task |
|---|---|---|---|
| 2.1 | **Nobody can cancel an `accepted` order.** Its only exit is `pickup_confirmed`. The only way out is an admin full refund, which leaves the order status unchanged. **verified** | `Functions/SyncFunction.cs:249-266` | ESC-06, ESC-15 |
| 2.2 | **Current cancel rights.** The buyer can cancel from `created`, `pending_payment`, `funded` and `payment_failed`. The farmer can only reject, and only from `funded`. The trucker can never cancel. **verified** | `Functions/SyncFunction.cs:253-266, 5934-5966` | ESC-15 |
| 2.3 | **An order-level dispute doesn't freeze the money.** A buyer or farmer can move an order `delivered -> disputed` through sync, and the escrow still auto-releases. | `Functions/SyncFunction.cs:263` | ESC-07 |
| 2.4 | **Clients can set `payment_failed`.** The buyer or farmer can set it; the contract says only the system can. | `Functions/SyncFunction.cs:5934-5966`; `order-lifecycle.md:42` | ESC-09 |
| 2.5 | **Late statuses are never reached.** Nothing sets `disbursing`, `settled` or `dispute_resolved` on an order. | code survey | ESC-09 |
| 2.6 | **Cancel reasons are unreliable.** `rejectionReason` and `cancelReason` are free text that any participant can change on any update. There is no `cancelledBy`. | `Functions/SyncFunction.cs:234-237, 6231-6247` | ESC-15 |
| 2.7 | **The order cron only handles unpaid orders.** It auto-cancels them after 24h. Nothing sweeps these states:<br>- `payment_submitted` or `payment_failed` orders<br>- unanswered `funded` orders (only the escrow's 7-day path, which needs an order escrow)<br>- accepted orders with no delivery | `Functions/OrderCronFunction.cs:91-179`; `infra/lib/stacks/api-stack.ts:1227-1240` | ESC-16 |
| 2.8 | **An order can be marked delivered without a delivery.** The client `delivered` gate is skipped when there is no `activeDeliveryId`. After a delivery cancel, a participant can mark an in-transit order delivered without a confirmed delivery. The escrow isn't stamped, so it isn't released, but it is extended forever. | `Functions/SyncFunction.cs:5315-5329` | ESC-08, ESC-15 |
| 2.9 | **Order advances can be silently lost.** `TryAdvanceOrderForDeliveryAsync` does a single compare-and-set with no retry; a conflict is logged and skipped. | `Functions/SyncFunction.cs:5537-5550` | ESC-15 |
| 2.10 | **Listings can oversell (known gap).** Concurrent fundings clamp stock at 0 and both orders stay funded. | `order-lifecycle.md` "Known gap"; `Functions/SyncFunction.cs:5598-5652` | ESC-18 |

## 3. Trips

| # | Finding | Evidence | Task |
|---|---|---|---|
| 3.1 | **Trips never reach `matched`.** Nothing sets it, so mobile "Start trip" (which requires `matched`) can't be reached, and trips never get to `inTransit` or `delivered`. | `Functions/SyncFunction.cs:285-290`; `features/trip_listing/data/repositories/trip_listing_repository.dart:354-366` | ESC-17 |
| 3.2 | **Trips are not booked.** Capacity is never checked or reserved, and any number of orders can bind one trip. Order create doesn't check the departure date, and the route isn't checked against the listing's woreda. | `Functions/SyncFunction.cs:6090-6135`; `Functions/TripFunction.cs:157-170` | ESC-17 |
| 3.3 | **Booked trips can still change.** Trip update accepts any ISO date and lets capacity, price, route and date change in any non-terminal status, even with orders bound. | `Functions/SyncFunction.cs:2576-2644, 2895-2923` | ESC-17 |
| 3.4 | **Trip cancel is inconsistent across platforms.**<br>- Trip delete sets `cancelled` from any non-terminal status, including `inTransit`, skipping the transition map.<br>- Mobile cancels with `delete`; web cancels with `update`, which is rejected from `inTransit`.<br>- Web cancel, start and deliver never check the sync result. | `Functions/SyncFunction.cs:2691-2800`; `trip_listing_repository.dart:334-347`; `web_trip_listing_repository.dart:118-225` | ESC-17, ESC-12 |
| 3.5 | **A cancelled trip leaves its orders untouched.** Trip cancel changes no order, delivery or escrow, and the trucker can still create a delivery for the order. `TripIdIndex` exists on the orders table but is unused. | `Functions/SyncFunction.cs:2487-2800, 2997-3012`; `Data/OrderRepository.cs:20, 175-182`; `infra/lib/stacks/database-stack.ts:495-500` | ESC-17 |
| 3.6 | **Past trips show in browse, and the drop-off geofence is dead.** Browse defaults to `status=open` with no date filter, so past trips are listed. Trips store no coordinates, so the trucker drop-off geofence (`destinationLatitude` and `destinationLongitude`) is dead code. | `Data/TripRepository.cs:40-59`; `Functions/SyncFunction.cs:4325-4352` | ESC-17, ESC-36 |
| 3.7 | **The trucker is never asked.** There is no trucker accept or decline and no notification. The trucker nav has no Orders tab (orders are reached from a Home tile), and the Trip model has no order ids. | `Functions/SyncFunction.cs:5190-5202`; `shared/navigation/role_navigation.dart:86-91`; `core/database/schemas/trip.dart:72` | ESC-15, ESC-23 |

## 4. Deliveries and handoff

| # | Finding | Evidence | Task |
|---|---|---|---|
| 4.1 | **Delivery delete skips the state machine.** Only the trucker can delete, but from any status except `cancelled`, including `pickedUp`, `inTransit`, `delivered` and `disputed`. It skips the transitions and writes no audit. Mobile farmer and buyer cancel send `delete` and fail; web sends `update` and works. | `Functions/SyncFunction.cs:4658-4751`; `features/delivery_verification/data/repositories/delivery_repository.dart:135-150, 637-673`; `web_delivery_repository.dart:372` | ESC-08 |
| 4.2 | **A cancelled delivery leaves the escrow stale.** The cancel releases the order's delivery slot but changes neither the order nor the escrow. The escrow keeps pointing at the dead delivery until something re-links it, and no refund is scheduled. | `Functions/SyncFunction.cs:4575-4584, 5495-5506`; `Services/EscrowService.cs:853-873` | ESC-08, ESC-19 |
| 4.3 | **Orphan delivery slots.** The slot is claimed before the delivery row is written and is not rolled back if the write fails, leaving an orphan `activeDeliveryId`. | `Functions/SyncFunction.cs:3683-3708` | ESC-08 |
| 4.4 | **Trucker deliveries aren't bound to the order.** Delivery create doesn't check that `tripId` and `listingId` match the order's, and never checks the trip's status or owner. The contract requires a `matched` or `inTransit` trip and the listing in `matchedListings`. | `Functions/SyncFunction.cs:3183-3751`; `delivery-verification-api.md:609-614` | ESC-08 |
| 4.5 | **Trucker deliveries may not be confirmable.** The buyer's `delivered` step re-checks the farmer's pickup nonce (with a separate consumed marker), but no screen shows a QR after pickup and no trucker QR exists. The contract says the buyer scans the trucker's QR. Check on a device whether trucker deliveries can be confirmed today. | `Functions/SyncFunction.cs:4012-4133`; `features/delivery_verification/presentation/screens/delivery_detail_screen.dart:320, 423-436`; `delivery-verification-api.md:417, 767` | ESC-35 |
| 4.6 | **GPS checks are weak.**<br>- Pickup GPS is optional (within 5 km of the listing when sent).<br>- Drop-off GPS is unchecked for trucker deliveries.<br>- The fraud GPS flag never blocks anything.<br>- Self-pickup requires drop-off GPS within 5 km of the listing. | `Functions/SyncFunction.cs:4199-4414` | ESC-34, ESC-36 |
| 4.7 | **The QR check has holes.** The server never verifies the client `qrToken` or its HMAC, only its length. When no escrow is linked, the nonce gate lets the step through, so an order escrow that failed to link leaves the gate open. | `Functions/SyncFunction.cs:3065-3080, 4096-4131` | ESC-35 |
| 4.8 | **Client-supplied contacts, no deadlines.** Delivery names and phones come from the client (SIM-46). The contract deadlines (pickup within 7 days, delivery within 14) are not implemented; only the 14-day nonce limits exist. | `Functions/SyncFunction.cs:3093, 311-312`; `delivery-verification-api.md:501-503` | ESC-16, ESC-34 |

## 5. Notifications

| # | Finding | Evidence | Task |
|---|---|---|---|
| 5.1 | **Nothing notifies anyone about orders.** Push covers only chat messages and buyer match alerts. Only the crops and messages tables have streams, SMS is used only for OTP, and there is no in-app alert store. | `Functions/MessageStreamFunction.cs:155`; `Functions/CropMatchStreamFunction.cs:250`; `infra/lib/stacks/database-stack.ts:115, 588` | ESC-21 |
| 5.2 | **Mobile push is half built.** It is Android only, and the foreground handler reacts only to `type == message`. There is no `onMessageOpenedApp`, `getInitialMessage` or background handler, and no `/orders/:id` route. | `core/notifications/push_notification_service.dart:17-87` | ESC-21, ESC-23 |
| 5.3 | **Push text may be in the wrong language.** A changed `preferredLanguage` is never synced to the server profile (SIM-37), and the server builds the push text. | `TASK.md` SIM-37 | ESC-21 |

## 6. Mobile and web clients

| # | Finding | Evidence | Task |
|---|---|---|---|
| 6.1 | **Rejected order updates get stuck on mobile.**<br>- ORDER_VALIDATION, UNAUTHORIZED and FORBIDDEN aren't treated as permanent, so `retryFailed` retries them forever on every reconnect.<br>- The optimistic Isar change is never reverted.<br>- `pullOrders` skips any order with a non-synced op, so the device keeps the wrong status.<br>- The user only sees an English "N failed" line. | `core/sync/sync_executor.dart:685-698, 845-850`; `core/sync/sync_queue.dart:227-245`; `core/sync/isar_marketplace_sync.dart:117-121, 186-193`; `screens/home_screen.dart:2233-2242` | ESC-12 |
| 6.2 | **Failed mobile actions report success.** The accept, reject and cancel guards return without throwing, so the bloc emits success and the screen pops although nothing happened. The update methods don't catch enqueue failures, after Isar has already changed. | `features/order/data/repositories/order_repository.dart:296-412` | ESC-12 |
| 6.3 | **Web hides version races.** `ensureSyncSucceeded` doesn't treat `conflict` as a failure, so a version race looks like success. | `core/network/api_exception.dart:192-205` | ESC-12 |
| 6.4 | **Unknown statuses become `created`.** The web repository, the Isar pull, the conflict resolver and the status filters all do this, which shows the buyer Pay Now and Cancel. | `web_order_repository.dart:396-403`; `isar_marketplace_sync.dart:471-478`; `conflict_resolver.dart:357-362`; `order_repository.dart:36-39, 55-58, 89-92, 107-110` | ESC-22, ESC-25 |
| 6.5 | **Adding a status touches many files:**<br>- both enums (then regenerate `order.g.dart`) and `order_status_sets`<br>- two exhaustive switches in `order_card`<br>- the order detail timeline, sets and actions<br>- the `orders_tab` filters and the parsers<br>- the 3 arb files<br>- admin `transaction.ts` and server `DeriveStage`<br>- the contract and the tests<br>- both web provider trees (`main.dart` and `main_web.dart`; deploy builds `main_web.dart`) | client survey, question 5 | ESC-22, ESC-23 |
| 6.6 | **Order detail never refreshes from the server on mobile.** It reads only Isar, through a route-scoped bloc created without `marketplaceSync`. | `screens/orders_tab_screen.dart:101-116` | ESC-23 |
| 6.7 | **Order detail gaps.**<br>- Pay Now shows only for `created`, not for `pending_payment` or `payment_failed`.<br>- The reject reason is never collected, and cancel and reject reasons are never shown.<br>- The info card shows no trucker and no trip. | `features/order/presentation/screens/order_detail_screen.dart:135-165, 247-315` | ESC-23 |
| 6.8 | **The buyer can't see or change the chosen trucker.** One-tap Order on match results preselects the first suggested trip. PlaceOrder can't remove or change it, and never names the trucker. | `features/buyer/presentation/screens/match_results_screen.dart:273-300`; `features/order/presentation/screens/place_order_screen.dart:283-290` | ESC-23 |
| 6.9 | **Price previews and totals disagree.** The previews charge 5% commission on crop plus transport; the server charges it on crop only (SIM-53). Mobile `totalPrice` is the crop subtotal, while web returns the full total. | `place_order_screen.dart:92`; `crop_listing_detail_screen.dart:164`; `order_repository.dart:142`; `web_order_repository.dart:190` | ESC-23 (SIM-53) |
| 6.10 | **A stale Create Delivery button.** The trucker's "Create Delivery" on the crop listing detail opens `CreateDeliveryScreen` with `orderId: ''`, which the server rejects with `DELIVERY_ORDER_REQUIRED`. | `features/crop_listing/presentation/screens/crop_listing_detail_screen.dart:332-354` | ESC-23 |
| 6.11 | **Missing index and untranslated statuses.** Isar indexes `tripId` but not `truckerId` on orders. Home recent activity shows raw status names in English. | `core/database/schemas/order.dart:68-73`; `screens/home_screen.dart:1698-1716` | ESC-22, ESC-23 |
| 6.12 | **Web has no offline queue** (SIM-57). The web `SyncExecutor` is a no-op, and web `SyncQueue.enqueue` throws. | `core/sync/sync_executor_web.dart:59-86` | spec section 7 |

## 7. Admin

| # | Finding | Evidence | Task |
|---|---|---|---|
| 7.1 | **No typed models.** Pages read `Record<string, unknown>` by key. The status vocabulary lives in `lib/transaction.ts` and is mirrored by the server's `AdminFunction.DeriveStage`. | `admin/src/lib/transaction.ts:12-122`; `Functions/AdminFunction.cs:1337-1346` | ESC-24 |
| 7.2 | **`rejected` is missing** from `orderStatusVariant`, from both `deriveStage` functions and from the admin i18n, so it shows as unknown. | same | ESC-24 |
| 7.3 | **The trucker payout field is always empty.** The admin escrow mapper omits `truckerPayout`, `truckerId` and `refundReason`. | `Functions/AdminFunction.cs:1805-1866`; `admin/src/pages/TransactionDetailPage.tsx:142` | ESC-24 |
| 7.4 | **User and fraud actions are thin.**<br>- The suspend/activate toggle sends only `{action}`, with no reason.<br>- There is no change-role UI, and the transaction history card is a placeholder.<br>- Fraud Review actions have no side effects and store any action string. | `admin/src/pages/UserDetailPage.tsx:27, 72-91`; `Functions/AdminFunction.cs:516-608` | ESC-32 (FRD-05) |

## 8. Ratings, reliability, fraud, users

| # | Finding | Evidence | Task |
|---|---|---|---|
| 8.1 | **Ratings are narrow, expensive and too open.**<br>- Only buyers rate, and only the order's farmer or trucker, on delivered or settled orders.<br>- The summary is recomputed on every read (up to 50 index pages) and isn't filtered by `rateeRole`.<br>- Any authenticated user can read any ratee's ratings.<br>- The ratings table has no stream. | `Functions/SyncFunction.cs:1114-1315`; `Data/RatingRepository.cs:28-29, 138-184`; `Functions/RatingFunction.cs:63-101` | ESC-28, ESC-29 |
| 8.2 | **Ratings are half shown.** Trucker ratings are never displayed, and the Profile rating is a hard-coded "-". Browse fetches one summary per farmer, and offline the summary comes back empty. | `features/crop_listing/presentation/screens/browse_listings_screen.dart:63-71`; `screens/profile_screen.dart:265, 866, 930`; `features/rating/data/repositories/rating_repository.dart:155-180` | ESC-31 |
| 8.3 | **Farmer reliability is broken.**<br>- `farmerReliability` = sold / (sold + expired), computed only at crop create.<br>- `expired` never happens, and the buyer-side "mark sold" sends an entity type the server doesn't know, so the value is always null or 1.0.<br>- Both scorers treat a missing value as the neutral 0.7, at weight 0.10.<br>- There is no trucker reliability. | `Functions/SyncFunction.cs:1848-1910, 2039-2048`; `Helpers/MatchScoring.cs:55-73, 207-217`; `features/buyer/domain/services/match_scorer.dart:34-44, 197-207`; `features/delivery_verification/data/repositories/delivery_repository.dart:203-207, 541` | ESC-30 |
| 8.4 | **Fraud detection writes signals but never acts on them.**<br>- Four listing rules plus payment velocity, with weights GPS 25, duplicate 20, velocity 15, price 10.<br>- Signals expire after 30 days, have no index, and become `reviewed` after any admin action.<br>- No risk score exists.<br>- `GPS_ANOMALY` is charged to whoever confirms delivery, usually the buyer. | `Services/FraudDetectionService.cs:16-258`; `Functions/SyncFunction.cs:4356, 4410-4412`; `infra/lib/stacks/database-stack.ts:436-452` | ESC-33, EPIC FRD |
| 8.5 | **Suspension blocks sign-in only.**<br>- User rows have a `suspended` boolean, `faydaVerified`, `faydaAuthenticatedAt` and `knownDevices`, but no counters.<br>- Profile updates set an explicit field list, so new server-owned fields are safe from overwrites.<br>- Suspension blocks login, verify-otp and refresh only. Access tokens stay valid for up to 15 minutes, and the user's listings stay orderable. | `Data/UserRepository.cs:104-107, 205-296`; `Functions/LoginFunction.cs:194-202`; `Functions/RefreshFunction.cs:108-119` | ESC-29 |
| 8.6 | **A gate pattern to reuse.** `KycPolicy.CheckPayment` reads the buyer and farmer profiles at payment time. | `Helpers/KycPolicy.cs:37-62` | ESC-29 |

## 9. Contract and doc drift

| # | Finding | Evidence | Task |
|---|---|---|---|
| 9.1 | **Version headers disagree with footers.**<br>- trip-listing-api: 1.0.0 vs 1.2.0<br>- delivery-verification-api: 1.4.2 vs 1.6.0, with the changelog out of order<br>- sync-protocol: 1.5.0 vs 1.6.0<br>- data-models: 1.0.0 vs 1.0.2; it also spells the trip status `in_transit` where the code uses `inTransit` | `specs/contracts/*` | ESC-14 |
| 9.2 | **trip-listing-api is out of date.** It says GET /trips is public, but the code returns 403 to farmers and unauthenticated callers. It says a matched cancel "re-opens matched listings"; nothing does. | `trip-listing-api.md:246-250, 383-384`; `Functions/TripFunction.cs:88-110` | ESC-14 |
| 9.3 | **delivery-verification-api is out of date.** Its cancel rules and deadlines differ from the code (4.1 and 4.8), and "buyer scans trucker's QR" is not implemented. | `delivery-verification-api.md:414-424, 501-503, 767-769` | ESC-14, ESC-35 |
| 9.4 | **escrow-protocol is out of date.** Its state table has no `dispute_resolved`. It still calls trucker payment out of scope, and its retry and alert promises are not implemented. | `escrow-protocol.md:74-99, 995-996, 1175` | ESC-14 |
| 9.5 | **user-entity.md 1.0.0 is stale.** It has no `suspended`, `faydaVerified`, `fullName` or `knownDevices`, it says role is immutable, and it lists the wrong index projection. | `specs/contracts/user-entity.md` | ESC-27 |
| 9.6 | **Other stale docs.**<br>- STATE.md and ROADMAP.md say risk scoring is done; it is not.<br>- The f14 decision doc still says "no new tables".<br>- `fraud-detection-architecture.md` contradicts itself on where the score is stored (lines 147 and 441).<br>- BACKLOG F-24 (ratings) is out of date.<br>- EPIC FRD still says it depends on uncommitted `feat/auth-hardening`, which merged in #56. | `STATE.md:170`; `specs/features/ROADMAP.md:50`; `specs/architecture/fraud-detection-architecture.md`; `TASK.md` EPIC FRD | FRD-01, ESC-27 |

## 10. Existing pieces to reuse

- **Idempotent reservations:** `stockRestoredFor` plus version CAS in `ICropRepository.TryRestoreStockAsync`. Use the same pattern to reserve and release trip capacity.
- **Indexes:** `TripIdIndex` on orders (unused today), and `FarmerIdCreatedAtIndex` / `TruckerIdCreatedAtIndex` on orders, trips and deliveries.
- **Gates and step-up:** `KycPolicy` for the reputation gate. `AUTH_040` step-up plus the mobile `SecurityPromptListener` for the fresh sign-in on cancels.
- **Drop-off QR:** the `delivery-qr-secrets` table and the fail-closed nonce gate.
- **Stream consumers and push:** the AOT-safe raw-stream JSON parsing in `CropMatchStreamFunction`, for the order stream consumers. SNS/FCM device tokens and `PushNotificationService` for pushes.
- **Audit:** `TransactionAuditWriter` and `EscrowAuditRepository`.
- **Drop-off evidence:** the presigned S3 upload for delivery photos.
- **Location and matching:** `WoredaCentroids` to default the order delivery location. The `MatchScorer` / `MatchScoring` parity tests for the reliability swap.

## 11. PRD alignment (`docs/project-background.md`)

- **Step 3** (farmer and trucker accept within 24h, else auto-cancel with refund): kept, except a trucker failure now gives the buyer a choice instead of an auto-cancel.
- **Step 6** (the buyer scans the trucker's QR, takes a photo, the trucker confirms, 48h window): delivered by ESC-35 and ESC-36.
- **§4.3** staged buyer-cancel refunds (95% / 90%): superseded. The buyer cannot cancel after paying, except after a transport failure.
- **§5 timeouts:**
  - PAYMENT_SUBMITTED 6h becomes T2.
  - DELIVERED auto-release with trucker evidence becomes T9.
  - FUNDED "allow cancellation after 48h without pickup" is replaced by T3, T4 and T7.
- **§8.5** phone vulnerability: a fresh sign-in is required to cancel after accepting.
- **§9.1:**
  - "Trucker never shows up" now leads to the buyer's choice, not an automatic refund.
  - "Farmer doesn't have goods ready" leads to cancel, full refund and a farmer penalty (ESC-34).
