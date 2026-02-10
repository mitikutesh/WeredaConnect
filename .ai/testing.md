# 🧪 Testing Strategy & Philosophy

## Purpose
This document defines the **comprehensive testing strategy** for WeredaConnect. It covers all testing types, patterns, and best practices across mobile, server, and infrastructure.

---

## Testing Philosophy

### Core Principles

1. **Offline-First Testing** - Test offline scenarios BEFORE online scenarios
2. **Test Pyramid** - Many unit tests, fewer integration tests, minimal E2E tests
3. **Fast Feedback** - Unit tests run in <1 minute, integration tests in <5 minutes
4. **Deterministic** - Tests produce same results every time (no flaky tests)
5. **Isolated** - Tests don't depend on each other or external state
6. **Maintainable** - Tests are as clean and readable as production code

### Testing Priorities for WeredaConnect

1. **Offline functionality** - Critical for Ethiopian context
2. **Sync conflict resolution** - Data integrity is paramount
3. **Low-end device performance** - Memory, battery, storage
4. **Multi-language support** - Amharic, Afan Oromo, English
5. **Security** - Authentication, authorization, data privacy

---

## Testing Types

### 1. Unit Tests

**What**: Test individual functions, classes, or components in isolation

**Coverage Target**: Mobile >80%, Server >75%, Infrastructure >70% for core business logic

**Speed**: <1 minute for entire suite

**When to Write**:
- For all business logic (domain layer)
- For utility functions
- For data transformations
- For validation logic

**When NOT to Write**:
- For trivial getters/setters
- For framework code (Flutter widgets, .NET controllers)
- For generated code (Isar, JSON serializers)

---

### 2. Integration Tests

**What**: Test multiple components working together

**Coverage Target**: Critical user flows (offline sync, auth, data persistence)

**Speed**: <5 minutes for entire suite

**When to Write**:
- For repository patterns (local DB + remote API)
- For sync engine logic
- For API endpoints with database access
- For offline-to-online transitions

**When NOT to Write**:
- For simple CRUD operations (covered by unit tests)
- For UI interactions (use E2E tests instead)

---

### 3. End-to-End (E2E) Tests

**What**: Test complete user flows from UI to backend

**Coverage Target**: Critical paths only (5-10 scenarios)

**Speed**: <15 minutes for entire suite

**When to Write**:
- For critical user journeys (signup, create report, sync)
- For offline-first workflows
- For multi-step processes
- Before major releases

**When NOT to Write**:
- For every feature (too slow, too brittle)
- For edge cases (use integration tests)
- For UI variations (use widget tests)

---

### 4. Smoke Tests

**What**: Quick validation that critical functionality works

**Coverage Target**: 5-10 critical paths

**Speed**: <2 minutes for entire suite

**When to Run**:
- After every deployment
- Before releasing to production
- After infrastructure changes
- As first step in CI/CD pipeline

**What to Test**:
- App launches successfully
- User can login
- Core features load (offline mode)
- API endpoints respond (health check)
- Database connections work

---

## Test Organization

### Mobile (Flutter)

```
mobile/
├── test/                     # Unit tests
│   ├── core/
│   │   ├── database/
│   │   ├── sync/
│   │   └── network/
│   ├── features/
│   │   └── [feature]/
│   │       ├── data/
│   │       ├── domain/
│   │       └── presentation/
│   └── shared/
├── integration_test/         # Integration & E2E tests
│   ├── offline_sync_test.dart
│   ├── auth_flow_test.dart
│   └── smoke_test.dart
└── test_driver/              # E2E test driver (Maestro)
    └── integration_test.dart
```

### Server (.NET)

```
server/
├── tests/
│   ├── Unit/                 # Unit tests
│   │   ├── Services/
│   │   ├── Models/
│   │   └── Validators/
│   ├── Integration/          # Integration tests
│   │   ├── Functions/
│   │   ├── Data/
│   │   └── LocalStack/       # AWS service mocks
│   └── Smoke/                # Smoke tests
│       └── HealthCheckTests.cs
└── src/
```

### Infrastructure (CDK)

```
infra/
├── test/
│   ├── unit/                 # CDK construct tests
│   │   ├── stacks/
│   │   └── constructs/
│   └── snapshot/             # Snapshot tests
│       └── __snapshots__/
└── lib/
```

---

## Test Naming Conventions

### Mobile (Dart)
```dart
// Pattern: methodName_stateUnderTest_expectedBehavior
test('syncReport_whenOffline_shouldQueueForLater', () { });
test('getReport_whenNotInLocalDB_shouldThrowOfflineException', () { });
```

### Server (C#)
```csharp
// Pattern: MethodName_StateUnderTest_ExpectedBehavior
[Fact]
public void SyncReport_WhenClientVersionBehind_ShouldReturnConflict() { }

[Fact]
public void CreateReport_WithDuplicateIdempotencyKey_ShouldReturnExisting() { }
```

---

## Test Data Management

### Principles
1. **Deterministic** - Same input always produces same output
2. **Isolated** - Each test creates its own data
3. **Cleaned up** - Tests clean up after themselves
4. **Realistic** - Test data resembles production data

### Patterns

#### Mobile (Flutter)
```dart
// Use factories for test data
class TestDataFactory {
  static Report createReport({
    String? id,
    String? title,
    bool synced = false,
  }) {
    return Report(
      id: id ?? 'test-${DateTime.now().millisecondsSinceEpoch}',
      title: title ?? 'Test Report',
      synced: synced,
    );
  }
}
```

#### Server (.NET)
```csharp
// Use builders for test data
public class ReportBuilder
{
    private string _id = Guid.NewGuid().ToString();
    private string _title = "Test Report";
    
    public ReportBuilder WithId(string id)
    {
        _id = id;
        return this;
    }
    
    public Report Build() => new Report { Id = _id, Title = _title };
}
```

---

## Mocking Strategies

### Mobile (Flutter)
```dart
// Use Mockito for mocking
import 'package:mockito/mockito.dart';
import 'package:mockito/annotations.dart';

@GenerateMocks([RemoteDataSource, LocalDataSource])
void main() {
  late MockRemoteDataSource mockRemote;
  late MockLocalDataSource mockLocal;
  
  setUp(() {
    mockRemote = MockRemoteDataSource();
    mockLocal = MockLocalDataSource();
  });
  
  test('should try local first', () async {
    when(mockLocal.getReport(any))
        .thenAnswer((_) async => testReport);
    
    final result = await repository.getReport('123');
    
    verify(mockLocal.getReport('123'));
    verifyNever(mockRemote.getReport(any));
  });
}
```

### Server (.NET)
```csharp
// Use NSubstitute for mocking (AOT-compatible)
using NSubstitute;

public class ReportServiceTests
{
    private readonly IReportRepository _mockRepo;
    private readonly ReportService _service;
    
    public ReportServiceTests()
    {
        _mockRepo = Substitute.For<IReportRepository>();
        _service = new ReportService(_mockRepo);
    }
    
    [Fact]
    public async Task GetReport_ShouldCallRepository()
    {
        _mockRepo.GetByIdAsync("123").Returns(testReport);
        
        var result = await _service.GetReport("123");
        
        await _mockRepo.Received(1).GetByIdAsync("123");
    }
}
```

---

## Coverage Measurement

### Mobile (Flutter)
```bash
# Generate coverage
flutter test --coverage

# View coverage report
genhtml coverage/lcov.info -o coverage/html
open coverage/html/index.html
```

### Server (.NET)
```bash
# Install coverage tool
dotnet tool install --global dotnet-coverage

# Run tests with coverage
dotnet test --collect:"XPlat Code Coverage"

# Generate report
reportgenerator -reports:**/coverage.cobertura.xml -targetdir:coverage
```

---

**See technology-specific guides:**
- `mobile/TESTING.md` - Flutter testing patterns
- `server/TESTING.md` - .NET testing patterns
- `infra/TESTING.md` - CDK testing patterns

---

**Last Updated**: 2026-02-09  
**Version**: 1.0.0

