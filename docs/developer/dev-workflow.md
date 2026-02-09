# Development Workflow

## Day-to-Day Development Process

This guide describes the typical development workflow for WeredaConnect contributors.

## Audience
- Developers
- Contributors
- Team leads

---

## Daily Workflow

### 1. Start Your Day

```bash
# Pull latest changes
git checkout main
git pull origin main

# Create feature branch
git checkout -b feature/your-feature-name

# Update dependencies (if needed)
cd mobile && flutter pub get
cd ../server && dotnet restore
cd ../infra && npm install
```

### 2. Work on Feature

```bash
# Make changes
# Write tests
# Run tests locally

# Mobile
cd mobile
flutter test

# Backend
cd server
dotnet test

# Infrastructure
cd infra
npm test
```

### 3. Commit Changes

```bash
# Stage changes
git add .

# Commit with conventional commit message
git commit -m "feat: add offline sync for reports"

# Pre-commit hooks will run:
# - Linting
# - Formatting
# - Unit tests
```

### 4. Push and Create PR

```bash
# Push to remote
git push origin feature/your-feature-name

# Create Pull Request on GitHub
# - Fill in PR template
# - Link related issues
# - Request reviewers
```

---

## Branching Strategy

### Branch Types

```
main
  \u2514\u2500 feature/add-report-categories
  \u2514\u2500 bugfix/sync-conflict-resolution
  \u2514\u2500 hotfix/critical-crash-fix
  \u2514\u2500 release/v1.2.0
```

### Branch Naming

- **Features**: `feature/short-description`
- **Bug Fixes**: `bugfix/short-description`
- **Hotfixes**: `hotfix/short-description`
- **Releases**: `release/vX.Y.Z`

### Branch Lifecycle

1. **Create** from `main`
2. **Develop** with frequent commits
3. **Push** regularly (at least daily)
4. **PR** when ready for review
5. **Merge** after approval and CI passes
6. **Delete** after merge

---

## Commit Messages

### Conventional Commits Format

```
<type>(<scope>): <subject>

<body>

<footer>
```

### Types

- **feat**: New feature
- **fix**: Bug fix
- **docs**: Documentation changes
- **style**: Code style changes (formatting, no logic change)
- **refactor**: Code refactoring
- **test**: Adding or updating tests
- **chore**: Maintenance tasks

### Examples

```bash
# Feature
git commit -m "feat(mobile): add offline report creation"

# Bug fix
git commit -m "fix(server): resolve sync conflict handling"

# Documentation
git commit -m "docs: update API reference for sync endpoint"

# With body
git commit -m "feat(mobile): add photo compression

Compress photos before upload to save data.
Uses 80% quality JPEG compression.

Closes #123"
```

---

## Pull Request Process

### 1. Create PR

**Title**: Same as commit message (if single commit) or descriptive summary

**Description Template**:
```markdown
## Description
Brief description of changes

## Type of Change
- [ ] Bug fix
- [ ] New feature
- [ ] Breaking change
- [ ] Documentation update

## Testing
- [ ] Unit tests added/updated
- [ ] Integration tests added/updated
- [ ] Manual testing completed

## Checklist
- [ ] Code follows style guidelines
- [ ] Self-review completed
- [ ] Comments added for complex code
- [ ] Documentation updated
- [ ] No new warnings
- [ ] Tests pass locally
- [ ] AOT compliance verified (for .NET changes)
- [ ] Offline-first principles followed (for mobile changes)

## Screenshots (if applicable)

## Related Issues
Closes #123
```

### 2. Code Review

**Reviewers should check**:
- Code quality and style
- Test coverage
- Documentation
- Offline-first compliance
- AOT compliance (for .NET)
- Ethiopian context considerations
- Performance implications

**Review turnaround**: 24-48 hours

### 3. Address Feedback

```bash
# Make requested changes
git add .
git commit -m "refactor: address PR feedback"
git push origin feature/your-feature-name

# PR automatically updates
```

### 4. Merge

**Merge strategies**:
- **Squash and merge**: For feature branches (default)
- **Rebase and merge**: For clean history
- **Merge commit**: For release branches

**After merge**:
```bash
# Delete remote branch (automatic on GitHub)
# Delete local branch
git checkout main
git pull origin main
git branch -d feature/your-feature-name
```

---

## Testing Workflow

### Test-Driven Development (TDD)

```
1. Write failing test
   \u2193
2. Write minimal code to pass
   \u2193
3. Refactor
   \u2193
4. Repeat
```

### Test Pyramid

```
        /\
       /E2E\      <- Few (slow, expensive)
      /------\
     /  Integ \   <- Some (medium speed)
    /----------\
   /   Unit     \ <- Many (fast, cheap)
  /--------------\
```

### Running Tests

```bash
# Mobile - Unit tests
cd mobile
flutter test

# Mobile - Integration tests
flutter test integration_test/

# Backend - Unit tests
cd server
dotnet test

# Backend - With coverage
dotnet test /p:CollectCoverage=true

# Infrastructure - Unit tests
cd infra
npm test

# All tests (from root)
./scripts/run-all-tests.sh
```

---

## Code Quality

### Linting

```bash
# Mobile
flutter analyze

# Backend
dotnet format --verify-no-changes

# Infrastructure
npm run lint
```

### Formatting

```bash
# Mobile
flutter format .

# Backend
dotnet format

# Infrastructure
npm run format
```

### Pre-commit Hooks

Automatically run on `git commit`:
- Linting
- Formatting
- Unit tests
- AOT compliance check (for .NET)

**Skip hooks** (not recommended):
```bash
git commit --no-verify
```

---

## Working with AI Agents

### Session Handoff

When ending a coding session:

```bash
# Update STATE.md
./scripts/update-state.sh

# Commit state
git add STATE.md TASK.md
git commit -m "chore: update session state"
git push
```

### Starting New Session

```bash
# Pull latest
git pull origin main

# Review state
cat STATE.md
cat TASK.md

# Continue work
```

### AI Agent Collaboration

See `.agents/` folder for SOPs:
- **Architect Agent**: Design decisions
- **Developer Agent**: Implementation
- **Testing Agent**: Test creation
- **Documentation Agent**: Docs updates

---

## Debugging

### Mobile (Flutter)

```bash
# Run with debugger
flutter run --debug

# Hot reload: Press 'r' in terminal
# Hot restart: Press 'R' in terminal

# Debug in VS Code
# Set breakpoints, press F5
```

### Backend (.NET)

```bash
# Run with debugger
dotnet run --configuration Debug

# Attach to process in VS Code
# Set breakpoints, F5
```

### Logs

```bash
# Mobile - View logs
flutter logs

# Backend - CloudWatch Logs (deployed)
aws logs tail /aws/lambda/WeredaConnect-CreateReport-Dev --follow

# Infrastructure - CDK logs
cdk deploy --verbose
```

---

## Performance Profiling

### Mobile

```bash
# Profile app
flutter run --profile

# Analyze performance
flutter run --trace-startup
```

### Backend

```bash
# Load testing
cd server/tests/LoadTests
dotnet run

# AWS X-Ray tracing (deployed)
# Enable in Lambda configuration
```

---

## Database Migrations

### Mobile (Isar)

```dart
// Update schema version
@collection
class Report {
  Id id = Isar.autoIncrement;
  
  // Add new field
  String? newField;
}

// Rebuild
flutter pub run build_runner build --delete-conflicting-outputs
```

### Backend (DynamoDB)

```typescript
// Update CDK stack
const table = new dynamodb.Table(this, 'Reports', {
  // Add new attribute (DynamoDB is schemaless)
  // Update GSI if needed
});

// Deploy
cdk deploy
```

---

## Deployment Workflow

### Development

```bash
# Auto-deploys on push to main
git push origin main

# GitHub Actions runs:
# 1. Tests
# 2. Build
# 3. Deploy to dev
# 4. Smoke tests
```

### Staging

```bash
# Manual trigger in GitHub Actions
# Or via CLI:
cd infra
npm run cdk deploy WeredaConnect-*-Staging
```

### Production

```bash
# Requires approval
# Trigger in GitHub Actions
# Or via CLI (with approval):
npm run cdk deploy WeredaConnect-*-Prod
```

---

## Troubleshooting Common Issues

### Merge Conflicts

```bash
# Pull latest main
git checkout main
git pull origin main

# Rebase feature branch
git checkout feature/your-feature
git rebase main

# Resolve conflicts
# Edit conflicted files
git add .
git rebase --continue

# Force push (rebase rewrites history)
git push --force-with-lease origin feature/your-feature
```

### Failed CI

1. Check GitHub Actions logs
2. Reproduce locally
3. Fix issue
4. Push fix
5. CI re-runs automatically

### AOT Compilation Errors

```bash
# Check for unsupported code
dotnet publish -c Release -r linux-arm64 /p:PublishAot=true

# Common issues:
# - Reflection usage
# - Dynamic code generation
# - Unsupported libraries (Moq, Newtonsoft.Json)
```

---

## Related Documentation

- **[Getting Started](./getting-started.md)** - Environment setup
- **[Contributing Guide](./contributing.md)** - Contribution guidelines
- **[Testing Guidelines](./testing-guidelines.md)** - Testing practices
- **[API Reference](./api-reference.md)** - API documentation

---

**Last Updated**: 2026-02-09  
**Version**: 1.0.0  
**Maintainer**: Development Team

