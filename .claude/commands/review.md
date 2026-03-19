Review recent changes against WeredaConnect quality criteria.

**MANDATORY**: Read `.ai/completeness-rules.md` first — it defines the role-based visibility rules and platform pair list.

Steps:
1. Run `git diff HEAD~1..HEAD` (or `git diff --cached` if uncommitted) to get the changeset

2. **CRITICAL CHECKS (automatic FAIL if violated):**

   **Role-based visibility** (read `.ai/completeness-rules.md` Rule 1):
   - Farmer can only see **own** crop listings (not other farmers')
   - Trucker can only see **own** trip listings (not other truckers')
   - Buyer can see **ALL** crop listings and **ALL** trip listings from anyone
   - Check: server queries, mobile browse screens, BLoC data-fetching, API contracts
   - Any code that shows listings/trips to the wrong role or without proper filtering is CRITICAL FAIL

   **Cross-platform consistency** (read `.ai/completeness-rules.md` Rule 2):
   - If a platform-paired file changed, was its counterpart updated?
   - If shared code changed, does web build still pass?
   - Use the canonical pair table from `.ai/completeness-rules.md` — do NOT maintain a separate list

3. **Standard checks:**

   **Offline-first**: Does it work without internet? Is Isar used before remote calls?
   **AOT compliance** (server .cs files): No Newtonsoft.Json, Moq, EF Core, reflection APIs
   **Localization** (mobile .dart files): All UI strings use AppLocalizations, all 3 .arb files updated
   **Performance**: Works on 2GB RAM Android 8+? Data transfer minimized?
   **Security**: No hardcoded secrets, input validation at boundaries
   **Cross-module**: Server DTOs match mobile models? API contracts aligned?
   **Tests**: Changed code has corresponding test updates?

4. Output a structured review:

```
## Review Summary
[PASS/FAIL] — [one-line summary]

## Critical Checks
- [PASS/FAIL] Role-based visibility (all 3 roles)
- [PASS/FAIL] Cross-platform (mobile + web)

## Issues Found
1. [CRITICAL/WARNING/INFO] — [description]
   - File: [path:line]
   - Fix: [suggestion]

## Standard Checks
- [x/!/ ] Offline-first
- [x/!/ ] AOT compliance
- [x/!/ ] Localization (3 languages)
- [x/!/ ] Performance
- [x/!/ ] Security
- [x/!/ ] Cross-module consistency
- [x/!/ ] Tests updated
```

A single FAIL in Critical Checks makes the entire review FAIL. Suggest fixes for all issues.
