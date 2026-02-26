# WeredaConnect Review Fixes Checklist

**Generated:** 2026-02-25
**Source:** Comprehensive 6-agent audit (121 findings)
**Branch:** `telebirr-mock`

---

## Tier 0 — Data Integrity (App is Broken)

These bugs mean core features silently fail. Fix first.

- [x] **FIX-001**: Add `"qrToken"` to `AllowedDeliveryFields` whitelist ✅ Fixed 2026-02-26
  - **File:** `server/src/Functions/SyncFunction.cs:55-62`
  - **Impact:** Every delivery created via sync has empty `qrToken` — QR handoff verification is broken
  - **Fix:** Added `"qrToken"` to `AllowedDeliveryFields` HashSet. Made qrToken immutable on UPDATE (security condition from review — prevents token tampering after creation)

- [x] **FIX-002**: Align `quantity` / `quantityKg` field name mismatch ✅ Fixed 2026-02-26
  - **File:** `server/src/Functions/CropFunction.cs:187` (was reading `"quantityKg"`, should read `"quantity"`)
  - **Impact:** Crop quantities always returned 0 from GET /crops endpoints
  - **Fix:** Changed `GetInt(item, "quantityKg")` → `GetInt(item, "quantity")` in CropFunction.cs. Updated 3 test fixtures in CropFunctionTests.cs. Mobile already uses `"quantity"` — no mobile changes needed

---

## Tier 1 — Security (Exploitable Now)

### Authentication & Authorization

- [x] **FIX-003**: Verify CropFunction authentication at API Gateway level ✅ Verified 2026-02-26
  - **File:** `server/src/Functions/CropFunction.cs:50-64`
  - **Impact:** If API Gateway authorizer is missing, all crop listings (farmer IDs, pricing) are publicly readable
  - **Fix:** Confirmed `jwtAuthorizer` is already attached to `/crops` and `/crops/{id}` routes in `infra/lib/stacks/api-stack.ts` (lines 487, 500). No changes needed

- [x] **FIX-004**: Verify TripFunction authentication at API Gateway level ✅ Verified 2026-02-26
  - **File:** `server/src/Functions/TripFunction.cs:50-63`
  - **Impact:** Trucker phone numbers (PII) publicly exposed
  - **Fix:** Confirmed `jwtAuthorizer` is already attached to `/trips` and `/trips/{id}` routes (lines 513, 526). No changes needed

- [x] **FIX-005**: Remove `qrNonce` and `scannedQrNonce` from client-writable `AllowedDeliveryFields` ✅ Fixed 2026-02-26
  - **File:** `server/src/Functions/SyncFunction.cs:55-62`
  - **Impact:** Client can bypass QR verification by directly setting `scannedQrNonce` to match `qrNonce`
  - **Fix:** Removed both from `AllowedDeliveryFields`. `qrNonce` now generated server-side via `Guid.NewGuid().ToString("N")` at delivery creation. `scannedQrNonce` still accepted for validation but never persisted

- [x] **FIX-006**: Remove `pickedUpAt` and `confirmedAt` from client-writable fields ✅ Fixed 2026-02-26
  - **File:** `server/src/Functions/SyncFunction.cs:55-62`
  - **Impact:** Client can backdate `confirmedAt` to manipulate 48-hour dispute window
  - **Fix:** Removed from `AllowedDeliveryFields`. Server now sets `pickedUpAt` when status → `"pickedUp"` and `confirmedAt` when status → `"delivered"` (server-authoritative timestamps)

- [x] **FIX-007**: Restrict identity fields to immutable after delivery creation ✅ Fixed 2026-02-26
  - **File:** `server/src/Functions/SyncFunction.cs:1609-1730`
  - **Impact:** Trucker can change `farmerId`/`buyerId` on a delivery, potentially redirecting escrow payouts
  - **Fix:** Added `ImmutableDeliveryFields` HashSet containing `farmerId`, `buyerId`, `truckerId` + names + phones + `tripId` + `listingId`. Update loop skips these fields

- [x] **FIX-008**: Remove `unsafeUnwrap()` — load pagination secret at runtime ✅ Fixed 2026-02-26
  - **File:** `infra/lib/stacks/api-stack.ts:228,245,291,321`
  - **Impact:** Pagination HMAC secret appears in plaintext in CloudFormation template, Lambda env vars, and CloudTrail
  - **Fix:** Replaced all 4 `unsafeUnwrap()` calls with `paginationSecret.secretArn`. Server now loads secret from Secrets Manager at runtime with in-memory caching (thread-safe, double-checked locking). Falls back to `PAGINATION_SECRET` env var for local dev. Added `AWSSDK.SecretsManager` NuGet package

### Input Validation

- [x] **FIX-009**: Add crop listing field validation in sync create/update ✅ Fixed 2026-02-26
  - **File:** `server/src/Functions/SyncFunction.cs:303-413`
  - **Impact:** Negative prices, empty crop types, unbounded strings accepted — data quality + DoS risk
  - **Fix:** Added `ValidateCropFields(data, isCreate)` method validating: `pricePerKg` (0.01-1000), `quantity` (0.01-10000), `cropType` against `AllowedCropTypes` enum (9 Ethiopian crops), `description` (max 1000 chars), `woreda` (1-100 chars). Called in both CREATE and UPDATE paths

- [x] **FIX-010**: Add crop listing status transition validation ✅ Fixed 2026-02-26
  - **File:** `server/src/Functions/SyncFunction.cs:303-588`
  - **Impact:** Users can resurrect deleted listings, re-list sold crops
  - **Fix:** Added `CropTransitions` dictionary: `available` → {reserved, sold, deleted}, `reserved` → {available, sold, deleted}. `sold` and `deleted` are terminal states. Enforced in crop UPDATE handler

- [x] **FIX-011**: Validate `pickupPhotoRef`/`dropoffPhotoRef` format ✅ Fixed 2026-02-26
  - **File:** `server/src/Functions/SyncFunction.cs:55-62`
  - **Impact:** Client can set photo ref to another user's S3 object, fabricating delivery evidence
  - **Fix:** Added AOT-safe `[GeneratedRegex]` for `PhotoRefRegex()` pattern: `^uploads/photos/[a-zA-Z0-9\-]+/(pickup|dropoff)-\d+\.webp$`. Also validates delivery ID ownership via `IsValidPhotoRef()` helper. Applied in both CREATE and UPDATE paths

- [x] **FIX-012**: Align password policy across Cognito / server / mobile ✅ Fixed 2026-02-26
  - **File:** `infra/lib/stacks/api-stack.ts:65-71` (min 8), `server/src/Validation/RequestValidator.cs:100` (min 6), `mobile/lib/core/auth/offline_auth_cache.dart:30` (min 6)
  - **Impact:** Users cache 6-char passwords offline that are rejected by Cognito online
  - **Fix:** Updated `RequestValidator.ValidatePassword()`: min 8 chars + at least one lowercase + at least one digit (matches Cognito). Updated mobile comment. Added 2 new test cases (no-lowercase, no-digit). Updated 3 existing test data values

---

## Tier 2 — Localization (User-Facing in Production)

### P0 — Hardcoded Strings (Visible to All Users)

- [x] **FIX-013**: Localize 7 escrow status descriptions ✅ Fixed 2026-02-26
  - **File:** `mobile/lib/features/escrow_payment/presentation/widgets/escrow_status_badge.dart:116-123`
  - **Fix:** Added 7 ARB keys (`escrowDescInitiated`, `escrowDescEscrowed`, `escrowDescReleased`, `escrowDescCompleted`, `escrowDescCancelled`, `escrowDescDisputed`, `escrowDescRefunded`) in all 3 languages. Wired `l10n` parameter in `_statusDescription()`

- [x] **FIX-014**: Localize conflict resolution field labels ✅ Fixed 2026-02-26
  - **Files:** `conflict_resolution_screen.dart`, `conflict_detail_card.dart`
  - **Fix:** Added ARB keys for 'Crop Type', 'Quantity', 'Price/kg', 'Status', 'Route', 'Capacity', 'Trip'. Passed `l10n` to `_extractDisplayFields`. Also replaced `_formatTimestamp` month array with `DateFormat.yMMMd()`

- [x] **FIX-015**: Localize "Pay" button in payment confirmation dialog ✅ Fixed 2026-02-26
  - **File:** `initiate_payment_screen.dart`
  - **Fix:** Replaced `const Text('Pay')` with `Text(l10n.escrowPayButton)` using new ARB key

- [x] **FIX-016**: Localize "Reason:" prefix in dispute details ✅ Fixed 2026-02-26
  - **File:** `escrow_status_screen.dart`
  - **Fix:** Added `escrowDisputeReasonPrefix` ARB key with parameterized format

- [x] **FIX-017**: Replace hardcoded English month names with locale-aware DateFormat ✅ Fixed 2026-02-26
  - **Files:** `conflict_detail_card.dart`, `escrow_timeline.dart`, `data_usage_screen.dart`
  - **Fix:** Replaced custom month arrays with `DateFormat.yMMMd()` and `DateFormat.yMMMd().add_jm()` from `intl`

- [x] **FIX-018**: Localize remaining escrow screen hardcoded strings ✅ Fixed 2026-02-26
  - **Files:** `initiate_payment_screen.dart`, `raise_dispute_screen.dart`, `payment_history_screen.dart`, `pending_intent_card.dart`
  - **Fix:** Added ARB keys for delivery number, payment-to, payment amount, delivery short, queued status

### P1 — Error Messages (Shown to Users on Failure)

- [x] **FIX-019**: Localize auth error fallback strings ✅ Fixed 2026-02-26
  - **Files:** `auth_bloc.dart`, `login_screen.dart`, `register_screen.dart`, `otp_verification_screen.dart`
  - **Fix:** BLoC uses error code `.userMessage` as fallback. Screens map error codes to l10n strings via `_localizeAuthError()`

- [x] **FIX-020**: Localize delivery error messages ✅ Fixed 2026-02-26
  - **Files:** `delivery_validation_exception.dart`, `delivery_bloc.dart`
  - **Fix:** Added `DeliveryErrorCode` class with 8 stable error codes. `_errorFromException()` extracts codes. Added `delivery_error_l10n.dart` helper and 8 ARB keys

- [x] **FIX-021**: Localize QR verification error messages ✅ Fixed 2026-02-26
  - **Files:** `qr_verification_service.dart`, `qr_scan_screen.dart`
  - **Fix:** Added `QrErrorCode` class with 6 codes. `_localizeQrError()` maps codes to l10n strings. Added 6 ARB keys

- [x] **FIX-022**: Localize escrow error messages ✅ Fixed 2026-02-26
  - **Files:** `escrow_bloc.dart`, `escrow_error_l10n.dart`, all 4 escrow screens
  - **Fix:** Added `localizeEscrowError()` helper mapping 7 error codes to l10n strings. Wired into `escrow_status_screen`, `payment_history_screen`, `initiate_payment_screen`, `raise_dispute_screen`

- [x] **FIX-023**: Map raw error messages to user-friendly l10n in all error states ✅ Fixed 2026-02-26
  - **Files:** `browse_listings_screen.dart`, `delivery_list_screen.dart`, `delivery_detail_screen.dart`, `qr_scan_screen.dart`, `create_delivery_screen.dart`, `dispute_screen.dart`
  - **Fix:** All error states now use `localizeDeliveryError()` or `l10n.genericError` instead of raw `state.message`

---

## Tier 3 — Infrastructure (Deploy Blockers)

- [ ] **FIX-024**: Add CloudWatch alarms for Lambda errors and escrow cron
  - **File:** `infra/lib/stacks/monitoring-stack.ts:1-47`
  - **Fix:** Replace placeholder text widget with: Lambda error rate alarms (all functions), DynamoDB throttle alarms, API Gateway 5xx alarm, escrow cron invocation error alarm, SNS topic with email

- [ ] **FIX-025**: Add DLQ for escrow cron Lambda
  - **Files:** `infra/lib/constructs/lambda-construct.ts`, `infra/lib/stacks/api-stack.ts:369-394`
  - **Fix:** Create SQS DLQ, attach to escrow cron EventBridge target with `retryAttempts: 2`, `maxEventAge: 1 hour`. Add alarm on DLQ message count

- [ ] **FIX-026**: Set Lambda log retention policy
  - **File:** `infra/lib/constructs/lambda-construct.ts:34-91`
  - **Fix:** Add `logRetention: logs.RetentionDays.THREE_MONTHS` as default. Override to `ONE_YEAR` for escrow/payment functions

- [ ] **FIX-027**: Set reserved concurrency on all Lambda functions
  - **File:** `infra/lib/stacks/api-stack.ts` (all Lambda instantiations)
  - **Fix:** health=5, auth functions=20, sync=10, crop/trip=10, delivery=10, escrow=15, escrowCron=2

- [ ] **FIX-028**: Enable Cognito MFA (at least optional)
  - **File:** `infra/lib/stacks/api-stack.ts:54`
  - **Fix:** Change `mfa: cognito.Mfa.OFF` to `mfa: cognito.Mfa.OPTIONAL`. Consider requiring for high-value escrow operations

- [ ] **FIX-029**: Reduce API Gateway throttle limits
  - **File:** `infra/lib/stacks/api-stack.ts:661-665`
  - **Fix:** Reduce from 200 burst / 100 RPS to 50 burst / 25 RPS (appropriate for low-traffic rural app)

---

## Tier 4 — Mobile UX (User Experience Quality)

### Touch Targets & Accessibility

- [ ] **FIX-030**: Fix GPS retry button touch target (currently 16x16dp)
  - **File:** `mobile/lib/features/crop_listing/presentation/screens/create_crop_listing_screen.dart:429-436`
  - **Fix:** Replace `GestureDetector` + 16px icon with `IconButton` (default 48dp target)

- [ ] **FIX-031**: Fix `_InfoChip` touch target on welcome screen
  - **File:** `mobile/lib/features/onboarding/screens/welcome_screen.dart:132-158`
  - **Fix:** Add `constraints: const BoxConstraints(minHeight: 48)` to InkWell

- [ ] **FIX-032**: Fix dispute radio button touch targets
  - **File:** `mobile/lib/features/escrow_payment/presentation/screens/raise_dispute_screen.dart:168-177`
  - **Fix:** Remove `dense: true` and `visualDensity: const VisualDensity(vertical: -2)`

- [ ] **FIX-033**: Add Semantics annotations to interactive widgets
  - **Files:** Multiple (only 9 Semantics usages across entire codebase)
  - **Fix:** Add `semanticLabel` to all Icon-based affordances, `Semantics` wrappers to custom interactive widgets (crop type grid, status chips, timeline steps)

### Navigation & Layout

- [ ] **FIX-034**: Reduce bottom navigation to 4-5 tabs
  - **File:** `mobile/lib/screens/main_navigation.dart:310-349`
  - **Fix:** Move Profile to drawer. Consider consolidating Trips+Deliveries. Test with Amharic labels on 5" screens

- [ ] **FIX-035**: Wrap OTP screen in SingleChildScrollView
  - **File:** `mobile/lib/features/auth/screens/otp_verification_screen.dart:150-332`
  - **Fix:** Wrap `Column` in `SingleChildScrollView` (matches login/register pattern)

- [ ] **FIX-036**: Fix ConflictResolutionScreen presentation (page vs bottom sheet)
  - **File:** `mobile/lib/screens/main_navigation.dart:252-260`
  - **Fix:** Either use `showModalBottomSheet` as the class docstring intends, or remove the drag handle and adjust layout for full-screen

### Error & Empty States

- [ ] **FIX-037**: Add retry button to PaymentHistoryScreen error state
  - **File:** `mobile/lib/features/escrow_payment/presentation/screens/payment_history_screen.dart:126-139`
  - **Fix:** Add retry button consistent with other screens

- [ ] **FIX-038**: Use ScaffoldMessenger for delivery success SnackBars
  - **File:** `mobile/lib/features/delivery_verification/presentation/screens/delivery_detail_screen.dart:46-80`
  - **Fix:** Use `ScaffoldMessenger.of(context)` which persists across navigation, or add brief delay before `pop()`

- [ ] **FIX-039**: Show farmer name instead of farmerId (UUID) to buyers
  - **File:** `mobile/lib/features/crop_listing/presentation/screens/crop_listing_detail_screen.dart:127`
  - **Fix:** Display farmer name and phone from listing data instead of raw UUID

### Formatting & Consistency

- [ ] **FIX-040**: Use locale-aware NumberFormat in escrow screens
  - **Files:** `initiate_payment_screen.dart:34`, `escrow_status_screen.dart:34`, `raise_dispute_screen.dart:46`
  - **Fix:** Replace `NumberFormat('#,##0.00', 'en_US')` with locale-aware formatter or document the intentional choice

- [ ] **FIX-041**: Standardize on `theme.colorScheme.primary` (not `theme.primaryColor`)
  - **Files:** `welcome_screen.dart:46`, `login_screen.dart:183`, `otp_verification_screen.dart:199`, `auth_gate.dart:103`
  - **Fix:** Replace all `theme.primaryColor` with `theme.colorScheme.primary` (Material 3 canonical source)

---

## Tier 5 — Server Quality (Before GA)

- [ ] **FIX-042**: Add delivery create and delete sync tests
  - **File:** `server/tests/Functions/SyncFunctionTests.cs`
  - **Fix:** Add tests for: valid create, missing required fields, unauthorized trucker, invalid GPS, duplicate idempotency key, valid delete, unauthorized delete, already-deleted, terminal state blocking

- [ ] **FIX-043**: Create CropRepository GSI (replace table Scan with Query)
  - **File:** `server/src/Data/CropRepository.cs:283-337`
  - **Fix:** Add GSI (e.g., `status-createdAt-index`) in CDK, convert `QueryCropsAsync` from Scan to Query

- [ ] **FIX-044**: Sanitize sync error messages (don't leak field names)
  - **File:** `server/src/Functions/SyncFunction.cs:344,1445,1782`
  - **Fix:** Return generic "Invalid data payload" instead of listing unknown fields. Log details server-side

- [ ] **FIX-045**: Fix handler routing — use exact match instead of `Contains()`
  - **File:** `server/src/Program.cs:33-86`
  - **Fix:** Replace `h.Contains("RegisterFunction")` with `h == "RegisterFunction"` or `h.EndsWith()`

- [ ] **FIX-046**: Extract shared `AuthHelper` class
  - **Files:** `DeliveryFunction.cs:368-401`, `EscrowFunction.cs:433-483`, `SyncFunction.cs:158-171`
  - **Fix:** Move `IsAuthenticated()`, `GetAuthenticatedUserId()`, `GetAuthenticatedUserRole()` to shared `AuthHelper.cs`

- [ ] **FIX-047**: Add pagination token expiration
  - **File:** `server/src/Helpers/PaginationTokenHelper.cs`
  - **Fix:** Embed Unix timestamp in token payload before HMAC signing. Reject tokens older than 1 hour in `DecodeNextToken`

- [ ] **FIX-048**: Add S3 upload file size limit
  - **File:** `server/src/Functions/DeliveryFunction.cs:298-305`
  - **Fix:** Add `Content-Length-Range` condition to pre-signed URL (max 500KB for WebP photos)

---

## Tier 6 — Infrastructure Polish (Before GA)

- [ ] **FIX-049**: Use KMS encryption for financial DynamoDB tables
  - **File:** `infra/lib/constructs/dynamodb-construct.ts:24-31`
  - **Fix:** Add `encryption: dynamodb.TableEncryption.CUSTOMER_MANAGED` with shared KMS key for escrows, escrow-audit, payments, fraud-signals tables

- [ ] **FIX-050**: Add API Gateway access logging
  - **File:** `infra/lib/stacks/api-stack.ts:398-401`
  - **Fix:** Configure access logging to CloudWatch with `requestId`, `sourceIp`, `httpMethod`, `routeKey`, `status`, `responseLatency`

- [ ] **FIX-051**: Optimize DynamoDB GSI projections
  - **File:** `infra/lib/stacks/database-stack.ts`
  - **Fix:** Change GSIs from `ALL` to `INCLUDE` with only required attributes. Remove redundant `StatusIndex` on deliveries table if `StatusCreatedAtIndex` suffices

- [ ] **FIX-052**: Wire or remove dead `paymentsTable`
  - **File:** `infra/lib/stacks/database-stack.ts:205-227`
  - **Fix:** Either grant Lambda access and use it, or remove the table definition

- [ ] **FIX-053**: Disable `userPassword` auth flow on Cognito client
  - **File:** `infra/lib/stacks/api-stack.ts:80-83`
  - **Fix:** Set `userPassword: false`, keep only `userSrp: true` (SRP avoids transmitting password)

- [ ] **FIX-054**: Add `fraudSignalsTable` to CDK test fixtures
  - **File:** `infra/test/unit/stack.test.ts:51-66`
  - **Fix:** Update all test fixtures to include `fraudSignalsTable: dbStack.fraudSignalsTable`

- [ ] **FIX-055**: Enable X-Ray tracing
  - **File:** `infra/lib/constructs/lambda-construct.ts`
  - **Fix:** Add `tracing: lambda.Tracing.ACTIVE` to Lambda construct (free tier: 100K traces/month)

---

## Tier 7 — Known Pre-Existing Issues (from TASK.md)

- [ ] **FIX-056**: Fix 2 failing mobile unit tests (T-MVP-01)
  - **File:** `mobile/test/features/delivery_verification/presentation/screens/delivery_list_screen_test.dart`
  - **Fix:** Fix "debug seed button visible in debug mode for empty list" test expectations

- [ ] **FIX-057**: Fix 12 integration test errors — add `locationName` (T-MVP-02)
  - **Files:** `mobile/integration_test/manual_stress_test.dart`, `offline_online_cycle_test.dart`, `sync_crop_listing_test.dart`
  - **Fix:** Add `locationName` parameter to all 12 `GpsCaptureResult()` call sites

- [ ] **FIX-058**: Fix trailing comma lint issues (T-MVP-03)
  - **Files:** `mobile/test/shared/widgets/voice_note_widget_test.dart` and others (119 infos)
  - **Fix:** Run `dart fix --apply` or manually add trailing commas

- [ ] **FIX-059**: Encrypt Isar sensitive fields (Security P1)
  - **File:** `mobile/lib/core/database/isar_service.dart:91-103`
  - **Fix:** Implement field-level encryption for PII (phone numbers, payment amounts, GPS) using the already-generated key from FlutterSecureStorage

- [ ] **FIX-060**: Implement real Telebirr gateway (T-GA-02)
  - **File:** `server/src/Functions/EscrowFunction.cs:495-511`
  - **Fix:** Replace `MockTelebirrGateway` with real API. Add startup guard preventing `TELEBIRR_MOCK=true` in prod

- [ ] **FIX-061**: Enable certificate pinning (T-GA-03)
  - **File:** `mobile/lib/core/network/api_client.dart:50-53`
  - **Fix:** Extract real SHA-256 hashes from API Gateway TLS cert. Implement proper `badCertificateCallback`. Enable by default in production builds

---

## Progress Summary

| Tier | Count | Theme | Status |
|------|-------|-------|--------|
| 0 — Data Integrity | 2 | Broken features | [x] ✅ |
| 1 — Security | 10 | Exploitable vulnerabilities | [x] ✅ |
| 2 — Localization | 11 | User-facing English strings | [ ] |
| 3 — Infrastructure | 6 | Deploy blockers | [ ] |
| 4 — Mobile UX | 12 | Touch targets, nav, errors | [ ] |
| 5 — Server Quality | 7 | Tests, validation, polish | [ ] |
| 6 — Infra Polish | 7 | KMS, logging, GSI optimization | [ ] |
| 7 — Pre-Existing | 6 | Known issues from TASK.md | [ ] |
| **TOTAL** | **61** | | |

---

**Last Updated:** 2026-02-26
**Generated By:** 6-agent comprehensive review (Security, Server, Infrastructure, Mobile UI/UX, Localization)
