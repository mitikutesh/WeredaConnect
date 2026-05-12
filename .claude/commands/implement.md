Implement a feature or fix following the full impact-first pipeline.

The user provides: $ARGUMENTS (description of the task, feature, or bug to implement/fix)

**MANDATORY**: Read `.ai/completeness-rules.md` before starting. Every phase must enforce these rules.

Follow these phases strictly and in order. Do NOT skip any phase.

---

## Phase 1: Impact Analysis

1. Understand the task from the user's description.

2. Read relevant context:
   - `.ai/completeness-rules.md` — cross-platform, cross-role, cross-module rules
   - `.ai/quick-start.md` for constraints and patterns
   - `specs/contracts/` for any relevant API contracts
   - Existing code in the area being changed

3. Identify ALL files that need modification:
   - Primary files (the direct change targets)
   - **Platform counterparts**: Check `.ai/completeness-rules.md` Rule 2 for the canonical pair table. If ANY changed file has a counterpart, include it.
   - **All 3 roles**: If the change touches role-dependent code (dashboards, navigation, browse screens, queries, BLoC data-fetching), list the impact on Farmer, Trucker, AND Buyer.
   - **Cross-module impacts**: See `.ai/completeness-rules.md` Rule 3. Server DTO changes → mobile + admin. Contract changes → all modules. L10n → all 3 languages.
   - Tests that need updating or creating

4. Present the impact summary:

   ```
   ## Impact Analysis: <task description>

   ### Files to Modify
   **mobile/**
   - mobile/lib/features/X/... (primary change)
   - mobile/lib/core/X_web.dart (platform pair — MUST update together)

   **server/**
   - server/src/Models/XDto.cs (if API change)

   **tests/**
   - mobile/test/features/X/...

   ### Role Impact
   - Farmer: [how this change affects farmer flows]
   - Trucker: [how this change affects trucker flows]
   - Buyer: [how this change affects buyer flows]
   - [or: "No role-dependent code changed"]

   ### Platform Pairs Affected
   - sync_queue.dart <-> sync_queue_web.dart (both need update)
   - [or: "No platform pairs affected"]

   ### Cross-Module Impact
   - [describe any cross-module effects, or "None"]

   ### Risk Level: LOW/MEDIUM/HIGH
   ```

5. **STOP and wait for user confirmation.** Do not proceed until the user approves.

---

## Phase 2: Implementation

6. Implement the changes as described in the impact summary.
   - **Platform pairs together**: Never leave one side out of sync. If you add a method to `sync_queue.dart`, add a matching stub to `sync_queue_web.dart` in the same step.
   - **All 3 roles**: If changing role-dependent UI or logic, verify each role's code path.
   - Follow existing patterns in the codebase
   - Format all edited files (dart format / dotnet format)

---

## Phase 3: Validation Pipeline

Run all three checks in sequence:

7. **Validate** (cross-platform + cross-role + builds):
   - Check platform pair consistency for all changed files (per `.ai/completeness-rules.md` Rule 2)
   - Check role-based visibility rules (per `.ai/completeness-rules.md` Rule 1)
   - Run builds for affected modules:
     - mobile: `cd mobile && flutter analyze`
     - mobile web: `cd mobile && flutter build web --no-tree-shake-icons 2>&1 | tail -20`
     - server: `cd server && dotnet build --no-restore`
   - Check localization key parity (en, am, om)

8. **Review** quality criteria:
   - Offline-first: works without internet?
   - AOT compliance: no reflection/banned libs in .cs files?
   - Localization: all UI strings in AppLocalizations?
   - **Role-based visibility**: Farmer sees only own, Trucker sees only own, Buyer sees all?
   - **Cross-platform**: web build passes, counterparts in sync?
   - Cross-module: API contracts aligned?

9. **Test** affected modules:
   - mobile: `cd mobile && flutter test`
   - server: `cd server && dotnet test --no-restore`

---

## Phase 4: Fix Loop

10. If ANY issue is found in Phase 3:
    - Fix the issue
    - Re-run ALL THREE checks (validate -> review -> test)
    - Repeat until all pass

11. Report final results with completeness self-check:

    ```
    ## Implementation Complete

    ### Changes Made
    - [list of files modified with brief description]

    ### Completeness Check (all 8 items from .ai/completeness-rules.md)
    - [PASS] All 3 roles verified (Farmer, Trucker, Buyer)
    - [PASS] Both platforms verified (mobile + web build)
    - [PASS] Platform pair counterparts updated
    - [PASS] Cross-module contracts aligned (server ↔ mobile ↔ admin)
    - [PASS] All 3 languages updated (or N/A)
    - [PASS] Tests updated and passing
    - [PASS] Core marketplace flow still works end-to-end
    - [PASS] No dart:io or Isar in shared/web code paths

    ### Validation
    - [PASS] Mobile analyze
    - [PASS] Mobile web build
    - [PASS] Server build
    - [PASS] Tests (X passed, 0 failed)
    ```

Do NOT mark the task as complete until ALL completeness checks pass.
