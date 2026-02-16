# WeredaConnect Server

.NET 10 serverless backend with **Native AOT** compilation for fast cold starts and low memory. Designed for AWS Lambda with ARM64.

> **Setup instructions**: See `docs/developer/getting-started.md`

---

## Project Structure

```
server/
├── src/
│   ├── Functions/        # Lambda function handlers
│   ├── Models/           # DTOs, entities, request/response models
│   ├── Services/         # Business logic
│   └── Data/             # Database access (DynamoDB, Dapper)
├── tests/                # Unit tests (xUnit)
└── AOT_COMPLIANCE.md     # CRITICAL: Banned libraries & AOT rules
```

---

## Key Technologies

| Component | Choice | Notes |
|-----------|--------|-------|
| Runtime | .NET 10 + Native AOT | ARM64, fast cold starts |
| JSON | System.Text.Json + source generators | **Banned**: Newtonsoft.Json (reflection) |
| Database | DynamoDB | **Banned**: EF Core (not AOT-compatible) |
| Testing | xUnit + NSubstitute | **Banned**: Moq (reflection) |

---

## Native AOT Compliance

**Before adding ANY dependency, check `AOT_COMPLIANCE.md`.**

### Required Patterns

```csharp
// JSON: Use source generators
[JsonSerializable(typeof(UserDto))]
public partial class AppJsonContext : JsonSerializerContext { }
var json = JsonSerializer.Serialize(user, AppJsonContext.Default.UserDto);

// DI: Validate on build
services.AddSingleton<IUserService, UserService>();
services.ValidateOnBuild();

// No reflection
// BAD:  Type.GetType("MyClass"); Activator.CreateInstance(type);
// GOOD: var instance = new MyClass();
```

---

## Architecture Patterns

### Idempotent API Endpoints
```csharp
public async Task<APIGatewayProxyResponse> CreateReport(APIGatewayProxyRequest request)
{
    var dto = Deserialize<CreateReportDto>(request.Body);
    var existing = await _repo.GetByIdempotencyKey(dto.IdempotencyKey);
    if (existing != null) return Ok(existing);
    var report = await _service.Create(dto);
    return Created(report);
}
```

### Sync Conflict Resolution
```csharp
public async Task<SyncResult> SyncReport(SyncRequest request)
{
    var serverVersion = await _repo.GetVersion(request.EntityId);
    if (request.Version < serverVersion.Version)
        return new SyncResult { Status = SyncStatus.Conflict, ServerData = serverVersion };
    await _repo.Update(request.Data, request.Version + 1);
    return new SyncResult { Status = SyncStatus.Success };
}
```

### Event-Driven (DynamoDB Streams + Lambda)
```csharp
public async Task ProcessReportCreated(DynamoDBEvent dynamoEvent)
{
    foreach (var record in dynamoEvent.Records.Where(r => r.EventName == "INSERT"))
    {
        var report = ParseRecord<Report>(record.Dynamodb.NewImage);
        await _notificationService.NotifySubscribers(report);
    }
}
```

---

## Performance Targets

| Metric | Target |
|--------|--------|
| Cold start | <2s |
| Memory | 512MB |
| Response time (p95) | <500ms |

---

## Deployment

```bash
# Local testing (SAM)
sam build && sam local start-api

# Deploy via CDK (recommended)
cd ../infra && cdk deploy

# AOT build validation
dotnet publish -c Release /p:PublishAot=true
```

---

## Common Issues

**AOT build fails**: Check `AOT_COMPLIANCE.md`, look for reflection usage, use source generators for JSON

**DynamoDB local**: Ensure LocalStack running (`docker ps | grep localstack`), set `AWS_ENDPOINT_URL=http://localhost:4566`

**Lambda timeout**: Increase timeout in CDK/SAM template, optimize DB queries, verify async/await usage

---

**Last Updated**: 2026-02-09
**Version**: 0.1.0 (pre-alpha)
