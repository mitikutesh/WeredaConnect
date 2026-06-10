<!-- TEMPLATE: execution summary. Authored by Claude (Lead Executor). -->
<!-- Filled copy lives at .ai/runs/<run_id>/06-execution-summary.md. -->
<!-- The actual code change is captured separately as 07-implementation.diff. -->

# Execution Summary — <task title>

- **Run:** <run_id>
- **Author:** Claude (Lead Executor)
- **Base ref:** <git sha the diff is taken against>
- **Date:** <ISO date>

## Files Changed

| File | Change | Why |
|------|--------|-----|
| `path` | added / modified / deleted | one line |

## Plan Coverage

| Plan step | Status | Notes |
|-----------|--------|-------|
| 1. … | done / partial / skipped | |

## Completeness Self-Check (CLAUDE.md)

- [ ] All 3 roles verified (Farmer, Trucker, Buyer)
- [ ] Both platforms verified (mobile + web build)
- [ ] Platform-pair counterparts updated
- [ ] Cross-module contracts aligned (server ↔ mobile ↔ admin)
- [ ] All 3 language files updated (if UI strings changed)
- [ ] Affected tests updated and passing
- [ ] Core marketplace flow still works end-to-end
- [ ] No `dart:io` or Isar in shared/web code paths

## Local Verification

| Command | Result |
|---------|--------|
| `dotnet build` / `flutter analyze` / `flutter test` / `cdk synth` | pass / fail / not run (why) |

## Deviations From Plan

Anything implemented differently than the finalized plan, with rationale.

## Follow-ups / Known Gaps

Items intentionally deferred (with reasoning). These do NOT include anything the
plan required — those must be done here.
