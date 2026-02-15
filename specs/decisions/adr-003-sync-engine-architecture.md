# ADR-003: Sync Engine Architecture

**Status**: Accepted
**Date**: 2026-02-15
**Deciders**: Architect (solo developer)
**Technical Story**: Phase R3 - Offline-First Sync Engine

---

## Context

WeredaConnect targets Ethiopian rural farmers operating on:
- **Intermittent 3G connectivity** (2G in remote woredas)
- **Expensive mobile data** ($0.50 per MB, ~17% of daily income for 10MB)
- **Low-end Android devices** ($50 phones, 2GB RAM, Android 8+)
- **Shared devices** (family or community-owned phones)
- **Low digital literacy** (many users illiterate in Amharic, let alone English)

The core product promise is: **"Your work never gets lost, even offline."**

To deliver on this promise, we need an offline-first architecture where:
1. User actions succeed immediately (saved locally)
2. Data syncs to server when connectivity returns
3. Conflicts are resolved without confusing users
4. No data is lost due to poor connectivity

---

## Decision

We will implement a **local queue + executor pattern** with the following components:

### Architecture Overview

```
User Action → Repository → Isar (local DB) → SyncQueue → SyncExecutor → Server
                                                  ↓            ↑
                                           ConnectivityMonitor
```

### Core Components

#### 1. **SyncQueue** (Local Persistence)
- **Storage**: Isar database (NoSQL, embedded, ACID)
- **Entity**: `SyncOperation` with fields:
  - `operationId` (UUID v4, unique per operation)
  - `idempotencyKey` (composite: `{userId}:{entityType}:{entityId}:{action}:{timestamp}`)
  - `entityType` (enum: crop_listing, trip, delivery, payment, user)
  - `entityId` (UUID of the entity being synced)
  - `action` (create, update, delete)
  - `data` (JSON payload)
  - `status` (pending, syncing, synced, failed)
  - `retryCount` (0-3)
  - `createdAt`, `updatedAt`
- **Operations**:
  - `enqueue()` — Add operation to queue when user creates/updates/deletes
  - `getPendingOperations()` — Fetch operations with status `pending` or `failed` (retryCount < 3)
  - `markAsSyncing()` — Update status to `syncing` before sending to server
  - `markAsSynced()` — Update status to `synced` after successful server response
  - `markAsFailed()` — Update status to `failed`, increment `retryCount`

#### 2. **ConnectivityMonitor**
- **Library**: `connectivity_plus` (cross-platform, maintained by Flutter team)
- **Features**:
  - Stream-based connectivity changes (mobile, WiFi, none)
  - Debounced events (2 seconds) to avoid rapid on/off flapping
  - Internet access verification (ping `/health` endpoint, not just OS connectivity)
- **Triggers**:
  - Notify `SyncExecutor` when connectivity restored
  - Notify UI to show offline banner

#### 3. **SyncExecutor** (Batch Processing)
- **Responsibility**: Process pending sync operations and send to server
- **Triggers**:
  - Connectivity restored (via `ConnectivityMonitor`)
  - App foregrounded (user returns to app)
  - Background timer (every 15 minutes, WiFi-only)
  - Manual "Sync Now" button (for debugging)
- **Batch Logic**:
  - Fetch up to 50 pending operations from `SyncQueue`
  - Mark all as `syncing` (prevent duplicate processing)
  - Send batch to POST `/sync` endpoint
  - Process responses:
    - `success` → `markAsSynced()`
    - `conflict` → Call `ConflictResolver`, then `markAsSynced()`
    - `error` → `markAsFailed()`, increment retry count
- **Retry Logic**:
  - Exponential backoff: 5s, 15s, 45s
  - Max 3 retries
  - After 3 failures, mark as `failed` permanently (requires manual review)
- **Concurrency**:
  - Use mutex/lock to prevent multiple sync executors running simultaneously
  - If sync already in progress, enqueue request and return

#### 4. **ConflictResolver** (Server-Wins Strategy)
- **Responsibility**: Resolve conflicts when server returns 409
- **Strategy**: Accept server version, update local Isar database
- **Process**:
  1. Parse `serverData` and `serverVersion` from 409 response
  2. Find local entity in Isar by `entityId`
  3. Replace all fields with server data
  4. Update `version` field to `serverVersion`
  5. Save to Isar
  6. Mark sync operation as `synced` (conflict resolved successfully)
- **Rationale**:
  - Solo farmers rarely have conflicts (one user per device)
  - Automatic resolution avoids confusing users with merge dialogs
  - Future phases can add user-controlled merge strategies

#### 5. **Server Sync Endpoint** (POST /sync)
- **Responsibility**: Receive batch sync requests, apply operations, detect conflicts
- **Idempotency**:
  - Check `idempotencyKey` in `SyncMetadata` DynamoDB table
  - If found, return cached result (no re-processing)
  - If not found, process operation and store result with 7-day TTL
- **Version Checking**:
  - Compare `clientVersion` vs. server's current version
  - If `clientVersion < serverVersion`: return 409 Conflict with server data
  - If `clientVersion == serverVersion`: apply operation, increment version, return 200 Success
- **Entity Routing**:
  - Route to entity-specific repository based on `entityType`
  - Each repository implements `Sync()` method with version checking
- **Soft Deletes**:
  - Don't physically delete entities
  - Set `deletedAt` timestamp
  - Filter out deleted entities in query results

---

## Alternatives Considered

### Alternative 1: CouchDB Replication Protocol
**Description**: Use CouchDB's built-in master-master replication with MVCC (Multi-Version Concurrency Control).

**Pros**:
- Mature, battle-tested replication protocol
- Automatic conflict detection and resolution
- Offline-first by design

**Cons**:
- Requires CouchDB server (unfamiliar stack, would need to self-host)
- Complex setup and operational overhead for solo developer
- Overkill for simple CRUD operations (farmers creating listings, not collaborative documents)
- MVCC conflict trees confuse users ("Which version do I keep?")

**Rejection Reason**: Operational complexity outweighs benefits for solo developer. Custom sync is simpler to debug and maintain.

### Alternative 2: Firebase Offline Persistence
**Description**: Use Firebase Realtime Database with offline persistence.

**Pros**:
- Managed service (no server infrastructure)
- Offline persistence built-in
- Real-time updates across devices

**Cons**:
- Vendor lock-in (cannot self-host if Firebase pricing changes)
- Limited control over conflict resolution logic
- Not suitable for Ethiopian deployment (Firebase does not have Middle East/Africa data residency)
- Expensive for read-heavy workloads (farmers browsing listings)

**Rejection Reason**: Vendor lock-in, lack of data residency options, and cost concerns.

### Alternative 3: Custom CRDTs (Conflict-free Replicated Data Types)
**Description**: Implement CRDTs (e.g., LWW-Element-Set) for automatic conflict-free merges.

**Pros**:
- Mathematical guarantee of eventual consistency
- No central authority needed for conflict resolution
- Elegant for distributed systems

**Cons**:
- High complexity for simple CRUD operations
- Requires understanding of CRDT mathematics (not accessible for future maintainers)
- Overkill for solo farmers (conflicts are rare)
- Difficult to implement soft deletes and tombstones correctly

**Rejection Reason**: Premature optimization. Simple version-based conflict detection is sufficient for Phase R3. CRDTs can be added later if multi-user collaboration becomes common.

### Alternative 4: Event Sourcing + CQRS
**Description**: Store all user actions as immutable events, derive current state from event log.

**Pros**:
- Full audit trail of all changes
- Easy to implement undo/redo
- Natural fit for offline scenarios (events are queued, then replayed)

**Cons**:
- Significant architectural complexity
- Requires event store infrastructure (e.g., EventStoreDB, Kafka)
- Eventual consistency can confuse users ("Why doesn't my listing show up immediately?")
- High cognitive load for solo developer to maintain

**Rejection Reason**: Over-engineered for Phase R3. Version-based sync is simpler and meets user needs.

---

## Decision Rationale

We chose the **local queue + executor pattern** because:

1. **Solo Developer Sustainability**:
   - Simple to understand and debug
   - No unfamiliar infrastructure (DynamoDB, Lambda, Isar are already in stack)
   - Clear separation of concerns (SyncQueue, SyncExecutor, ConflictResolver)

2. **Offline-First User Experience**:
   - User actions succeed immediately (save to Isar)
   - Sync happens in background, transparent to user
   - Conflicts resolve automatically (server wins) without user intervention

3. **Ethiopian Context**:
   - Minimizes data transfer (batch operations, gzip compression)
   - Handles intermittent connectivity (queue persists across app restarts)
   - Works on low-end devices (Isar is memory-efficient, no background services)

4. **Production Readiness**:
   - Idempotency prevents duplicate operations on retry
   - Exponential backoff prevents overwhelming server during outages
   - Version checking detects conflicts reliably
   - 7-day TTL prevents unbounded DynamoDB growth

5. **Future Flexibility**:
   - Can add user-controlled merge strategies later (e.g., "Keep my description, accept server price")
   - Can implement delta sync (only send changed fields)
   - Can add P2P sync between devices (e.g., Bluetooth) if internet unavailable

---

## Consequences

### Positive

- ✅ **Zero data loss**: Operations persist in Isar queue until successfully synced
- ✅ **Immediate user feedback**: UI updates instantly on local save
- ✅ **Low cognitive load**: Simple to understand and maintain
- ✅ **Cost-effective**: Minimal data transfer (batch operations, retry backoff)
- ✅ **Testable**: Each component (SyncQueue, SyncExecutor, ConflictResolver) can be unit tested

### Negative

- ⚠️ **Eventual consistency**: Users may see stale data until sync completes
  - **Mitigation**: Show "Syncing..." indicator, display last sync timestamp
- ⚠️ **Server-wins may discard local edits**: If user edits offline while someone else edits online, local edits are lost
  - **Mitigation**: For MVP, this is acceptable (solo farmers rarely conflict). Future phases can add merge strategies.
- ⚠️ **Queue can grow unbounded**: If offline for weeks, thousands of operations may queue
  - **Mitigation**: Limit queue to 1000 operations, purge synced operations >30 days old
- ⚠️ **No peer-to-peer sync**: Cannot sync between devices without server
  - **Mitigation**: Accept limitation for MVP. P2P sync is a future optimization.

### Neutral

- 📊 **Performance**: Sync latency depends on batch size and network speed
  - Target: <5s for 50 operations on 3G
  - Measured in integration tests
- 📊 **Scalability**: SyncMetadata table partitions naturally by `idempotencyKey` (includes userId)
  - No hot partition issues expected
  - DynamoDB auto-scaling handles load

---

## Implementation Notes

### Key Design Decisions

1. **Composite Idempotency Keys**: Format `{userId}:{entityType}:{entityId}:{action}:{timestamp}` ensures uniqueness across users and actions, preventing collisions.

2. **Server-Wins Strategy**: Simplest approach for MVP. Users trust the server as "source of truth" (consistent with mental model of "syncing to cloud").

3. **Exponential Backoff**: Retry delays (5s, 15s, 45s) balance responsiveness vs. server load. Max 3 retries prevents infinite loops.

4. **7-Day TTL on Idempotency**: Balances idempotency guarantees vs. DynamoDB storage costs. 7 days is sufficient for retries (users won't wait weeks to sync).

5. **Batch Processing (50 ops)**: Small enough to complete on 3G in <5s, large enough to amortize HTTP overhead. Configurable via environment variable.

### Testing Strategy

- **Unit Tests**: SyncQueue, SyncExecutor, ConflictResolver tested in isolation with mocks
- **Integration Tests**: End-to-end tests with real Isar database and HTTP calls to deployed backend
- **Performance Tests**: Measure sync latency for 1, 10, 50 operations on simulated 3G
- **Stress Tests**: 100 offline-online cycles to validate zero data loss

### Monitoring and Observability

- **CloudWatch Metrics**:
  - Sync operation count (per entityType, per action)
  - Conflict rate (conflicts / total operations)
  - Idempotency hit rate (duplicate requests / total requests)
  - Sync latency (p50, p95, p99)
- **Mobile Logging**:
  - Log all sync operations (operation ID, status, retry count)
  - Log conflicts (local version, server version, resolution strategy)
  - Log failures (error code, retry count)

---

## Related Documents

- **Sync Protocol Contract**: `specs/contracts/sync-protocol.md` (version 1.1.0)
- **Crop Listing API Contract**: `specs/contracts/crop-listing-api.md`
- **Authentication Plan**: `specs/decisions/authentication-encryption-plan.md` (ADR-002)
- **Roadmap**: `specs/features/ROADMAP.md` (Phase R3)

---

## Revision History

| Version | Date | Change |
|---------|------|--------|
| 1.0.0 | 2026-02-15 | Initial ADR: Sync engine architecture, alternatives considered, decision rationale |

---

**Status**: Accepted
**Last Updated**: 2026-02-15
**Author**: Claude Sonnet 4.5 (Architect)
