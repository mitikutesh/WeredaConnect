<!-- AUTO-MIRRORED from .ai/runs/20260610-185446-add-phone-validation-to-login/05-final-plan.md — do not edit here. -->
<!-- Run: 20260610-185446-add-phone-validation-to-login  •  Mirrored: 2026-06-10T19:31:56+0300 -->

# Plan — Add phone validation to login (FINAL)

- **Run:** 20260610-185446-add-phone-validation-to-login
- **Author:** Claude (Lead Planner)
- **Status:** FINAL — incorporates Codex review (F1–F3, all accepted)
- **Date:** 2026-06-10
- **Branch:** multi-agent-set-up · **Base ref:** 605a5bab110d8d2d566a83aa2405e5f6b3656812

## 1. Objective & Scope

### Objective

Ensure every login surface validates and normalizes the Ethiopian phone number **before** an authentication attempt is made, with clear, localized feedback when the format is wrong.

### Current-state audit (what already exists)

| Surface | Validation today | Verdict |
|---|---|---|
| **Mobile/Flutter-web login** (`mobile/lib/features/auth/screens/login_screen.dart`) | Validates via `isValidEthiopianPhone()` and normalizes via `normalizeEthiopianPhone()` (`mobile/lib/core/utils/phone_utils.dart`) before dispatching `AuthLoginRequested`. Covered by `login_screen_test.dart` and `form_validation_test.dart`. | ✅ Done — two small gaps (G2, G3) |
| **Server** (`server/src/Functions/LoginFunction.cs`) | Normalizes (`RequestValidator.NormalizePhoneNumber`) then validates (`ValidateLoginRequest` → `AUTH_001`), matching `specs/contracts/auth-api.md`. Covered by `RequestValidatorTests.cs` and `LoginFunctionTests.cs`. | ✅ Done — no change |
| **Admin web** (`admin/src/pages/LoginPage.tsx`) | **None.** Raw input passed straight to Cognito via `amazon-cognito-identity-js` (`admin/src/lib/auth.ts`). Additionally (F1) the page pairs `VITE_COGNITO_USER_POOL_ID` with the **admin** client — a likely pool/client mismatch versus the dedicated `AdminUserPool` in `infra/lib/stacks/api-stack.ts`. | ❌ **Gaps G0 + G1 — primary work** |

### Identified gaps (the actual scope)

- **G0 (Admin auth wiring — from F1):** The admin SPA pairs `VITE_COGNITO_USER_POOL_ID` (fed by `secrets.COGNITO_USER_POOL_ID` in `.github/workflows/deploy-admin.yml:57-58`) with `VITE_COGNITO_ADMIN_CLIENT_ID`, but `AdminClient` belongs to the separate `AdminUserPool` (`api-stack.ts:127,168`; CfnOutputs `AdminUserPoolId`/`AdminClientId` at lines 1181–1187). Verify and repair: introduce `VITE_COGNITO_ADMIN_USER_POOL_ID` sourced from the `AdminUserPoolId` stack output. Without this, admin login can fail for reasons unrelated to phone format and the manual verification of G1 is meaningless.
- **G1 (Admin validation):** Add client-side phone validation + normalization to the admin login page, mirroring `phone_utils.dart` semantics (accept `+251XXXXXXXXX`, `00251XXXXXXXXX`, `0XXXXXXXXX`; canonicalize to E.164 before calling Cognito). Per F2, the form must use `noValidate` so the localized errors actually render instead of browser-native constraint tooltips, and validation must run before any `loading` state change.
- **G2 (Mobile UX):** The empty-phone validator in `login_screen.dart` returns `l10n.phoneNumber` — the field **label** ("Phone Number") — instead of a "required" message. Add a proper `phoneNumberRequired` message (the password field has the same flaw; flagged for follow-up, not fixed here).
- **G3 (Mobile robustness):** `_handleLogin` uses `normalizedPhone!` (non-null assertion). Safe today because the validator runs first in the same call, but brittle. Replace with a null-guard that aborts dispatch (no behavior change for valid input).

### Out of scope (explicitly NOT doing)

- Server-side changes — `LoginFunction.cs` and `RequestValidator.cs` already conform to the contract. No contract version bump.
- Registration / forgot-password / reset-password phone validation (same utils are already in use there; any gaps are a separate task).
- Changing accepted phone formats, the E.164 canonical form, or Cognito pool/client *configuration* in infra (G0 only re-points the SPA at the already-existing admin pool; no CDK change expected — if Step 0 verification shows infra itself is wrong, that becomes a stop-and-report, not silent scope creep).
- Rate-limiting / lockout logic.
- Fixing the password-field empty-message flaw (flagged, not fixed — keeps the diff phone-only).

## 2. Impact Analysis

| Area | Detail |
|------|--------|
| **Files to add** | `admin/src/lib/phone.ts` — TS port of `normalizeEthiopianPhone`/`isValidEthiopianPhone` (G1); `admin/src/lib/phone.test.ts` — unit matrix; `admin/src/pages/LoginPage.test.tsx` — component test (F3); `admin/vitest.config.ts` — minimal harness (F3) |
| **Files to modify** | `admin/src/lib/auth.ts` — env var rename to `VITE_COGNITO_ADMIN_USER_POOL_ID` + error message at `auth.ts:13` (G0); `.github/workflows/deploy-admin.yml` — supply `secrets.COGNITO_ADMIN_USER_POOL_ID` (G0); admin `.env.example` / setup docs — document new secret and var (G0); `admin/package.json` — vitest devDeps + `test` script (F3); `admin/src/pages/LoginPage.tsx` — `noValidate`, validate-then-load, inline localized errors (G1, F2); `admin/src/i18n/en.json`, `am.json`, `om.json` — `auth.phoneRequired`, `auth.invalidPhoneFormat` (G1); `mobile/lib/features/auth/screens/login_screen.dart` — required-message + null-guard (G2, G3); `mobile/lib/shared/l10n/app_en.arb`, `app_am.arb`, `app_om.arb` — `phoneNumberRequired` (G2); `mobile/test/features/auth/screens/login_screen_test.dart` |
| **Platform-pair counterparts** | No CLAUDE.md-paired file touched. `login_screen.dart` is shared mobile+web Dart, so the **web build gate still applies** (`flutter build web --no-tree-shake-icons`). |
| **Roles affected** | Login is pre-authentication and identical for Farmer, Trucker, and Buyer — no role-conditional logic touched; role-visibility rules unaffected. All 3 test users still exercised (Test Plan §E2E). Admin is a fourth, separate Cognito surface. |
| **Cross-module impact** | No change to `specs/contracts/auth-api.md` — server behavior, error codes (`AUTH_001`), and request shape unchanged. Admin↔infra: G0 consumes the existing `AdminUserPoolId` CfnOutput — read-only dependency, no CDK edit. GitHub secret `COGNITO_ADMIN_USER_POOL_ID` must be created by the user (documented; secrets aren't repo-editable). Normalizing to E.164 client-side aligns admin with the canonical phone format in `specs/contracts/user-entity.md`. |
| **Localization** | Mobile: 1 new key × 3 `.arb` files. Admin: 2 new keys × 3 i18n JSON files. All 3 languages mandatory (Completeness Rule 3). |
| **Tests** | Add: `phone.test.ts`, `LoginPage.test.tsx` (F3). Update: mobile `login_screen_test.dart`. Existing `form_validation_test.dart`, `RequestValidatorTests.cs`, `LoginFunctionTests.cs` remain green untouched (regression proof). |

## 3. Step-by-Step Plan

Each step is independently verifiable.

0. **G0 / F1 — Verify & fix admin Cognito wiring (precondition).**
   a. Read the deployed dev stack outputs (`aws cloudformation describe-stacks` or CDK outputs) and confirm which pool ID the current `COGNITO_USER_POOL_ID` secret value corresponds to versus `AdminUserPoolId`.
   b. Rename in code regardless (clarity + correctness): `admin/src/lib/auth.ts` reads `VITE_COGNITO_ADMIN_USER_POOL_ID`; update the missing-config error message at `auth.ts:13`.
   c. `deploy-admin.yml`: `VITE_COGNITO_ADMIN_USER_POOL_ID: ${{ secrets.COGNITO_ADMIN_USER_POOL_ID }}`.
   d. Document the new GitHub secret (value = `AdminUserPoolId` output) in the admin README/`.env.example`.
   *Verify:* `cd admin && npx tsc --noEmit`; local `.env` with admin pool ID + admin client ID logs in against dev Cognito. **Step 3's manual verification is blocked until this passes.**
1. **Add `admin/src/lib/phone.ts`** exporting `normalizeEthiopianPhone(input: string): string | null` and `isValidEthiopianPhone(input: string): boolean`, semantics identical to `phone_utils.dart` (trim, strip spaces/hyphens, three accepted prefixes, `^\+251\d{9}$` canonical check). Pure, dependency-free; header comment naming `phone_utils.dart` and `RequestValidator.cs` as sibling implementations.
   *Verify:* `npx tsc --noEmit` passes.
2. **Add admin i18n keys** `auth.phoneRequired` and `auth.invalidPhoneFormat` to `en.json`, `am.json`, `om.json` (content mirrors mobile's `invalidPhoneFormat`: "Use +251XXXXXXXXX, 00251XXXXXXXXX, or 0XXXXXXXXX").
   *Verify:* keys present in all 3 files; JSON parses.
3. **Wire validation into `LoginPage.tsx` (G1 + F2):** add `noValidate` to the `<form>` (keep `required` attrs for `aria-required` semantics). In `handleSubmit`, **before** `setLoading(true)`: empty phone → set inline `t("auth.phoneRequired")` and return; `normalizeEthiopianPhone` returns null → `t("auth.invalidPhoneFormat")` and return; otherwise clear field error, `setLoading(true)`, call `login(normalizedPhone, password)`. Error rendered inline next to the phone input per the page's existing error-display pattern; no Cognito call and no loading flicker on validation failure.
   *Verify:* `cd admin && npm run build`; manual (post-Step-0): `0911234567` → Cognito receives `+251911234567`; `12345` → localized error, network tab shows no request; empty submit → localized required message (not a browser tooltip).
4. **Add admin test harness + tests (F3, firm — no longer contingent):** `vitest`, `@testing-library/react`, `@testing-library/user-event`, `jsdom` as devDependencies; minimal `vitest.config.ts`; `"test": "vitest run"` script. Tests:
   - `phone.test.ts` — matrix mirrored from `form_validation_test.dart`: all 3 valid formats → same E.164 output; rejects empty, short, long, non-Ethiopian country code, letters, partial input; whitespace/hyphen stripping.
   - `LoginPage.test.tsx` (mocked `useAuth`/`useLocale`) — empty phone → localized required message, `login()` not called, button not stuck in loading; `12345` → localized format error, `login()` not called; `0912345678` → `login("+251912345678", …)`.
   *Verify:* `cd admin && npm test` passes.
5. **Mobile G2 — `phoneNumberRequired` l10n key**: add to `app_en.arb` ("Phone number is required" + `@` description), `app_am.arb`, `app_om.arb`; change the empty-case return in `login_screen.dart`'s phone validator from `l10n.phoneNumber` to `l10n.phoneNumberRequired`.
   *Verify:* l10n codegen succeeds; `flutter analyze` clean.
6. **Mobile G3 — remove non-null assertion** in `_handleLogin`: `final normalizedPhone = normalizeEthiopianPhone(...); if (normalizedPhone == null) return;` then dispatch. No user-visible change (validator already blocks this path).
   *Verify:* `flutter analyze` clean; existing login tests green.
7. **Update `login_screen_test.dart`:** empty-phone submit shows the new required message (not the label); valid local format (`0912345678`) still dispatches `+251912345678` (existing cases keep passing).
   *Verify:* `cd mobile && flutter test test/features/auth/`.
8. **Run completeness gates:** `cd mobile && flutter analyze && flutter test`; `cd mobile && flutter build web --no-tree-shake-icons 2>&1 | tail -20`; `cd admin && npm run build && npm test`; `cd server && dotnet build --no-restore && dotnet test --no-restore` (expected no-op green — proves no server regression).

## 4. Risks & Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| `COGNITO_USER_POOL_ID` secret already (correctly) holds the admin pool ID under a misleading name — G0 "fix" then only renames | Medium | Low | Step 0a verifies against stack outputs first; rename proceeds either way for clarity, behavior verified by live login |
| GitHub secret change is user-side; deploy breaks if code merges before the secret exists | Medium | Medium | PR description carries an explicit "create `COGNITO_ADMIN_USER_POOL_ID` secret before merging" checklist item; missing config fails loudly via the existing error in `auth.ts` |
| TS port of phone normalization drifts from Dart/C# semantics (3 implementations of one rule) | Medium | Medium | Identical test matrix mirrored from `form_validation_test.dart` into `phone.test.ts`; sibling-pointer comments in `phone.ts`; consolidation noted for backlog |
| `noValidate` removes browser-native fallback for the password-empty case | Low | Low | Existing Cognito error path still surfaces a message; localized password-required handling is the already-flagged follow-up |
| Amharic/Oromo translations machine-drafted | Medium | Low | Short, simple strings (low-literacy guideline); 4 non-English strings flagged for native-speaker review in PR description |
| New `.arb` key breaks codegen on web build | Low | Medium | Web build is an explicit gate (Step 8) |
| Vitest harness addition expands the diff | Low | Low | Mandated by review (F3); minimal config, devDependencies only |
| Admin normalization changes the username sent to Cognito for admins who previously typed E.164 | Low | Low | Normalization is identity for already-E.164 input; only previously-failing formats change behavior (they start working) |

**Completeness-rule gates:**

1. **All 3 roles** — login is role-agnostic pre-auth code; still smoke-tested with all 3 test users (Test Plan §E2E).
2. **Platform pairs** — no paired file touched; shared-Dart change gated by the web build.
3. **Cross-module sync** — no contract/DTO change; l10n updated in all 3 languages on both mobile (`.arb`) and admin (i18n JSON); admin↔infra dependency is read-only on an existing CfnOutput.
4. **Tests follow code** — every modified production file has a named test add/update, including `LoginPage.test.tsx` (F3); the admin harness gap is closed, not skipped.
5. **Core flow** — flow begins after login; only pre-auth input handling changes and the server path is untouched; E2E login per role confirms the entry point still works.

## 5. Test Plan

**Unit**
- `admin/src/lib/phone.test.ts`: `+251912345678` / `00251912345678` / `0912345678` → `+251912345678`; invalid: empty, `12345`, `+2519123456789`, `+1555…`, letters, partial `091-123`; whitespace/hyphen tolerance.
- `mobile/test/core/validation/form_validation_test.dart`: existing phone matrix stays green (regression guard — `phone_utils.dart` is not modified).
- `server/tests/Validation/RequestValidatorTests.cs` + `Functions/LoginFunctionTests.cs`: untouched, run as regression proof.

**Component (admin, new — F3)**
- `LoginPage.test.tsx`: empty phone → localized required message, no `login()` call, no stuck loading state; invalid phone → localized format error, no `login()` call; `0912345678` → `login("+251912345678", …)`.

**Widget (mobile)**
- Empty phone + submit → `phoneNumberRequired` text shown, no `AuthLoginRequested` dispatched.
- Invalid phone (`12345`) → `invalidPhoneFormat`, no dispatch.
- Valid local format (`0912345678`) → dispatches `+251912345678`.

**Integration/manual (admin, post-Step-0)**
- Invalid phone never reaches Cognito (network tab shows no request); local format logs in against dev Cognito using the **admin** pool; empty submit shows the localized message, not a browser tooltip.

**E2E / 3-role verification**
- Against dev API (`POST /auth/login`): log in as Farmer `+251900100001`, Trucker `+251900100002`, Buyer `+251900100003` (password per `.ai/test-users.md`) — all succeed; repeat with `0900…` local format per role (normalization path), proving identical behavior across roles.

**Offline behavior**
- Mobile validation is fully client-side (no network needed to show errors). Offline cached login (`offline_auth_cache`) keys on the normalized phone the screen already produces — verified by existing AuthBloc/offline tests staying green; G3's guard cannot dispatch an unnormalized phone.

## 6. Rollback Plan

- All changes are client-side and additive; **no server, schema, contract, or CDK changes**, so no data migration or API rollback concerns.
- Mobile and admin land as independent commits (`fix(mobile): …`, `feat(admin): …`, `fix(admin): … cognito wiring` + `ci(admin): …` for the workflow line) so either surface reverts alone via `git revert`.
- G0 rollback: revert the env-var rename commit; the old secret name still exists in GitHub, so the previous (pre-fix) behavior is restored exactly.
- Unused l10n keys left behind by a partial revert are harmless (dead keys don't break codegen).
- Admin deploys are static-site artifacts — redeploying the previous build restores prior behavior immediately.

## 7. Self-Review (Claude, final)

- **Scoping judgment:** the task says "add phone validation to login," but mobile and server already have it. Scope targets the verified gaps (admin page + wiring, mobile empty-message, non-null assertion) rather than redundant rework. The admin page is the only surface that was actually missing validation.
- **F1 closed a hole my draft only hedged on** — Step 0 verifies the pool wiring against deployed stack outputs *before* renaming, so we don't "fix" a secret that was already correct.
- **F2 was a genuine plan bug:** the localized empty-message I promised could never render past native constraint validation. `noValidate` + validate-before-loading is the standard React pattern and keeps `aria-required` intact.
- **F3 hardened a hedge into a commitment;** the harness is minimal (vitest + RTL, devDeps only) and `npm test` is now a Step-8 gate.
- **Residual risks:** the GitHub secret is user-managed (PR checklist mitigates); three parallel phone implementations remain a drift hazard (shared test matrix mitigates; consolidation is a backlog note); 4 non-English strings need native-speaker review.
- **Deliberately not fixed:** password empty-case shows the label too; register/forgot-password screens repeat the same pattern — flagged for a follow-up task to keep this diff reviewable. AuthBloc defense-in-depth normalization was considered and dropped (single dispatch site; mandate forbids business-logic changes beyond the task).

## Changes Since Draft

All three Codex findings were verified against the repo and **accepted**; none rejected.

1. **F1 (accepted — High):** Draft assumed the admin Cognito wiring was correct while admitting it was unverified. Final adds **Step 0** (new gap G0): verify the `COGNITO_USER_POOL_ID` secret against the `AdminUserPoolId` stack output, rename the env var end-to-end to `VITE_COGNITO_ADMIN_USER_POOL_ID` (`auth.ts`, `deploy-admin.yml`, docs), and gate all manual admin verification on it. Verified basis: `admin/src/lib/auth.ts:9-10` pairs the main-pool var with the admin client; `api-stack.ts:127,168,1181-1187` defines the separate admin pool/client and outputs.
2. **F2 (accepted — Medium):** Draft's localized empty-phone error was dead code — native `required` attributes (`LoginPage.tsx:69,81`, no `noValidate` on the form) would block `onSubmit` first, and `setLoading(true)` fired before validation would strand the button. Step 3 now specifies `noValidate` (keeping `required` for `aria-required`), validation **before** `setLoading(true)`, and clean early returns. The new component test asserts both behaviors.
3. **F3 (accepted — Medium):** Draft made the admin test harness contingent on reviewer approval and tested only the `phone.ts` utility. Final makes the vitest + React Testing Library harness **firm** and adds `admin/src/pages/LoginPage.test.tsx` covering the integration failure modes (raw phone reaching Cognito, unrendered error, validation bypass); `npm test` added to the Step-8 gates. Verified basis: `admin/package.json` has no test runner or `test` script.

Secondary edits driven by the acceptances: Impact Analysis gained the G0/F3 files (`auth.ts`, `deploy-admin.yml`, `.env.example`/docs, `vitest.config.ts`, `LoginPage.test.tsx`, `package.json`); Risks gained the misleading-secret-name and user-managed-secret rows; Rollback now covers the env-var rename and split commit types; Out-of-scope clarifies that a CDK-level misconfiguration discovered in Step 0 is a stop-and-report, not silent scope creep.

EVALUATION_STATUS: ALL_RESOLVED
