# ⚡ WeredaConnect Server

## Overview
.NET 10 serverless backend with **Native AOT** compilation for fast cold starts and low memory usage. Designed for AWS Lambda with ARM64 architecture.

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
├── AOT_COMPLIANCE.md     # ⚠️ CRITICAL: Banned libraries & AOT rules
└── README.md             # This file
```

---

## Getting Started

### Prerequisites
- .NET 10 SDK
- AWS CLI configured
- AWS SAM CLI (for local testing)
- Docker (for LocalStack/SAM)

### Setup
```bash
# Restore dependencies
dotnet restore

# Build (AOT validation)
dotnet build

# Run tests
dotnet test

# Publish with AOT
dotnet publish -c Release
```

---

## Key Technologies

### Runtime
- **.NET 10**: Latest LTS version
- **Native AOT**: Ahead-of-time compilation for fast cold starts
- **ARM64**: Cost-optimized Lambda architecture

### JSON Serialization
- **System.Text.Json**: AOT-compatible (with source generators)
- **❌ BANNED**: Newtonsoft.Json (uses reflection)

### Database
- **DynamoDB**: Primary data store (serverless, scalable)
- **Dapper**: Lightweight ORM for SQL if needed
- **❌ BANNED**: Entity Framework Core (not AOT-compatible)

### Testing
- **xUnit**: Unit testing framework
- **NSubstitute**: Mocking library
- **❌ BANNED**: Moq (uses reflection)

---

## Native AOT Compliance

### ⚠️ CRITICAL
**Before adding ANY dependency, check `AOT_COMPLIANCE.md`**

AOT compilation fails at build time if you use:
- Reflection-heavy libraries
- Dynamic code generation
- Certain LINQ expressions

### Required Patterns

#### 1. JSON Serialization with Source Generators
```csharp
[JsonSerializable(typeof(UserDto))]
[JsonSerializable(typeof(List<UserDto>))]
public partial class AppJsonContext : JsonSerializerContext { }

// Usage
var json = JsonSerializer.Serialize(user, AppJsonContext.Default.UserDto);
var user = JsonSerializer.Deserialize<UserDto>(json, AppJsonContext.Default.UserDto);
```

#### 2. Dependency Injection Validation
```csharp
// Validate DI container at startup for AOT
var services = new ServiceCollection();
services.AddSingleton<IUserService, UserService>();
services.ValidateOnBuild(); // Fails at build time if issues
```

#### 3. Avoid Reflection
```csharp
// ❌ BAD (uses reflection)
var type = Type.GetType("MyNamespace.MyClass");
var instance = Activator.CreateInstance(type);

// ✅ GOOD (explicit)
var instance = new MyClass();
```

---

## Architecture Patterns

### 1. Idempotent API Endpoints
All write operations must be idempotent (safe to retry).

```csharp
public async Task<APIGatewayProxyResponse> CreateReport(
    APIGatewayProxyRequest request)
{
    var dto = Deserialize<CreateReportDto>(request.Body);
    
    // Check idempotency key
    var existing = await _repo.GetByIdempotencyKey(dto.IdempotencyKey);
    if (existing != null)
    {
        return Ok(existing); // Already processed
    }
    
    var report = await _service.Create(dto);
    return Created(report);
}
```

### 2. Conflict Resolution for Sync
Handle offline clients syncing stale data.

```csharp
public async Task<SyncResult> SyncReport(SyncRequest request)
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

### 3. Event-Driven Architecture
Use DynamoDB Streams + Lambda for async processing.

```csharp
public async Task ProcessReportCreated(DynamoDBEvent dynamoEvent)
{
    foreach (var record in dynamoEvent.Records)
    {
        if (record.EventName == "INSERT")
        {
            var report = ParseRecord<Report>(record.Dynamodb.NewImage);
            await _notificationService.NotifySubscribers(report);
        }
    }
}
```

---

## Testing

### Unit Tests
```bash
dotnet test
```

### Integration Tests (LocalStack)
```bash
# Start LocalStack
docker-compose up -d

# Run integration tests
dotnet test --filter Category=Integration
```

### AOT Build Validation
```bash
# This will fail if AOT-incompatible code exists
dotnet publish -c Release /p:PublishAot=true
```

---

## Deployment

### Local Testing (SAM)
```bash
# Build
sam build

# Invoke locally
sam local invoke CreateReportFunction -e events/create-report.json

# Start local API
sam local start-api
```

### Deploy to AWS
```bash
# Via CDK (recommended)
cd ../infra
cdk deploy

# Or via SAM
sam deploy --guided
```

---

## Performance Targets

### Cold Start
- **Target**: <2 seconds
- **Current**: TBD
- **Why**: Serverless, intermittent traffic

### Memory
- **Target**: 512MB
- **Current**: TBD
- **Why**: Cost optimization

### Response Time
- **Target**: <500ms (p95)
- **Current**: TBD
- **Why**: 3G network latency

---

## Common Issues

### AOT Build Fails
1. Check `AOT_COMPLIANCE.md` for banned libraries
2. Review error messages for reflection usage
3. Use source generators for JSON serialization
4. Avoid `Type.GetType()`, `Activator.CreateInstance()`, etc.

### DynamoDB Local Connection Issues
```bash
# Ensure LocalStack is running
docker ps | grep localstack

# Check endpoint configuration
export AWS_ENDPOINT_URL=http://localhost:4566
```

### Lambda Timeout
- Increase timeout in CDK/SAM template
- Optimize database queries
- Use async/await properly

---

## Security

### Authentication
- API Gateway with Cognito or custom authorizer
- JWT tokens validated in Lambda

### Authorization
- Role-based access control (RBAC)
- Resource-level permissions

### Data Privacy
- Encrypt sensitive data at rest (DynamoDB encryption)
- Encrypt in transit (TLS 1.2+)
- Minimal data collection (see `CLAUDE.md`)

---

## Monitoring

### CloudWatch Metrics
- Invocation count
- Error rate
- Duration (cold start vs. warm)
- Throttles

### CloudWatch Logs
- Structured logging (JSON)
- Correlation IDs for tracing
- Error stack traces

### Alarms
- Error rate >1%
- Duration >5 seconds
- Throttles >0

---

**Last Updated**: 2026-02-09  
**Version**: 0.1.0 (pre-alpha)

