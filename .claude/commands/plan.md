Create an implementation plan for a feature before building it.

The user should provide: $ARGUMENTS (feature description)

Steps:
1. Read the relevant API contracts in `specs/contracts/` if applicable
2. Read `.ai/quick-start.md` for existing patterns and constraints
3. Check `TASK.md` and `specs/features/ROADMAP.md` for context on how this fits the roadmap
4. Create a plan covering:
   - **What**: Clear description of the feature
   - **Why**: Business context in Ethiopian agricultural market
   - **Files to create/modify**: List with brief descriptions
   - **Offline behavior**: How it works without internet
   - **Sync strategy**: How data syncs when connectivity returns
   - **Localization**: New l10n keys needed (en, am, om)
   - **API contract**: Endpoint design if server-side work needed
   - **Testing**: Key test scenarios including offline cases
   - **Risks**: AOT compliance, performance, data usage concerns
5. Save the plan to `specs/features/` as a markdown file with a descriptive name
6. Ask the user for approval before implementation begins
