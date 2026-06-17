# Testing Guidelines

For testing philosophy, principles, and strategy, see **[.ai/testing.md](../../.ai/testing.md)**.

## Quick Reference

```bash
# Run all tests (per module — no aggregate script exists)
cd mobile && flutter test
cd server && dotnet test
cd infra && npm test
```

### Coverage

```bash
# Mobile
cd mobile && flutter test --coverage
genhtml coverage/lcov.info -o coverage/html && open coverage/html/index.html

# Backend
cd server && dotnet test /p:CollectCoverage=true /p:CoverageReportsFormat=html

# Infrastructure
cd infra && npm test
```

---

## Test Types

### Unit Tests

```dart
// Mobile example (Mocktail)
test('should return reports from repository', () async {
  when(() => mockRepository.getReports()).thenAnswer((_) async => [Report(id: '1', title: 'Test')]);
  final result = await useCase();
  expect(result.length, 1);
  verify(() => mockRepository.getReports()).called(1);
});
```

```csharp
// Backend example (NSubstitute - AOT compatible)
[Fact]
public async Task CreateReport_ShouldSaveToRepository()
{
    var dto = new CreateReportDto { Title = "Test" };
    var result = await _service.CreateReport(dto);
    Assert.NotNull(result);
    await _repository.Received(1).SaveAsync(Arg.Any<Report>());
}
```

### Integration Tests

See **[E2E Local Testing Guide](./e2e-local-testing.md)** for full mobile + LocalStack setup.

```bash
# Quick start
cd server && docker-compose -f docker-compose.localstack.yml up -d
cd mobile && flutter run --dart-define=API_BASE_URL=http://10.0.2.2:4566
```

### E2E / Maestro Tests

```yaml
# mobile/maestro/create_report.yaml
appId: com.weredaconnect.app
---
- launchApp
- tapOn: "Create Report"
- inputText: "Broken road"
  id: "title_field"
- tapOn: "Submit"
- assertVisible: "Report created successfully"
```

### Smoke Tests

```bash
# scripts/smoke-test.sh
API_URL=$1
curl -f $API_URL/health || exit 1
TOKEN=$(curl -s -X POST $API_URL/auth/login \
  -d '{"phoneNumber":"+251912345678","password":"test"}' | jq -r '.token')
curl -f -X POST $API_URL/reports -H "Authorization: Bearer $TOKEN" \
  -d '{"title":"Smoke test"}' || exit 1
```

---

## Testing Patterns

### Offline-First

Always test offline scenarios first, then online:

```dart
group('Offline', () {
  setUp(() async => await setConnectivity(false));
  test('should save locally when offline', () async { /* ... */ });
});
group('Online', () {
  setUp(() async => await setConnectivity(true));
  test('should sync when online', () async { /* ... */ });
});
```

### Mocking

- **Mobile**: Mocktail (`class MockReportRepository extends Mock implements ReportRepository`)
- **Backend**: NSubstitute (AOT-compatible): `Substitute.For<IReportRepository>()`

---

## Coverage Targets

- **Mobile**: >80% core logic
- **Backend**: >75% business logic
- **Infrastructure**: >70% custom constructs

---

## CI/CD Integration

### GitHub Actions

```yaml
# .github/workflows/test.yml
name: Tests

on: [push, pull_request]

jobs:
  mobile:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v3
      - uses: subosito/flutter-action@v2
      - run: cd mobile && flutter test --coverage
      - uses: codecov/codecov-action@v3

  backend:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v3
      - uses: actions/setup-dotnet@v3
        with:
          dotnet-version: '10.0.x'
      - run: cd server && dotnet test /p:CollectCoverage=true

  infra:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v3
      - uses: actions/setup-node@v3
      - run: cd infra && npm test
```

---

## Related Documentation

- **[.ai/testing.md](../../.ai/testing.md)** - Testing philosophy and strategy
- **[E2E Local Testing](./e2e-local-testing.md)** - Mobile emulator + LocalStack

---

**Last Updated**: 2026-02-16
