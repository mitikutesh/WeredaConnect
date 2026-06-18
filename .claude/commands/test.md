Run tests for all modules that have changes.

The user may provide: $ARGUMENTS (specific module: mobile, server, infra, all, or a specific test file/pattern)

Steps:

1. Determine what to test:
   - If user specified a module (`mobile`, `server`, `infra`), test only that module
   - If user specified `all`, test everything
   - If user specified a file path or pattern, run that specific test
   - If no argument, detect changed modules from `git diff --name-only HEAD` and `git diff --name-only --cached`

2. Run the appropriate tests:

   **Server** (.NET):
   ```bash
   cd server && dotnet test --no-restore --verbosity normal
   ```
   If a specific test class/method was requested:
   ```bash
   cd server && dotnet test --no-restore --filter "FullyQualifiedName~<pattern>"
   ```

   **Mobile** (Flutter):
   ```bash
   cd mobile && flutter test
   ```
   If a specific test file was requested:
   ```bash
   cd mobile && flutter test <file_path>
   ```

   **Infra** (CDK):
   ```bash
   cd infra && npm test
   ```

   **Maestro E2E** (if user specifies `e2e` or `maestro`):
   ```bash
   cd mobile && maestro test .maestro/
   ```
   Or a specific flow:
   ```bash
   cd mobile && maestro test .maestro/<flow_name>.yaml
   ```

   **Playwright E2E** (if user specifies `e2e`, `playwright`, or `all`):
   Run all Playwright tests:
   ```bash
   cd e2e && npm test
   ```
   Run only webapp tests:
   ```bash
   cd e2e && npx playwright test --project=webapp
   ```
   Run only admin tests:
   ```bash
   cd e2e && npx playwright test --project=admin
   ```
   Note: Playwright tests require the apps to be running locally:
   - Flutter web app: `cd mobile && flutter run -d chrome --web-port=8080 --target lib/main_web.dart`
   - Admin dashboard: `cd admin && npm run dev`
   Override URLs with env vars: `WEBAPP_URL=... ADMIN_URL=... npx playwright test`

3. Parse test output and report:
   - Total passed / failed / skipped
   - List any failing test names with file:line references
   - For failures, read the relevant test file and suggest fixes

4. If all tests pass, confirm success. If any fail, offer to fix them.
