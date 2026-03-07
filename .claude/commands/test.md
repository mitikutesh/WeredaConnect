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
   maestro test .maestro/
   ```
   Or a specific flow:
   ```bash
   maestro test .maestro/<flow_name>.yaml
   ```

3. Parse test output and report:
   - Total passed / failed / skipped
   - List any failing test names with file:line references
   - For failures, read the relevant test file and suggest fixes

4. If all tests pass, confirm success. If any fail, offer to fix them.
