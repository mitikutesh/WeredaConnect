Analyze the impact/blast radius of a file or feature change before making modifications.

The user provides: $ARGUMENTS (a file path, directory, feature name, or description of planned change)

Steps:

1. **Identify the target**:
   - If a file path: analyze that specific file
   - If a directory: analyze all files in it
   - If a feature name: find relevant files in `mobile/lib/features/<name>/`, `server/src/`, `admin/src/`, `specs/`
   - If a description: identify which files would be affected

2. **Map platform pairs**:
   Check if the target file has a platform counterpart that would also need updating:

   Known platform pairs in `mobile/lib/core/`:
   - `auth/auth_storage_mobile.dart` <-> `auth/auth_storage_web.dart`
   - `auth/offline_auth_cache.dart` <-> `auth/offline_auth_cache_web.dart`
   - `database/isar_service.dart` <-> `database/isar_service_web.dart`
   - `sync/sync_executor.dart` <-> `sync/sync_executor_web.dart`
   - `sync/sync_queue.dart` <-> `sync/sync_queue_web.dart`
   - `network/cert_pinning_io.dart` <-> `network/cert_pinning_stub.dart`
   - `database/schemas/<name>.dart` <-> `models/web/<name>.dart`
   - `models/<name>.dart` (barrel) conditionally exports both of the above

   Report which counterpart files exist and would need changes.

3. **Trace imports/references** (within mobile/):
   ```bash
   grep -rl "import.*<target_file_basename>" mobile/lib/ --include="*.dart" | head -30
   ```
   This shows which files import/depend on the target.

   For each importing file, note:
   - Is it a screen/widget? (presentation impact)
   - Is it a BLoC? (state management impact)
   - Is it a repository? (data layer impact)
   - Is it a test? (test needs updating)

4. **Trace cross-module dependencies**:

   If target is in **server/**:
   - Find the API endpoint(s) affected
   - Check `specs/contracts/` for the relevant contract
   - Search mobile for API calls to that endpoint:
     ```bash
     grep -rl "<endpoint_path>" mobile/lib/ --include="*.dart"
     ```
   - Search admin for API calls:
     ```bash
     grep -rl "<endpoint_path>" admin/src/ --include="*.ts" --include="*.tsx"
     ```

   If target is in **mobile/lib/core/** (shared code):
   - List all features that import from it
   - Check if it affects web platform (conditional imports)
   - Check test coverage:
     ```bash
     grep -rl "<target_basename>" mobile/test/ --include="*.dart"
     ```

   If target is in **specs/contracts/**:
   - Flag ALL modules as potentially affected (server, mobile, admin)
   - List specific DTOs/endpoints defined in the contract

   If target is in **admin/**:
   - Check if it uses API endpoints that are also used by mobile
   - Check localization key consistency

5. **Find related tests**:
   ```bash
   # Direct test file (convention: <name>_test.dart)
   find mobile/test -name "<target_basename>_test.dart"
   # Files that reference the target in tests
   grep -rl "<target_class_or_function>" mobile/test/ --include="*.dart" | head -20
   # Server tests
   grep -rl "<target_class>" server/tests/ --include="*.cs" | head -20
   ```

6. **Output impact report**:

   ```
   ## Impact Analysis: <target>

   ### Direct Changes Required
   - <target_file> (the file itself)
   - <counterpart_file> (platform pair — needs matching interface)

   ### Dependent Files (would break if interface changes)
   - mobile/lib/features/X/data/repositories/x_repository.dart (imports target)
   - mobile/lib/features/X/presentation/bloc/x_bloc.dart (uses repository)

   ### Cross-Module Impact
   - server/src/Models/XDto.cs (defines the API shape)
   - admin/src/pages/XPage.tsx (consumes same API)
   - specs/contracts/x-api.md (contract definition)

   ### Tests to Run
   - mobile/test/features/X/...
   - server/tests/...

   ### Platform Considerations
   - [WEB] Target uses Isar — web stub must be updated
   - [MOBILE] Target uses dart:io — not available on web
   - [SHARED] Target is imported by 12 files across 4 features

   ### Risk Level: [LOW/MEDIUM/HIGH]
   - LOW: Change is isolated to one feature, no platform pairs
   - MEDIUM: Change touches shared code or has platform pairs
   - HIGH: Change affects API contracts, multiple modules, or core infrastructure
   ```

7. If the user wants to proceed with the change, suggest the order of modifications:
   - Start with contracts/interfaces
   - Then server (if API change)
   - Then mobile platform pair files together
   - Then dependent features
   - Then tests
   - Finally, run `/validate` to confirm nothing broke
