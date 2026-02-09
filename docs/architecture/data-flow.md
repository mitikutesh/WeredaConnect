# Data Flow

## Purpose
This document explains how data flows through WeredaConnect in various scenarios, with special emphasis on offline-first patterns and sync mechanisms.

## Audience
- Developers implementing features
- Architects designing data flows
- Contributors understanding offline-first

## Prerequisites
- Read [System Architecture](./system-architecture.md)
- Read [Component Relationships](./component-relationships.md)
- Understand offline-first principles

---

## Core Data Flow Principles

### 1. Local-First
**Principle**: Local database is the source of truth for the mobile app.

### 2. Eventual Consistency
**Principle**: Data will eventually sync when connectivity is available.

### 3. Conflict Resolution
**Principle**: Server wins, client merges (last-write-wins with version tracking).

### 4. Idempotency
**Principle**: Operations can be safely retried without side effects.

---

## Read Operations

### Scenario 1: Read Data (Available Locally)

```
User Action                Local DB              Remote API
    │                          │                      │
    │ 1. Request report        │                      │
    │─────────────────────────>│                      │
    │                          │ 2. Query local DB    │
    │                          │                      │
    │ 3. Return data           │                      │
    │<─────────────────────────│                      │
    │                          │                      │
    │ 4. Display to user       │                      │
    │                          │                      │
```

**Code Example (Mobile)**:
```dart
Future<Report> getReport(String id) async {
  // Try local first
  final local = await _localDataSource.getReport(id);
  if (local != null) {
    return local; // Fast! No network call
  }
  
  // Not found locally, need to fetch
  // (see Scenario 2)
}
```

---

### Scenario 2: Read Data (Not Cached, Online)

```
User Action                Local DB              Remote API
    │                          │                      │
    │ 1. Request report        │                      │
    │─────────────────────────>│                      │
    │                          │ 2. Query local DB    │
    │                          │ (not found)          │
    │                          │                      │
    │ 3. Check connectivity    │                      │
    │ (online)                 │                      │
    │                          │ 4. Fetch from API    │
    │                          │─────────────────────>│
    │                          │                      │ 5. Query DynamoDB
    │                          │ 6. Return data       │
    │                          │<─────────────────────│
    │                          │ 7. Cache locally     │
    │                          │                      │
    │ 8. Return data           │                      │
    │<─────────────────────────│                      │
    │                          │                      │
```

**Code Example (Mobile)**:
```dart
Future<Report> getReport(String id) async {
  // Try local first
  final local = await _localDataSource.getReport(id);
  if (local != null) return local;
  
  // Check connectivity
  if (!await _connectivity.isOnline) {
    throw OfflineException('Report not available offline');
  }
  
  // Fetch from remote
  final remote = await _remoteDataSource.getReport(id);
  
  // Cache for offline use
  await _localDataSource.saveReport(remote);
  
  return remote;
}
```

---

### Scenario 3: Read Data (Not Cached, Offline)

```
User Action                Local DB              Remote API
    │                          │                      │
    │ 1. Request report        │                      │
    │─────────────────────────>│                      │
    │                          │ 2. Query local DB    │
    │                          │ (not found)          │
    │                          │                      │
    │ 3. Check connectivity    │                      │
    │ (offline)                │                      │
    │                          │                      │
    │ 4. Throw OfflineException│                      │
    │<─────────────────────────│                      │
    │                          │                      │
    │ 5. Show offline message  │                      │
    │                          │                      │
```

**Code Example (Mobile)**:
```dart
Future<Report> getReport(String id) async {
  final local = await _localDataSource.getReport(id);
  if (local != null) return local;
  
  if (!await _connectivity.isOnline) {
    throw OfflineException('Report not available offline');
  }
  
  // ... fetch from remote
}
```

---

## Write Operations

### Scenario 4: Create Data (Offline)

```
User Action          Local DB          Sync Queue          Remote API
    │                    │                  │                   │
    │ 1. Create report   │                  │                   │
    │───────────────────>│                  │                   │
    │                    │ 2. Save locally  │                   │
    │                    │ (synced=false)   │                   │
    │                    │                  │                   │
    │                    │ 3. Add to queue  │                   │
    │                    │─────────────────>│                   │
    │                    │                  │ 4. Queue operation│
    │                    │                  │                   │
    │ 5. Confirm saved   │                  │                   │
    │<───────────────────│                  │                   │
    │                    │                  │                   │
    │ 6. Show "Pending   │                  │                   │
    │    Sync" indicator │                  │                   │
    │                    │                  │                   │
```

**Code Example (Mobile)**:
```dart
Future<void> createReport(Report report) async {
  // Save locally first
  report.synced = false;
  await _localDataSource.saveReport(report);
  
  // Add to sync queue
  await _syncQueue.add(SyncOperation(
    type: SyncType.create,
    entityType: 'report',
    entityId: report.id,
    data: report.toJson(),
    idempotencyKey: report.idempotencyKey,
  ));
  
  // User sees report immediately (offline-first!)
}
```

---

### Scenario 5: Sync Data (When Online)

```
Sync Engine          Sync Queue          Remote API          Local DB
    │                    │                   │                   │
    │ 1. Connectivity    │                   │                   │
    │    detected        │                   │                   │
    │                    │                   │                   │
    │ 2. Get pending ops │                   │                   │
    │───────────────────>│                   │                   │
    │                    │ 3. Return ops     │                   │
    │<───────────────────│                   │                   │
    │                    │                   │                   │
    │ 4. POST /sync      │                   │                   │
    │───────────────────────────────────────>│                   │
    │                    │                   │ 5. Check version  │
    │                    │                   │ 6. Check idempotency
    │                    │                   │ 7. Save to DynamoDB
    │                    │                   │                   │
    │ 8. Success response│                   │                   │
    │<───────────────────────────────────────│                   │
    │                    │                   │                   │
    │ 9. Update local    │                   │                   │
    │    (synced=true)   │                   │                   │
    │───────────────────────────────────────────────────────────>│
    │                    │                   │                   │
    │ 10. Remove from    │                   │                   │
    │     queue          │                   │                   │
    │───────────────────>│                   │                   │
    │                    │                   │                   │
```

**Code Example (Mobile)**:
```dart
Future<void> syncPendingOperations() async {
  if (!await _connectivity.isOnline) return;
  
  final pendingOps = await _syncQueue.getPending();
  
  for (final op in pendingOps) {
    try {
      // Send to server
      final response = await _apiClient.post('/sync', data: {
        'entityType': op.entityType,
        'entityId': op.entityId,
        'operation': op.type.toString(),
        'data': op.data,
        'version': op.version,
        'idempotencyKey': op.idempotencyKey,
      });
      
      // Update local DB
      await _localDataSource.updateSyncStatus(op.entityId, synced: true);
      
      // Remove from queue
      await _syncQueue.remove(op.id);
      
    } catch (e) {
      // Keep in queue, retry later
      await _syncQueue.incrementRetryCount(op.id);
    }
  }
}
```

---

## Conflict Resolution

### Scenario 6: Sync Conflict (Client Behind Server)

```
Mobile App           Remote API           DynamoDB
    │                    │                    │
    │ 1. POST /sync      │                    │
    │    version: 3      │                    │
    │───────────────────>│                    │
    │                    │ 2. Get server      │
    │                    │    version         │
    │                    │───────────────────>│
    │                    │ 3. version: 5      │
    │                    │<───────────────────│
    │                    │                    │
    │                    │ 4. Conflict!       │
    │                    │    (3 < 5)         │
    │                    │                    │
    │ 5. 409 Conflict    │                    │
    │    + server data   │                    │
    │<───────────────────│                    │
    │                    │                    │
    │ 6. Merge changes   │                    │
    │    (server wins)   │                    │
    │                    │                    │
    │ 7. Retry with      │                    │
    │    merged data     │                    │
    │    version: 5      │                    │
    │───────────────────>│                    │
    │                    │ 8. Save version 6  │
    │                    │───────────────────>│
    │ 9. 200 OK          │                    │
    │<───────────────────│                    │
```

**Code Example (Server)**:
```csharp
public async Task<SyncResult> SyncReport(SyncRequest request)
{
    // Get server version
    var serverReport = await _repository.GetAsync(request.EntityId);
    
    // Check for conflict
    if (request.Version < serverReport.Version)
    {
        return new SyncResult
        {
            Status = SyncStatus.Conflict,
            ServerData = serverReport,
            Message = "Client version behind server"
        };
    }
    
    // No conflict, apply changes
    serverReport.ApplyChanges(request.Data);
    serverReport.Version++;
    
    await _repository.UpdateAsync(serverReport);
    
    return new SyncResult
    {
        Status = SyncStatus.Success,
        Data = serverReport
    };
}
```

**Code Example (Mobile - Conflict Handling)**:
```dart
Future<void> handleSyncConflict(
  SyncOperation op, 
  ConflictResponse conflict
) async {
  // Server wins, client merges
  final serverData = conflict.serverData;
  final clientData = op.data;
  
  // Merge strategy: keep server data, add client-only fields
  final merged = {
    ...serverData,
    // Add any client-specific fields that don't conflict
  };
  
  // Update local DB with server version
  await _localDataSource.update(op.entityId, merged);
  
  // Retry sync with merged data
  await _syncQueue.update(op.id, 
    data: merged,
    version: conflict.serverVersion,
  );
}
```

---

## Related Documentation

- **[System Architecture](./system-architecture.md)** - Architecture overview
- **[Component Relationships](./component-relationships.md)** - Component interactions
- **[Testing Guidelines](../developer/testing-guidelines.md)** - How to test data flows

---

**Last Updated**: 2026-02-09  
**Version**: 1.0.0  
**Owner**: Architecture Team

