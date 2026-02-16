# Server Testing Guide (.NET 10)

> **Testing philosophy and priorities**: See `.ai/testing.md`

## Quick Start

```bash
dotnet test                                    # All tests
dotnet test --collect:"XPlat Code Coverage"    # With coverage
dotnet test tests/Unit/                        # Specific project
dotnet test --filter Category=Integration      # By category
```

---

## Unit Tests

### Service with AOT-Compatible Mocking (NSubstitute)

```csharp
// tests/Unit/Services/ReportServiceTests.cs
public class ReportServiceTests
{
    private readonly IReportRepository _mockRepo;
    private readonly ISyncService _mockSync;
    private readonly ReportService _service;

    public ReportServiceTests()
    {
        _mockRepo = Substitute.For<IReportRepository>();
        _mockSync = Substitute.For<ISyncService>();
        _service = new ReportService(_mockRepo, _mockSync);
    }

    [Fact]
    public async Task CreateReport_WithValidData_ShouldSaveToRepository()
    {
        var dto = new CreateReportDto { Title = "Test Report", IdempotencyKey = "key-123" };
        _mockRepo.GetByIdempotencyKeyAsync(dto.IdempotencyKey).Returns((Report?)null);

        var result = await _service.CreateReport(dto);

        result.Should().NotBeNull();
        result.Title.Should().Be("Test Report");
        await _mockRepo.Received(1).SaveAsync(Arg.Any<Report>());
    }

    [Fact]
    public async Task CreateReport_WithDuplicateIdempotencyKey_ShouldReturnExisting()
    {
        var existingReport = new Report { Id = "123", Title = "Existing" };
        var dto = new CreateReportDto { Title = "New Report", IdempotencyKey = "key-123" };
        _mockRepo.GetByIdempotencyKeyAsync(dto.IdempotencyKey).Returns(existingReport);

        var result = await _service.CreateReport(dto);

        result.Should().Be(existingReport);
        await _mockRepo.DidNotReceive().SaveAsync(Arg.Any<Report>());
    }
}
```

### Sync Conflict Resolution

```csharp
// tests/Unit/Services/SyncServiceTests.cs
public class SyncServiceTests
{
    private readonly IReportRepository _mockRepo;
    private readonly SyncService _service;

    public SyncServiceTests()
    {
        _mockRepo = Substitute.For<IReportRepository>();
        _service = new SyncService(_mockRepo);
    }

    [Fact]
    public async Task SyncReport_WhenClientVersionBehind_ShouldReturnConflict()
    {
        var serverReport = new Report { Id = "123", Version = 5 };
        var clientRequest = new SyncRequest { EntityId = "123", Version = 3 };
        _mockRepo.GetVersionAsync("123").Returns(serverReport);

        var result = await _service.SyncReport(clientRequest);

        result.Status.Should().Be(SyncStatus.Conflict);
        result.ServerData.Should().Be(serverReport);
        await _mockRepo.DidNotReceive().UpdateAsync(Arg.Any<Report>());
    }

    [Fact]
    public async Task SyncReport_WhenClientVersionCurrent_ShouldApplyChanges()
    {
        var serverReport = new Report { Id = "123", Version = 3 };
        var clientRequest = new SyncRequest { EntityId = "123", Version = 3 };
        _mockRepo.GetVersionAsync("123").Returns(serverReport);

        var result = await _service.SyncReport(clientRequest);

        result.Status.Should().Be(SyncStatus.Success);
        await _mockRepo.Received(1).UpdateAsync(Arg.Is<Report>(r => r.Version == 4));
    }
}
```

---

## Integration Tests (LocalStack)

### LocalStack Fixture

```csharp
// tests/Integration/LocalStackFixture.cs
public class LocalStackFixture : IAsyncLifetime
{
    public IAmazonDynamoDB DynamoDbClient { get; private set; }

    public async Task InitializeAsync()
    {
        var config = new AmazonDynamoDBConfig { ServiceURL = "http://localhost:4566" };
        DynamoDbClient = new AmazonDynamoDBClient(config);
        await CreateTestTables();
    }

    private async Task CreateTestTables()
    {
        await DynamoDbClient.CreateTableAsync(new CreateTableRequest
        {
            TableName = "Reports",
            KeySchema = new List<KeySchemaElement>
            {
                new KeySchemaElement { AttributeName = "Id", KeyType = "HASH" }
            },
            AttributeDefinitions = new List<AttributeDefinition>
            {
                new AttributeDefinition { AttributeName = "Id", AttributeType = "S" }
            },
            BillingMode = BillingMode.PAY_PER_REQUEST
        });
    }

    public async Task DisposeAsync()
    {
        await DynamoDbClient.DeleteTableAsync("Reports");
        DynamoDbClient.Dispose();
    }
}
```

### Lambda Handler Integration Test

```csharp
// tests/Integration/Functions/CreateReportFunctionTests.cs
[Collection("LocalStack")]
public class CreateReportFunctionTests : IClassFixture<LocalStackFixture>
{
    private readonly LocalStackFixture _fixture;
    private readonly CreateReportFunction _function;

    public CreateReportFunctionTests(LocalStackFixture fixture)
    {
        _fixture = fixture;
        var repo = new DynamoDbReportRepository(_fixture.DynamoDbClient);
        _function = new CreateReportFunction(new ReportService(repo));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Handle_WithValidRequest_ShouldCreateReportInDynamoDB()
    {
        var request = new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new CreateReportDto
            {
                Title = "Integration Test Report",
                IdempotencyKey = Guid.NewGuid().ToString()
            }, AppJsonContext.Default.CreateReportDto)
        };

        var response = await _function.Handle(request);

        response.StatusCode.Should().Be(201);
        var report = JsonSerializer.Deserialize<Report>(
            response.Body, AppJsonContext.Default.Report);
        report.Should().NotBeNull();
        report!.Title.Should().Be("Integration Test Report");
    }
}
```

---

## AOT-Compatible Testing Patterns

```csharp
// GOOD: NSubstitute (AOT-compatible)
var mock = Substitute.For<IRepository>();
mock.GetAsync("123").Returns(testData);

// BAD: Moq (uses reflection, fails AOT)
var mock = new Mock<IRepository>();

// GOOD: System.Text.Json source generators
var json = JsonSerializer.Serialize(dto, AppJsonContext.Default.ReportDto);

// BAD: Reflection-based serialization
var json = JsonSerializer.Serialize(dto);

// GOOD: Explicit DI registration
services.AddSingleton<IReportService, ReportService>();

// BAD: Assembly scanning (not AOT-compatible)
services.Scan(scan => scan.FromAssemblyOf<Startup>());
```

---

## Best Practices

### Test idempotency
```csharp
[Fact]
public async Task CreateReport_CalledTwiceWithSameKey_ShouldReturnSameResult()
{
    var dto = new CreateReportDto { IdempotencyKey = "key-123" };
    var result1 = await _service.CreateReport(dto);
    var result2 = await _service.CreateReport(dto);
    result1.Id.Should().Be(result2.Id);
}
```

### Test conflict resolution with Theory
```csharp
[Theory]
[InlineData(3, 5, SyncStatus.Conflict)]  // Client behind
[InlineData(5, 5, SyncStatus.Success)]   // Client current
[InlineData(6, 5, SyncStatus.Error)]     // Client ahead (shouldn't happen)
public async Task SyncReport_VersionComparison(int clientVer, int serverVer, SyncStatus expected)
{
    // test implementation
}
```

### Use test data builders
```csharp
public class ReportBuilder
{
    private string _id = Guid.NewGuid().ToString();
    private string _title = "Test Report";
    private int _version = 1;

    public ReportBuilder WithTitle(string title) { _title = title; return this; }
    public ReportBuilder WithVersion(int version) { _version = version; return this; }
    public Report Build() => new Report { Id = _id, Title = _title, Version = _version };
}

// Usage
var report = new ReportBuilder().WithTitle("Custom").WithVersion(5).Build();
```

---

## Coverage

```bash
dotnet test --collect:"XPlat Code Coverage"
dotnet tool install -g dotnet-reportgenerator-globaltool
reportgenerator -reports:**/coverage.cobertura.xml -targetdir:coverage -reporttypes:Html
open coverage/index.html
```

---

**Last Updated**: 2026-02-09
