Analyze the impact/blast radius of a file or feature change before making modifications.

The user provides: $ARGUMENTS (a file path, directory, feature name, or description of planned change)

**MANDATORY**: Read `.ai/completeness-rules.md` first — it defines the canonical platform pair list, role-based visibility rules, and cross-module dependency table.

Steps:

1. **Identify the target**:
   - If a file path: analyze that specific file
   - If a directory: analyze all files in it
   - If a feature name: find relevant files in `mobile/lib/features/<name>/`, `server/src/`, `specs/`
   - If a description: identify which files would be affected

2. **Map platform pairs**:

   Read the canonical pair table from `.ai/completeness-rules.md` Rule 2.
   Check if the target file has a platform counterpart that would also need updating.
   Report which counterpart files exist and would need changes.

3. **Check role impact**:

   Read `.ai/completeness-rules.md` Rule 1.
   Does this change touch role-dependent code? Search for: `farmer`, `trucker`, `buyer`, `UserRole`, `role ==`, dashboard/navigation code.
   If yes, flag that ALL 3 roles need verification.

4. **Trace imports/references** (within mobile/):
   Use Grep to find files that import the target. For each importing file, note:
   - Is it a screen/widget? (presentation impact)
   - Is it a BLoC? (state management impact)
   - Is it a repository? (data layer impact)
   - Is it a test? (test needs updating)

5. **Trace cross-module dependencies**:

   Read `.ai/completeness-rules.md` Rule 3.

   If target is in **server/**: Find affected endpoints, check mobile + admin clients, check `specs/contracts/`.
   If target is in **mobile/lib/core/**: List all features that import it, check web platform impact, check test coverage.
   If target is in **specs/contracts/**: Flag ALL modules as potentially affected.

6. **Find related tests**:
   Search `mobile/test/` and `server/tests/` for files that test the changed code.

7. **Output impact report**:

   ```
   ## Impact Analysis: <target>

   ### Direct Changes Required
   - <target_file> (the file itself)
   - <counterpart_file> (platform pair — MUST update together)

   ### Role Impact
   - Farmer: [affected / not affected — why]
   - Trucker: [affected / not affected — why]
   - Buyer: [affected / not affected — why]

   ### Dependent Files
   - mobile/lib/features/X/... (imports target)

   ### Cross-Module Impact
   - server/src/Models/XDto.cs (defines the API shape)
   - specs/contracts/x-api.md (contract definition)

   ### Tests to Update
   - mobile/test/features/X/...
   - server/tests/...

   ### Platform Considerations
   - [WEB] Target uses Isar — web stub must be updated
   - [MOBILE] Target uses dart:io — not available on web

   ### Risk Level: [LOW/MEDIUM/HIGH]
   ```

8. If the user wants to proceed, suggest modification order:
   - Start with contracts/interfaces
   - Then server (if API change)
   - Then mobile platform pair files together
   - Then dependent features
   - Then tests
   - Finally, run `/validate` to confirm nothing broke
