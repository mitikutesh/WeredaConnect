<!-- AUTO-MIRRORED from .ai/runs/20260610-185446-add-phone-validation-to-login/08-codex-code-review.md — do not edit here. -->
<!-- Run: 20260610-185446-add-phone-validation-to-login  •  Mirrored: 2026-06-11T00:00:33+0300 -->

## Assessment

The core login validation changes are mostly correct: admin and mobile normalize before dispatch/auth, l10n was added in all three languages, and no role-visibility or platform-pair HARD rule appears violated. However, the admin Cognito env-var rename is not fully synchronized with the repo’s deployment setup path, and a generated orchestration lock file leaked into the diff.

## Findings

### [F1] Admin secret rename is not reflected in deployment setup docs/tools  (.github/workflows/deploy-admin.yml:59)
- Classification: Important
- Description: The workflow now requires `secrets.COGNITO_ADMIN_USER_POOL_ID`, but the standard secret setup script still only collects/sets `COGNITO_USER_POOL_ID` plus `COGNITO_ADMIN_CLIENT_ID`, and `docs/developer/deployment.md` still documents `VITE_COGNITO_USER_POOL_ID` for admin.
- Reasoning: This leaves the documented deploy path unable to provision the new required admin pool secret. A Vite build can still deploy with the missing env value, after which `auth.ts` fails admin login via missing Cognito config.
- Recommended action: Update `scripts/setup-gh-secrets.sh` to collect `AdminUserPoolId` and set `COGNITO_ADMIN_USER_POOL_ID`; update `docs/developer/deployment.md` to list the new secret and `VITE_COGNITO_ADMIN_USER_POOL_ID` for admin.
- Risk level: Medium

### [F2] Runtime orchestration lock file is included in the diff  (.ai/state/orchestrator.lock/meta:1)
- Classification: Important
- Description: The implementation diff includes `.ai/state/orchestrator.lock/meta`, containing a local PID/run lock.
- Reasoning: `.ai/state/orchestrator.lock` is runtime concurrency state, not source. If committed, it can confuse or block later orchestration runs when the lock PID appears active, and it violates the repo’s own “runtime state, regenerated on every run” convention.
- Recommended action: Remove `.ai/state/orchestrator.lock/meta` from the working tree and add `.ai/state/orchestrator.lock/` to `.gitignore` so future runs do not leak it.
- Risk level: Low

VERDICT: CHANGES_REQUESTED
