<!-- AUTO-MIRRORED from .ai/runs/20260610-185446-add-phone-validation-to-login/01-draft-plan.md — do not edit here. -->
<!-- Run: 20260610-185446-add-phone-validation-to-login  •  Mirrored: 2026-06-10T19:23:55+0300 -->

# Plan — Add phone validation to login

- **Run:** 20260610-185446-add-phone-validation-to-login
- **Author:** Claude (Lead Planner)
- **Status:** DRAFT
- **Date:** 2026-06-10

## 1. Objective & Scope

### Objective

Ensure every login surface validates and normalizes the Ethiopian phone number **before** an authentication attempt is made, with clear, localized feedback when the format is wrong.

### Current-state audit (what already exists)

This task is mostly **already implemented** on two of the three login surfaces. The plan below targets the verified gaps, not a rebuild:

| Surface | Validation today | Verdict |
|---|---|---|
| **Mobile/Flutter-web login** (`mobile/lib/features/auth/screens/login_screen.dart`) | Validates via `isValidEthiopianPhone()` and normalizes via `normalizeEthiopianPhone()` (`mobile/lib/core/utils/phone_utils.dart`) before dispatching `AuthLoginRequested`. Covered by `login_screen_test.dart` and `form_validation_test.dart`. | ✅ Done — two small gaps (G2, G3 below) |
| **Server** (`server/src/Functions/LoginFunction.cs`) | Normalizes (`RequestValidator.NormalizePhoneNumber`) then validates (`ValidateLoginRequest` → `AUTH_001`), matching `specs/contracts/auth-api.md`. Covered by `RequestValidatorTests.cs` and `LoginFunctionTests.cs`. | ✅ Done — no change |
| **Admin web** (`admin/src/pages/LoginPage.tsx`) | **None.** Raw input is passed straight to Cognito via `amazon-cognito-identity-js` (`admin/src/lib/auth.ts`). Cognito usernames are E.164, so `0911234567` fails as `invalidCredentials` — misleading UX, and each attempt burns toward Cognito throttling. | ❌ **Gap G1 — primary work** |

### Identified gaps (the actual scope)

- **G1 (Admin):** Add client-side phone validation + normalization to the admin login page, mirroring `phone_utils.dart` semantics (accept `+251XXXXXXXXX`, `00251XXXXXXXXX`, `0XXXXXXXXX`; canonicalize to E.164 before calling Cognito).
- **G2 (Mobile UX):** The empty-phone validator in `login_screen.dart` returns `l10n.phoneNumber` — the field **label** ("Phone Number") — instead of a "required" message. Add a proper `phoneNumberRequired` message (same flaw exists for the password field's empty case; fix only the phone field per task scope, flag password for follow-up).
- **G3 (Mobile robustness):** `_handleLogin` uses `normalizedPhone!` (non-null assertion). Safe today because the validator runs first in the same call, but brittle. Replace with a null-guard that aborts dispatch (no behavior change for valid input).

### Out of scope (explicitly NOT doing)

- Server-side changes — `LoginFunction.cs` and `RequestValidator.cs` already conform to the contract. No contract version bump.
- Registration / forgot-password / reset-password phone validation (already use the same utils; any gaps there are a separate task).
- Changing accepted phone formats, the E.164 canonical form, or Cognito configuration.
- Rate-limiting / lockout logic.
- Fixing the password-field empty-message flaw (flagged, not fixed — keeps the diff phone-only).

## 2. Impact Analysis

| Area | Detail |
|------|--------|
| **Files to add** | `admin/src/lib/phone.ts` — TS port of `normalizeEthiopianPhone`/`isValidEthiopianPhone` (G1); `admin/src/lib/phone.test.ts` — unit tests (see test-harness note in Risks) |
| **Files to modify** | `admin/src/pages/LoginPage.tsx` — validate/normalize before `login()`, inline error display (G1); `admin/src/i18n/en.json`, `am.json`, `om.json` — `auth.phoneRequired`, `auth.invalidPhoneFormat` keys (G1); `mobile/lib/features/auth/screens/login_screen.dart` — required-message + null-guard (G2, G3); `mobile/lib/shared/l10n/app_en.arb`, `app_am.arb`, `app_om.arb` — `phoneNumberRequired` key (G2) |
| **Platform-pair counterparts** | None of the paired files in CLAUDE.md's table are touched. `login_screen.dart` is *shared* mobile+web Dart code, so the **web build gate still applies** (`flutter build web --no-tree-shake-icons`). |
| **Roles affected** | Login is pre-authentication and identical for Farmer, Trucker, and Buyer — no role-conditional logic touched. Role-visibility rules unaffected. Verification still exercises all 3 test users (see Test Plan). Admin is a fourth, separate surface (Cognito `admin` group). |
| **Cross-module impact** | None to `specs/contracts/auth-api.md` — server behavior, error codes (`AUTH_001`), and request shape are unchanged. Admin talks to Cognito directly (outside that contract); normalizing to E.164 client-side aligns it with the canonical phone format in `specs/contracts/user-entity.md`. No mobile↔server DTO changes. |
| **Localization** | Mobile: 1 new key × 3 `.arb` files. Admin: 2 new keys × 3 i18n JSON files. All 3 languages mandatory (Completeness Rule 3). |
| **Tests** | Add: `admin/src/lib/phone.test.ts` (normalization matrix mirrored from `form_validation_test.dart`). Update: `mobile/test/features/auth/screens/login_screen_test.dart` (empty-phone shows required message, not label). Existing `form_validation_test.dart`, `RequestValidatorTests.cs`, `LoginFunctionTests.cs` remain green untouched. |

## 3. Step-by-Step Plan

Each step is independently verifiable.

1. **Add `admin/src/lib/phone.ts`** exporting `normalizeEthiopianPhone(input: string): string | null` and `isValidEthiopianPhone(input: string): boolean`, with semantics identical to `mobile/lib/core/utils/phone_utils.dart` (trim, strip spaces/hyphens, three accepted prefixes, `^\+251\d{9}$` canonical check). Pure function, no dependencies.
   *Verify:* `cd admin && npx tsc --noEmit` passes.
2. **Add admin i18n keys** `auth.phoneRequired` and `auth.invalidPhoneFormat` to `en.json`, `am.json`, `om.json` (message content mirrors mobile's `invalidPhoneFormat`: "Use +251XXXXXXXXX, 00251XXXXXXXXX, or 0XXXXXXXXX").
   *Verify:* keys present in all 3 files; JSON parses.
3. **Wire validation into `admin/src/pages/LoginPage.tsx`**: on submit, if phone is empty → show `t("auth.phoneRequired")`; if `normalizeEthiopianPhone` returns null → show `t("auth.invalidPhoneFormat")`; otherwise call `login(normalizedPhone, password)`. Render the error inline next to the phone input following the page's existing error-display pattern; no Cognito call on validation failure.
   *Verify:* `cd admin && npm run build` passes; manual check that `0911234567` normalizes (Cognito receives `+251911234567`) and `12345` never reaches Cognito.
4. **Add `admin/src/lib/phone.test.ts`** covering: all 3 valid formats → same E.164 output; rejects empty, short, long, non-Ethiopian country code, letters, `+2510…` with wrong digit count; whitespace/hyphen stripping. (See Risks for the missing-test-runner contingency.)
   *Verify:* admin test command passes.
5. **Mobile G2 — `phoneNumberRequired` l10n key**: add to `app_en.arb` ("Phone number is required" + `@` description), `app_am.arb`, `app_om.arb`; change the empty-case return in `login_screen.dart`'s phone validator from `l10n.phoneNumber` to `l10n.phoneNumberRequired`.
   *Verify:* `flutter gen-l10n` (or build) succeeds; `flutter analyze` clean.
6. **Mobile G3 — remove non-null assertion** in `_handleLogin`: `final normalizedPhone = normalizeEthiopianPhone(...); if (normalizedPhone == null) return;` then dispatch. No user-visible change (validator already blocks this path).
   *Verify:* `flutter analyze` clean; existing login tests green.
7. **Update `mobile/test/features/auth/screens/login_screen_test.dart`**: assert empty-phone submit shows the new required message; assert valid local-format input still dispatches normalized phone (existing cases keep passing).
   *Verify:* `cd mobile && flutter test test/features/auth/`.
8. **Run completeness gates**: `cd mobile && flutter analyze && flutter test`; `cd mobile && flutter build web --no-tree-shake-icons 2>&1 | tail -20`; `cd admin && npm run build`; `cd server && dotnet build --no-restore && dotnet test --no-restore` (expected no-op green — proves no server regression).

## 4. Risks & Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| TS port of phone normalization drifts from Dart/C# semantics (3 implementations of one rule) | Medium | Medium | Mirror the exact test matrix from `form_validation_test.dart` into `phone.test.ts`; comment in `phone.ts` pointing at `phone_utils.dart` + `RequestValidator.cs` as siblings |
| Admin has **no test runner** (no vitest/jest in `package.json`) | High | Low | Add `vitest` as a devDependency with a minimal config and a `test` script — smallest viable harness. This is a scope addition; if the reviewer rejects it, fall back to documenting manual verification and flagging the missing harness (Completeness Rule 4 requires tests to be at least *flagged*) |
| Amharic/Oromo translations are machine-drafted | Medium | Low | Keep messages short/simple (low-literacy guideline); flag the 4 non-English strings for native-speaker review in the PR description |
| New `.arb` key breaks codegen on web build | Low | Medium | Web build is an explicit gate (step 8) |
| Admin normalization changes the username sent to Cognito for admins who previously typed E.164 | Low | Low | Normalization is identity for already-E.164 input; only previously-*failing* formats change behavior (they start working) |

**Completeness-rule gates:**

1. **All 3 roles** — login is role-agnostic pre-auth code; still smoke-tested with all 3 test users (Test Plan §E2E).
2. **Platform pairs** — no paired file touched; shared-Dart change gated by the web build.
3. **Cross-module sync** — no contract/DTO change; l10n updated in all 3 languages on both mobile (.arb) and admin (i18n JSON).
4. **Tests follow code** — every modified production file has a named test add/update (steps 4, 7); the admin harness gap is addressed, not silently skipped.
5. **Core flow** — flow begins after login; only pre-auth input handling changes, and the server path is untouched. E2E login per role confirms the entry point still works.

## 5. Test Plan

**Unit**
- `admin/src/lib/phone.test.ts`: valid `+251912345678` / `00251912345678` / `0912345678` → `+251912345678`; invalid: empty, `12345`, `+2519123456789`, `+1555…`, letters, `091-123` (partial); whitespace/hyphen tolerance.
- `mobile/test/core/validation/form_validation_test.dart`: existing phone matrix — must stay green (regression guard for `phone_utils.dart`, which is not modified).
- `server/tests/Validation/RequestValidatorTests.cs` + `Functions/LoginFunctionTests.cs`: untouched, run as regression proof.

**Widget (mobile)**
- Empty phone + submit → shows `phoneNumberRequired` text, no `AuthLoginRequested` dispatched.
- Invalid phone (`12345`) → shows `invalidPhoneFormat`, no dispatch.
- Valid local format (`0912345678`) → dispatches with `+251912345678`.

**Integration/manual (admin)**
- `npm run build` + manual run: invalid phone never reaches Cognito (network tab shows no request); local format logs in successfully against dev Cognito.

**E2E / 3-role verification**
- Against dev API (`POST /auth/login`): log in as Farmer `+251900100001`, Trucker `+251900100002`, Buyer `+251900100003` (password per `.ai/test-users.md`) — all succeed; one request with `0900100001` for each also succeeds (normalization path), proving identical behavior across roles.

**Offline behavior**
- Mobile validation is fully client-side (no network needed to show errors). Offline cached login path (`offline_auth_cache`) keys on the normalized phone the screen already produces — verified by existing AuthBloc/offline tests staying green; G3's guard cannot dispatch an unnormalized phone.

## 6. Rollback Plan

- All changes are client-side and additive; **no server, schema, contract, or infra changes**, so no data migration or API rollback concerns.
- Revert is a single `git revert` of the feature commit(s); mobile and admin changes are independent commits (`fix(mobile): …`, `feat(admin): …`) so either surface can be reverted alone.
- Unused l10n keys left behind by a partial revert are harmless (dead keys don't break codegen).
- Admin deploys are static-site artifacts — redeploy the previous build restores prior behavior immediately.

## 7. Self-Review (Claude)

- **Biggest judgment call:** the task says "add phone validation to login," but mobile and server already have it. I scoped to the verified gaps (admin page, mobile empty-message, non-null assertion) rather than inventing redundant work. If the task author meant a *specific* surface, the admin page is the only one actually missing validation — but the orchestrator should confirm intent if context exists beyond the one-line description.
- **Weakest step:** Step 4 / the vitest addition. Introducing a test harness to a module that has none is a real scope expansion and brushes against "never create files without explicit request." I've made the fallback (flag-and-document) explicit so Codex can rule on it.
- **Third implementation of one rule:** `phone.ts` makes three parallel phone validators (Dart, C#, TS). A shared spec/test matrix mitigates but doesn't eliminate drift risk. A generated/shared source of truth is out of scope here but worth a backlog note.
- **Assumption:** admin Cognito usernames are E.164 phone numbers. Strongly implied by `getCurrentUser()` reading `payload.phone_number` and the platform-wide convention, but I did not verify the User Pool config in `infra/`. If admins log in with non-phone usernames, G1's validation would *block* them — the implementer must confirm against `infra/lib/stacks/` (Cognito pool definition) before wiring step 3.
- **Deliberately not fixed:** password empty-case shows the label too; register/forgot-password screens repeat the same pattern. Flagged for a follow-up task to keep this diff reviewable.
- **AuthBloc defense-in-depth** (normalizing inside `_onAuthLoginRequested`) was considered and dropped: only one dispatch site exists and the mandate forbids touching business logic beyond the task. Listed here so the reviewer sees it was a choice, not an oversight.
- **L10n quality:** I can draft Amharic/Afan Oromo strings, but they need native review — called out in Risks rather than hidden.
