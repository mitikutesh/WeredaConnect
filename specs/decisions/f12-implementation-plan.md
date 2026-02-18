# F-12 Delivery Verification — Implementation Plan

## Version
1.1.0 | 2026-02-18

## Status
Approved — Security Review Incorporated (see §9 for findings tracker)

---

## 1. Executive Summary

This plan covers the **missing pieces** for F-12 Delivery Verification: 3 core services, 5 screens, shared widgets, BLoC/repository updates, localization (~70 strings), and navigation integration. Server, Isar schema, and base BLoC/repository already exist.

**Scope**: Mobile (core services + presentation) + server security fixes (dispute window, phone validation, GPS proximity, dispute reason length, photo upload validation).

**Security Review**: `qa/security-reviews/f12-implementation-review.md` — 2 MUST FIX (resolved), 7 SHOULD FIX (incorporated), 5 NICE TO HAVE (deferred). See §9 for full findings tracker.

**Estimated files**: 14 new files, 7 modified files (mobile), 2 modified files (server), 3 localization files modified.

---

## 2. Existing Code Inventory

### Already Complete (DO NOT MODIFY unless noted)

| Layer | File | What It Does |
|-------|------|-------------|
| Isar Schema | `mobile/lib/core/database/schemas/delivery.dart` | Full schema: deliveryId, tripId, listingId, farmerId, farmerName, truckerId, truckerName, buyerId, buyerName, qrToken, gpsPickup, gpsDropoff, pickupPhotoRef, dropoffPhotoRef, status, pickedUpAt, confirmedAt, disputeReason, createdAt, updatedAt, version. Indexes on all ID fields + status. |
| BLoC Events | `.../presentation/bloc/delivery_event.dart` | LoadDeliveries(tripId), CreateDelivery(...), ConfirmPickup(deliveryId, truckerId, gpsPickup?, photoRef?), ConfirmDelivery(deliveryId, buyerId, gpsDropoff?, photoRef?), RaiseDispute(deliveryId, userId, reason, tripId), CancelDelivery(deliveryId, userId, tripId), FilterDeliveries(tripId, statusFilter?) |
| BLoC States | `.../presentation/bloc/delivery_state.dart` | DeliveryInitial, DeliveryLoading, DeliveriesLoaded(deliveries, statusFilter?), DeliveryCreated(delivery), PickupConfirmed, DeliveryConfirmed, DeliveryError(message) |
| BLoC Logic | `.../presentation/bloc/delivery_bloc.dart` | Handlers for all events above. Uses DeliveryRepository. |
| Repository | `.../data/repositories/delivery_repository.dart` | createDelivery, confirmPickup, confirmDelivery, raiseDispute, cancelDelivery, getDeliveriesByTrip, getDeliveriesByStatus, getDeliveryById. Offline-first with SyncQueue integration. |
| Server | `server/src/Functions/DeliveryFunction.cs` | GET /deliveries, GET /deliveries/{id} with participant auth |
| Server | `server/src/Functions/SyncFunction.cs` | Delivery create/update sync handler |
| Server | `server/src/Data/DeliveryRepository.cs` | DynamoDB queries with StatusCreatedAtIndex, TripIdIndex |
| Server | `server/src/Models/DeliveryDtos.cs` | DeliveryDto, LatLngDto, GetDeliveriesResponse, GetDeliveryResponse |
| Infra | `infra/lib/stacks/database-stack.ts` | Deliveries DynamoDB table provisioned with GSIs |

### What Must Be Built

| Category | Count | Description |
|----------|-------|-------------|
| Core Services | 3 files | QrVerificationService, GpsCaptureService, PhotoManager |
| Screens | 5 files | DeliveryList, DeliveryDetail, QrGenerate, QrScan, Dispute |
| Widgets | 3 files | DeliveryCard, DeliveryStatusIndicator, PhotoEvidenceWidget |
| BLoC/Repo Updates | 3 files modified | New events/states for user-centric queries + detail loading |
| Localization | 3 files modified | ~70 strings in en/am/om .arb files |
| Navigation | 1 file modified | Wire screens into app routing |
| pubspec.yaml | 1 file modified | Add 6 packages |

---

## 3. Package Dependencies

### New Packages to Add

| Package | Version | Purpose | Size Impact | AOT/ARM64 Safe |
|---------|---------|---------|-------------|----------------|
| `crypto` | `^3.0.6` | HMAC-SHA256 for QR tokens | ~50KB (pure Dart) | Yes |
| `qr_flutter` | `^4.1.0` | QR code display widget | ~80KB (pure Dart) | Yes |
| `mobile_scanner` | `^6.0.2` | Camera QR code scanning | ~400KB (uses CameraX) | Yes |
| `geolocator` | `^13.0.2` | GPS coordinates capture | ~200KB (platform channels) | Yes |
| `image_picker` | `^1.1.2` | Camera/gallery photo capture | ~150KB (Android intents) | Yes |
| `flutter_image_compress` | `^2.4.0` | WebP compression (native) | ~300KB (uses BitmapFactory) | Yes |

**Total estimated APK size increase**: ~1.2MB (well within 50MB budget).

### Package Rationale: `flutter_image_compress` vs `image`

The architecture doc lists `image` for WebP conversion, but the `image` package **cannot encode WebP** — it only decodes WebP. `flutter_image_compress` uses Android's native BitmapFactory which:
- Encodes WebP natively (required by contract)
- Uses ~10x less memory than pure Dart image processing
- Critical for 2GB RAM target devices
- Provides quality parameter for 75% compression

### pubspec.yaml Changes

```yaml
dependencies:
  # ... existing deps ...

  # F-12: Delivery Verification
  crypto: ^3.0.6              # HMAC-SHA256 for QR verification
  qr_flutter: ^4.1.0          # QR code display
  mobile_scanner: ^6.0.2      # QR code camera scanning
  geolocator: ^13.0.2         # GPS coordinates
  image_picker: ^1.1.2        # Camera/gallery photo capture
  flutter_image_compress: ^2.4.0  # WebP compression (native)
```

---

## 4. Android Permissions

Add to `mobile/android/app/src/main/AndroidManifest.xml`:

```xml
<!-- Camera for QR scanning and photo capture -->
<uses-permission android:name="android.permission.CAMERA" />
<uses-feature android:name="android.hardware.camera" android:required="false" />

<!-- GPS for pickup/delivery location -->
<uses-permission android:name="android.permission.ACCESS_FINE_LOCATION" />
<uses-permission android:name="android.permission.ACCESS_COARSE_LOCATION" />
```

Note: `INTERNET` and `ACCESS_NETWORK_STATE` are already present. Storage permissions are not needed on Android 10+ (scoped storage). For Android 8-9, `image_picker` handles storage access via intents.

---

## 5. Phase 1: Core Services

### 5.1 QrVerificationService

**File**: `mobile/lib/features/delivery_verification/data/services/qr_verification_service.dart`

**Purpose**: Offline HMAC-SHA256 QR token generation and verification with nonce-based physical proximity enforcement. Zero network dependency.

> **Security Fix (F12-SEC-01)**: The original design used a deterministic HMAC secret derivable from synced delivery metadata, allowing any participant to forge QR tokens without physical proximity. The revised design adds a **cryptographic nonce** that only exists in the QR code, ensuring the scanner must physically see the generator's screen.

**Class Design**:

```dart
import 'dart:convert';
import 'dart:math';
import 'package:crypto/crypto.dart';

class QrVerificationService {
  /// Generate QR token string for display as QR code.
  /// Includes a cryptographic nonce for physical proximity enforcement.
  /// Returns (qrTokenString, nonce) — nonce must be stored locally by caller
  /// for server-side cross-validation during sync.
  (String token, String nonce) generateQrToken(Delivery delivery) { ... }

  /// Verify a scanned QR token against local delivery data.
  /// Returns QrVerificationResult with status, extracted fields, and nonce.
  /// The nonce is extracted from the QR and must be included in the
  /// confirm-pickup/delivery sync operation for server cross-validation.
  QrVerificationResult verifyQrToken(String qrToken, Delivery delivery) { ... }

  /// Internal: derive HMAC secret from delivery metadata.
  List<int> _deriveSecret(String deliveryId, String tripId,
                           String listingId, int creationTimestamp) { ... }

  /// Internal: generate 32-byte cryptographic random nonce.
  String _generateNonce() {
    final random = Random.secure();
    final bytes = List<int>.generate(32, (_) => random.nextInt(256));
    return base64Url.encode(bytes);
  }

  /// Internal: constant-time byte comparison to prevent timing attacks.
  /// (F12-SEC-03)
  bool _constantTimeEquals(List<int> a, List<int> b) {
    if (a.length != b.length) return false;
    var result = 0;
    for (var i = 0; i < a.length; i++) {
      result |= a[i] ^ b[i];
    }
    return result == 0;
  }
}

/// Result of QR verification.
class QrVerificationResult {
  final bool isValid;
  final String? errorMessage;
  final String? deliveryId;
  final String? tripId;
  final String? farmerId;
  final int? timestamp;
  final String? nonce;  // Extracted nonce for sync cross-validation

  const QrVerificationResult.valid({
    required this.deliveryId,
    required this.tripId,
    required this.farmerId,
    required this.timestamp,
    required this.nonce,
  }) : isValid = true, errorMessage = null;

  const QrVerificationResult.invalid(this.errorMessage)
      : isValid = false, deliveryId = null, tripId = null,
        farmerId = null, timestamp = null, nonce = null;
}
```

**Nonce-Based HMAC Flow** (revised per F12-SEC-01):

1. **Secret derivation** (unchanged): `SHA256(deliveryId + ":" + tripId + ":" + listingId + ":" + creationTimestamp)`
2. **Nonce generation**: `Random.secure()` → 32 bytes → base64url string (~44 chars)
3. **Message construction** (includes nonce): `deliveryId + "|" + tripId + "|" + listingId + "|" + farmerId + "|" + timestamp + "|" + nonce`
4. **HMAC**: `HMAC-SHA256(secret, message)`
5. **Payload**: JSON with abbreviated keys: `{did, tid, lid, fid, ts, n, hmac}` (added `n` for nonce)
6. **Encoding**: `base64Url.encode(utf8.encode(jsonEncode(payload)))`

**Key Invariant**: The nonce is NOT stored in the synced delivery record. It exists only:
- In the QR code displayed on the generator's screen
- In the generator's local storage (for server cross-validation during sync)
- In the scanner's confirm-pickup/delivery sync operation (obtained by physically scanning)

**Verification Flow** (revised per F12-SEC-03 and F12-SEC-07):

```dart
QrVerificationResult verifyQrToken(String qrToken, Delivery delivery) {
  // F12-SEC-07: Hard size limit BEFORE any processing
  if (qrToken.length > 700) {
    return QrVerificationResult.invalid('QR code too large');
  }

  // F12-SEC-07: Wrap all parsing in try-catch
  try {
    final decoded = utf8.decode(base64Url.decode(base64Url.normalize(qrToken)));
    final payload = jsonDecode(decoded) as Map<String, dynamic>;

    // F12-SEC-07: Validate field presence and string length bounds
    final did = payload['did']?.toString() ?? '';
    final tid = payload['tid']?.toString() ?? '';
    final lid = payload['lid']?.toString() ?? '';
    final fid = payload['fid']?.toString() ?? '';
    final nonce = payload['n']?.toString() ?? '';
    final receivedHmac = payload['hmac']?.toString() ?? '';

    if ([did, tid, lid, fid, nonce, receivedHmac].any((s) => s.isEmpty)) {
      return QrVerificationResult.invalid('Incomplete QR data');
    }
    if ([did, tid, lid, fid].any((s) => s.length > 100)) {
      return QrVerificationResult.invalid('Invalid QR data');
    }
    if (nonce.length > 100 || receivedHmac.length > 100) {
      return QrVerificationResult.invalid('Invalid QR data');
    }

    // Cross-check deliveryId matches expected delivery
    if (did != delivery.deliveryId) {
      return QrVerificationResult.invalid('QR code does not match this delivery');
    }

    // Recompute HMAC
    final ts = payload['ts'] as int? ?? 0;
    final secret = _deriveSecret(
      delivery.deliveryId, delivery.tripId,
      delivery.listingId, delivery.createdAt.millisecondsSinceEpoch);
    final message = '$did|$tid|$lid|$fid|$ts|$nonce';
    final computed = Hmac(sha256, secret).convert(utf8.encode(message));

    // F12-SEC-03: Constant-time comparison
    final computedBytes = utf8.encode(computed.toString());
    final receivedBytes = utf8.encode(receivedHmac);
    if (!_constantTimeEquals(computedBytes, receivedBytes)) {
      return QrVerificationResult.invalid('Verification failed — possible tampering');
    }

    return QrVerificationResult.valid(
      deliveryId: did, tripId: tid, farmerId: fid,
      timestamp: ts, nonce: nonce,
    );
  } on FormatException {
    return QrVerificationResult.invalid('Invalid QR code format');
  } on TypeError {
    return QrVerificationResult.invalid('Malformed QR data');
  } catch (_) {
    return QrVerificationResult.invalid('Could not process QR code');
  }
}
```

**Nonce Sync Flow** (server cross-validation):

1. **Generator** (farmer at pickup): Generates QR with nonce → stores nonce locally → syncs nonce with delivery create/update operation (field: `qrNonce`)
2. **Scanner** (trucker at pickup): Scans QR → extracts nonce → includes nonce in `ConfirmPickup` sync operation (field: `scannedNonce`)
3. **Server**: On pickup confirmation, cross-validates: `operation.scannedNonce == delivery.qrNonce`. If mismatch, reject with error.
4. **Offline**: Offline, the QR scan enforces physical proximity (scanner must see generator's screen). Server cross-validation happens at sync time as an additional guarantee.

> **Important**: Offline QR verification is a **UI ritual enforcing physical proximity** — the scanner must physically see the QR on the generator's screen to obtain the nonce. The server provides **cryptographic proof** at sync time by cross-checking nonces from both parties. This is documented as an explicit design tradeoff for offline-first architecture.

**Security Considerations**:
- F12-SEC-01: Nonce prevents token forgery without physical proximity
- F12-SEC-03: Constant-time `_constantTimeEquals()` prevents timing attacks
- F12-SEC-07: Hard size limit (700 chars), try-catch around all parsing, field length bounds
- No sensitive data in QR payload (no phone numbers, no names)

**Error Cases**:
- Oversized QR (>700 chars): `QrVerificationResult.invalid('QR code too large')`
- Malformed base64: `QrVerificationResult.invalid('Invalid QR code format')`
- Missing/oversized fields: `QrVerificationResult.invalid('Invalid QR data')`
- HMAC mismatch: `QrVerificationResult.invalid('Verification failed — possible tampering')`
- Wrong delivery: `QrVerificationResult.invalid('QR code does not match this delivery')`
- Delivery not found locally: handled by caller (not this service's concern)

---

### 5.2 GpsCaptureService

**File**: `mobile/lib/features/delivery_verification/data/services/gps_capture_service.dart`

**Purpose**: GPS coordinate capture with Ethiopian bounds validation and graceful degradation.

**Class Design**:

```dart
import 'package:geolocator/geolocator.dart';

class GpsCaptureService {
  /// Capture current GPS position.
  /// Returns GpsCaptureResult (success with coords, or unavailable with reason).
  Future<GpsCaptureResult> captureCurrentPosition() async { ... }

  /// Validate coordinates are within Ethiopian bounds.
  /// Bounds: 3-15°N latitude, 33-48°E longitude.
  bool isWithinEthiopianBounds(double latitude, double longitude) { ... }

  /// Calculate Haversine distance between two points in km.
  /// Used for 5km proximity check at pickup/dropoff.
  double haversineDistance(double lat1, double lon1,
                           double lat2, double lon2) { ... }

  /// Check if captured position is within threshold of reference point.
  /// threshold defaults to 5.0 km per contract.
  ProximityResult checkProximity(
    double capturedLat, double capturedLon,
    double refLat, double refLon,
    {double thresholdKm = 5.0}) { ... }
}

/// GPS capture result with graceful degradation.
class GpsCaptureResult {
  final bool isAvailable;
  final double? latitude;
  final double? longitude;
  final double? accuracy;   // meters
  final String? unavailableReason;

  const GpsCaptureResult.success({
    required this.latitude,
    required this.longitude,
    required this.accuracy,
  }) : isAvailable = true, unavailableReason = null;

  const GpsCaptureResult.unavailable(this.unavailableReason)
      : isAvailable = false, latitude = null,
        longitude = null, accuracy = null;
}

/// Proximity check result.
class ProximityResult {
  final bool isWithinThreshold;
  final double distanceKm;

  const ProximityResult({
    required this.isWithinThreshold,
    required this.distanceKm,
  });
}
```

**Implementation Flow**:

1. Check `Geolocator.isLocationServiceEnabled()`
2. Check/request `Geolocator.checkPermission()` → `Geolocator.requestPermission()`
3. Call `Geolocator.getCurrentPosition(desiredAccuracy: LocationAccuracy.high, timeLimit: Duration(seconds: 15))`
4. Validate Ethiopian bounds (3-15°N, 33-48°E)
5. Return `GpsCaptureResult.success()` or `GpsCaptureResult.unavailable(reason)`

**Graceful Degradation** (per architecture doc §3.3):
- GPS available → capture + validate + return coordinates
- GPS service disabled → return `unavailable('Location services disabled')`
- Permission denied → return `unavailable('Location permission denied')`
- Permission denied forever → return `unavailable('Location permission permanently denied')`
- Timeout (15s) → return `unavailable('GPS signal not available')`
- Outside Ethiopian bounds → return success but with warning flag (don't reject)

**Ethiopian Bounds Constants**:
```dart
static const double minLatitude = 3.0;   // Southern tip
static const double maxLatitude = 15.0;  // Northern tip
static const double minLongitude = 33.0; // Western border
static const double maxLongitude = 48.0; // Eastern tip
```

**Haversine Formula**:
```dart
double haversineDistance(double lat1, double lon1, double lat2, double lon2) {
  const R = 6371.0; // Earth radius in km
  final dLat = _toRadians(lat2 - lat1);
  final dLon = _toRadians(lon2 - lon1);
  final a = sin(dLat / 2) * sin(dLat / 2) +
      cos(_toRadians(lat1)) * cos(_toRadians(lat2)) *
      sin(dLon / 2) * sin(dLon / 2);
  final c = 2 * atan2(sqrt(a), sqrt(1 - a));
  return R * c;
}
```

**Security Considerations**:
- GPS can be spoofed on cheap phones; GPS is evidence, not sole verification
- 15-second timeout prevents indefinite waiting
- Ethiopian bounds check catches obvious GPS errors
- Accuracy field stored for audit (low accuracy flagged for review)

---

### 5.3 PhotoManager

**File**: `mobile/lib/features/delivery_verification/data/services/photo_manager.dart`

**Purpose**: Photo capture → resize → WebP compress → local store → upload queue.

**Class Design**:

```dart
import 'package:image_picker/image_picker.dart';
import 'package:flutter_image_compress/flutter_image_compress.dart';
import 'package:path_provider/path_provider.dart';

class PhotoManager {
  final ImagePicker _picker;

  PhotoManager({ImagePicker? picker}) : _picker = picker ?? ImagePicker();

  /// Capture photo from camera, compress to WebP, save locally.
  /// Returns PhotoResult with local file path or error.
  Future<PhotoResult> captureAndCompress({
    required String deliveryId,
    required PhotoType type,  // pickup or dropoff
  }) async { ... }

  /// Get local photo file path (for display before upload).
  /// Returns null if no local photo exists.
  Future<String?> getLocalPhotoPath(String deliveryId, PhotoType type) async { ... }

  /// Upload pending photos for a delivery.
  /// Called by background sync when connectivity is available.
  Future<PhotoUploadResult> uploadPhoto({
    required String deliveryId,
    required String localPath,
    required PhotoType type,
    required Dio dio,
  }) async { ... }

  /// Delete local photo after successful server upload.
  Future<void> deleteLocalPhoto(String path) async { ... }

  /// Get the photo storage directory.
  Future<Directory> _getPhotoDir(String deliveryId) async { ... }
}

enum PhotoType { pickup, dropoff }

class PhotoResult {
  final bool success;
  final String? localPath;
  final int? fileSizeBytes;
  final String? errorMessage;

  const PhotoResult.success({
    required this.localPath,
    required this.fileSizeBytes,
  }) : success = true, errorMessage = null;

  const PhotoResult.error(this.errorMessage)
      : success = false, localPath = null, fileSizeBytes = null;
}

class PhotoUploadResult {
  final bool success;
  final String? serverPhotoRef;  // S3 key returned by server
  final String? errorMessage;

  const PhotoUploadResult.success(this.serverPhotoRef)
      : success = true, errorMessage = null;

  const PhotoUploadResult.error(this.errorMessage)
      : success = false, serverPhotoRef = null;
}
```

**Photo Pipeline** (per architecture doc §4.1):

1. `ImagePicker.pickImage(source: ImageSource.camera, maxWidth: 800)` — capture with initial resize
2. `FlutterImageCompress.compressWithFile(path, format: CompressFormat.webp, quality: 75, minWidth: 800, minHeight: 600)` — WebP compression
3. Validate output size < 200KB; if over, reduce quality to 60% and retry
4. Save to `{appDocDir}/photos/{deliveryId}/{type}-{timestamp}.webp`
5. Return local file path

**Photo Upload** (separate from sync queue):

1. Called when connectivity is available (by SyncExecutor or manual trigger)
2. Reads local file, creates multipart request
3. `POST /deliveries/{deliveryId}/photos` with `photo` (file) + `type` (string)
4. On success: returns `serverPhotoRef` (S3 key)
5. Caller updates Isar delivery with serverPhotoRef, deletes local file

**Constraints** (per contract):
- Format: WebP only
- Max width: 800px (enforced at capture)
- Max file size: 200KB (enforced after compression, retry at lower quality)
- Max photos per type: 3 (enforced in UI — disable camera after 3)
- Quality: 75% (fallback to 60% if over 200KB)

**Storage Layout**:
```
{appDocDir}/photos/
  {deliveryId}/
    pickup-1708084900000.webp
    dropoff-1708171200000.webp
```

**Security Considerations**:
- Photos stored in app-private directory (not accessible by other apps)
- Local photos deleted after successful upload
- **F12-SEC-09**: Validate `deliveryId` matches UUID format (`RegExp(r'^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$')`) before constructing file paths — prevents path traversal attacks
- **F12-SEC-12**: EXIF stripping by `flutter_image_compress` must be verified by unit test (see §12). If library does NOT strip EXIF, add explicit removal step before compression

---

## 6. Phase 2: BLoC & Repository Updates

### 6.1 New BLoC Events

The existing BLoC events are trip-centric (`LoadDeliveries(tripId)`). The DeliveryListScreen needs user-centric queries. Add to `delivery_event.dart`:

```dart
/// Load all deliveries for the authenticated user across all trips.
/// Filters by role (farmer/trucker/buyer) when specified.
class LoadUserDeliveries extends DeliveryEvent {
  final String userId;
  final String? role;  // 'farmer', 'trucker', 'buyer', or null for all

  const LoadUserDeliveries({required this.userId, this.role});

  @override
  List<Object?> get props => [userId, role];
}

/// Load a single delivery by ID for detail screen.
class LoadDeliveryDetail extends DeliveryEvent {
  final String deliveryId;

  const LoadDeliveryDetail({required this.deliveryId});

  @override
  List<Object?> get props => [deliveryId];
}

/// Filter user deliveries by status.
class FilterUserDeliveries extends DeliveryEvent {
  final String userId;
  final String? role;
  final String? statusFilter;

  const FilterUserDeliveries({
    required this.userId,
    this.role,
    this.statusFilter,
  });

  @override
  List<Object?> get props => [userId, role, statusFilter];
}
```

### 6.2 New BLoC States

Add to `delivery_state.dart`:

```dart
/// Single delivery loaded for detail view.
class DeliveryDetailLoaded extends DeliveryState {
  final Delivery delivery;

  const DeliveryDetailLoaded({required this.delivery});

  @override
  List<Object?> get props => [delivery];
}

/// Dispute raised successfully.
class DisputeRaised extends DeliveryState {
  const DisputeRaised();
}

/// Delivery cancelled successfully.
class DeliveryCancelled extends DeliveryState {
  const DeliveryCancelled();
}
```

### 6.3 BLoC Handler Updates

Add to `delivery_bloc.dart` constructor:

```dart
on<LoadUserDeliveries>(_onLoadUserDeliveries);
on<LoadDeliveryDetail>(_onLoadDeliveryDetail);
on<FilterUserDeliveries>(_onFilterUserDeliveries);
```

New handler implementations:

```dart
Future<void> _onLoadUserDeliveries(
  LoadUserDeliveries event, Emitter<DeliveryState> emit,
) async {
  emit(const DeliveryLoading());
  try {
    final deliveries = await _repository.getDeliveriesByUser(
      event.userId, role: event.role);
    emit(DeliveriesLoaded(deliveries: deliveries));
  } on Exception catch (e) {
    emit(DeliveryError(message: e.toString()));
  }
}

Future<void> _onLoadDeliveryDetail(
  LoadDeliveryDetail event, Emitter<DeliveryState> emit,
) async {
  emit(const DeliveryLoading());
  try {
    final delivery = await _repository.getDeliveryById(event.deliveryId);
    if (delivery != null) {
      emit(DeliveryDetailLoaded(delivery: delivery));
    } else {
      emit(const DeliveryError(message: 'Delivery not found'));
    }
  } on Exception catch (e) {
    emit(DeliveryError(message: e.toString()));
  }
}

Future<void> _onFilterUserDeliveries(
  FilterUserDeliveries event, Emitter<DeliveryState> emit,
) async {
  emit(const DeliveryLoading());
  try {
    final deliveries = await _repository.getDeliveriesByUser(
      event.userId, role: event.role, statusFilter: event.statusFilter);
    emit(DeliveriesLoaded(
      deliveries: deliveries, statusFilter: event.statusFilter));
  } on Exception catch (e) {
    emit(DeliveryError(message: e.toString()));
  }
}
```

### 6.4 Repository Updates

#### 6.4.1 Client-Side Validation (F12-SEC-09)

The existing repository methods (`confirmPickup`, `confirmDelivery`, `raiseDispute`, `cancelDelivery`) must validate BEFORE enqueuing to the sync queue to prevent invalid operations from clogging the queue and wasting expensive mobile data. Add a `_validateDeliveryOperation()` helper:

```dart
/// Validate delivery operation before enqueuing to sync queue.
/// Mirrors server-side checks to reject invalid operations early.
/// Throws DeliveryValidationException with localized message on failure.
void _validateDeliveryOperation(Delivery delivery, String action, String userId, {
  String? disputeReason,
}) {
  // Validate deliveryId format (prevents path traversal in PhotoManager)
  final uuidRegex = RegExp(r'^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$');
  if (!uuidRegex.hasMatch(delivery.deliveryId)) {
    throw DeliveryValidationException('deliveryId', 'Invalid delivery ID format');
  }

  switch (action) {
    case 'confirmPickup':
      if (delivery.status != DeliveryStatus.pending) {
        throw DeliveryValidationException('status', 'Delivery must be pending for pickup');
      }
      if (delivery.truckerId != userId) {
        throw DeliveryValidationException('truckerId', 'Only the trucker can confirm pickup');
      }
    case 'confirmDelivery':
      if (delivery.status != DeliveryStatus.inTransit) {
        throw DeliveryValidationException('status', 'Delivery must be in transit for confirmation');
      }
      if (delivery.buyerId != userId) {
        throw DeliveryValidationException('buyerId', 'Only the buyer can confirm delivery');
      }
    case 'raiseDispute':
      if (delivery.status != DeliveryStatus.delivered) {
        throw DeliveryValidationException('status', 'Can only dispute delivered items');
      }
      if (delivery.confirmedAt != null &&
          DateTime.now().toUtc().difference(delivery.confirmedAt!) > const Duration(hours: 48)) {
        throw DeliveryValidationException('confirmedAt', 'Dispute window (48 hours) has expired');
      }
      if (disputeReason == null || disputeReason.isEmpty || disputeReason.length > 500) {
        throw DeliveryValidationException('disputeReason', 'Reason must be 1-500 characters');
      }
      if (delivery.farmerId != userId && delivery.truckerId != userId && delivery.buyerId != userId) {
        throw DeliveryValidationException('userId', 'Only participants can raise disputes');
      }
    case 'cancel':
      if (delivery.status != DeliveryStatus.pending && delivery.status != DeliveryStatus.pickedUp) {
        throw DeliveryValidationException('status', 'Cannot cancel after delivery');
      }
      if (delivery.farmerId != userId && delivery.truckerId != userId && delivery.buyerId != userId) {
        throw DeliveryValidationException('userId', 'Only participants can cancel');
      }
  }
}

/// GPS coordinate range validation (if provided).
void _validateGpsCoordinates(double? lat, double? lon) {
  if (lat == null || lon == null) return; // GPS is optional
  if (lat < -90 || lat > 90) {
    throw DeliveryValidationException('latitude', 'Latitude must be between -90 and 90');
  }
  if (lon < -180 || lon > 180) {
    throw DeliveryValidationException('longitude', 'Longitude must be between -180 and 180');
  }
}
```

Each existing repository method (`confirmPickup`, `confirmDelivery`, `raiseDispute`, `cancelDelivery`) must call `_validateDeliveryOperation()` as its **first operation**, before any Isar writes or sync queue enqueues.

#### 6.4.2 New Query Method

Add to `delivery_repository.dart`:

```dart
/// Get all deliveries where the user is a participant.
/// Queries Isar by farmerId, truckerId, or buyerId.
/// Optional role filter: 'farmer', 'trucker', 'buyer'.
/// Optional statusFilter: matches DeliveryStatus enum name.
Future<List<Delivery>> getDeliveriesByUser(
  String userId, {String? role, String? statusFilter}) async {
  List<Delivery> results;

  if (role == 'farmer') {
    results = await _isar.deliverys
        .filter().farmerIdEqualTo(userId)
        .sortByCreatedAtDesc().findAll();
  } else if (role == 'trucker') {
    results = await _isar.deliverys
        .filter().truckerIdEqualTo(userId)
        .sortByCreatedAtDesc().findAll();
  } else if (role == 'buyer') {
    results = await _isar.deliverys
        .filter().buyerIdEqualTo(userId)
        .sortByCreatedAtDesc().findAll();
  } else {
    // All roles: union of farmer + trucker + buyer queries
    final asFarmer = await _isar.deliverys
        .filter().farmerIdEqualTo(userId).findAll();
    final asTrucker = await _isar.deliverys
        .filter().truckerIdEqualTo(userId).findAll();
    final asBuyer = await _isar.deliverys
        .filter().buyerIdEqualTo(userId).findAll();
    // Deduplicate by deliveryId
    final seen = <String>{};
    results = [...asFarmer, ...asTrucker, ...asBuyer]
        .where((d) => seen.add(d.deliveryId)).toList()
      ..sort((a, b) => b.createdAt.compareTo(a.createdAt));
  }

  // Apply status filter if provided
  if (statusFilter != null) {
    final targetStatus = DeliveryStatus.values
        .firstWhere((s) => s.name == statusFilter,
            orElse: () => DeliveryStatus.pending);
    results = results.where((d) => d.status == targetStatus).toList();
  }

  return results;
}
```

---

## 7. Phase 3: Presentation Layer

### 7.1 Shared Widgets

#### 7.1.1 DeliveryCard

**File**: `mobile/lib/features/delivery_verification/presentation/widgets/delivery_card.dart`

**Purpose**: Reusable card for delivery list items. Follows CropListingCard/TripCard pattern.

**Design**:
```
┌────────────────────────────────────────┐
│ [Status Icon 48x48]  Delivery #del-101 │
│                       Farmer: Alemayehu│
│                       → Buyer: Fatuma  │
│                       Teff, 5 quintals │
│                       Feb 18, 2026     │
│                       [Status Chip]    │
└────────────────────────────────────────┘
```

**Props**:
- `delivery: Delivery` — the delivery data
- `onTap: VoidCallback?` — navigates to detail screen
- `currentUserId: String` — to highlight user's role

**Widget Composition**:
- Leading: `DeliveryStatusIndicator` (48x48 icon container)
- Title: Delivery ID (truncated) or crop type from listing
- Subtitle: Farmer name → Buyer name (with arrow)
- Trailing: `_StatusChip` colored by delivery status
- Tap: navigates to DeliveryDetailScreen

#### 7.1.2 DeliveryStatusIndicator

**File**: `mobile/lib/features/delivery_verification/presentation/widgets/delivery_status_indicator.dart`

**Purpose**: Icon-based status display for low-literacy users. Per contract: "Icon-based status: package, truck, checkmark, warning."

**Status → Icon Mapping**:

| Status | Icon | Color | Description |
|--------|------|-------|-------------|
| pending | `Icons.inventory_2` | Colors.orange | Package waiting |
| picked_up | `Icons.local_shipping` | Colors.blue | Truck loaded |
| in_transit | `Icons.route` | Colors.indigo | Truck moving |
| delivered | `Icons.check_circle` | Colors.green | Checkmark |
| disputed | `Icons.warning` | Colors.red | Warning |
| cancelled | `Icons.cancel` | Colors.grey | Cancelled |

**Design**: 48x48 container with rounded background color at 10% opacity, centered icon at 24dp. Follows the existing `_StatusChip` pattern in crop_listing.

#### 7.1.3 PhotoEvidenceWidget

**File**: `mobile/lib/features/delivery_verification/presentation/widgets/photo_evidence_widget.dart`

**Purpose**: Displays photo thumbnail with capture/view capability.

**Design**:
```
┌─────────────────────────┐
│  [Photo Thumbnail 120px]│  ← If photo exists
│  or                     │
│  [Camera Icon + "Add"]  │  ← If no photo
│  Pickup Photo           │
│  120KB · Feb 18         │
└─────────────────────────┘
```

**Props**:
- `photoRef: String?` — local path or server ref
- `photoType: PhotoType` — pickup or dropoff
- `deliveryId: String` — for file path resolution
- `onCapture: VoidCallback?` — trigger photo capture
- `canCapture: bool` — false if max photos reached or wrong status

---

### 7.2 DeliveryListScreen

**File**: `mobile/lib/features/delivery_verification/presentation/screens/delivery_list_screen.dart`

**Purpose**: Browse user's deliveries with role and status filtering. Entry point for delivery feature.

**Layout**:
```
┌──────────────────────────────────┐
│ [OfflineBanner]                  │
│                                  │
│ My Deliveries          [Filter ▼]│
│                                  │
│ [Role Chips: All|Farmer|Trucker| │
│              Buyer]              │
│                                  │
│ [Status Chips: All|Pending|      │
│  PickedUp|InTransit|Delivered]   │
│                                  │
│ ┌──────────────────────────────┐ │
│ │ DeliveryCard #1              │ │
│ └──────────────────────────────┘ │
│ ┌──────────────────────────────┐ │
│ │ DeliveryCard #2              │ │
│ └──────────────────────────────┘ │
│ ... (ListView.builder)           │
│                                  │
│ [Empty State: truck icon +       │
│  "No deliveries yet"]            │
└──────────────────────────────────┘
```

**BLoC Integration**:
- `initState()`: dispatch `LoadUserDeliveries(userId: authUserId)`
- Role chips: dispatch `FilterUserDeliveries(userId, role: selectedRole)`
- Status chips: dispatch `FilterUserDeliveries(userId, role, statusFilter)`
- Pull-to-refresh: `RefreshIndicator` → re-dispatch `LoadUserDeliveries`

**States Handled**:
- `DeliveryLoading` → `CircularProgressIndicator`
- `DeliveriesLoaded` → `ListView.builder` with `DeliveryCard`s
- `DeliveryError` → Error icon + message + retry button
- Empty list → Truck icon + "No deliveries yet" + instructions

**Navigation**:
- Tap card → push `DeliveryDetailScreen(delivery: delivery)`

---

### 7.3 DeliveryDetailScreen

**File**: `mobile/lib/features/delivery_verification/presentation/screens/delivery_detail_screen.dart`

**Purpose**: Full delivery details with status timeline, GPS, photos, and action buttons.

**Layout**:
```
┌──────────────────────────────────┐
│ ← Delivery Details               │
│                                  │
│ [Status Timeline Widget]         │
│ ● Pending → ● Picked Up →       │
│   ○ In Transit → ○ Delivered     │
│                                  │
│ ─────────────────────────────── │
│ Participants                     │
│ 👨‍🌾 Farmer: Alemayehu Bekele     │
│ 🚛 Trucker: Tadesse Worku       │
│ 🏪 Buyer: Fatuma Ahmed          │
│                                  │
│ ─────────────────────────────── │
│ Pickup                           │
│ GPS: 9.6778, 39.5318 ✓ (2.3km)  │
│ Photo: [Thumbnail]               │
│ Time: Feb 18, 2026 08:15        │
│                                  │
│ ─────────────────────────────── │
│ Delivery                         │
│ GPS: Not yet captured            │
│ Photo: Not yet captured          │
│ Time: Pending                    │
│                                  │
│ ─────────────────────────────── │
│ [Action Buttons per role/status] │
│ [Show QR Code]  (farmer, pending)│
│ [Scan QR Code]  (trucker, pending)│
│ [Raise Dispute] (any, delivered) │
│ [Cancel]        (any, pre-pickup)│
└──────────────────────────────────┘
```

**Status Timeline Widget** (inline, not separate file):
- Horizontal row of 4 status circles connected by lines
- Filled circle = reached; outline circle = not yet reached
- Colors match `DeliveryStatusIndicator` scheme
- Current status circle has pulse animation

**Action Button Logic** (based on current user role + delivery status):

| User Role | Delivery Status | Available Actions |
|-----------|----------------|-------------------|
| Farmer | pending | Show QR Code |
| Farmer | delivered | Raise Dispute (within 48h) |
| Farmer | pending | Cancel |
| Trucker | pending | Scan QR (pickup) |
| Trucker | picked_up | Mark In Transit |
| Trucker | pending/picked_up | Cancel |
| Buyer | in_transit | Scan QR (delivery) |
| Buyer | delivered | Raise Dispute (within 48h) |
| Any | disputed | View dispute reason (read-only) |

**BLoC Integration**:
- Constructor receives `Delivery` object (from list screen navigation)
- On action: dispatch appropriate event (ConfirmPickup, ConfirmDelivery, etc.)
- Listen for state changes: `PickupConfirmed`, `DeliveryConfirmed`, `DisputeRaised`
- After state change: reload delivery detail with `LoadDeliveryDetail`

---

### 7.4 QrGenerateScreen

**File**: `mobile/lib/features/delivery_verification/presentation/screens/qr_generate_screen.dart`

**Purpose**: Display QR code for farmers to show truckers at pickup (or truckers to show buyers at dropoff).

**Layout**:
```
┌──────────────────────────────────┐
│ ← QR Code                        │
│                                  │
│ Show this QR code to the trucker │
│                                  │
│ ┌──────────────────────────────┐ │
│ │                              │ │
│ │      [QR Code 280x280]      │ │
│ │                              │ │
│ └──────────────────────────────┘ │
│                                  │
│ Delivery: del-101                │
│ Farmer: Alemayehu Bekele         │
│ Created: Feb 18, 2026            │
│                                  │
│ [🔄 Regenerate QR]              │
│                                  │
│ ⓘ This QR code works offline.   │
│   No internet needed.            │
└──────────────────────────────────┘
```

**Implementation**:
- Takes `Delivery` object as constructor param
- Uses `QrVerificationService.generateQrToken(delivery)` to get token string
- Displays with `QrImageView` from `qr_flutter` package
- QR size: 280x280dp (readable on cheap cameras)
- Error correction: `QrErrorCorrectLevel.M` (15% error tolerance)
- Background: white with padding (improves camera readability)
- "Regenerate" button: calls `generateQrToken()` again (new timestamp)
- Informational text: "Works offline, no internet needed"

**Low-Literacy UX**:
- Large QR code (fills most of screen)
- Simple instruction text with icon
- Minimal text, maximum visual clarity
- High contrast (black QR on white background)

---

### 7.5 QrScanScreen

**File**: `mobile/lib/features/delivery_verification/presentation/screens/qr_scan_screen.dart`

**Purpose**: Scan QR code, verify HMAC offline, capture GPS, optional photo, confirm pickup/delivery.

**Layout — Scanning Phase**:
```
┌──────────────────────────────────┐
│ ← Scan QR Code                   │
│                                  │
│ ┌──────────────────────────────┐ │
│ │                              │ │
│ │     [Camera Preview]         │ │
│ │                              │ │
│ │    ┌────────────────┐       │ │
│ │    │  Scan Frame    │       │ │
│ │    │  (overlay)     │       │ │
│ │    └────────────────┘       │ │
│ │                              │ │
│ └──────────────────────────────┘ │
│                                  │
│ Point camera at the QR code      │
│ [💡 Toggle Flash]               │
└──────────────────────────────────┘
```

**Layout — Verified Phase**:
```
┌──────────────────────────────────┐
│ ← Verification                   │
│                                  │
│ ┌──────────────────────────────┐ │
│ │    ✅ Verification Passed    │ │  ← Green checkmark (animated)
│ │    Delivery #del-101         │ │
│ │    Farmer: Alemayehu         │ │
│ └──────────────────────────────┘ │
│                                  │
│ 📍 GPS Captured                  │
│    9.6778, 39.5318 (±15m)       │
│    ✓ Within 5km of pickup       │  ← Or warning if outside
│                                  │
│ 📷 Photo Evidence (Optional)    │
│    [Take Photo]                  │  ← Camera button
│    or [Photo Thumbnail] ✓       │  ← After capture
│                                  │
│ ──────────────────────────────  │
│ [Confirm Pickup]                 │  ← Large, green button (48dp)
│                                  │
│ ⚠️ GPS unavailable. Delivery    │  ← If GPS failed
│    will be flagged for review.   │
└──────────────────────────────────┘
```

**Scan Flow**:

1. Open `MobileScanner` camera preview
2. On barcode detected: `controller.stop()` (prevent multiple scans)
3. Call `QrVerificationService.verifyQrToken(scannedValue, delivery)`
4. If **valid**:
   - Show green checkmark animation
   - Play success sound (if available)
   - Auto-trigger GPS capture via `GpsCaptureService.captureCurrentPosition()`
   - Show GPS result (coordinates + proximity)
   - Show optional photo capture button
   - Show "Confirm Pickup/Delivery" button
5. If **invalid**:
   - Show red X animation
   - Play failure sound
   - Show error message from `QrVerificationResult.errorMessage`
   - Show "Try Again" button (re-opens camera)

**Constructor Params**:
- `delivery: Delivery` — the delivery being verified
- `verificationType: VerificationType` — `.pickup` or `.delivery`
- `qrVerificationService: QrVerificationService`
- `gpsCaptureService: GpsCaptureService`
- `photoManager: PhotoManager`

**Confirmation Action**:
- For pickup: dispatch `ConfirmPickup(deliveryId, truckerId, gpsPickup, photoRef)` — include `nonce` from `QrVerificationResult` in sync operation data
- For delivery: dispatch `ConfirmDelivery(deliveryId, buyerId, gpsDropoff, photoRef)` — include `nonce` from `QrVerificationResult` in sync operation data
- The nonce is passed through the BLoC event → repository → sync queue, so the server can cross-validate it at sync time (F12-SEC-01)
- On success state: pop back to detail screen with updated delivery

**Visual Feedback** (per contract — low-literacy UX):
- Green checkmark: large (64dp), animated scale-in
- Red X: large (64dp), animated shake
- GPS indicator: location pin icon + distance text
- Photo indicator: camera icon + thumbnail

---

### 7.6 DisputeScreen

**File**: `mobile/lib/features/delivery_verification/presentation/screens/dispute_screen.dart`

**Purpose**: Raise a dispute within 48 hours of delivery confirmation.

**Layout**:
```
┌──────────────────────────────────┐
│ ← Raise Dispute                  │
│                                  │
│ Delivery #del-101                │
│ Confirmed: Feb 19, 2026 08:00   │
│ Time remaining: 23h 45m          │  ← Countdown
│                                  │
│ ─────────────────────────────── │
│ Reason for Dispute               │
│ ┌──────────────────────────────┐ │
│ │                              │ │
│ │  [TextFormField]             │ │
│ │  Max 500 characters          │ │
│ │  (multiline, 4 rows)        │ │
│ │                              │ │
│ └──────────────────────────────┘ │
│ Characters: 45/500               │
│                                  │
│ ⚠️ Disputes are reviewed by     │
│    WeredaConnect. Provide clear  │
│    details about the issue.      │
│                                  │
│ [Submit Dispute]                 │  ← Red button, 48dp height
│                                  │
│ ⚠️ Dispute window expired       │  ← If >48h, show instead
│    (disabled submit button)      │
└──────────────────────────────────┘
```

**Validation**:
- Reason: 1-500 characters (required)
- 48-hour window: `DateTime.now().difference(delivery.confirmedAt!) < Duration(hours: 48)`
- If expired: disable form, show "Dispute window has expired" message
- Confirmation dialog before submission

**BLoC Integration**:
- Dispatch `RaiseDispute(deliveryId, userId, reason, tripId)`
- On `DeliveriesLoaded` or `DisputeRaised`: pop back to detail screen
- On `DeliveryError`: show error SnackBar, keep form

---

## 8. Phase 4: Integration

### 8.1 Navigation Wiring

**Entry Points**:

1. **Main app navigation** → DeliveryListScreen (tab or drawer item)
   - Shows "My Deliveries" across all trips
   - Accessible to all user roles

2. **Trip Detail Screen** → "Create Delivery" button
   - Only shown to truckers
   - Navigates to delivery creation flow (creates delivery + shows detail)

3. **Delivery List** → tap card → DeliveryDetailScreen
   - Standard `MaterialPageRoute` push

4. **Delivery Detail** → action buttons → QrGenerateScreen / QrScanScreen / DisputeScreen
   - Role-based button visibility (see §7.3 action table)

**Navigation Map**:
```
DeliveryListScreen
  └── tap card → DeliveryDetailScreen
       ├── Show QR → QrGenerateScreen
       ├── Scan QR → QrScanScreen
       │    └── confirmed → pop to DeliveryDetailScreen (refreshed)
       ├── Dispute → DisputeScreen
       │    └── submitted → pop to DeliveryDetailScreen (refreshed)
       └── Cancel → confirmation dialog → pop to list (refreshed)
```

**BlocProvider Setup**:
- `DeliveryBloc` provided at `DeliveryListScreen` level
- Child screens access via `context.read<DeliveryBloc>()`
- Repository and services injected at provider level

```dart
BlocProvider(
  create: (context) => DeliveryBloc(
    repository: DeliveryRepository(
      isar: context.read<Isar>(),
      syncQueue: context.read<SyncQueue>(),
    ),
  ),
  child: const DeliveryListScreen(),
)
```

### 8.2 Localization

**Key Naming Convention** (follows existing pattern):
- Prefix: `delivery` for all delivery-related strings
- Status: `deliveryStatus{Value}` (e.g., `deliveryStatusPending`)
- Actions: `delivery{Action}` (e.g., `deliveryConfirmPickup`)
- Errors: `delivery{Error}` (e.g., `deliveryErrorNotFound`)
- Labels: `delivery{Label}` (e.g., `deliveryGpsLocation`)

**Estimated String Count**: ~70 strings across 3 files.

**String Categories**:

| Category | Count | Examples |
|----------|-------|---------|
| Screen titles | 5 | myDeliveries, deliveryDetails, scanQrCode, showQrCode, raiseDispute |
| Status labels | 6 | deliveryStatusPending, deliveryStatusPickedUp, etc. |
| Action buttons | 8 | deliveryConfirmPickup, deliveryConfirmDelivery, deliveryScanQr, deliveryShowQr, deliveryRaiseDispute, deliveryCancel, deliveryTakePhoto, deliveryRegenerateQr |
| Participant labels | 6 | deliveryFarmer, deliveryTrucker, deliveryBuyer, deliveryFarmerLabel, deliveryTruckerLabel, deliveryBuyerLabel |
| GPS labels | 6 | deliveryGpsCaptured, deliveryGpsUnavailable, deliveryGpsWithinRange, deliveryGpsOutOfRange, deliveryGpsCapturing, deliveryGpsPermissionDenied |
| Photo labels | 4 | deliveryPhotoPickup, deliveryPhotoDropoff, deliveryAddPhoto, deliveryPhotoOptional |
| QR labels | 5 | deliveryQrInstructions, deliveryQrOfflineNote, deliveryQrVerified, deliveryQrFailed, deliveryQrPointCamera |
| Dispute labels | 5 | deliveryDisputeReason, deliveryDisputeTimeRemaining, deliveryDisputeExpired, deliveryDisputeWarning, deliveryDisputeSubmit |
| Validation messages | 8 | deliveryReasonRequired, deliveryReasonTooLong, deliveryNotFound, deliveryInvalidTransition, deliveryUnauthorized, deliveryAlreadyPickedUp, deliveryAlreadyDelivered, deliveryDisputeWindowExpired |
| Filter labels | 6 | deliveryFilterAll, deliveryFilterPending, deliveryFilterPickedUp, deliveryFilterInTransit, deliveryFilterDelivered, deliveryFilterRole |
| Empty/info states | 4 | deliveryNoDeliveries, deliveryNoDeliveriesHint, deliveryOfflineNote, deliveryCreatedAt |
| Confirmation dialogs | 6 | deliveryCancelConfirmTitle, deliveryCancelConfirmMessage, deliveryDisputeConfirmTitle, deliveryDisputeConfirmMessage, deliveryConfirmButton, deliveryCancelButton |

**Files to Modify**:
- `mobile/lib/shared/l10n/app_en.arb` — English strings
- `mobile/lib/shared/l10n/app_am.arb` — Amharic translations
- `mobile/lib/shared/l10n/app_om.arb` — Afan Oromo translations

After adding strings, run: `flutter gen-l10n`

### 8.3 Sync Integration

The sync engine already handles delivery entity type (confirmed in SyncFunction.cs). The SyncQueue integration is already built into `DeliveryRepository` (it enqueues sync operations on create/update/delete).

**Photo Upload Integration** (NEW — not in sync queue):

The photo upload needs a separate trigger since it's not part of the standard sync flow. Options:

1. **Manual trigger**: After photo capture, if online, upload immediately. If offline, queue for later.
2. **SyncExecutor hook**: After successful delivery sync batch, check for pending photo uploads.

**Recommended approach**: Add a `uploadPendingPhotos()` method to PhotoManager that SyncExecutor can call after processing delivery operations. This keeps photo upload separate but triggered by the same connectivity event.

```dart
// In SyncExecutor, after processing a batch:
if (result.status == SyncBatchStatus.success) {
  // Check for pending photo uploads
  await photoManager.uploadPendingPhotos(dio: _dio);
}
```

**Pending Photo Tracking**:
- Deliveries with local file paths (not S3 keys) in `pickupPhotoRef`/`dropoffPhotoRef` have pending uploads
- Query: `_isar.deliverys.filter().pickupPhotoRefStartsWith(appDocDir)` (local paths start with app directory)
- After upload: update Isar with server S3 key, delete local file

---

## 9. Security Mitigations

### 9.1 Security Review Findings Tracker

Source: `qa/security-reviews/f12-implementation-review.md` v1.0.0

#### MUST FIX (Both Resolved)

| ID | Finding | Resolution | Implemented In |
|----|---------|-----------|----------------|
| **F12-SEC-01** | QR HMAC secret derivable by all synced participants — any participant could forge QR tokens without physical proximity | **RESOLVED**: Added cryptographic nonce to QR flow. Nonce is generated at QR time (32 bytes, `Random.secure()`), included in HMAC payload, but NOT stored in synced delivery record. Scanner must physically see QR to obtain nonce. Server cross-validates nonces at sync time. | §5.1 (QrVerificationService redesign) |
| **F12-SEC-02** | 48-hour dispute window not enforced server-side — attacker could raise disputes weeks/months after delivery via direct API | **RESOLVED**: Add temporal check in `ProcessDeliveryAsync` for `delivered → disputed` transition. Server must validate `DateTime.UtcNow - confirmedAt <= 48 hours`. ~15-line code change in `SyncFunction.cs`. | §9.2 (Server Fix: Dispute Window) |

#### SHOULD FIX (All Incorporated)

| ID | Finding | Resolution | Implemented In |
|----|---------|-----------|----------------|
| **F12-SEC-03** | HMAC comparison uses short-circuit `==` instead of constant-time | **INCORPORATED**: Added `_constantTimeEquals()` method using XOR accumulator. All HMAC comparisons use byte-level constant-time comparison. | §5.1 (verifyQrToken implementation) |
| **F12-SEC-04** | Phone validation uses `StartsWith("+251")` instead of regex | **INCORPORATED**: Replace with existing `EthiopianPhoneRegex()` (`^\+251\d{9}$`) in `ValidateDeliveryFields`. | §9.3 (Server Fix: Phone Validation) |
| **F12-SEC-05** | No server-side GPS proximity validation | **INCORPORATED**: Add `ValidateGpsProximity` to delivery sync handler. Pickup: check 5km from listing location. Delivery: check 5km from trip destination. If GPS absent, flag `requires_review`. | §9.4 (Server Fix: GPS Proximity) |
| **F12-SEC-06** | Dispute reason length mismatch: contract says 500, server allows 2000 | **INCORPORATED**: Align server `ValidateDeliveryFields` and inline check to 500 chars, matching contract. | §9.5 (Server Fix: Dispute Length) |
| **F12-SEC-07** | Malicious QR payload handling not specified | **INCORPORATED**: Added to `verifyQrToken()`: hard size limit (700 chars) before base64 decode, try-catch around all parsing, field length bounds (100 chars max per field). | §5.1 (verifyQrToken implementation) |
| **F12-SEC-08** | Photo upload endpoint needs server-side content validation | **INCORPORATED**: Specified validation requirements for `POST /deliveries/{id}/photos` handler: magic byte check (RIFF+WEBP), 200KB server limit, max count check, participant auth, server-generated filenames. | §9.6 (Server Fix: Photo Validation) |
| **F12-SEC-09** | Mobile repository lacks client-side validation before sync queue | **INCORPORATED**: Added `_validateDeliveryOperation()` method to repository. Validates status transitions, participant authorization, dispute window, dispute reason length, and UUID format before any Isar writes or sync enqueues. | §6.4.1 (Repository validation) |

#### NICE TO HAVE (Deferred to Future Phases)

| ID | Finding | Deferral Rationale | Target Phase |
|----|---------|-------------------|--------------|
| **F12-SEC-10** | No GPS spoofing detection heuristics | Low risk for MVP — GPS is one of multiple evidence types (QR + GPS + photo). Spoofing detection adds complexity without blocking escrow flow. | F-14 (Fraud Detection) |
| **F12-SEC-11** | No audit trail for QR verification attempts | Not blocking — QR scan outcomes are transient UI feedback. Audit trail valuable for fraud scoring but requires analytics infrastructure not yet built. | F-14 (Fraud Detection) |
| **F12-SEC-12** | EXIF stripping should be verified by test | Added to testing approach (§12) as a unit test requirement. If `flutter_image_compress` doesn't strip EXIF, an explicit removal step will be added. | F-12 (during implementation) |
| **F12-SEC-13** | Shared device data isolation could be stronger | Current auth model prevents cross-user access at BLoC level. Isar encryption and logout cleanup are valuable but require auth system changes (F-02). | F-02 (Auth hardening) |
| **F12-SEC-14** | QR error correction level may be insufficient for Ethiopian conditions | Level M (15%) is standard. Upgrade to Q (25%) only if device testing reveals scan failures. Added as a testing checkpoint. | F-12 (during device testing) |

### 9.2 Server Fix: 48-Hour Dispute Window (F12-SEC-02)

**File**: `server/src/Functions/SyncFunction.cs` (within `ProcessDeliveryAsync`, around line 1610)

**Change**: Add temporal validation before allowing `delivered → disputed` transition:

```csharp
case "disputed":
    // F12-SEC-02: Enforce 48-hour dispute window (server-side)
    if (existing.TryGetValue("confirmedAt", out var confirmedAtAttr)
        && !string.IsNullOrEmpty(confirmedAtAttr.S)
        && DateTime.TryParse(confirmedAtAttr.S, null,
            System.Globalization.DateTimeStyles.RoundtripKind, out var confirmedDate))
    {
        if (DateTime.UtcNow - confirmedDate > TimeSpan.FromHours(48))
        {
            return new SyncOperationResult
            {
                OperationId = operation.OperationId,
                Status = "error",
                Message = "DELIVERY_DISPUTE_EXPIRED: Dispute window (48 hours) has expired."
            };
        }
    }
    else if (currentStatus == "delivered")
    {
        // confirmedAt is missing for a delivered delivery — data integrity issue
        return new SyncOperationResult
        {
            OperationId = operation.OperationId,
            Status = "error",
            Message = "DELIVERY_VALIDATION: confirmedAt is missing for delivered delivery."
        };
    }
    break;
```

### 9.3 Server Fix: Phone Validation (F12-SEC-04)

**File**: `server/src/Functions/SyncFunction.cs` (within `ValidateDeliveryFields`, around line 1322-1332)

**Change**: Replace `StartsWith("+251")` with existing `EthiopianPhoneRegex()`:

```csharp
// Before (vulnerable):
if (!phoneObj.ToString()!.StartsWith("+251"))

// After (fixed):
if (!EthiopianPhoneRegex().IsMatch(phoneObj.ToString()!))
{
    errors.Add($"{phoneField} must be valid Ethiopian E.164 format (+251XXXXXXXXX).");
}
```

### 9.4 Server Fix: GPS Proximity Validation (F12-SEC-05)

**File**: `server/src/Functions/SyncFunction.cs` (within `ProcessDeliveryAsync`)

**Change**: After GPS coordinate range validation, add proximity check:

```csharp
// On pickup (status → pickedUp): validate GPS is within 5km of listing location
if (newStatus == "picked_up" && gpsPickup != null)
{
    var listing = await _cropRepo.GetByIdAsync(listingId);
    if (listing != null && listing.ContainsKey("latitude") && listing.ContainsKey("longitude"))
    {
        var listingLat = Convert.ToDouble(listing["latitude"]);
        var listingLon = Convert.ToDouble(listing["longitude"]);
        var distance = GpsValidator.HaversineDistance(
            gpsPickup.Latitude, gpsPickup.Longitude, listingLat, listingLon);
        if (distance > 5.0)
        {
            return ErrorResult(operation.OperationId,
                $"DELIVERY_GPS_PROXIMITY: Pickup GPS is {distance:F1}km from listing location (max 5km).");
        }
    }
}

// On delivery (status → delivered): validate GPS is within 5km of trip destination
// Similar pattern querying trip destination coordinates

// If GPS is absent, flag for review in metadata (graceful degradation)
if (gpsPickup == null && newStatus == "picked_up")
{
    // Add requires_review flag to item attributes
    updateExpressions.Add("#requiresReview = :true");
}
```

**Note**: The existing `GpsValidator.cs` already has the Haversine formula. This fix wires it into the delivery sync handler.

### 9.5 Server Fix: Dispute Reason Length (F12-SEC-06)

**File**: `server/src/Functions/SyncFunction.cs` (two locations)

**Change 1** — `ValidateDeliveryFields` (around line 1338):
```csharp
// Before: if (drText.Length > 2000)
// After:
if (string.IsNullOrWhiteSpace(drText) || drText.Length > 500)
{
    errors.Add("disputeReason must be 1-500 characters.");
}
```

**Change 2** — Inline check in update handler (around line 1666):
```csharp
// Before: if (drText.Length > 2000)
// After:
if (drText.Length > 500)
```

### 9.6 Server Fix: Photo Upload Validation (F12-SEC-08)

**File**: `server/src/Functions/DeliveryFunction.cs` (new `HandlePhotoUploadAsync` method)

When implementing `POST /deliveries/{id}/photos`, the handler **must** validate:

1. **Content-Type header**: Reject if not `image/webp`
2. **Magic bytes**: WebP files start with `RIFF` (bytes 0-3) and `WEBP` (bytes 8-11). Check before storing.
3. **File size**: Reject files > 200KB with 413 response
4. **Max count**: Query existing photos for the delivery+type. Reject if ≥ 3 per type.
5. **Participant authorization**: Validate JWT `sub` matches a delivery participant
6. **Filename**: Do NOT use client-provided filename. Generate server-side: `uploads/photos/{deliveryId}/{type}-{timestamp}.webp`

### Per-Component Security Summary

| Component | Threat | Mitigation |
|-----------|--------|-----------|
| QrVerificationService | Token forgery without physical proximity | **F12-SEC-01**: Nonce-based HMAC — nonce only exists in QR code, not synced data |
| QrVerificationService | Timing attack on HMAC comparison | **F12-SEC-03**: Constant-time `_constantTimeEquals()` byte comparison |
| QrVerificationService | Malicious QR payload | **F12-SEC-07**: Size limit, try-catch, field bounds before processing |
| QrVerificationService | Replay attacks | deliveryId + nonce in HMAC; each QR generation has unique nonce |
| GpsCaptureService | GPS spoofing | GPS is evidence only; server validates 5km proximity (**F12-SEC-05**) |
| GpsCaptureService | Privacy leak | Coordinates stored per-delivery, not shared with non-participants |
| PhotoManager | Path traversal | **F12-SEC-09**: UUID format validation before constructing file paths |
| PhotoManager | EXIF data leak | **F12-SEC-12**: Verified by unit test; explicit removal if library doesn't strip |
| PhotoManager | Malicious file upload | **F12-SEC-08**: Server validates magic bytes, size, count, content-type |
| Repository | Invalid operations clogging sync | **F12-SEC-09**: `_validateDeliveryOperation()` before Isar write + sync enqueue |
| Repository | Dispute after 48h window | Client: `_validateDeliveryOperation()` checks window. Server: **F12-SEC-02** temporal check |
| Server | Phone validation bypass | **F12-SEC-04**: `EthiopianPhoneRegex()` replaces `StartsWith` |
| Server | Dispute reason overflow | **F12-SEC-06**: 500-char limit aligned across contract, client, server |

### Input Validation Layers

1. **Screen layer**: Form validators (length, format, required fields)
2. **Repository layer**: `_validateDeliveryOperation()` — state machine, participant auth, dispute window, UUID format (F12-SEC-09)
3. **Server layer**: Full re-validation (authoritative) — GPS proximity (F12-SEC-05), phone regex (F12-SEC-04), dispute window (F12-SEC-02), file validation (F12-SEC-08)

### Offline Security Model

- QR verification uses nonce-based HMAC — offline scan is a physical proximity ritual; server cross-validates nonces at sync time (F12-SEC-01)
- No sensitive data transmitted during offline operations
- All offline state changes are re-validated by server during sync
- If server rejects a state change, ConflictResolver reverts local Isar to server state

---

## 10. File-by-File Implementation Order

### Dependency Graph

```
pubspec.yaml (packages)
    ↓
AndroidManifest.xml (permissions)
    ↓
QrVerificationService ──────────────────┐
GpsCaptureService ──────────────────────┤
PhotoManager ───────────────────────────┤
    ↓                                   │
BLoC events/states (modifications) ─────┤
Repository (modifications) ─────────────┤
    ↓                                   │
DeliveryStatusIndicator (widget) ───────┤
DeliveryCard (widget) ──────────────────┤
PhotoEvidenceWidget (widget) ───────────┤
    ↓                                   │
DeliveryListScreen ←────────────────────┤
DeliveryDetailScreen ←──────────────────┤
QrGenerateScreen ←──────────────────────┤
QrScanScreen ←──────────────────────────┤
DisputeScreen ←─────────────────────────┘
    ↓
Localization (.arb files)
    ↓
Navigation wiring
```

### Implementation Order (Sequential)

| Order | File | Task | Depends On | Parallelizable |
|-------|------|------|-----------|----------------|
| 1 | `mobile/pubspec.yaml` | Add 6 packages | — | No (all depend on this) |
| 2 | `mobile/android/app/src/main/AndroidManifest.xml` | Add camera + location permissions | — | Yes (with #1) |
| 3 | `.../data/services/qr_verification_service.dart` | **NEW** — HMAC generation + verification | #1 (crypto pkg) | Yes (with #4, #5) |
| 4 | `.../data/services/gps_capture_service.dart` | **NEW** — GPS capture + Ethiopian bounds | #1 (geolocator pkg) | Yes (with #3, #5) |
| 5 | `.../data/services/photo_manager.dart` | **NEW** — Photo pipeline + upload queue | #1 (image_picker, flutter_image_compress) | Yes (with #3, #4) |
| 6 | `.../presentation/bloc/delivery_event.dart` | **MODIFY** — Add 3 new events | — | Yes (with #7, #8) |
| 7 | `.../presentation/bloc/delivery_state.dart` | **MODIFY** — Add 3 new states | — | Yes (with #6, #8) |
| 8 | `.../data/repositories/delivery_repository.dart` | **MODIFY** — Add getDeliveriesByUser | — | Yes (with #6, #7) |
| 9 | `.../presentation/bloc/delivery_bloc.dart` | **MODIFY** — Add 3 new handlers | #6, #7, #8 | No |
| 10 | `.../presentation/widgets/delivery_status_indicator.dart` | **NEW** — Status icon widget | — | Yes (with #11, #12) |
| 11 | `.../presentation/widgets/delivery_card.dart` | **NEW** — List item card | #10 | Yes (with #12) |
| 12 | `.../presentation/widgets/photo_evidence_widget.dart` | **NEW** — Photo thumbnail widget | — | Yes (with #10, #11) |
| 13 | `.../presentation/screens/delivery_list_screen.dart` | **NEW** — Browse deliveries | #9, #11 | Yes (with #14) |
| 14 | `.../presentation/screens/delivery_detail_screen.dart` | **NEW** — Delivery detail view | #9, #10, #12 | Yes (with #13) |
| 15 | `.../presentation/screens/qr_generate_screen.dart` | **NEW** — QR display | #3 | Yes (with #16, #17) |
| 16 | `.../presentation/screens/qr_scan_screen.dart` | **NEW** — QR scan + GPS + photo | #3, #4, #5 | Yes (with #15, #17) |
| 17 | `.../presentation/screens/dispute_screen.dart` | **NEW** — Dispute form | #9 | Yes (with #15, #16) |
| 18 | `mobile/lib/shared/l10n/app_en.arb` | **MODIFY** — Add ~70 English strings | #13-#17 (all screens) | Yes (with #19, #20) |
| 19 | `mobile/lib/shared/l10n/app_am.arb` | **MODIFY** — Add ~70 Amharic strings | #18 | Yes (with #20) |
| 20 | `mobile/lib/shared/l10n/app_om.arb` | **MODIFY** — Add ~70 Oromo strings | #18 | Yes (with #19) |
| 21 | Navigation wiring (app routing file) | **MODIFY** — Add delivery routes | #13-#17 | No |

### Parallelization Opportunities

**Wave 1** (independent): #1, #2
**Wave 2** (core services, independent of each other): #3, #4, #5
**Wave 3** (BLoC/repo updates, independent of each other): #6, #7, #8
**Wave 4** (BLoC integration): #9
**Wave 5** (widgets, independent): #10, #11, #12
**Wave 6** (screens, partially independent): #13, #14, #15, #16, #17
**Wave 7** (localization, independent): #18, #19, #20
**Wave 8** (navigation): #21

---

## 11. Task-to-File Mapping (T1-T12)

Maps TASK.md tasks to implementation files:

| Task | Files | Wave |
|------|-------|------|
| **T1**: QrVerificationService | #3 (qr_verification_service.dart) | 2 |
| **T2**: GpsCaptureService | #4 (gps_capture_service.dart) | 2 |
| **T3**: PhotoManager | #5 (photo_manager.dart) | 2 |
| **T4**: DeliveryListScreen | #6, #7, #8, #9, #10, #11, #13 (events, states, repo, bloc, widgets, screen) | 3-6 |
| **T5**: DeliveryDetailScreen | #10, #12, #14 (status indicator, photo widget, screen) | 5-6 |
| **T6**: QrGenerateScreen | #15 (qr_generate_screen.dart) | 6 |
| **T7**: QrScanScreen | #16 (qr_scan_screen.dart) | 6 |
| **T8**: DisputeScreen | #17 (dispute_screen.dart) | 6 |
| **T9**: Localization | #18, #19, #20 (.arb files) | 7 |
| **T10**: Navigation wiring | #21 (app routing) | 8 |
| **T11**: Integration tests | Test files (separate from this plan) | 9 |
| **T12**: Photo upload endpoint | Server-side (already exists per TASK.md) | N/A |

### Recommended Agent Assignment

For team-based implementation with parallel agents:

| Agent | Tasks | Rationale |
|-------|-------|-----------|
| **Agent A** (Core Services) | T1, T2, T3 | Independent services, can be built in parallel |
| **Agent B** (BLoC + Screens) | T4, T5, T8, BLoC/repo updates | BLoC modifications + list/detail/dispute screens |
| **Agent C** (QR Screens) | T6, T7 | QR generate + scan screens depend on T1 |
| **Agent D** (Integration) | T9, T10 | Localization + navigation wiring (after screens exist) |

---

## 12. Testing Approach

### Unit Tests (per service)

| Service | Test Cases | Count |
|---------|-----------|-------|
| QrVerificationService | Generate token, verify valid, reject tampered, reject malformed, constant-time comparison, empty delivery, truncated base64 | 7 |
| GpsCaptureService | Capture success, permission denied, service disabled, timeout, Ethiopian bounds valid, bounds invalid, haversine calculation, proximity check | 8 |
| PhotoManager | Capture success, compress to WebP, under 200KB, fallback quality, local storage, cleanup after upload, max photos check | 7 |
| DeliveryRepository | getDeliveriesByUser (all roles), getDeliveriesByUser (single role), status filter, empty results | 4 |
| DeliveryBloc | New events (LoadUserDeliveries, LoadDeliveryDetail, FilterUserDeliveries) | 3 |

### Widget Tests (per screen)

| Screen | Test Cases | Count |
|--------|-----------|-------|
| DeliveryListScreen | Renders list, empty state, loading state, error state, role filter, status filter | 6 |
| DeliveryDetailScreen | Renders all sections, action buttons per role/status, timeline display | 4 |
| QrGenerateScreen | Renders QR code, regenerate button, delivery info | 3 |
| QrScanScreen | Camera preview, verification success/failure UI, GPS display, photo capture | 4 |
| DisputeScreen | Form validation, 48h window check, submit, expired state | 4 |
| Shared widgets | DeliveryCard, StatusIndicator, PhotoEvidence | 3 |

### Integration Tests

| Flow | Description |
|------|-------------|
| Offline QR | Generate QR → scan → verify → confirm pickup (no network) |
| GPS capture | Capture position → validate bounds → check proximity |
| Photo pipeline | Capture → compress → store → verify size |
| Status transitions | pending → picked_up → in_transit → delivered |
| Dispute window | Deliver → dispute within 48h → dispute after 48h (rejected) |
| Sync flow | Create delivery offline → sync → verify server state |

---

## Appendix A: Verification Type Enum

```dart
/// Determines QrScanScreen behavior for pickup vs delivery verification.
enum VerificationType {
  /// Trucker scanning farmer's QR at pickup.
  /// Triggers: ConfirmPickup event with gpsPickup.
  pickup,

  /// Buyer scanning trucker's QR at delivery.
  /// Triggers: ConfirmDelivery event with gpsDropoff.
  delivery,
}
```

---

## Appendix B: Constant Definitions

```dart
/// Delivery verification constants.
abstract final class DeliveryConstants {
  /// Maximum QR token size in bytes.
  static const int maxQrTokenBytes = 500;

  /// GPS proximity threshold in km.
  static const double gpsProximityThresholdKm = 5.0;

  /// GPS capture timeout.
  static const Duration gpsCaptureTimeout = Duration(seconds: 15);

  /// Ethiopian latitude bounds.
  static const double minLatitude = 3.0;
  static const double maxLatitude = 15.0;

  /// Ethiopian longitude bounds.
  static const double minLongitude = 33.0;
  static const double maxLongitude = 48.0;

  /// Photo max file size in bytes (200KB).
  static const int maxPhotoSizeBytes = 200 * 1024;

  /// Photo max width in pixels.
  static const int maxPhotoWidth = 800;

  /// Photo WebP compression quality (0-100).
  static const int photoQuality = 75;

  /// Photo fallback quality if initial compression exceeds size limit.
  static const int photoFallbackQuality = 60;

  /// Dispute window duration from delivery confirmation.
  static const Duration disputeWindow = Duration(hours: 48);

  /// Maximum dispute reason length.
  static const int maxDisputeReasonLength = 500;

  /// QR error correction level.
  /// M = 15% error tolerance (good balance of size vs readability).
  static const int qrErrorCorrectionLevel = 1; // QrErrorCorrectLevel.M
}
```

---

---

## 13. MVP Security Limitation: QR Nonce Server Validation (F12-CR-01)

**Finding**: F12-CR-01 identified that the nonce generated during QR token creation is not synced to the server, making server-side nonce cross-validation impossible. The `qrToken` field was also being synced to all participants via `AllowedDeliveryFields`.

**MVP Decision (Option B)**: Rather than implementing the full nonce sync flow (Option A), the following targeted fixes were applied:

1. **Removed `qrToken` from `AllowedDeliveryFields`** in `SyncFunction.cs` — the raw QR token is no longer synced to other participants, eliminating the information leakage.
2. **Stored nonce in widget state** in `qr_generate_screen.dart` — the nonce is captured (not discarded) for potential future use.
3. **Updated documentation** in `qr_verification_service.dart` — removed the aspirational claim that "server cross-validates nonces at sync time" and documented the actual MVP behavior.

**Current Security Model**: Offline QR verification is a **physical proximity ritual** — the scanner must physically see the generator's screen to obtain the nonce. This prevents trivial remote forgery. The nonce adds entropy that is not available in synced data.

**Upgrade Path**: When the photo upload endpoint (T12) provides guaranteed server connectivity, implement full nonce flow:
1. Store nonce locally on farmer device
2. Include nonce in both farmer's and trucker's sync operations
3. Server cross-validates nonce match at sync time
4. This provides cryptographic proof of physical co-presence

**Risk Assessment**: LOW for MVP. The primary attack (a trucker forging QR verification without physically meeting the farmer) requires:
- Knowledge of the HMAC construction (security through obscurity is not relied upon)
- Access to all delivery metadata (available locally)
- Ability to choose a valid nonce and compute HMAC (possible but requires technical sophistication)
- The attack is detectable via GPS evidence mismatch and photo evidence

---

**Last Updated**: 2026-02-19
**Author**: Architect Agent
**Status**: Approved — Security Fixes Applied (F12-CR-01, F12-CR-02, F12-CR-03)
