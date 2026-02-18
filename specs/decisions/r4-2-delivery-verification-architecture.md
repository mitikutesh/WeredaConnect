# R4.2 Delivery Verification System Architecture

## Version
1.0.0 | 2026-02-17

## Status
Approved (Architect)

---

## 1. Component Overview

```
┌─────────────────────────────────────────────────────────────┐
│                       MOBILE (Flutter)                       │
│                                                              │
│  ┌──────────────┐  ┌────────────────┐  ┌────────────────┐  │
│  │  Delivery     │  │  Delivery      │  │   Isar DB      │  │
│  │  Verification │──│  Repository    │──│  (Delivery     │  │
│  │  BLoC         │  │  (offline-     │  │   schema)      │  │
│  │               │  │   first)       │  │                │  │
│  └──────────────┘  └───────┬────────┘  └────────────────┘  │
│                            │                                 │
│  ┌──────────────┐  ┌──────┴────────┐  ┌────────────────┐   │
│  │ QR Generator  │  │  SyncQueue    │  │ Photo Manager  │   │
│  │ (HMAC-SHA256) │  │  (delivery    │  │ (WebP convert, │   │
│  │ (offline)     │  │   ops)        │  │  local store)  │   │
│  └──────────────┘  └──────┬────────┘  └───────┬────────┘   │
│                            │                    │            │
│  ┌──────────────┐          │                    │            │
│  │ QR Scanner    │          │                    │            │
│  │ (HMAC verify) │          │                    │            │
│  │ (offline)     │          │                    │            │
│  └──────────────┘          │                    │            │
│                            │                    │            │
│  ┌──────────────┐          │                    │            │
│  │ GPS Capture   │          │                    │            │
│  │ (Geolocator)  │          │                    │            │
│  └──────────────┘          │                    │            │
└────────────────────────────┼────────────────────┼────────────┘
                             │                    │
              POST /sync     │    POST /deliveries/{id}/photos
              GET /deliveries│
                             ▼
┌─────────────────────────────────────────────────────────────┐
│                  API GATEWAY (HTTP API v2)                    │
│  Routes:                                                     │
│    POST /sync (delivery entityType) → SyncFunction           │
│    GET /deliveries → DeliveryFunction                        │
│    GET /deliveries/{id} → DeliveryFunction                   │
│    POST /deliveries/{id}/photos → DeliveryFunction           │
└──────────────────────────────┬───────────────────────────────┘
                               │
                               ▼
┌─────────────────────────────────────────────────────────────┐
│                   SERVER (.NET 10 AOT)                        │
│                                                              │
│  ┌──────────────────┐  ┌───────────────────────────┐        │
│  │ SyncFunction      │  │ DeliveryFunction           │        │
│  │ (POST /sync)      │  │ (GET /deliveries,          │        │
│  │ delivery entity   │  │  GET /deliveries/{id},     │        │
│  │ handler           │  │  POST .../photos)          │        │
│  └────────┬─────────┘  └─────────────┬─────────────┘        │
│           │                           │                      │
│           ▼                           ▼                      │
│  ┌─────────────────────────────────────────────────┐        │
│  │          IDeliveryRepository                     │        │
│  │   + GpsValidator (Haversine)                     │        │
│  │   + StatusTransitionValidator                    │        │
│  └──────────────────────┬──────────────────────────┘        │
│                          │                                   │
└──────────────────────────┼───────────────────────────────────┘
                           ▼
┌─────────────────────────────────────────────────────────────┐
│                    AWS Resources                             │
│                                                              │
│  ┌─────────────────────────────┐  ┌──────────────────────┐  │
│  │ DeliveriesTable              │  │ S3 Uploads Bucket    │  │
│  │ PK: deliveryId | SK: tripId  │  │ (already exists)     │  │
│  │                              │  │                      │  │
│  │ GSI: StatusCreatedAtIndex    │  │ uploads/photos/      │  │
│  │ GSI: TripIdIndex             │  │   {deliveryId}/      │  │
│  │ GSI: ParticipantIndex        │  │   {type}-{ts}.webp   │  │
│  └─────────────────────────────┘  └──────────────────────┘  │
└─────────────────────────────────────────────────────────────┘
```

---

## 2. QR Code Verification (Offline)

### 2.1 Architecture Decision: HMAC-SHA256 over JWT

| Factor | HMAC-SHA256 | JWT |
|--------|------------|-----|
| Payload size | ~200 bytes | ~500+ bytes |
| QR readability | Better (smaller data) | Worse on cheap cameras |
| Offline verify | Yes | Yes |
| Complexity | Simple | Overkill for this use case |
| Dependencies | dart:crypto (built-in) | jwt_decoder package |

**Decision**: HMAC-SHA256 (as specified in contract).

### 2.2 QR Token Flow

```
                       FARMER'S DEVICE                    TRUCKER'S DEVICE
                       ──────────────                     ─────────────────

1. Delivery created    ┌─────────────────┐
   (offline)           │ QR Generator     │
                       │                  │
                       │ secret = SHA256( │
                       │   deliveryId +   │
                       │   ":" + tripId + │
                       │   ":" + listingId│
                       │   + ":" + ts)    │
                       │                  │
                       │ message = did +  │
                       │   "|" + tid +    │
                       │   "|" + lid +    │
                       │   "|" + fid +    │
                       │   "|" + ts       │
                       │                  │
                       │ hmac = HMAC-256( │
                       │   secret, msg)   │
                       │                  │
                       │ payload = {      │
                       │   did, tid, lid, │
                       │   fid, ts, hmac  │
                       │ }                │
                       │                  │
                       │ qrToken =        │
                       │   base64url(     │
                       │     JSON(payload)│
                       │   )              │
                       └───────┬─────────┘
                               │
                               ▼
                       ┌───────────────┐
                       │ QR Code shown │     2. Trucker scans
                       │ on screen     │────────────────────────►┌─────────────┐
                       └───────────────┘                         │ QR Scanner   │
                                                                 │              │
                                                                 │ 1. Decode    │
                                                                 │    base64url │
                                                                 │ 2. Parse JSON│
                                                                 │ 3. Look up   │
                                                                 │    delivery  │
                                                                 │    in Isar   │
                                                                 │ 4. Recompute │
                                                                 │    HMAC      │
                                                                 │ 5. Compare   │
                                                                 │    ✓ or ✗    │
                                                                 └──────┬──────┘
                                                                        │
                                                                 3. If valid:
                                                                    Update local
                                                                    delivery →
                                                                    picked_up
                                                                    Enqueue sync
```

### 2.3 QR Service (Mobile)

```dart
/// Offline QR verification service.
/// No network required for generation or scanning.
class QrVerificationService {
  /// Generate HMAC secret from delivery metadata.
  /// Deterministic: same inputs always produce same secret.
  List<int> _deriveSecret(String deliveryId, String tripId,
                           String listingId, int creationTimestamp) {
    final input = '$deliveryId:$tripId:$listingId:$creationTimestamp';
    return sha256.convert(utf8.encode(input)).bytes;
  }

  /// Generate QR token for display.
  String generateQrToken(Delivery delivery) {
    final secret = _deriveSecret(
      delivery.deliveryId, delivery.tripId,
      delivery.listingId, delivery.createdAt.millisecondsSinceEpoch);

    final ts = DateTime.now().toUtc().millisecondsSinceEpoch;
    final message = '${delivery.deliveryId}|${delivery.tripId}|'
        '${delivery.listingId}|${delivery.farmerId}|$ts';
    final hmac = Hmac(sha256, secret).convert(utf8.encode(message));

    final payload = {
      'did': delivery.deliveryId,
      'tid': delivery.tripId,
      'lid': delivery.listingId,
      'fid': delivery.farmerId,
      'ts': ts,
      'hmac': hmac.toString(),
    };

    return base64Url.encode(utf8.encode(jsonEncode(payload)));
  }

  /// Verify a scanned QR token against local delivery data.
  /// Returns true if HMAC matches.
  bool verifyQrToken(String qrToken, Delivery delivery) {
    final decoded = utf8.decode(base64Url.decode(qrToken));
    final payload = jsonDecode(decoded) as Map<String, dynamic>;

    final secret = _deriveSecret(
      delivery.deliveryId, delivery.tripId,
      delivery.listingId, delivery.createdAt.millisecondsSinceEpoch);

    final message = '${payload['did']}|${payload['tid']}|'
        '${payload['lid']}|${payload['fid']}|${payload['ts']}';
    final computed = Hmac(sha256, secret).convert(utf8.encode(message));

    return computed.toString() == payload['hmac'];
  }
}
```

---

## 3. GPS Validation

### 3.1 Haversine Distance (Shared Logic)

Implemented both client-side (immediate feedback) and server-side (authoritative):

```
Server (.NET):
  static double HaversineDistance(double lat1, double lon1, double lat2, double lon2) {
      const R = 6371.0; // Earth radius in km
      var dLat = ToRadians(lat2 - lat1);
      var dLon = ToRadians(lon2 - lon1);
      var a = Math.Sin(dLat/2) * Math.Sin(dLat/2) +
              Math.Cos(ToRadians(lat1)) * Math.Cos(ToRadians(lat2)) *
              Math.Sin(dLon/2) * Math.Sin(dLon/2);
      var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1-a));
      return R * c;
  }
```

### 3.2 Validation Thresholds

| Check | Threshold | Rationale |
|-------|-----------|-----------|
| Pickup GPS vs. crop listing location | 5 km | Accounts for GPS inaccuracy + farm-to-road distance |
| Dropoff GPS vs. destination woreda | 5 km | Accounts for market area size |
| Ethiopian bounds sanity check | 3-15°N, 33-48°E | Rejects obviously wrong coordinates |

### 3.3 Graceful GPS Degradation

```
GPS Available → Capture + validate + store
GPS Unavailable → Allow operation, flag requires_review in server metadata
GPS Outside Threshold → Reject, allow retry with explanation
```

**Mobile package**: `geolocator` (already approved, no reflection issues).

---

## 4. Photo Evidence

### 4.1 Photo Pipeline

```
Camera (JPEG) → Resize (800px max width) → WebP (75% quality)
  → Store local: {appDir}/photos/{deliveryId}/{type}-{ts}.webp
  → Isar: update delivery with local path
  → Upload queue (separate from sync queue, lower priority)
  → POST /deliveries/{id}/photos (multipart)
  → S3: uploads/photos/{deliveryId}/{type}-{ts}.webp
  → Update Isar with server photoRef
  → Delete local file
```

### 4.2 Constraints

| Constraint | Value |
|------------|-------|
| Format | WebP only |
| Max width | 800px |
| Max file size | 200KB |
| Max photos per type | 3 (pickup: 3, dropoff: 3) |
| Compression quality | 75% |

### 4.3 Photo Upload Function

The `DeliveryFunction` handles photo uploads separately from sync:
- `POST /deliveries/{id}/photos` (multipart/form-data)
- Validates: participant authorization, file format, size, type
- Stores in S3 (existing uploads bucket)
- Returns photoRef (S3 key)

---

## 5. Server Implementation

### 5.1 New Files

| File | Description |
|------|-------------|
| `server/src/Functions/DeliveryFunction.cs` | GET /deliveries, GET /deliveries/{id}, POST /deliveries/{id}/photos |
| `server/src/Data/IDeliveryRepository.cs` | Repository interface |
| `server/src/Data/DeliveryRepository.cs` | DynamoDB implementation with GSI queries |
| `server/src/Models/DeliveryDtos.cs` | DeliveryDto, GetDeliveriesResponse, etc. |
| `server/src/Services/GpsValidator.cs` | Haversine distance + Ethiopian bounds check |

### 5.2 Modify Existing Files

| File | Change |
|------|--------|
| `server/src/Functions/SyncFunction.cs` | Add `ProcessDeliveryAsync()` handler for entityType="delivery" |
| `server/src/AppJsonContext.cs` | Register DeliveryDto, GetDeliveriesResponse, etc. |

### 5.3 SyncFunction — Delivery Entity Handler

```csharp
// Add to SyncFunction.ProcessOperationAsync switch:
case "delivery" => await ProcessDeliveryAsync(operation, authenticatedUserId, context),

// ProcessDeliveryAsync follows same pattern as ProcessTripAsync:
// 1. Validate participant authorization (trucker for create/pickup, buyer for delivery confirm)
// 2. Field whitelist
// 3. Validate status transitions
// 4. GPS proximity check (if GPS coordinates provided)
// 5. DynamoDB PutItem/UpdateItem with version check
```

### 5.4 Delivery Status Transitions (Server-Side)

```csharp
private static readonly Dictionary<string, HashSet<string>> DeliveryTransitions = new()
{
    { "pending", new HashSet<string> { "picked_up", "cancelled" } },
    { "picked_up", new HashSet<string> { "in_transit", "cancelled" } },
    { "in_transit", new HashSet<string> { "delivered" } },
    { "delivered", new HashSet<string> { "disputed" } },
};
```

### 5.5 DeliveryFunction Pattern

```csharp
public class DeliveryFunction
{
    private readonly IDeliveryRepository _deliveryRepo;

    public async Task<APIGatewayProxyResponse> FunctionHandler(
        APIGatewayProxyRequest request, ILambdaContext context)
    {
        return request.HttpMethod switch
        {
            "GET" when request.PathParameters?.ContainsKey("id") == true
                => await HandleGetDeliveryByIdAsync(request, context),
            "GET" => await HandleGetDeliveriesAsync(request, context),
            "POST" when request.Path.EndsWith("/photos")
                => await HandlePhotoUploadAsync(request, context),
            _ => LambdaResponse.Error(405, "DELIVERY_VALIDATION", "Method not allowed.")
        };
    }
}
```

### 5.6 DeliveryDtos (AOT-Safe)

```csharp
public sealed record DeliveryDto
{
    public required string DeliveryId { get; init; }
    public required string TripId { get; init; }
    public required string ListingId { get; init; }
    public required string FarmerId { get; init; }
    public required string FarmerName { get; init; }
    public required string TruckerId { get; init; }
    public required string TruckerName { get; init; }
    public required string BuyerId { get; init; }
    public required string BuyerName { get; init; }
    public required string QrToken { get; init; }
    public required string Status { get; init; }
    public LatLngDto? GpsPickup { get; init; }
    public LatLngDto? GpsDropoff { get; init; }
    public string? PickupPhotoRef { get; init; }
    public string? DropoffPhotoRef { get; init; }
    public string? PickedUpAt { get; init; }
    public string? ConfirmedAt { get; init; }
    public string? DisputeReason { get; init; }
    public required string CreatedAt { get; init; }
    public required string UpdatedAt { get; init; }
    public string? DeletedAt { get; init; }
    public required int Version { get; init; }
}

public sealed record LatLngDto
{
    public required double Latitude { get; init; }
    public required double Longitude { get; init; }
}
```

### 5.7 AOT Compliance

Register in `AppJsonContext.cs`:
```csharp
[JsonSerializable(typeof(DeliveryDto))]
[JsonSerializable(typeof(List<DeliveryDto>))]
[JsonSerializable(typeof(GetDeliveriesResponse))]
[JsonSerializable(typeof(GetDeliveryResponse))]
[JsonSerializable(typeof(LatLngDto))]
[JsonSerializable(typeof(PhotoUploadResponse))]
```

---

## 6. DynamoDB Design

### 6.1 DeliveriesTable — Already Provisioned

From `database-stack.ts`:
```
PK: deliveryId | SK: tripId
GSI: ListingIdIndex (PK: listingId, SK: status)
GSI: StatusIndex (PK: status, SK: createdAt)
```

### 6.2 Additional GSI Needed

The contract specifies a `ParticipantIndex` GSI that isn't in the current database stack. This GSI enables "My Deliveries" queries:

```
GSI: ParticipantIndex
  PK: participantId (userId)
  SK: createdAt
  Use case: Query all deliveries where user is farmer, trucker, or buyer
  Note: Each delivery creates 3 items in this GSI (one per participant)
```

**Decision**: Defer ParticipantIndex to a later iteration. For MVP, we can query by status and filter by participant. The `role` query parameter in GET /deliveries can use the existing StatusIndex + filter expression. This avoids the complexity of triple-writing to a GSI for each delivery.

**Alternative for MVP**: Query StatusIndex, FilterExpression on `farmerId = :uid OR truckerId = :uid OR buyerId = :uid`. Acceptable because the `status` partition is bounded (small number of active deliveries per status).

---

## 7. Mobile Implementation

### 7.1 New Files

| File | Description |
|------|-------------|
| `mobile/lib/features/delivery_verification/data/repositories/delivery_repository.dart` | Offline-first CRUD |
| `mobile/lib/features/delivery_verification/data/services/qr_verification_service.dart` | HMAC generate/verify |
| `mobile/lib/features/delivery_verification/data/services/photo_manager.dart` | Capture, compress, upload |
| `mobile/lib/features/delivery_verification/data/services/gps_capture_service.dart` | GPS with graceful degradation |
| `mobile/lib/features/delivery_verification/presentation/bloc/delivery_bloc.dart` | Events/States |
| `mobile/lib/features/delivery_verification/presentation/screens/delivery_list_screen.dart` | My deliveries |
| `mobile/lib/features/delivery_verification/presentation/screens/delivery_detail_screen.dart` | Single delivery |
| `mobile/lib/features/delivery_verification/presentation/screens/qr_generate_screen.dart` | Show QR for farmer |
| `mobile/lib/features/delivery_verification/presentation/screens/qr_scan_screen.dart` | Scan QR for trucker/buyer |
| `mobile/lib/features/delivery_verification/presentation/widgets/delivery_status_indicator.dart` | Icon-based status |

### 7.2 Flutter Packages Required

| Package | Purpose | AOT/Size Safe |
|---------|---------|---------------|
| `qr_flutter` | QR code generation (display) | Yes |
| `mobile_scanner` | QR code scanning (camera) | Yes |
| `geolocator` | GPS coordinates | Yes |
| `image` | Image resize (WebP conversion) | Yes |
| `crypto` | HMAC-SHA256 | Yes (dart:core) |

### 7.3 Delivery Isar Schema — Already Scaffolded

The schema at `mobile/lib/core/database/schemas/delivery.dart` already has the required fields. Needs additions:

```dart
// Fields to add:
late String farmerId;
late String farmerName;
late String truckerName;
late String buyerId;
late String buyerName;
late String? pickupPhotoRef;
late String? dropoffPhotoRef;
late DateTime? pickedUpAt;
late String? disputeReason;
```

### 7.4 SyncExecutor Integration

```dart
// In sync_executor.dart, add delivery handling:
case 'delivery':
  if (result.status == 'success') {
    await _updateLocalDelivery(operation.entityId, result.serverVersion);
  } else if (result.status == 'conflict') {
    await _replaceLocalDelivery(operation.entityId, result.serverData);
  }
```

---

## 8. Implementation Sequence

```
Phase 1: Server — Delivery Sync Handler
  1. Create DeliveryDtos (sealed records, AOT-safe)
  2. Register in AppJsonContext
  3. Add ProcessDeliveryAsync to SyncFunction
  4. Implement status transition validation
  5. Implement GPS proximity validation (Haversine)
  6. Implement participant authorization checks

Phase 2: Server — DeliveryFunction (Read API)
  1. Create IDeliveryRepository + DeliveryRepository
  2. Create DeliveryFunction (GET /deliveries, GET /deliveries/{id})
  3. Add photo upload handler (POST /deliveries/{id}/photos)
  4. Add routes to API Gateway (infra)

Phase 3: Mobile — QR Verification Service
  1. Implement QrVerificationService (generate + verify)
  2. Unit test HMAC generation/verification offline
  3. Implement QR display screen (qr_flutter)
  4. Implement QR scan screen (mobile_scanner)

Phase 4: Mobile — Delivery Feature Module
  1. Update Delivery Isar schema (add missing fields)
  2. Run `flutter pub run build_runner build`
  3. Implement DeliveryRepository (offline-first)
  4. Implement GPS capture service (geolocator)
  5. Implement Photo manager (capture, compress, upload queue)
  6. Implement DeliveryBloc
  7. Implement delivery screens
  8. Add delivery sync handling to SyncExecutor
  9. Add l10n strings (3 languages)

Phase 5: Integration Testing
  1. QR: generate on device A → scan on device B → verify offline
  2. GPS: pickup within 5km → accept; outside 5km → reject
  3. Photo: capture offline → upload on connectivity → S3 storage
  4. Status transitions: pending → picked_up → delivered (happy path)
  5. Dispute: delivered → disputed (within 48h window)
```

---

## 9. Security Considerations

| Concern | Mitigation |
|---------|-----------|
| QR token forgery | HMAC-SHA256 with delivery-specific secret |
| Replay attacks across deliveries | deliveryId is part of HMAC payload |
| GPS spoofing on cheap phones | GPS is evidence, not sole verification (QR is primary) |
| Photo tampering | Photo uploaded with delivery context, audit trail |
| Unauthorized status changes | JWT sub checked against participant role for each action |
| Phone number privacy | Only visible to delivery participants |

---

## 10. Offline-First Validation

| Operation | Offline | Sync Trigger |
|-----------|---------|-------------|
| Create delivery | Full (Isar + QR) | POST /sync |
| Generate QR | Full (HMAC local) | None |
| Scan QR | Full (HMAC verify local) | None |
| Capture GPS | Full (stored in Isar) | POST /sync |
| Capture photo | Full (WebP local) | POST /deliveries/{id}/photos |
| Update status | Full (Isar update) | POST /sync |
| Browse deliveries | Full (Isar cache) | Refresh on sync |

---

**Last Updated**: 2026-02-17
**Author**: Architect Agent
