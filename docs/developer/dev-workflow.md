# Development Workflow

## Daily Workflow

### 1. Start Your Day

```bash
# Pull latest changes
git checkout main
git pull origin main

# Create scoped branch (see .ai/workflows.md)
git checkout -b mobile/your-feature-name  # or server/, infra/, etc.

# Update dependencies (if needed)
cd mobile && flutter pub get
cd ../server && dotnet restore
cd ../infra && npm install
```

### 2. Work on Feature

```bash
# Run tests locally
cd mobile && flutter test
cd server && dotnet test
cd infra && npm test
```

### 3. Commit Changes

```bash
git add .
git commit -m "feat: add offline sync for reports"

# Pre-commit hooks run: linting, formatting, unit tests
```

### 4. Push and Create PR

```bash
git push origin mobile/your-feature-name
# Create PR on GitHub, link related issues
```

---

## Branching Strategy

- **Scoped work**: `<scope>/<short-description>` (e.g., `mobile/crop-listing-ui`, `server/escrow-endpoint`)
- **Hotfixes** (cross-scope): `hotfix/<description>`
- **Releases**: `release/vX.Y.Z`

**Valid scope prefixes**: `mobile`, `server`, `infra`, `l10n`, `testing`, `security`, `architect`

See `.ai/workflows.md` for full branch naming rules and workflow details.

---

## Commit Messages

Follow [Conventional Commits](https://www.conventionalcommits.org/). See `CLAUDE.md` for full rules, types, scopes, and examples.

---

## Testing

```bash
cd mobile && flutter test                    # mobile unit
cd mobile && flutter test integration_test/  # mobile integration
cd server && dotnet test                     # backend
cd infra && npm test                         # infrastructure
./scripts/run-all-tests.sh                   # all
```

See **[testing-guidelines.md](./testing-guidelines.md)** for full testing strategy.

---

## Code Quality

### Linting

```bash
flutter analyze                    # mobile
dotnet format --verify-no-changes  # backend
npm run lint                       # infrastructure
```

### Formatting

```bash
dart format .     # mobile
dotnet format     # backend
npm run format    # infrastructure
```

### Pre-commit Hooks

Automatically run on `git commit`: linting, formatting, unit tests, AOT compliance check (.NET).

---

## Debugging

```bash
# Mobile
flutter run --debug       # hot reload: 'r', hot restart: 'R'
flutter logs

# Backend
dotnet run --configuration Debug

# CloudWatch Logs (deployed)
aws logs tail /aws/lambda/WeredaConnect-CreateReport-Dev --follow
```

---

## Performance Profiling

```bash
# Mobile
flutter run --profile
flutter run --trace-startup

# Backend load testing
cd server/tests/LoadTests && dotnet run
```

---

## Database Migrations

### Mobile (Isar)

```dart
@collection
class Report {
  Id id = Isar.autoIncrement;
  String? newField; // Add new field
}

// Rebuild
// flutter pub run build_runner build --delete-conflicting-outputs
```

### Backend (DynamoDB)

DynamoDB is schemaless. Update GSI in CDK stack if needed, then `cdk deploy`.

---

## Deployment Workflow

See **[deployment.md](./deployment.md)** for full guide.

- **Dev**: Auto-deploys on push to `main`
- **Staging**: `cd infra && npm run cdk deploy WeredaConnect-*-Staging`
- **Production**: `npm run cdk deploy WeredaConnect-*-Prod` (requires approval)

---

## Troubleshooting

### Merge Conflicts

```bash
git checkout main && git pull origin main
git checkout mobile/your-feature && git rebase main
# Resolve conflicts, then:
git add . && git rebase --continue
git push --force-with-lease origin mobile/your-feature
```

### AOT Compilation Errors

```bash
dotnet publish -c Release -r linux-arm64 /p:PublishAot=true
# Common issues: reflection, dynamic codegen, banned libs (Moq, Newtonsoft.Json)
```

---

## Related Documentation

- **[Testing Guidelines](./testing-guidelines.md)** - Testing practices
- **[API Reference](./api-reference.md)** - API documentation
- **[Deployment](./deployment.md)** - Deployment guide

---

**Last Updated**: 2026-02-16
