<!-- TEMPLATE: implementation plan. Authored by Claude (Lead Planner). -->
<!-- Filled copy lives at .ai/runs/<run_id>/01-draft-plan.md & 05-final-plan.md -->
<!-- and is mirrored to docs/ai-plans/draft-plan.md / final-plan.md. -->

# Plan — <task title>

- **Run:** <run_id>
- **Author:** Claude (Lead Planner)
- **Status:** DRAFT | FINAL
- **Date:** <ISO date>

## 1. Objective & Scope

What this change accomplishes. **Out of scope:** what it deliberately does NOT do.

## 2. Impact Analysis

| Area | Detail |
|------|--------|
| Files to add/modify | `path` — reason |
| Platform-pair counterparts | mobile ↔ web files that must move together |
| Roles affected | Farmer / Trucker / Buyer (which, and how) |
| Cross-module impact | server ↔ mobile ↔ admin contract effects |
| Localization | keys to add in all 3 `.arb` files (if UI strings) |
| Tests | unit / integration / E2E to add or update |

## 3. Step-by-Step Plan

1. …
2. …

Each step should be independently verifiable.

## 4. Risks & Mitigations

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| | | | |

Explicitly address the 5 completeness-rule gates (3 roles, platform pairs,
cross-module sync, tests-follow-code, core flow).

## 5. Test Plan

How correctness is proven, including how **all 3 roles** are verified and how
offline behavior is exercised.

## 6. Rollback Plan

How to revert safely if this ships and misbehaves.

## 7. Self-Review (Claude)

Candid pass over the plan: assumptions, gaps, weakest steps, anything a reviewer
would legitimately flag.

<!-- FINAL plan only -->
## 8. Changes Since Draft

What changed after Codex's review and why. List accepted findings (with the
revision) and any rejected/disputed findings (with rationale).
