Review recent changes against WeredaConnect quality criteria.

Steps:
1. Run `git diff HEAD~1..HEAD` (or `git diff --cached` if uncommitted) to get the changeset
2. Analyze every changed file against these criteria:

   **Offline-first**: Does it work without internet? Is Isar used before remote calls?
   **AOT compliance** (server .cs files): No Newtonsoft.Json, Moq, EF Core, reflection APIs
   **Localization** (mobile .dart files): All UI strings use AppLocalizations, all 3 .arb files updated
   **Performance**: Works on 2GB RAM Android 8+? Data transfer minimized?
   **Security**: No hardcoded secrets, input validation at boundaries

   **Role-based visibility** (HARD REQUIREMENT — any violation is FAIL):
   - Farmer can only see **own** crop listings (not other farmers')
   - Trucker can only see **own** trip listings (not other truckers')
   - Buyer can see **ALL** crop listings and **ALL** trip listings from anyone
   - Check server queries, mobile browse screens, BLoC data-fetching, and API contracts
   - Any code that shows listings/trips to the wrong role or without proper filtering is a CRITICAL issue

3. Output a structured review with PASS/FAIL per criterion and specific issues with file:line references
4. Suggest fixes for any issues found
