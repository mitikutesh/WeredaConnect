# Testing Quick Reference

Quick commands to run tests locally before pushing to GitHub.

---

## Mobile (Flutter)

### Prerequisites
```bash
cd mobile
flutter pub get
```

### Run All Tests
```bash
# Unit tests with coverage
flutter test --coverage

# Smoke tests only
flutter test test/smoke/

# Integration tests only
flutter test integration_test/

# Specific test file
flutter test test/main_test.dart
```

### Code Analysis
```bash
# Analyze code
flutter analyze

# Check formatting
dart format --set-exit-if-changed .

# Fix formatting
dart format .
```

### Build APK
```bash
# Debug APK
flutter build apk --debug

# Release APK (requires signing)
flutter build apk --release
```

---

## Server (.NET)

### Prerequisites
```bash
cd server
dotnet restore
```

### Run Tests
```bash
# All unit tests
dotnet test --filter "Category!=Integration"

# All tests (including integration)
dotnet test

# With coverage
dotnet test --collect:"XPlat Code Coverage"

# Specific test
dotnet test --filter "FullyQualifiedName~HealthCheckFunctionTests"
```

### Build
```bash
# Debug build
dotnet build

# Release build
dotnet build --configuration Release

# AOT publish (requires Linux or CI)
CI=true dotnet publish -c Release /p:PublishAot=true
```

### Code Analysis
```bash
# Check for warnings
dotnet build --configuration Release
```

---

## Infrastructure (CDK)

### Prerequisites
```bash
cd infra
npm install
```

### Run Tests
```bash
# All tests
npm test

# Unit tests only
npm test -- --testPathPattern=unit

# Integration tests only
npm test -- --testPathPattern=integration

# With coverage
npm test -- --coverage
```

### Synth & Deploy
```bash
# Synthesize CloudFormation
npx cdk synth

# Deploy to dev
npx cdk deploy --context environment=dev

# Diff changes
npx cdk diff
```

---

## Pre-Push Checklist

Before creating a pull request, run:

```bash
# Mobile
cd mobile
flutter analyze
dart format .
flutter test --coverage

# Server
cd ../server
dotnet build --configuration Release
dotnet test

# Infrastructure
cd ../infra
npm run lint
npm test

# Back to root
cd ..
```

---

## CI/CD Workflows

### Triggers

**Mobile Tests** (`.github/workflows/mobile-tests.yml`):
- Runs on: Push to `main`/`develop`, PRs affecting `mobile/**`
- Jobs: Unit tests, integration tests, smoke tests, APK build

**Server Tests** (`.github/workflows/server-tests.yml`):
- Runs on: Push to `main`/`develop`, PRs affecting `server/**`
- Jobs: Unit tests, integration tests (LocalStack), AOT validation, coverage

**Infrastructure Tests** (`.github/workflows/infra-tests.yml`):
- Runs on: Push to `main`/`develop`, PRs affecting `infra/**`
- Jobs: Unit tests, snapshot tests, CDK synth, integration tests

**PR Validation** (`.github/workflows/pr-validation.yml`):
- Runs on: All pull requests
- Jobs: Detects changes, runs relevant test suites, E2E tests

---

## Troubleshooting

### Flutter Tests Fail
```bash
# Clean and rebuild
flutter clean
flutter pub get
flutter pub run build_runner build --delete-conflicting-outputs
flutter test
```

### .NET Tests Fail
```bash
# Clean and rebuild
dotnet clean
dotnet restore
dotnet build
dotnet test
```

### AOT Publish Fails on macOS
This is expected. AOT compilation for Linux ARM64 requires Linux tools.
Use CI/CD for AOT validation, or use Docker:
```bash
docker run --rm -v $(pwd):/app -w /app mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet publish -c Release /p:PublishAot=true
```

---

## Coverage Reports

### View Coverage Locally

**Flutter:**
```bash
flutter test --coverage
genhtml coverage/lcov.info -o coverage/html
open coverage/html/index.html
```

**Server:**
```bash
dotnet test --collect:"XPlat Code Coverage"
# Coverage files in: TestResults/*/coverage.cobertura.xml
```

**Infrastructure:**
```bash
npm test -- --coverage
open coverage/lcov-report/index.html
```

---

## Quick Test Status Check

```bash
# Run this from project root to check all tests
echo "=== Mobile Tests ===" && cd mobile && flutter test && \
echo "=== Server Tests ===" && cd ../server && dotnet test && \
echo "=== Infra Tests ===" && cd ../infra && npm test && \
cd .. && echo "✅ All tests passed!"
```

