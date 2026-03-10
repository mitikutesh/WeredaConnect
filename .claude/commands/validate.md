Run build validation for all modules that have changes, including cross-platform and cross-module impact checks.

The user may provide: $ARGUMENTS (specific module: mobile, server, infra, admin, all, or "web" for web-only checks)

Steps:

1. Run `git diff --name-only HEAD` and `git diff --name-only --cached` to detect which files have changed.

2. **Cross-platform pair check** (mobile/ changes only):
   Scan the changed files for any file that has a platform counterpart. Flag if one side was changed but not the other:

   Platform pair mappings:
   - `core/auth/auth_storage_mobile.dart` <-> `core/auth/auth_storage_web.dart` (both implement `auth_storage.dart`)
   - `core/auth/offline_auth_cache.dart` <-> `core/auth/offline_auth_cache_web.dart`
   - `core/database/isar_service.dart` <-> `core/database/isar_service_web.dart`
   - `core/sync/sync_executor.dart` <-> `core/sync/sync_executor_web.dart`
   - `core/sync/sync_queue.dart` <-> `core/sync/sync_queue_web.dart`
   - `core/network/cert_pinning_io.dart` <-> `core/network/cert_pinning_stub.dart`
   - Each file in `core/database/schemas/*.dart` (not .g.dart) <-> its counterpart in `core/models/web/*.dart`

   For each changed file in a pair:
   - Read the changed file to understand what changed (new methods, signature changes, field additions)
   - Read the counterpart file to check if it needs a matching update
   - Report: "[CROSS-PLATFORM] Changed `X` but counterpart `Y` may need updating — [reason]"
   - If the counterpart already has the matching interface, report PASS

   Also check: if a shared abstract/interface file was changed (e.g., `auth_storage.dart`), both implementations must satisfy the new interface.

   Also check: if a barrel file in `core/models/*.dart` was changed, check both the schema and web model it conditionally exports.

3. **Cross-module impact check**:
   Detect when changes in one module may require updates in another:

   - **Server API changes** (`server/src/Functions/` or `server/src/Models/`):
     - Flag: "Server API changed — verify mobile client and admin dashboard are compatible"
     - Check `specs/contracts/` for the relevant contract and compare
     - List the specific endpoints/DTOs that changed

   - **API contract changes** (`specs/contracts/`):
     - Flag: "API contract changed — server, mobile, and admin may all need updates"

   - **Mobile shared code** (`mobile/lib/core/` or `mobile/lib/shared/`):
     - If models, services, or network code changed, flag: "Shared mobile code changed — affects both Android and Web platforms"

   - **Admin API client** (`admin/src/lib/api.ts`):
     - Flag: "Admin API client changed — verify it matches server endpoints"

   - **Localization files** (`mobile/lib/shared/l10n/` or `admin/src/i18n/`):
     - Check that all language files have the same keys (en, am, om)
     - For mobile: compare keys in app_en.arb, app_am.arb, app_om.arb
     - For admin: compare keys in en.json, am.json, om.json

4. **Role-based visibility rules check** (if changes touch listings, trips, browse screens, or query/repository logic):

   These are **hard requirements** — any violation is a FAIL:
   - **Farmer**: Can create crop listings. Can only see **own** listings. Must NOT see other farmers' listings.
   - **Trucker**: Can create trip listings. Can only see **own** trips. Must NOT see other truckers' trips.
   - **Buyer**: Can see **ALL** crop listings and **ALL** trip listings from anyone/anywhere.

   Check for violations in:
   - **Server**: Repository/query logic in `server/src/Data/` and handler logic in `server/src/Functions/` — verify that list/browse endpoints filter by owner for Farmer/Trucker roles, and return all items for Buyer role.
   - **Mobile**: Browse/list screens (`browse_listings_screen.dart`, `browse_trips_screen.dart`, any `my_*` screens) — verify that Farmer/Trucker see only their own data, and Buyer sees everything.
   - **Mobile BLoCs/Cubits**: Data-fetching logic must respect role-based filtering.
   - **API contracts** (`specs/contracts/`): Verify query parameters or endpoint design enforces role-based access.

   Report format:
   - `[PASS] Role-based visibility: Farmer sees only own crop listings`
   - `[FAIL] Role-based visibility: Trucker browse screen shows all trips — must filter to own trips only`

5. **Build validation** for each changed module:

   **Mobile** (mobile/ changes):
   ```bash
   cd mobile && flutter analyze
   ```

   **Mobile Web build** (if mobile/ changes touch shared code, models, core/, or screens):
   ```bash
   cd mobile && flutter build web --no-tree-shake-icons 2>&1 | tail -20
   ```
   This catches web-incompatible imports (Isar, dart:io, platform plugins).
   Skip this if changes are only in test/ files or platform-specific mobile-only files (like *_mobile.dart, schemas/*.g.dart).

   **Server** (server/ changes):
   ```bash
   cd server && dotnet build --no-restore
   ```

   **Infra** (infra/ changes):
   ```bash
   cd infra && npx cdk synth --quiet
   ```

   **Admin** (admin/ changes):
   ```bash
   cd admin && npm run build 2>&1 | tail -20
   ```

5. **Test impact analysis**:
   If shared code changed (core/, models/, network/, services/), run ALL tests, not just tests for the specific feature:
   ```bash
   cd mobile && flutter test
   ```
   If only a single feature's presentation layer changed, running that feature's tests is sufficient.

6. **Report results** in this format:

   ```
   ## Validation Results

   ### Cross-Platform Pairs
   - [PASS/WARN] auth_storage_mobile.dart <-> auth_storage_web.dart
   - [WARN] sync_queue.dart changed but sync_queue_web.dart was NOT updated — new method `retryFailed()` needs web stub

   ### Cross-Module Impact
   - [INFO] Server DTO UserDto changed — mobile UserModel and admin user types may need updating
   - [PASS] No API contract changes detected

   ### Role-Based Visibility
   - [PASS/FAIL] Farmer sees only own crop listings
   - [PASS/FAIL] Trucker sees only own trip listings
   - [PASS/FAIL] Buyer sees all listings and trips
   (Only shown when changes touch listings/trips/browse/repository code)

   ### Builds
   - [PASS/FAIL] Mobile analyze
   - [PASS/FAIL] Mobile web build
   - [PASS/FAIL] Server build
   - [PASS/FAIL] Admin build

   ### Action Items
   1. Update sync_queue_web.dart to add `retryFailed()` stub
   2. Verify admin user type matches new UserDto fields
   ```

7. If any WARN or FAIL items found, offer to fix them.

Only run validations for modules with actual changes, unless user specified "all".
