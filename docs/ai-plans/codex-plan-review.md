<!-- AUTO-MIRRORED from .ai/runs/20260610-185446-add-phone-validation-to-login/02-codex-plan-review.md — do not edit here. -->
<!-- Run: 20260610-185446-add-phone-validation-to-login  •  Mirrored: 2026-06-10T19:28:17+0300 -->

## Assessment

The plan is mostly scoped correctly and does not introduce an obvious role-visibility leak. However, the admin-login work is under-specified in ways that can leave the target surface either still unable to log in or insufficiently verified. Changes are requested.

## Findings

### [F1] Admin Cognito pool wiring is omitted
- Classification: Important
- Description: The plan adds validation to admin login but does not address that admin auth uses `VITE_COGNITO_USER_POOL_ID` with `VITE_COGNITO_ADMIN_CLIENT_ID`.
- Reasoning: `infra/lib/stacks/api-stack.ts` creates a separate admin user pool and admin client, but `.github/workflows/deploy-admin.yml` supplies the normal `COGNITO_USER_POOL_ID` with the admin client. A pool/client mismatch can make valid admin login fail regardless of phone normalization.
- Recommended action: Add a plan step to wire admin login to `AdminUserPoolId`, e.g. `VITE_COGNITO_ADMIN_USER_POOL_ID`, and update deployment/secrets/docs accordingly before manual admin-login verification.
- Risk level: High

### [F2] Empty-phone localized error may never render on admin
- Classification: Important
- Description: `LoginPage.tsx` currently has native `required` attributes on the phone/password inputs. The plan adds custom empty-phone handling but does not say to disable native constraint validation.
- Reasoning: Browser validation can block the React `onSubmit` handler before the plan’s `auth.phoneRequired` logic runs, so the required message may remain browser-native and unlocalized. This conflicts with the task’s “clear, localized feedback” goal and localization rules.
- Recommended action: Add `noValidate` to the form or remove native `required` from the phone input, then validate before setting `loading` or reset `loading` on every early return.
- Risk level: Medium

### [F3] Admin LoginPage behavior is not covered by automated tests
- Classification: Important
- Description: The plan adds `admin/src/lib/phone.test.ts`, but it does not add a test for `LoginPage.tsx` proving invalid input blocks `login()` and valid local input calls `login()` with E.164.
- Reasoning: Completeness Rule 4 requires tests to follow production changes. A pure utility test would not catch the actual integration failure modes in this task: raw phone still passed to Cognito, no localized error, or validation bypassed.
- Recommended action: Add a Vitest/React Testing Library test for `LoginPage.tsx` with mocked `useAuth`/`useLocale`; include package/config/script changes and run `npm test` in the admin gates.
- Risk level: Medium

VERDICT: CHANGES_REQUESTED
