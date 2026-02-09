# 🧪 Server Testing Guide (.NET 10)

## Overview
Comprehensive testing guide for WeredaConnect .NET 10 serverless backend. Covers unit tests, integration tests, smoke tests, and AOT-compatible testing patterns.

---

## Quick Start

```bash
# Run all tests
dotnet test

# Run with coverage
dotnet test --collect:"XPlat Code Coverage"

# Run specific test project
dotnet test tests/Unit/

# Run tests by category
dotnet test --filter Category=Integration

# Run in watch mode
dotnet watch test
```

---

## 1. Unit Tests

### What to Test
- Business logic in services
- Validation logic
- Data transformations
- Idempotency checks
- Conflict resolution logic

### Example: Testing Service with AOT-Compatible Mocking

```csharp
// tests/Unit/Services/ReportServiceTests.cs
using Xunit;
using NSubstitute;
using FluentAssertions;

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
        // Arrange
        var dto = new CreateReportDto
        {
            Title = "Test Report",
            IdempotencyKey = "key-123"
        };

        _mockRepo.GetByIdempotencyKeyAsync(dto.IdempotencyKey)
            .Returns((Report?)null);

        // Act
        var result = await _service.CreateReport(dto);

        // Assert
        result.Should().NotBeNull();
        result.Title.Should().Be("Test Report");
        await _mockRepo.Received(1).SaveAsync(Arg.Any<Report>());
    }

    [Fact]
    public async Task CreateReport_WithDuplicateIdempotencyKey_ShouldReturnExisting()
    {
        // Arrange
        var existingReport = new Report { Id = "123", Title = "Existing" };
        var dto = new CreateReportDto
        {
            Title = "New Report",
            IdempotencyKey = "key-123"
        };

        _mockRepo.GetByIdempotencyKeyAsync(dto.IdempotencyKey)
            .Returns(existingReport);

        // Act
        var result = await _service.CreateReport(dto);

        // Assert
        result.Should().Be(existingReport);
        await _mockRepo.DidNotReceive().SaveAsync(Arg.Any<Report>());
    }
}
```

### Example: Testing Sync Conflict Resolution

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
        // Arrange
        var serverReport = new Report { Id = "123", Version = 5 };
        var clientRequest = new SyncRequest
        {
            EntityId = "123",
            Version = 3, // Behind server
            Data = new { Title = "Client Update" }
        };

        _mockRepo.GetVersionAsync("123").Returns(serverReport);

        // Act
        var result = await _service.SyncReport(clientRequest);

        // Assert
        result.Status.Should().Be(SyncStatus.Conflict);
        result.ServerData.Should().Be(serverReport);
        await _mockRepo.DidNotReceive().UpdateAsync(Arg.Any<Report>());
    }

    [Fact]
    public async Task SyncReport_WhenClientVersionCurrent_ShouldApplyChanges()
    {
        // Arrange
        var serverReport = new Report { Id = "123", Version = 3 };
        var clientRequest = new SyncRequest
        {
            EntityId = "123",
            Version = 3, // Current
            Data = new { Title = "Updated Title" }
        };

        _mockRepo.GetVersionAsync("123").Returns(serverReport);

        // Act
        var result = await _service.SyncReport(clientRequest);

        // Assert
        result.Status.Should().Be(SyncStatus.Success);
        await _mockRepo.Received(1).UpdateAsync(
            Arg.Is<Report>(r => r.Version == 4)
        );
    }
}
```

---

## 2. Integration Tests

### What to Test
- Lambda function handlers
- DynamoDB operations
- API Gateway integration
- End-to-end request/response flow

### Setup LocalStack for AWS Services

```csharp
// tests/Integration/LocalStackFixture.cs
public class LocalStackFixture : IAsyncLifetime
{
    public IAmazonDynamoDB DynamoDbClient { get; private set; }
    
    public async Task InitializeAsync()
    {
        var config = new AmazonDynamoDBConfig
        {
            ServiceURL = "http://localhost:4566"
        };
        
        DynamoDbClient = new AmazonDynamoDBClient(config);
        
        // Create test tables
        await CreateTestTables();
    }
    
    private async Task CreateTestTables()
    {
        var request = new CreateTableRequest
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
        };
        
        await DynamoDbClient.CreateTableAsync(request);
    }
    
    public async Task DisposeAsync()
    {
        await DynamoDbClient.DeleteTableAsync("Reports");
        DynamoDbClient.Dispose();
    }
}
```

### Example: Integration Test with LocalStack

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
        var service = new ReportService(repo);
        _function = new CreateReportFunction(service);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Handle_WithValidRequest_ShouldCreateReportInDynamoDB()
    {
        // Arrange
        var request = new APIGatewayProxyRequest
        {
            Body = JsonSerializer.Serialize(new CreateReportDto
            {
                Title = "Integration Test Report",
                IdempotencyKey = Guid.NewGuid().ToString()
            }, AppJsonContext.Default.CreateReportDto)
        };

        // Act
        var response = await _function.Handle(request);

        // Assert
        response.StatusCode.Should().Be(201);
        
        var report = JsonSerializer.Deserialize<Report>(
            response.Body, 
            AppJsonContext.Default.Report
        );
        
        report.Should().NotBeNull();
        report!.Title.Should().Be("Integration Test Report");
        
        // Verify in DynamoDB
        var dbReport = await _fixture.DynamoDbClient.GetItemAsync(
            "Reports",
            new Dictionary<string, AttributeValue>
            {
                ["Id"] = new AttributeValue { S = report.Id }
            }
        );
        
        dbReport.Item.Should().NotBeNull();
    }
}
```

---

## 3. Smoke Tests

### What to Test
- Lambda functions can be invoked
- DynamoDB connections work
- Health check endpoints respond
- AOT compilation succeeds

### Example: Smoke Test Suite

```csharp
// tests/Smoke/HealthCheckTests.cs
public class HealthCheckTests
{
    [Fact]
    [Trait("Category", "Smoke")]
    public async Task HealthCheck_ShouldReturnOk()
    {
        // Arrange
        var function = new HealthCheckFunction();
        var request = new APIGatewayProxyRequest();

        // Act
        var response = await function.Handle(request);

        // Assert
        response.StatusCode.Should().Be(200);
        response.Body.Should().Contain("healthy");
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task DynamoDB_ShouldBeAccessible()
    {
        // Arrange
        var client = new AmazonDynamoDBClient();

        // Act
        var tables = await client.ListTablesAsync();

        // Assert
        tables.HttpStatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }
}
```

---

## AOT-Compatible Testing Patterns

### ✅ Use NSubstitute (Not Moq)
```csharp
// ✅ GOOD: NSubstitute is AOT-compatible
var mock = Substitute.For<IRepository>();
mock.GetAsync("123").Returns(testData);

// ❌ BAD: Moq uses reflection
var mock = new Mock<IRepository>(); // Will fail AOT build
```

### ✅ Use System.Text.Json with Source Generators
```csharp
// ✅ GOOD: Source generator for AOT
var json = JsonSerializer.Serialize(dto, AppJsonContext.Default.ReportDto);

// ❌ BAD: Reflection-based serialization
var json = JsonSerializer.Serialize(dto); // May fail AOT
```

### ✅ Explicit Type Registration
```csharp
// ✅ GOOD: Explicit DI registration
services.AddSingleton<IReportService, ReportService>();

// ❌ BAD: Assembly scanning
services.Scan(scan => scan.FromAssemblyOf<Startup>()); // Not AOT-compatible
```

---

## Test Configuration

### Project File (.csproj)
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
    <IsTestProject>true</IsTestProject>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="xunit" Version="2.9.0" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.8.0" />
    <PackageReference Include="NSubstitute" Version="5.1.0" />
    <PackageReference Include="FluentAssertions" Version="6.12.0" />
    <PackageReference Include="AWSSDK.DynamoDBv2" Version="3.7.400" />
    <PackageReference Include="coverlet.collector" Version="6.0.0" />
  </ItemGroup>
</Project>
```

---

## CI/CD Integration

### GitHub Actions Example
```yaml
name: .NET Tests

on: [push, pull_request]

jobs:
  test:
    runs-on: ubuntu-latest

    services:
      localstack:
        image: localstack/localstack
        ports:
          - 4566:4566
        env:
          SERVICES: dynamodb,s3

    steps:
      - uses: actions/checkout@v3

      - name: Setup .NET
        uses: actions/setup-dotnet@v3
        with:
          dotnet-version: '10.0.x'

      - name: Restore dependencies
        run: dotnet restore

      - name: Build
        run: dotnet build --no-restore

      - name: Run unit tests
        run: dotnet test --no-build --filter Category!=Integration

      - name: Run integration tests
        run: dotnet test --no-build --filter Category=Integration
        env:
          AWS_ENDPOINT_URL: http://localhost:4566

      - name: Validate AOT compatibility
        run: dotnet publish -c Release /p:PublishAot=true
```

---

## Best Practices

### 1. Test Idempotency
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

### 2. Test Conflict Resolution
```csharp
[Theory]
[InlineData(3, 5, SyncStatus.Conflict)]  // Client behind
[InlineData(5, 5, SyncStatus.Success)]   // Client current
[InlineData(6, 5, SyncStatus.Error)]     // Client ahead (shouldn't happen)
public async Task SyncReport_VersionComparison_ShouldHandleCorrectly(
    int clientVersion,
    int serverVersion,
    SyncStatus expectedStatus)
{
    // Test implementation
}
```

### 3. Use Test Data Builders
```csharp
public class ReportBuilder
{
    private string _id = Guid.NewGuid().ToString();
    private string _title = "Test Report";
    private int _version = 1;

    public ReportBuilder WithId(string id) { _id = id; return this; }
    public ReportBuilder WithTitle(string title) { _title = title; return this; }
    public ReportBuilder WithVersion(int version) { _version = version; return this; }

    public Report Build() => new Report
    {
        Id = _id,
        Title = _title,
        Version = _version
    };
}

// Usage
var report = new ReportBuilder()
    .WithTitle("Custom Title")
    .WithVersion(5)
    .Build();
```

---

## Coverage Reporting

### Generate Coverage Report
```bash
# Run tests with coverage
dotnet test --collect:"XPlat Code Coverage"

# Install report generator
dotnet tool install -g dotnet-reportgenerator-globaltool

# Generate HTML report
reportgenerator \
  -reports:**/coverage.cobertura.xml \
  -targetdir:coverage \
  -reporttypes:Html

# Open report
open coverage/index.html
```

---

**Last Updated**: 2026-02-09
**Version**: 1.0.0

