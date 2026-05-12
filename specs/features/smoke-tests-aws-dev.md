# Feature Plan: AWS Dev Environment Smoke Tests

**Status**: Awaiting approval  
**Branch target**: `main`  
**Created**: 2026-04-06  

---

## What

A CI smoke test suite that runs **after every PR merge to `main`** and validates the complete WeredaConnect marketplace flow against the **live AWS dev environment** (`https://b8xqjg4myb.execute-api.me-south-1.amazonaws.com`). It covers both the **API layer** (exercised by the mobile app) and the **Flutter web app** at `dev-app.weredaconnect.com` via Playwright.

---

## Why

The current CI pipeline (`pr-validation.yml`) runs unit and LocalStack integration tests before merge but has **no post-deploy validation** against real AWS. This means:

- A broken Lambda, misconfigured IAM policy, or DynamoDB key mismatch can silently ship to `main`
- Role-based visibility regressions (Farmer seeing another Farmer's crops) are only caught manually
- The escrow and order flows have never been exercised end-to-end in CI

This smoke suite closes that gap with fast (~5 min), real-AWS assertions on every merge.

---

## Trigger Strategy

```yaml
on:
  push:
    branches: [main]        # fires on every PR merge to main
  workflow_dispatch:        # allow manual runs from Actions UI
```

**NOT** on `pull_request` — this suite always hits real AWS and is intentionally post-deploy.

---

## Technology Stack

| Layer | Tool | Why |
|-------|------|-----|
| API smoke tests | TypeScript + `axios` | Lightweight, no Flutter needed, matches admin test patterns |
| Web smoke tests | Playwright TypeScript | Already used for admin E2E (`.playwright-mcp/`), supports Flutter web CanvasKit |
| Test runner | Playwright Test (`@playwright/test`) | Unified runner for both API (via `request` fixture) and browser tests |
| Phone uniqueness | Timestamp suffix: `+2519001{run_id%10000}` | Avoids conflicts across runs without needing cleanup |
| Directory | `smoke-tests/` (project root) | Isolated from unit/integration test trees |

---

## Files to Create / Modify

### New files

| Path | Purpose |
|------|---------|
| `.github/workflows/smoke-tests.yml` | GitHub Actions workflow — triggers on push to `main` |
| `smoke-tests/package.json` | Node project with Playwright + axios dependencies |
| `smoke-tests/tsconfig.json` | TypeScript config |
| `smoke-tests/playwright.config.ts` | Playwright config — API base URL + web app URL from env |
| `smoke-tests/utils/api-client.ts` | Typed HTTP client wrapping axios; handles JWT auth headers |
| `smoke-tests/utils/test-data.ts` | Unique phone/user generators based on `GITHUB_RUN_NUMBER` |
| `smoke-tests/tests/api/auth.spec.ts` | Register + login + password reset (all 3 roles) |
| `smoke-tests/tests/api/farmer.spec.ts` | Farmer: create crop, list own crops, role isolation check |
| `smoke-tests/tests/api/trucker.spec.ts` | Trucker: create trip, list own trips, role isolation check |
| `smoke-tests/tests/api/buyer.spec.ts` | Buyer: browse all crops + trips, create order (with + without trip) |
| `smoke-tests/tests/api/escrow.spec.ts` | Full escrow lifecycle: initiate → release + initiate → dispute → refund |
| `smoke-tests/tests/web/auth.spec.ts` | Playwright: web app login screen renders + login succeeds (Buyer) |
| `smoke-tests/tests/web/buyer-browse.spec.ts` | Playwright: Buyer can see crop list + trip list on web app |
| `smoke-tests/tests/web/farmer-create.spec.ts` | Playwright: Farmer can create crop and see it in own list |

### Modified files

| Path | Change |
|------|--------|
| `.github/workflows/smoke-tests.yml` | New file (creates this workflow) |

---

## Complete Test Scenarios

### 1. Auth — All 3 Roles

**Registration** (`POST /auth/register` × 3)
- Farmer: unique phone `+2519001{N}1`, role `farmer`, woreda `DebreBirhan`
- Trucker: unique phone `+2519001{N}2`, role `trucker`, woreda `AddisAbaba`
- Buyer: unique phone `+2519001{N}3`, role `buyer`, woreda `DireDawa`
- `faydaVerificationToken` not required — `DEV_FAYDA_BYPASS=true` is set in Lambda env for dev
- Assert: 201 response, `tokens.accessToken` present, `user.role` matches

**Login** (`POST /auth/login` × 3)
- Each newly registered user logs in
- Assert: 200 response, `tokens.accessToken` present
- Assert: `user.role` correct per user

**Password Reset** (`POST /auth/forgot-password` + `POST /auth/reset-password`)
- Call `forgot-password` for each of the 3 registered phones
- Assert: 200 / non-enumeration response (both valid and invalid phones must return same shape)
- **Note on confirmation step**: SNS is in sandbox mode in dev. The full OTP confirmation flow (`reset-password`) cannot be fully automated in CI without SNS sandbox exit (T-SEC-SNS). The smoke test validates the API contract (shape, status codes) for `reset-password` using a deliberately invalid code, expecting the correct `AUTH_007` error shape — not a 500.
- When T-SEC-SNS is resolved (Sprint 2), extend to confirm real OTP via a dedicated test Cognito admin call.

### 2. Farmer Flow

**Setup**: Farmer logs in with registered test token.

**Create crop listing** (`POST /sync`)
```json
{
  "syncBatchId": "smoke-{runId}",
  "clientId": "smoke-test",
  "operations": [{
    "operationId": "op-1",
    "idempotencyKey": "{farmerId}:crop_listing:{cropId}:create:{ts}",
    "entityType": "crop_listing",
    "action": "create",
    "data": {
      "cropType": "TEFF",
      "quantity": 100,
      "unit": "quintal",
      "pricePerUnit": 5000,
      "currency": "ETB",
      "location": "DebreBirhan",
      "availableFrom": "{today+1d}"
    }
  }]
}
```
- Assert: sync result `status: "success"`, `serverData.listingId` present

**List own crops** (`GET /crops`)
- Assert: 200, `items` array contains the created listing
- Assert: `items` only contain listings where `farmerId === currentUser.userId` (**role isolation**)

**Role isolation check**
- Login as Trucker, call `GET /crops`
- Assert: Trucker gets `403` or empty result (Truckers do not access crop listings)
- Login as second Farmer (different user), call `GET /crops`
- Assert: Second Farmer does NOT see first Farmer's crop in result

### 3. Trucker Flow

**Setup**: Trucker logs in with registered test token.

**Create trip** (`POST /sync`)
```json
{
  "operations": [{
    "entityType": "trip",
    "action": "create",
    "data": {
      "origin": "DebreBirhan",
      "destination": "AddisAbaba",
      "departureDate": "{today+2d}",
      "capacity": 500,
      "unit": "quintal",
      "pricePerUnit": 200,
      "currency": "ETB",
      "vehicleType": "TRUCK"
    }
  }]
}
```
- Assert: sync result `status: "success"`, `serverData.tripId` present

**List own trips** (`GET /trips`)
- Assert: 200, `items` array contains the created trip
- Assert: `items` only contain trips where `truckerId === currentUser.userId` (**role isolation**)

**Role isolation check**
- Login as Farmer, call `GET /trips`
- Assert: Farmer gets `403` or empty result (Farmers do not access trip listings)
- Login as second Trucker, call `GET /trips`
- Assert: Second Trucker does NOT see first Trucker's trip

### 4. Buyer Flow — Browse

**Setup**: Buyer logs in.

**Browse all crops** (`GET /crops`)
- Assert: 200, Buyer's `items` contains the Farmer's crop created in step 2
- Assert: Buyer sees crops from all farmers (no owner filter)

**Browse all trips** (`GET /trips`)
- Assert: 200, Buyer's `items` contains the Trucker's trip created in step 3
- Assert: Buyer sees trips from all truckers (no owner filter)

**Search / filter crops**
- `GET /crops?status=available` — Assert filtered results
- `GET /crops?limit=5` — Assert pagination (`hasMore` + `nextToken`)

### 5. Buyer Flow — Transaction with Trip (Full Marketplace Flow)

```
Farmer's crop ──┐
                ├──► Buyer creates Order ──► Escrow initiated ──► Delivery ──► Escrow released
Trucker's trip ─┘
```

**Create order (with trip)** (`POST /sync`)
```json
{
  "operations": [{
    "entityType": "order",
    "action": "create",
    "data": {
      "cropListingId": "{farmerId-crop-id}",
      "tripId": "{truckerId-trip-id}",
      "quantity": 50,
      "unit": "quintal",
      "agreedPrice": 250000,
      "currency": "ETB"
    }
  }]
}
```
- Assert: sync `status: "success"`, `serverData.orderId` present, `serverData.status: "pending_payment"`

**Get order** (`GET /orders/{orderId}`)
- Assert: 200, `status: "pending_payment"`, `buyerId === currentUser.userId`

**Initiate escrow** (`POST /escrow/initiate`)
```json
{
  "deliveryId": "{deliveryId-from-order}",
  "buyerId": "{buyerId}",
  "farmerId": "{farmerId}",
  "amount": 250000,
  "currency": "ETB",
  "idempotencyKey": "{buyerId}:escrow:{orderId}:initiate:{ts}"
}
```
- Assert: 200, `escrowId` present, `status: "pending"`, `commission` ~= `amount × 0.05`

**Get escrow** (`GET /escrow/{escrowId}`)
- Assert: 200, `status: "pending"`, amounts match

**Release escrow** (`POST /escrow/release`)
```json
{
  "escrowId": "{escrowId}",
  "trigger": "delivery_confirmed",
  "deliveryId": "{deliveryId}",
  "idempotencyKey": "{userId}:escrow:{escrowId}:release:{ts}"
}
```
- Assert: 200, `status: "released"`, `farmerPayout` = `amount × 0.95`

**Escrow history** (`GET /escrow/history`)
- Assert: 200, history includes the released escrow

### 6. Buyer Flow — Direct Purchase (No Trip)

```
Farmer's crop ──► Buyer creates Order (no tripId) ──► Escrow initiated ──► Escrow released
```

**Create order (no trip)** — same as above but omit `tripId`
- Assert: sync `status: "success"`, order created without `tripId`

**Initiate escrow** — same flow as above
- Assert: escrow works without an associated trip

**Idempotency test**: Send the same `POST /escrow/initiate` twice with the same `idempotencyKey`
- Assert: Both calls return 200, same `escrowId` (idempotent — no duplicate escrow)

### 7. Escrow Edge Cases

**Dispute flow**
- Initiate escrow (new order)
- Call `POST /escrow/dispute` within dispute window
- Assert: 200, `status: "disputed"`, `disputeId` present

**Refund flow**
- Initiate escrow (new order)
- Call `POST /escrow/refund`
- Assert: 200, `status: "refunded"`, `refundAmount` matches original

### 8. Web App Smoke (Playwright)

Target: `https://dev-app.weredaconnect.com`

| Test | Steps | Assert |
|------|-------|--------|
| App loads | Navigate to URL | Flutter CanvasKit renders — no crash page |
| Login screen renders | Wait for login form | Phone input + password input visible |
| Buyer login | Fill credentials, submit | Redirected to Buyer dashboard |
| Crop list visible | Navigate to browse crops | At least 1 crop card visible (from API test run) |
| Trip list visible | Navigate to browse trips | At least 1 trip card visible |
| Farmer login | Fill Farmer credentials | Redirected to Farmer dashboard |
| Farmer sees own crops only | Navigate to my crops | Only own listing visible |
| Language toggle | Switch to Amharic | UI text changes (key strings rendered in Amharic) |

---

## Execution Order & Dependencies

Tests run in this order to satisfy dependencies:

```
auth (register + login) ──► farmer-create ──► trucker-create ──► buyer-browse
                                                                       │
                                              ┌────────────────────────┤
                                              │                        │
                                         buyer-order             buyer-order
                                         (with trip)            (without trip)
                                              │
                                         escrow-full-flow
                                         escrow-dispute
                                         escrow-refund
```

Shared state (tokens, IDs) is passed between tests via a **test fixture** file written during setup and read by later tests. No test mutates data created by another test's assertions.

---

## Phone Number Strategy (Test Isolation)

To avoid phone uniqueness conflicts across parallel runs:

```typescript
// utils/test-data.ts
const runId = (process.env.GITHUB_RUN_NUMBER ?? Date.now().toString()).slice(-4).padStart(4, '0');

export const testPhones = {
  farmer:  `+2519002${runId}1`,   // e.g. +251900212341
  trucker: `+2519002${runId}2`,
  buyer:   `+2519002${runId}3`,
};
```

These phones use the `+2519002XXXX` prefix range (distinct from the permanent dev test users at `+2519001XXXXX`). They accumulate in the dev Cognito pool — acceptable for dev, where pool is periodically reset.

---

## GitHub Actions Workflow Design

```yaml
# .github/workflows/smoke-tests.yml
name: Smoke Tests (AWS Dev)

on:
  push:
    branches: [main]
  workflow_dispatch:

concurrency:
  group: smoke-tests
  cancel-in-progress: false  # never cancel in-progress smoke tests

jobs:
  api-smoke:
    name: API Smoke Tests
    runs-on: ubuntu-latest
    timeout-minutes: 15
    environment: dev           # gates secrets to dev environment
    env:
      API_BASE_URL: ${{ secrets.API_BASE_URL }}
      COGNITO_USER_POOL_ID: ${{ secrets.COGNITO_USER_POOL_ID }}
      COGNITO_CLIENT_ID: ${{ secrets.COGNITO_CLIENT_ID }}
      COGNITO_REGION: ${{ secrets.COGNITO_REGION }}
    steps:
      - checkout, node setup, npm install
      - run: npx playwright test tests/api/ --reporter=github

  web-smoke:
    name: Web App Smoke Tests (Playwright)
    runs-on: ubuntu-latest
    timeout-minutes: 15
    needs: api-smoke          # run after API tests to guarantee seed data exists
    environment: dev
    env:
      WEB_APP_URL: https://dev-app.weredaconnect.com
      API_BASE_URL: ${{ secrets.API_BASE_URL }}
      ...
    steps:
      - checkout, node setup, npm install
      - install-playwright-browsers: chromium only (fastest)
      - run: npx playwright test tests/web/ --reporter=github

  notify-on-failure:
    name: Notify on Failure
    needs: [api-smoke, web-smoke]
    if: failure()
    runs-on: ubuntu-latest
    steps:
      - post GitHub check annotation with failure summary
```

---

## Secrets Required

All already exist in the `dev` GitHub environment from `setup-gh-secrets.sh`:

| Secret | Used By |
|--------|---------|
| `API_BASE_URL` | All API tests |
| `COGNITO_USER_POOL_ID` | Registration (may be used for admin confirm) |
| `COGNITO_CLIENT_ID` | Auth token exchange |
| `COGNITO_REGION` | Auth SDK |
| `WEBAPP_CF_DISTRIBUTION_ID` | Web smoke (base URL derivation) |

No new secrets needed.

---

## Offline Behavior

Not applicable — this suite is explicitly online-only, testing the AWS dev environment. It does not test offline paths (those are covered by unit + integration tests in `pr-validation.yml`).

---

## Localization

No new UI strings — this is test infrastructure only.

---

## Risks & Mitigations

| Risk | Impact | Mitigation |
|------|--------|-----------|
| SNS sandbox blocks real OTP delivery | Password reset confirmation untestable | Test API contract shape only; extend when T-SEC-SNS resolved |
| Fayda eKYC required for registration | Registration tests blocked | `DEV_FAYDA_BYPASS=true` already set in dev Lambda env |
| Escrow/delivery state machine requires specific preconditions | Test ordering fragile | Explicit dependency chain (`needs:`) in workflow; shared fixture file for IDs |
| Flutter web CanvasKit loading slow (>30s on cold start) | Web Playwright tests time out | Set `waitForTimeout: 45000` for initial page load; skip animation wait |
| Phone number accumulation in dev Cognito | Pool gets cluttered | Acceptable for dev; add a periodic Cognito cleanup Lambda in Sprint 3 |
| Duplicate smoke runs on rapid merges | Concurrent phone conflicts | Use `GITHUB_RUN_NUMBER` (unique per run) not wall-clock time |
| Telebirr mock gateway non-deterministic | Escrow release may vary | `TELEBIRR_MOCK=true` in dev — mock is synchronous and predictable |

---

## Out of Scope

- Admin dashboard smoke tests (covered by existing Playwright E2E suite in `admin/`)
- Offline/sync flows (covered by mobile unit tests)
- Performance testing (not a smoke test concern)
- Staging or production environments (intentionally dev-only to avoid polluting prod data)

---

## Acceptance Criteria

- [ ] Workflow triggers on every merge to `main` and NOT on PRs
- [ ] All 3 roles can register (with `DEV_FAYDA_BYPASS`)
- [ ] All 3 roles can login and receive valid JWT
- [ ] Password reset API returns correct shapes (initiate + error on bad code)
- [ ] Farmer creates crop; only sees own crops; second farmer cannot see it
- [ ] Trucker creates trip; only sees own trips; second trucker cannot see it
- [ ] Buyer sees ALL crops and ALL trips
- [ ] Buyer creates order + escrow (with trip): full release flow passes
- [ ] Buyer creates order + escrow (without trip): direct purchase passes
- [ ] Idempotency key prevents duplicate escrow creation
- [ ] Escrow dispute flow returns `status: "disputed"`
- [ ] Escrow refund flow returns `status: "refunded"`
- [ ] Web app loads without crash in Playwright (Chromium)
- [ ] Buyer login via web app succeeds
- [ ] Crop list and trip list visible on web app after login
- [ ] Farmer login shows only own crops on web app
- [ ] Total CI runtime under 15 minutes

---

## Implementation Order (suggested)

1. `smoke-tests/` scaffolding — `package.json`, `tsconfig.json`, `playwright.config.ts`
2. `utils/api-client.ts` + `utils/test-data.ts`
3. `tests/api/auth.spec.ts` — validate all 3 roles
4. `tests/api/farmer.spec.ts` + `tests/api/trucker.spec.ts` — role isolation
5. `tests/api/buyer.spec.ts` — browse + visibility
6. `tests/api/escrow.spec.ts` — full transaction flow
7. `tests/web/*.spec.ts` — Playwright web app
8. `.github/workflows/smoke-tests.yml` — wire it all together
9. Verify first green run on a manual `workflow_dispatch`
