# Testing Guidelines

## How to Write and Run Tests

Comprehensive guide to testing in WeredaConnect.

## Audience
- Developers
- QA engineers
- Contributors

## Prerequisites
- Read [Testing Philosophy](../../.ai/testing.md)
- Read technology-specific guides:
  - [Mobile Testing](../../mobile/TESTING.md)
  - [Server Testing](../../server/TESTING.md)
  - [Infrastructure Testing](../../infra/TESTING.md)

---

## Quick Reference

### Run All Tests

```bash
# From project root
./scripts/run-all-tests.sh

# Or individually:
cd mobile && flutter test
cd server && dotnet test
cd infra && npm test
```

### Coverage Reports

```bash
# Mobile
cd mobile
flutter test --coverage
genhtml coverage/lcov.info -o coverage/html
open coverage/html/index.html

# Backend
cd server
dotnet test /p:CollectCoverage=true /p:CoverageReportsFormat=html
open coverage/index.html

# Infrastructure
cd infra
npm run test:coverage
open coverage/lcov-report/index.html
```

---

## Test Types

### 1. Unit Tests

**Purpose**: Test individual functions/classes in isolation

**When to write**:
- Every new function
- Every new class
- Every bug fix

**Example (Mobile)**:
```dart
// test/domain/usecases/get_reports_test.dart
void main() {
  late GetReportsUseCase useCase;
  late MockReportRepository mockRepository;

  setUp(() {
    mockRepository = MockReportRepository();
    useCase = GetReportsUseCase(mockRepository);
  });

  test('should return reports from repository', () async {
    // Arrange
    final reports = [Report(id: '1', title: 'Test')];
    when(mockRepository.getReports())
        .thenAnswer((_) async => reports);

    // Act
    final result = await useCase();

    // Assert
    expect(result, reports);
    verify(mockRepository.getReports()).called(1);
  });
}
```

**Example (Backend)**:
```csharp
// tests/Services/ReportServiceTests.cs
public class ReportServiceTests
{
    private readonly IReportRepository _repository;
    private readonly ReportService _service;

    public ReportServiceTests()
    {
        _repository = Substitute.For<IReportRepository>();
        _service = new ReportService(_repository);
    }

    [Fact]
    public async Task CreateReport_ShouldSaveToRepository()
    {
        // Arrange
        var dto = new CreateReportDto { Title = "Test" };

        // Act
        var result = await _service.CreateReport(dto);

        // Assert
        Assert.NotNull(result);
        await _repository.Received(1).SaveAsync(Arg.Any<Report>());
    }
}
```

### 2. Integration Tests

**Purpose**: Test multiple components working together

**When to write**:
- API endpoints
- Database operations
- External service integrations

**Example (Mobile)**:
```dart
// integration_test/report_flow_test.dart
void main() {
  IntegrationTestWidgetsFlutterBinding.ensureInitialized();

  testWidgets('Create report offline and sync online', (tester) async {
    // Start app
    await tester.pumpWidget(MyApp());

    // Go offline
    await setConnectivity(false);

    // Create report
    await tester.tap(find.byIcon(Icons.add));
    await tester.pumpAndSettle();
    await tester.enterText(find.byKey(Key('title')), 'Test Report');
    await tester.tap(find.text('Submit'));
    await tester.pumpAndSettle();

    // Verify saved locally
    expect(find.text('Pending Sync'), findsOneWidget);

    // Go online
    await setConnectivity(true);
    await tester.pumpAndSettle(Duration(seconds: 5));

    // Verify synced
    expect(find.text('Synced'), findsOneWidget);
  });
}
```

**Example (Backend with LocalStack)**:
```csharp
// tests/Integration/ReportApiTests.cs
public class ReportApiTests : IClassFixture<LocalStackFixture>
{
    private readonly HttpClient _client;

    public ReportApiTests(LocalStackFixture fixture)
    {
        _client = fixture.CreateClient();
    }

    [Fact]
    public async Task CreateReport_ShouldSaveToDynamoDB()
    {
        // Arrange
        var report = new { title = "Test", category = "infrastructure" };

        // Act
        var response = await _client.PostAsJsonAsync("/reports", report);

        // Assert
        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<Report>();
        Assert.NotNull(created.Id);
    }
}
```

### 3. E2E Tests

**Purpose**: Test complete user flows

**When to write**:
- Critical user journeys
- Smoke tests for deployments

**Example (Maestro)**:
```yaml
# mobile/maestro/create_report.yaml
appId: com.weredaconnect.app
---
- launchApp
- tapOn: "Create Report"
- inputText: "Broken road"
  id: "title_field"
- tapOn: "Infrastructure"
- tapOn: "Submit"
- assertVisible: "Report created successfully"
```

### 4. Smoke Tests

**Purpose**: Verify basic functionality after deployment

**When to run**:
- After every deployment
- Before releasing to users

**Example (Mobile)**:
```dart
// test/smoke/app_smoke_test.dart
void main() {
  testWidgets('App launches and shows home screen', (tester) async {
    await tester.pumpWidget(MyApp());
    await tester.pumpAndSettle();
    
    expect(find.text('WeredaConnect'), findsOneWidget);
    expect(find.byIcon(Icons.add), findsOneWidget);
  });
}
```

**Example (Backend)**:
```bash
# scripts/smoke-test.sh
#!/bin/bash

API_URL=$1

# Test health endpoint
curl -f $API_URL/health || exit 1

# Test auth
TOKEN=$(curl -s -X POST $API_URL/auth/login \
  -d '{"phoneNumber":"+251912345678","password":"test"}' \
  | jq -r '.token')

# Test create report
curl -f -X POST $API_URL/reports \
  -H "Authorization: Bearer $TOKEN" \
  -d '{"title":"Smoke test"}' || exit 1

echo "Smoke tests passed!"
```

---

## Testing Patterns

### Offline-First Testing

**Always test offline scenarios FIRST**:

```dart
group('Offline scenarios', () {
  setUp(() async {
    await setConnectivity(false);
  });

  test('should save report locally when offline', () async {
    // Test offline behavior
  });
});

group('Online scenarios', () {
  setUp(() async {
    await setConnectivity(true);
  });

  test('should sync report when online', () async {
    // Test online behavior
  });
});
```

### Mocking

**Mobile (Mockito)**:
```dart
@GenerateMocks([ReportRepository, ConnectivityService])
void main() {
  late MockReportRepository mockRepository;
  
  setUp(() {
    mockRepository = MockReportRepository();
  });
}
```

**Backend (NSubstitute - AOT compatible)**:
```csharp
var repository = Substitute.For<IReportRepository>();
repository.GetAsync(Arg.Any<string>())
    .Returns(Task.FromResult(new Report()));
```

### Test Data Builders

```dart
// test/helpers/report_builder.dart
class ReportBuilder {
  String _id = 'test-id';
  String _title = 'Test Report';
  bool _synced = false;

  ReportBuilder withId(String id) {
    _id = id;
    return this;
  }

  ReportBuilder withTitle(String title) {
    _title = title;
    return this;
  }

  ReportBuilder synced() {
    _synced = true;
    return this;
  }

  Report build() {
    return Report(id: _id, title: _title, synced: _synced);
  }
}

// Usage
final report = ReportBuilder()
    .withTitle('Custom title')
    .synced()
    .build();
```

---

## Coverage Targets

- **Mobile**: >80% for core logic
- **Backend**: >75% for business logic
- **Infrastructure**: >70% for custom constructs

**Check coverage**:
```bash
# Mobile
flutter test --coverage
# View: coverage/lcov.info

# Backend
dotnet test /p:CollectCoverage=true
# View: coverage.json

# Infrastructure
npm run test:coverage
# View: coverage/lcov-report/index.html
```

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

## Best Practices

1. **Test offline scenarios first**
2. **Use descriptive test names**: `should_returnReports_when_repositoryHasData`
3. **Follow AAA pattern**: Arrange, Act, Assert
4. **One assertion per test** (when possible)
5. **Mock external dependencies**
6. **Test edge cases**: null, empty, error conditions
7. **Keep tests fast**: Unit tests <1s, integration <5s
8. **No flaky tests**: Fix or delete
9. **Test behavior, not implementation**
10. **Write tests before fixing bugs**

---

## Related Documentation

- **[Testing Philosophy](../../.ai/testing.md)** - Central testing strategy
- **[Mobile Testing](../../mobile/TESTING.md)** - Flutter-specific guide
- **[Server Testing](../../server/TESTING.md)** - .NET-specific guide
- **[Infrastructure Testing](../../infra/TESTING.md)** - CDK-specific guide

---

**Last Updated**: 2026-02-09  
**Version**: 1.0.0  
**Maintainer**: QA Team

