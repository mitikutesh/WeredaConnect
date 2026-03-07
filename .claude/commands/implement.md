Implement a feature or fix following the full impact-first pipeline.

The user provides: $ARGUMENTS (description of the task, feature, or bug to implement/fix)

Follow these phases strictly and in order. Do NOT skip any phase.

---

## Phase 1: Impact Analysis

1. Understand the task from the user's description.

2. Read relevant context:
   - `.ai/quick-start.md` for constraints and patterns
   - `specs/contracts/` for any relevant API contracts
   - Existing code in the area being changed

3. Identify ALL files that need modification:
   - Primary files (the direct change targets)
   - Platform counterparts (web stubs, web models):
     - `*_mobile.dart` <-> `*_web.dart`
     - `*_io.dart` <-> `*_stub.dart`
     - `core/database/schemas/*.dart` <-> `core/models/web/*.dart`
     - Shared abstracts/interfaces that both implement
   - Cross-module impacts:
     - Server API/DTO changes -> mobile client + admin dashboard
     - API contract changes -> all three modules
     - Localization -> all 3 language files (en, am, om)
   - Tests that need updating or creating

4. Present the impact summary to the user:

   ```
   ## Impact Analysis: <task description>

   ### Files to Modify
   **mobile/**
   - mobile/lib/features/X/... (primary change)
   - mobile/lib/core/X_web.dart (platform pair)

   **server/**
   - server/src/Models/XDto.cs (if API change)

   **admin/**
   - admin/src/pages/XPage.tsx (if API change)

   **tests/**
   - mobile/test/features/X/...

   ### Platform Pairs Affected
   - sync_queue.dart <-> sync_queue_web.dart

   ### Cross-Module Impact
   - [describe any cross-module effects]

   ### Risk Level: LOW/MEDIUM/HIGH

   Shall I proceed with implementation?
   ```

5. **STOP and wait for user confirmation.** Do not proceed until the user approves.

---

## Phase 2: Implementation

6. Implement the changes as described in the impact summary.
   - Update platform counterparts together (never leave a pair out of sync)
   - Follow existing patterns in the codebase
   - Format all edited files (dart format / dotnet format)

---

## Phase 3: Validation Pipeline

Run all three checks in sequence:

7. **Validate** (cross-platform pairs + builds):
   - Check platform pair consistency for all changed files
   - Check cross-module consistency
   - Run builds for affected modules:
     - mobile: `cd mobile && flutter analyze`
     - mobile web: `cd mobile && flutter build web --no-tree-shake-icons 2>&1 | tail -20` (if shared code changed)
     - server: `cd server && dotnet build --no-restore`
     - admin: `cd admin && npm run build 2>&1 | tail -20`
   - Check localization key parity (en, am, om)

8. **Review** quality criteria:
   - Offline-first: works without internet?
   - AOT compliance: no reflection/banned libs in .cs files?
   - Localization: all UI strings in AppLocalizations?
   - Performance: works on 2GB RAM Android 8+?
   - Security: no hardcoded secrets, input validation at boundaries?
   - Cross-platform: counterparts in sync?
   - Cross-module: API contracts aligned?

9. **Test** affected modules:
   - mobile: `cd mobile && flutter test` (all tests if shared code changed, feature tests otherwise)
   - server: `cd server && dotnet test --no-restore`
   - admin: (if admin has tests)

---

## Phase 4: Fix Loop

10. If ANY issue is found in Phase 3:
    - Fix the issue
    - Re-run ALL THREE checks (validate -> review -> test)
    - Repeat until all pass

11. Report final results:

    ```
    ## Implementation Complete

    ### Changes Made
    - [list of files modified with brief description]

    ### Validation
    - [PASS] Cross-platform pairs
    - [PASS] Builds (mobile, web, server)
    - [PASS] Review (offline-first, AOT, l10n, security)
    - [PASS] Tests (X passed, 0 failed)

    ### Ready for commit
    ```

Do NOT mark the task as complete until all Phase 3 checks pass.
