# 📐 Coding Standards

## Purpose
This document defines the **coding patterns and conventions** for WeredaConnect. All code must follow these standards for consistency and maintainability.

---

## General Principles

1. **Offline-first is non-negotiable** - Every feature works offline before online
2. **Explicit over implicit** - Code should be self-documenting
3. **Fail gracefully** - Handle errors with user-friendly messages
4. **Optimize for 3G** - Minimize data transfer, compress payloads
5. **Solo-developer friendly** - Avoid clever code, prefer readable code

---

## Mobile (Flutter/Dart)

### File Organization
```
mobile/lib/
├── core/                 # Shared infrastructure
│   ├── database/         # Isar schemas and DAOs
│   ├── sync/             # Sync engine
│   ├── network/          # HTTP client
│   └── error/            # Error handling
├── features/             # Feature modules
│   └── [feature_name]/
│       ├── data/         # Data sources, repositories
│       ├── domain/       # Business logic, entities
│       └── presentation/ # UI, state management
└── shared/               # Shared widgets, utilities
    ├── l10n/             # Localization files
    ├── widgets/          # Reusable widgets
    └── utils/            # Helper functions
```

### Naming Conventions
- **Files**: `snake_case.dart`
- **Classes**: `PascalCase`
- **Variables/Functions**: `camelCase`
- **Constants**: `kConstantName` or `SCREAMING_SNAKE_CASE`
- **Private members**: `_leadingUnderscore`

### Code Patterns

#### 1. Offline-First Repository Pattern
```dart
abstract class Repository<T> {
  // Always try local first
  Future<T> get(String id) async {
    final local = await _localDataSource.get(id);
    if (local != null) return local;
    
    // Fallback to remote if online
    if (await _connectivity.isOnline) {
      final remote = await _remoteDataSource.get(id);
      await _localDataSource.save(remote); // Cache for offline
      return remote;
    }
    
    throw OfflineException('Data not available offline');
  }
}
```

#### 2. Sync Queue Pattern
```dart
class SyncQueue {
  final IsarDatabase _db;
  
  Future<void> enqueue(SyncOperation op) async {
    await _db.syncOperations.put(op); // Persist locally
    _attemptSync(); // Try to sync if online
  }
  
  Future<void> _attemptSync() async {
    if (!await _connectivity.isOnline) return;
    
    final pending = await _db.syncOperations.where().findAll();
    for (final op in pending) {
      try {
        await _api.execute(op);
        await _db.syncOperations.delete(op.id); // Remove on success
      } catch (e) {
        // Keep in queue, retry later
      }
    }
  }
}
```

#### 3. Localization Pattern
```dart
// Use generated l10n, not hardcoded strings
Text(AppLocalizations.of(context)!.welcomeMessage)

// NOT this:
Text('Welcome') // ❌ Never hardcode strings
```

### Testing Standards

**See `mobile/TESTING.md` for detailed examples and patterns.**

#### Test Organization
```
mobile/
├── test/                     # Unit tests (mirror lib/ structure)
│   ├── core/
│   ├── features/
│   └── shared/
├── integration_test/         # Integration & E2E tests
└── test_driver/              # E2E test driver (Maestro)
```

#### Test Types
- **Unit tests**: Business logic, repositories, sync engine
- **Widget tests**: All custom widgets and screens
- **Integration tests**: Offline → online flows, sync conflicts
- **E2E tests**: Critical user journeys (Maestro)
- **Smoke tests**: App launch, core features load

#### Offline-First Testing Pattern
```dart
// ✅ ALWAYS test offline scenarios first
test('should return local data when available', () async {
  when(mockLocal.getReport(testId))
      .thenAnswer((_) async => testReport);

  final result = await repository.getReport(testId);

  expect(result, testReport);
  verify(mockLocal.getReport(testId));
  verifyNever(mockRemote.getReport(any)); // Never hit remote
});

test('should throw OfflineException when not cached and offline', () async {
  when(mockLocal.getReport(testId)).thenAnswer((_) async => null);
  when(mockConnectivity.isOnline).thenAnswer((_) async => false);

  expect(
    () => repository.getReport(testId),
    throwsA(isA<OfflineException>()),
  );
});
```

#### Coverage Target
- **>80%** for core business logic
- **>70%** for UI/presentation layer
- **100%** for sync engine (critical for data integrity)

#### Tools
- `flutter_test` for unit/widget tests
- `integration_test` for integration tests
- `mockito` with code generation for mocking
- `Maestro` for E2E tests

---

## Server (.NET 10)

### File Organization
```
server/
├── src/
│   ├── Functions/        # Lambda function handlers
│   ├── Models/           # DTOs, entities
│   ├── Services/         # Business logic
│   └── Data/             # Database access
├── tests/
└── AOT_COMPLIANCE.md     # Banned libraries
```

### Naming Conventions
- **Files**: `PascalCase.cs`
- **Classes/Interfaces**: `PascalCase`, interfaces start with `I`
- **Methods**: `PascalCase`
- **Variables**: `camelCase` (local), `PascalCase` (properties)
- **Constants**: `PascalCase` or `SCREAMING_SNAKE_CASE`

### Code Patterns

#### 1. AOT-Compatible JSON Serialization
```csharp
// ALWAYS use source generators for AOT
[JsonSerializable(typeof(UserDto))]
[JsonSerializable(typeof(List<UserDto>))]
public partial class AppJsonContext : JsonSerializerContext { }

// Usage
var json = JsonSerializer.Serialize(user, AppJsonContext.Default.UserDto);
```

#### 2. Idempotent API Pattern
```csharp
public class CreateReportFunction
{
    public async Task<APIGatewayProxyResponse> Handle(
        APIGatewayProxyRequest request)
    {
        var dto = JsonSerializer.Deserialize<CreateReportDto>(
            request.Body, AppJsonContext.Default.CreateReportDto);
        
        // Check for duplicate (idempotency key)
        var existing = await _repo.GetByIdempotencyKey(dto.IdempotencyKey);
        if (existing != null) return Ok(existing); // Already processed
        
        var report = await _service.Create(dto);
        return Created(report);
    }
}
```

#### 3. Conflict Resolution Pattern
```csharp
public async Task<SyncResult> Sync(SyncRequest request)
{
    var serverVersion = await _repo.GetVersion(request.EntityId);
    
    if (request.Version < serverVersion.Version)
    {
        // Client is behind, send server version
        return new SyncResult 
        { 
            Status = SyncStatus.Conflict,
            ServerData = serverVersion 
        };
    }
    
    // Client is current, apply changes
    await _repo.Update(request.Data, request.Version + 1);
    return new SyncResult { Status = SyncStatus.Success };
}
```

### Testing Standards

**See `server/TESTING.md` for detailed examples and patterns.**

#### Test Organization
```
server/
└── tests/
    ├── Unit/                 # Unit tests (mirror src/ structure)
    │   ├── Services/
    │   ├── Models/
    │   └── Validators/
    ├── Integration/          # Integration tests with LocalStack
    │   ├── Functions/
    │   └── Data/
    └── Smoke/                # Smoke tests (health checks)
```

#### AOT-Compatible Testing Patterns
```csharp
// ✅ GOOD: NSubstitute (AOT-compatible)
var mockRepo = Substitute.For<IReportRepository>();
mockRepo.GetByIdAsync("123").Returns(testReport);

// ❌ BAD: Moq (uses reflection, breaks AOT)
var mockRepo = new Mock<IReportRepository>(); // Don't use!

// ✅ GOOD: System.Text.Json with source generators
var json = JsonSerializer.Serialize(dto, AppJsonContext.Default.ReportDto);

// ❌ BAD: Newtonsoft.Json (not AOT-compatible)
var json = JsonConvert.SerializeObject(dto); // Don't use!
```

#### Idempotency Testing Pattern
```csharp
[Fact]
public async Task CreateReport_WithDuplicateIdempotencyKey_ShouldReturnExisting()
{
    var existingReport = new Report { Id = "123", Title = "Existing" };
    var dto = new CreateReportDto
    {
        Title = "New Report",
        IdempotencyKey = "key-123"
    };

    _mockRepo.GetByIdempotencyKeyAsync(dto.IdempotencyKey)
        .Returns(existingReport);

    var result = await _service.CreateReport(dto);

    result.Should().Be(existingReport);
    await _mockRepo.DidNotReceive().SaveAsync(Arg.Any<Report>());
}
```

#### Conflict Resolution Testing Pattern
```csharp
[Theory]
[InlineData(3, 5, SyncStatus.Conflict)]  // Client behind
[InlineData(5, 5, SyncStatus.Success)]   // Client current
public async Task SyncReport_VersionComparison_ShouldHandleCorrectly(
    int clientVersion,
    int serverVersion,
    SyncStatus expectedStatus)
{
    // Test implementation
}
```

#### Coverage Target
- **>75%** for business logic
- **>60%** for Lambda handlers
- **100%** for sync conflict resolution (critical)

#### Tools
- `xUnit` for test framework
- `NSubstitute` for mocking (AOT-compatible)
- `FluentAssertions` for readable assertions
- `LocalStack` for AWS service integration tests

#### CI/CD Validation
```bash
# Always validate AOT compatibility in CI
dotnet publish -c Release /p:PublishAot=true
```

---

## Infrastructure (AWS CDK)

### File Organization
```
infra/
├── lib/
│   ├── stacks/           # CDK stack definitions
│   ├── constructs/       # Reusable constructs
│   └── config/           # Environment configs
└── bin/
    └── app.ts            # CDK app entry point
```

### Naming Conventions
- **Stacks**: `[Service]Stack` (e.g., `ApiStack`)
- **Constructs**: `[Resource]Construct` (e.g., `LambdaConstruct`)
- **Resources**: `[service]-[env]-[resource]` (e.g., `wereda-prod-api`)

### Code Patterns

#### 1. Cost-Optimized Lambda
```typescript
new Function(this, 'ApiFunction', {
  runtime: Runtime.DOTNET_10,
  architecture: Architecture.ARM_64, // Cheaper than x86
  memorySize: 512, // Start small, scale if needed
  timeout: Duration.seconds(10),
  reservedConcurrentExecutions: 5, // Prevent runaway costs
});
```

### Testing Standards

**See `infra/TESTING.md` for detailed examples and patterns.**

#### Test Organization
```
infra/
└── test/
    ├── unit/                 # CDK construct tests
    │   ├── stacks/
    │   └── constructs/
    └── snapshot/             # Snapshot tests
        └── __snapshots__/
```

#### CDK Testing Pattern
```typescript
// Unit test for Lambda construct
test('creates Lambda function with ARM64 architecture', () => {
  const app = new App();
  const stack = new Stack(app, 'TestStack');

  new LambdaConstruct(stack, 'TestLambda', {
    functionName: 'test-function',
    handler: 'bootstrap',
    runtime: 'dotnet10',
  });

  const template = Template.fromStack(stack);

  template.hasResourceProperties('AWS::Lambda::Function', {
    FunctionName: 'test-function',
    Runtime: 'dotnet10',
    Architectures: ['arm64'],
  });
});
```

#### IAM Policy Testing Pattern
```typescript
// Verify no wildcard permissions
test('Lambda has minimal IAM permissions', () => {
  template.allResourcesProperties('AWS::IAM::Policy', {
    PolicyDocument: {
      Statement: Match.arrayWith([
        Match.not(Match.objectLike({
          Resource: '*',
          Action: '*',
        })),
      ]),
    },
  });
});
```

#### Coverage Target
- **>70%** for custom constructs
- **100%** for IAM policies (security critical)
- **Snapshot tests** for all stacks

#### Tools
- `@aws-cdk/assertions` for CDK testing
- `jest` for test framework
- Snapshot testing for regression detection

---

## Documentation Standards

### Code Comments
- **When to comment**: Why, not what (code should be self-explanatory)
- **Offline considerations**: Always document offline behavior
- **Ethiopian context**: Note cultural/local adaptations

### Commit Messages

**Full rules in `CLAUDE.md` — all agents must follow Conventional Commits.**

**Format:** `<type>(<scope>): <subject>`

**Types:** `feat`, `fix`, `docs`, `style`, `refactor`, `test`, `chore`, `ci`, `perf`, `build`

**Scopes:** `mobile`, `server`, `infra`, `docs`, `l10n`, `sync`, `escrow`, `auth`

**Subject rules:**
- Imperative mood: "add feature" not "added feature" or "adds feature"
- Lowercase after colon: `feat(mobile): add ...` not `feat(mobile): Add ...`
- No period at end
- Max 72 characters total

**Examples:**
```
feat(mobile): add voice-to-text crop listing in Amharic
fix(sync): resolve conflict when offline queue has duplicate entries
docs(specs): add ADR for Telebirr escrow flow
chore(infra): update Lambda memory to 512MB
refactor(server): extract escrow validation into service layer
test(mobile): add offline sync queue retry tests
ci: add AOT compliance check to PR workflow
```

**Multi-line commit (with body):**
```
feat(escrow): add idempotent Telebirr payment release

Ensure escrow release is idempotent by checking idempotency key
before calling Telebirr API. Prevents double-spending when
sync queue retries after network failure.

Closes #45
```

**Forbidden:** `fix bug`, `update code`, `WIP`, `misc`, past/future tense

---

**Last Updated**: 2026-02-09  
**Version**: 1.0.0

