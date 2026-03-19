Run build validation for all modules that have changes, including cross-platform, cross-role, and cross-module impact checks.

The user may provide: $ARGUMENTS (specific module: mobile, server, infra, admin, all, or "web" for web-only checks)

**MANDATORY**: Read `.ai/completeness-rules.md` first — it defines the canonical platform pair list and role-based visibility rules.

Steps:

1. Run `git diff --name-only HEAD` and `git diff --name-only --cached` to detect which files have changed.

2. **Cross-platform pair check** (mobile/ changes only):

   Read the canonical pair table from `.ai/completeness-rules.md` Rule 2.

   For each changed file in a pair:
   - Read the changed file to understand what changed (new methods, signature changes, field additions)
   - Read the counterpart file to check if it needs a matching update
   - Report: "[CROSS-PLATFORM] Changed `X` but counterpart `Y` may need updating — [reason]"
   - If the counterpart already has the matching interface, report PASS

   Also check: if a shared abstract/interface file was changed, both implementations must satisfy the new interface.
   Also check: if a barrel file in `core/models/*.dart` was changed, check both the schema and web model it conditionally exports.

3. **Role-based visibility rules check** (CRITICAL — any violation is FAIL):

   Read `.ai/completeness-rules.md` Rule 1. These are **hard requirements**:
   - **Farmer**: Can create crop listings. Can only see **own** listings. Must NOT see other farmers' listings.
   - **Trucker**: Can create trip listings. Can only see **own** trips. Must NOT see other truckers' trips.
   - **Buyer**: Can see **ALL** crop listings and **ALL** trip listings from anyone/anywhere.

   This check applies when changes touch: listings, trips, browse screens, query/repository logic, BLoC data-fetching, navigation, or dashboards.

   Check for violations in:
   - **Server**: Repository/query logic in `server/src/Data/` and handler logic in `server/src/Functions/` — verify list/browse endpoints filter by owner for Farmer/Trucker, return all for Buyer.
   - **Mobile**: Browse/list screens, `my_*` screens — verify Farmer/Trucker see only own data, Buyer sees everything.
   - **Mobile BLoCs/Cubits**: Data-fetching logic must respect role-based filtering.

   Report format:
   - `[PASS] Role-based visibility: Farmer sees only own crop listings`
   - `[FAIL] Role-based visibility: Trucker browse screen shows all trips — must filter to own trips only`

4. **Cross-module impact check**:

   Read `.ai/completeness-rules.md` Rule 3.

   - **Server API changes** (`server/src/Functions/` or `server/src/Models/`):
     Flag: "Server API changed — verify mobile client and admin dashboard are compatible"
   - **API contract changes** (`specs/contracts/`):
     Flag: "API contract changed — server, mobile, and admin may all need updates"
   - **Mobile shared code** (`mobile/lib/core/` or `mobile/lib/shared/`):
     Flag: "Shared mobile code changed — affects both Android and Web platforms"
   - **Localization files** (`mobile/lib/shared/l10n/`):
     Check that all 3 language files have the same keys (en, am, om)

5. **Build validation** for each changed module:

   **Mobile** (mobile/ changes):
   ```bash
   cd mobile && flutter analyze
   ```

   **Mobile Web build** (if mobile/ changes touch shared code, models, core/, or screens):
   ```bash
   cd mobile && flutter build web --no-tree-shake-icons 2>&1 | tail -20
   ```

   **Server** (server/ changes):
   ```bash
   cd server && dotnet build --no-restore
   ```

   **Infra** (infra/ changes):
   ```bash
   cd infra && npx cdk synth --quiet
   ```

6. **Report results**:

   ```
   ## Validation Results

   ### Role-Based Visibility (CRITICAL)
   - [PASS/FAIL] Farmer sees only own crop listings
   - [PASS/FAIL] Trucker sees only own trip listings
   - [PASS/FAIL] Buyer sees all listings and trips
   (Only shown when changes touch role-dependent code)

   ### Cross-Platform Pairs
   - [PASS/WARN] auth_storage_mobile.dart <-> auth_storage_web.dart
   - [WARN] sync_queue.dart changed but sync_queue_web.dart was NOT updated

   ### Cross-Module Impact
   - [INFO] Server DTO changed — mobile and admin may need updating
   - [PASS] No API contract changes detected

   ### Builds
   - [PASS/FAIL] Mobile analyze
   - [PASS/FAIL] Mobile web build
   - [PASS/FAIL] Server build

   ### Action Items
   1. [list any fixes needed]
   ```

7. If any FAIL items found, offer to fix them. Role-based visibility FAILs are CRITICAL and must be fixed before proceeding.

Only run validations for modules with actual changes, unless user specified "all".
