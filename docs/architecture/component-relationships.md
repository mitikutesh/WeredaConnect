# Component Relationships

## Purpose
This document details how different components in WeredaConnect interact with each other, including dependencies, communication patterns, and integration points.

## Audience
- Developers implementing features
- Architects designing new components
- Contributors understanding the codebase

## Prerequisites
- Read [System Architecture](./system-architecture.md) first
- Basic understanding of Flutter and .NET

---

## Mobile Component Relationships

### Layer Architecture

```
┌─────────────────────────────────────────────────────────┐
│                  Presentation Layer                      │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐              │
│  │ Screens  │→ │ Widgets  │→ │ BLoC     │              │
│  └──────────┘  └──────────┘  └──────────┘              │
└─────────────────────────────────────────────────────────┘
                        ↓ Events / States
┌─────────────────────────────────────────────────────────┐
│                   Domain Layer                           │
│  ┌──────────────┐  ┌──────────────┐                     │
│  │ Use Cases    │  │ Entities     │                     │
│  └──────────────┘  └──────────────┘                     │
└─────────────────────────────────────────────────────────┘
                        ↓ Repository Interface
┌─────────────────────────────────────────────────────────┐
│                    Data Layer                            │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐  │
│  │ Repository   │→ │ Local DS     │  │ Remote DS    │  │
│  │ (Impl)       │  │ (Isar)       │  │ (HTTP)       │  │
│  └──────────────┘  └──────────────┘  └──────────────┘  │
└─────────────────────────────────────────────────────────┘
                        ↓ Sync Queue
┌─────────────────────────────────────────────────────────┐
│                   Core Layer                             │
│  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐  │
│  │ Sync Engine  │  │ Connectivity │  │ Error        │  │
│  │              │  │ Service      │  │ Handling     │  │
│  └──────────────┘  └──────────────┘  └──────────────┘  │
└─────────────────────────────────────────────────────────┘
```

### Component Dependencies

#### 1. Presentation → Domain
```dart
// Screen depends on BLoC
class ReportListScreen extends StatelessWidget {
  @override
  Widget build(BuildContext context) {
    return BlocProvider(
      create: (context) => ReportListBloc(
        getReports: context.read<GetReportsUseCase>(),
      ),
      child: ReportListView(),
    );
  }
}

// BLoC depends on Use Cases
class ReportListBloc extends Bloc<ReportListEvent, ReportListState> {
  final GetReportsUseCase getReports;
  
  ReportListBloc({required this.getReports});
}
```

#### 2. Domain → Data
```dart
// Use Case depends on Repository Interface
class GetReportsUseCase {
  final ReportRepository repository;
  
  Future<List<Report>> call() async {
    return await repository.getReports();
  }
}

// Repository Interface (in domain layer)
abstract class ReportRepository {
  Future<List<Report>> getReports();
  Future<Report> getReport(String id);
  Future<void> createReport(Report report);
}
```

#### 3. Data → Core
```dart
// Repository Implementation depends on Data Sources
class ReportRepositoryImpl implements ReportRepository {
  final ReportLocalDataSource localDataSource;
  final ReportRemoteDataSource remoteDataSource;
  final ConnectivityService connectivity;
  final SyncQueue syncQueue;
  
  @override
  Future<Report> getReport(String id) async {
    // Try local first
    final local = await localDataSource.getReport(id);
    if (local != null) return local;
    
    // Check connectivity
    if (!await connectivity.isOnline) {
      throw OfflineException();
    }
    
    // Fetch from remote and cache
    final remote = await remoteDataSource.getReport(id);
    await localDataSource.saveReport(remote);
    return remote;
  }
}
```

---

## Backend Component Relationships

### Lambda Function Architecture

```
┌─────────────────────────────────────────────────────────┐
│                  API Gateway                             │
│  - Routes requests to Lambda functions                   │
│  - Handles CORS, throttling, API keys                    │
└─────────────────────────────────────────────────────────┘
                        ↓ HTTP Request
┌─────────────────────────────────────────────────────────┐
│              Lambda Function Handler                     │
│  ┌──────────────────────────────────────────────────┐   │
│  │ FunctionHandler.cs                                │   │
│  │  - Parse request                                  │   │
│  │  - Validate input                                 │   │
│  │  - Call service                                   │   │
│  │  - Return response                                │   │
│  └──────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────┘
                        ↓ Service Call
┌─────────────────────────────────────────────────────────┐
│                  Service Layer                           │
│  ┌──────────────────────────────────────────────────┐   │
│  │ ReportService.cs                                  │   │
│  │  - Business logic                                 │   │
│  │  - Idempotency checks                             │   │
│  │  - Conflict resolution                            │   │
│  │  - Validation                                     │   │
│  └──────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────┘
                        ↓ Data Access
┌─────────────────────────────────────────────────────────┐
│                  Data Layer                              │
│  ┌──────────────────────────────────────────────────┐   │
│  │ ReportRepository.cs                               │   │
│  │  - DynamoDB operations                            │   │
│  │  - S3 operations (for media)                      │   │
│  │  - Query optimization                             │   │
│  └──────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────┘
                        ↓ AWS SDK
┌─────────────────────────────────────────────────────────┐
│                  AWS Services                            │
│  ┌──────────┐  ┌──────────┐  ┌──────────┐              │
│  │ DynamoDB │  │ S3       │  │ Cognito  │              │
│  └──────────┘  └──────────┘  └──────────┘              │
└─────────────────────────────────────────────────────────┘
```

### Component Dependencies

#### 1. Handler → Service
```csharp
// Lambda handler depends on Service
public class CreateReportFunction
{
    private readonly IReportService _reportService;
    
    public CreateReportFunction(IReportService reportService)
    {
        _reportService = reportService;
    }
    
    public async Task<APIGatewayProxyResponse> Handle(
        APIGatewayProxyRequest request)
    {
        var dto = JsonSerializer.Deserialize<CreateReportDto>(
            request.Body, 
            AppJsonContext.Default.CreateReportDto
        );
        
        var report = await _reportService.CreateReport(dto);
        
        return new APIGatewayProxyResponse
        {
            StatusCode = 201,
            Body = JsonSerializer.Serialize(
                report, 
                AppJsonContext.Default.Report
            )
        };
    }
}
```

#### 2. Service → Repository
```csharp
// Service depends on Repository
public class ReportService : IReportService
{
    private readonly IReportRepository _repository;
    
    public async Task<Report> CreateReport(CreateReportDto dto)
    {
        // Check idempotency
        var existing = await _repository
            .GetByIdempotencyKeyAsync(dto.IdempotencyKey);
        if (existing != null) return existing;
        
        // Create new report
        var report = new Report
        {
            Id = Guid.NewGuid().ToString(),
            Title = dto.Title,
            Version = 1,
            IdempotencyKey = dto.IdempotencyKey
        };
        
        await _repository.SaveAsync(report);
        return report;
    }
}
```

---

## Cross-Platform Communication

### Mobile ↔ Backend

#### 1. API Request Flow
```
Mobile App                    API Gateway              Lambda
    │                              │                      │
    │ 1. HTTP POST /reports        │                      │
    │─────────────────────────────>│                      │
    │                              │ 2. Route to Lambda   │
    │                              │─────────────────────>│
    │                              │                      │ 3. Process
    │                              │                      │    request
    │                              │ 4. Response          │
    │                              │<─────────────────────│
    │ 5. HTTP 201 Created          │                      │
    │<─────────────────────────────│                      │
    │                              │                      │
```

#### 2. Sync Flow
```
Mobile App                    Sync Queue              Backend
    │                              │                      │
    │ 1. Create report offline     │                      │
    │ 2. Save to local DB          │                      │
    │ 3. Add to sync queue         │                      │
    │───────────────────────────>  │                      │
    │                              │                      │
    │ 4. Connectivity detected     │                      │
    │ 5. Process queue             │                      │
    │                              │ 6. POST /sync        │
    │                              │─────────────────────>│
    │                              │                      │ 7. Check version
    │                              │                      │ 8. Resolve conflict
    │                              │ 9. Response          │
    │                              │<─────────────────────│
    │ 10. Update local DB          │                      │
    │<─────────────────────────────│                      │
    │ 11. Remove from queue        │                      │
    │───────────────────────────>  │                      │
```

---

## Infrastructure Component Relationships

### CDK Stack Dependencies

```
┌─────────────────────────────────────────────────────────┐
│                    CDK App                               │
│  - Entry point (bin/app.ts)                              │
│  - Creates all stacks                                    │
└─────────────────────────────────────────────────────────┘
                        ↓ Creates
┌─────────────────────────────────────────────────────────┐
│                 Database Stack                           │
│  - DynamoDB tables                                       │
│  - Exports: table names, ARNs                            │
└─────────────────────────────────────────────────────────┘
                        ↓ Depends on
┌─────────────────────────────────────────────────────────┐
│                   API Stack                              │
│  - Lambda functions                                      │
│  - API Gateway                                           │
│  - Imports: table names from Database Stack             │
└─────────────────────────────────────────────────────────┘
                        ↓ Depends on
┌─────────────────────────────────────────────────────────┐
│                  CDN Stack                               │
│  - CloudFront distribution                               │
│  - S3 buckets                                            │
│  - Imports: API endpoint from API Stack                 │
└─────────────────────────────────────────────────────────┘
```

---

## Related Documentation

- **[System Architecture](./system-architecture.md)** - High-level overview
- **[Data Flow](./data-flow.md)** - Data flow patterns
- **[API Reference](../developer/api-reference.md)** - API endpoints

---

**Last Updated**: 2026-02-09  
**Version**: 1.0.0  
**Owner**: Architecture Team

