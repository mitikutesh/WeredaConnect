Run build validation for all modules that have changes.

Steps:
1. Run `git diff --name-only HEAD` and `git diff --name-only --cached` to detect which modules have changed files
2. For each changed module, run the appropriate validation:
   - **mobile/** changes: `cd mobile && flutter analyze`
   - **server/** changes: `cd server && dotnet build --no-restore`
   - **infra/** changes: `cd infra && npx cdk synth --quiet`
3. Report results — pass or fail with error details
4. If any validation fails, suggest fixes

Only run validations for modules with actual changes. Skip modules that haven't been touched.
