# F-12 Delivery Verification — Security Review of Implementation Plan

## Version
1.0.0 | 2026-02-18

## Reviewer
security-engineer (team: f12-delivery-verification)

## Scope
Security review of the **F-12 Implementation Plan** (`specs/decisions/f12-implementation-plan.md` v1.0.0) covering:
- QR HMAC-SHA256 verification design (§5.1)
- GPS capture service design (§5.2)
- Photo manager design (§5.3)
- BLoC/repository updates (§6)
- Presentation layer security (§7)
- Sync integration (§8.3)
- Security mitigations table (§9)

Cross-referenced against:
- `specs/contracts/delivery-verification-api.md` v1.0.0 (API contract)
- `specs/decisions/r4-2-delivery-verification-architecture.md` v1.0.0 (architecture)
- `server/src/Functions/SyncFunction.cs` (existing server code, lines 1358-1927)
- `server/src/Functions/DeliveryFunction.cs` (existing server code)
- `mobile/lib/core/database/schemas/delivery.dart` (Isar schema)

## Verdict

**APPROVED WITH CONDITIONS**

Two MUST FIX findings block safe implementation. Seven SHOULD FIX items should be addressed during implementation. Five NICE TO HAVE items are noted for future hardening.

---

## Summary of Controls Verified

| Control | Status | Notes |
|---------|--------|-------|
| HMAC-SHA256 token generation | PARTIAL | Secret derivation is deterministic and reconstructable by all synced participants — see F12-SEC-01 |
| QR token size limit (500 bytes) | PASS | Enforced in plan (§5.1) and server `ValidateDeliveryFields` |
| GPS coordinate range validation | PASS | Server validates -90/90 lat, -180/180 lng, NaN/Infinity checks |
| Ethiopian bounds check (3-15°N, 33-48°E) | PASS | Client-side check in GpsCaptureService (§5.2) |
| GPS graceful degradation | PASS | Four failure modes handled with descriptive reasons |
| Photo WebP compression | PASS | 800px max width, 75% quality, 200KB hard limit |
| Photo local storage isolation | PASS | App-private directory, not accessible to other apps |
| Status transition validation (server) | PASS | `DeliveryTransitions` dictionary enforces valid state machine |
| Role-based authorization (server) | PASS | Trucker for pickup, buyer for delivery, any participant for dispute/cancel |
| Field whitelist (server) | PASS | `AllowedDeliveryFields` rejects unknown fields |
| Optimistic concurrency (server) | PASS | `ConditionExpression = "#v = :expectedVersion"` on updates |
| Soft delete (server) | PASS | `deletedAt` + `status=cancelled` set atomically |
| Participant-only access (DeliveryFunction) | PASS | `IsParticipant()` check on GET /deliveries/{id} |
| Phone number privacy in list view | PASS | `includePhone: false` in GET /deliveries |
| QR token hidden in list view | PASS | `includeQrToken: false` in GET /deliveries |
| Dispute window (48h) client-side | PASS | Plan §7.6 checks `DateTime.now().difference(confirmedAt)` |
| Dispute window (48h) server-side | **FAIL** | Not enforced — see F12-SEC-02 |
| DynamoDB injection prevention | PASS | All queries use parameterized `ExpressionAttributeValues` |
| Input validation (dispute reason) | PARTIAL | Length mismatch between contract and server — see F12-SEC-06 |

---

## Findings

### MUST FIX (Blocks Implementation)

---

### F12-SEC-01 — QR HMAC Secret Derivable by All Synced Participants

| Field | Value |
|-------|-------|
| **ID** | F12-SEC-01 |
| **Severity** | MUST FIX (High) |
| **Affected Section** | §5.1 QrVerificationService, §2.2 of architecture doc |
| **Category** | Cryptographic Design / Authentication Bypass |

**Description:**

The HMAC secret is derived as:
```
secret = SHA256(deliveryId + ":" + tripId + ":" + listingId + ":" + creationTimestamp)
```

All four inputs (`deliveryId`, `tripId`, `listingId`, `createdAt`) are present in the Isar delivery record that is synced to **all participants**. Additionally, the `qrToken` field (a previously-generated valid token) is synced to all participants via the delivery record.

This means any participant who has the delivery in their Isar database can:
1. Derive the HMAC secret from delivery metadata
2. Generate a valid QR token with any timestamp
3. Feed this token to `QrVerificationService.verifyQrToken()` and receive `QrVerificationResult.valid()`

Concretely, a **trucker could confirm pickup without physically meeting the farmer** by:
- Reading the delivery record from Isar
- Generating a QR token programmatically (or using the already-synced `qrToken` field)
- Feeding it to the verification service

This completely bypasses the physical proximity guarantee that QR scanning is meant to provide.

**Risk:** A trucker (or buyer) could fabricate delivery confirmations without actual physical handoff, triggering incorrect escrow releases. In the Ethiopian context where disputes are hard to resolve and legal recourse is limited, this could cause significant financial harm to farmers.

**Recommended Fix:**

Add a **random nonce** to the QR flow that is NOT derivable from synced delivery data:

1. At QR generation time, the farmer's device generates a cryptographically random nonce (32 bytes via `dart:math` `Random.secure()`).
2. The nonce is included in the QR payload: `{did, tid, lid, fid, ts, nonce, hmac}`.
3. The HMAC is computed over all fields INCLUDING the nonce: `HMAC(secret, did|tid|lid|fid|ts|nonce)`.
4. The nonce is **not stored** in the synced delivery record — it exists only in the QR code itself.
5. On the trucker's device, scanning the QR is the ONLY way to obtain the nonce.
6. The trucker's confirm-pickup sync operation must include the nonce.
7. The farmer's device also syncs the nonce (stored locally, separately from the delivery record).
8. The server cross-validates: the nonce from the trucker's confirmation must match the nonce the farmer generated.

**Offline behavior:** Offline, the QR scan remains a UI ritual enforcing physical proximity (the trucker must physically scan the farmer's screen to obtain the nonce). Online, the server provides cryptographic proof by cross-checking nonces from both parties.

**Alternative (simpler):** If the nonce approach is too complex for MVP, at minimum:
- Do NOT sync the `qrToken` field to other participants' devices
- Remove the HMAC secret derivation from shared data (use a local random secret stored only on the generator's device)
- Accept that offline verification is a UI ritual, not a cryptographic guarantee, and document this explicitly in §9

---

### F12-SEC-02 — 48-Hour Dispute Window Not Enforced Server-Side

| Field | Value |
|-------|-------|
| **ID** | F12-SEC-02 |
| **Severity** | MUST FIX (Critical) |
| **Affected Section** | §7.6 DisputeScreen (client-side only), `SyncFunction.cs` lines 1610-1622 |
| **Category** | Business Logic / Escrow Security |

**Description:**

The 48-hour dispute window is only enforced on the **mobile client** (§7.6):
```dart
DateTime.now().difference(delivery.confirmedAt!) < Duration(hours: 48)
```

The server's `ProcessDeliveryAsync` allows the `delivered → disputed` status transition without any time check. The relevant server code (SyncFunction.cs line 1610) only validates that the authenticated user is a participant — it does NOT check whether `confirmedAt` is within 48 hours of the current time.

A malicious user could bypass the mobile client and submit a sync operation directly to the server (via POST /sync) with `status: "disputed"` **weeks or months** after delivery, long past the 48-hour window.

**Risk:** Critical. The escrow release mechanism depends on the dispute window. Per the contract: "After 48 hours, the `delivered` status becomes final and escrow is released automatically." If disputes can be raised indefinitely, the escrow system is broken — funds could be frozen long after delivery, causing financial harm to truckers and undermining platform trust.

**Recommended Fix:**

Add a temporal check in `ProcessDeliveryAsync` for the `delivered → disputed` transition:

```csharp
case "disputed":
    // Enforce 48-hour dispute window
    if (existing.TryGetValue("confirmedAt", out var confirmedAtAttr)
        && !string.IsNullOrEmpty(confirmedAtAttr.S)
        && DateTime.TryParse(confirmedAtAttr.S, null,
            DateTimeStyles.RoundtripKind, out var confirmedDate))
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
    // Also verify confirmedAt exists (delivery was actually confirmed)
    else if (currentStatus == "delivered")
    {
        return new SyncOperationResult
        {
            OperationId = operation.OperationId,
            Status = "error",
            Message = "DELIVERY_VALIDATION: confirmedAt is missing for delivered delivery."
        };
    }
    break;
```

This must be added to SyncFunction.cs within the status transition authorization switch block (around line 1610), BEFORE the existing participant check.

---

### SHOULD FIX (Implement If Feasible)

---

### F12-SEC-03 — HMAC Comparison is Not Constant-Time

| Field | Value |
|-------|-------|
| **ID** | F12-SEC-03 |
| **Severity** | SHOULD FIX (Medium) |
| **Affected Section** | §5.1 QrVerificationService, architecture doc §2.3 |
| **Category** | Cryptographic Implementation |

**Description:**

The implementation plan (§5.1) states: "Use constant-time comparison for HMAC to prevent timing attacks." However, the architecture doc (§2.3) shows the actual verification as:

```dart
return computed.toString() == payload['hmac'];
```

This is standard Dart string equality (`==`), which short-circuits on the first mismatched character. This is NOT constant-time.

Dart does not provide a built-in constant-time string comparison function.

**Risk:** Medium. For a purely offline/local comparison, a network-based timing attack is not practical. However, if the verification service is ever called in a server context, or if the device is instrumented, timing differences could leak HMAC bytes. Defense-in-depth recommends constant-time comparison for all HMAC operations.

**Recommended Fix:**

Implement a constant-time byte comparison in the QrVerificationService:

```dart
bool _constantTimeEquals(List<int> a, List<int> b) {
  if (a.length != b.length) return false;
  var result = 0;
  for (var i = 0; i < a.length; i++) {
    result |= a[i] ^ b[i];
  }
  return result == 0;
}
```

Use it as:
```dart
final computedBytes = utf8.encode(computed.toString());
final receivedBytes = utf8.encode(payload['hmac'] as String);
return _constantTimeEquals(computedBytes, receivedBytes);
```

---

### F12-SEC-04 — Phone Number Validation Insufficient in Delivery Fields

| Field | Value |
|-------|-------|
| **ID** | F12-SEC-04 |
| **Severity** | SHOULD FIX (Medium) |
| **Affected Section** | `SyncFunction.cs` `ValidateDeliveryFields()` lines 1322-1332 |
| **Category** | Input Validation |

**Description:**

The `ValidateDeliveryFields` method validates phone numbers with only:
```csharp
if (!phoneObj.ToString()!.StartsWith("+251"))
```

This accepts invalid values like `+251`, `+251abc`, or `+25191234567890000000` (arbitrarily long strings).

The same file already has a proper `EthiopianPhoneRegex()` (line 77: `^\+251\d{9}$`) which is used for trip phone validation but NOT for delivery phone validation.

This is the same issue as F-SEC-02 from the F-11 review and should be fixed consistently.

**Recommended Fix:**

Replace the `StartsWith("+251")` check with the existing regex:
```csharp
if (!EthiopianPhoneRegex().IsMatch(phoneObj.ToString()!))
{
    errors.Add($"{phoneField} must be valid Ethiopian E.164 format (+251XXXXXXXXX).");
}
```

---

### F12-SEC-05 — Server Does Not Validate GPS Proximity for Deliveries

| Field | Value |
|-------|-------|
| **ID** | F12-SEC-05 |
| **Severity** | SHOULD FIX (Medium) |
| **Affected Section** | `SyncFunction.cs` ProcessDeliveryAsync, API contract §GPS Validation |
| **Category** | Business Logic / Anti-Fraud |

**Description:**

The API contract explicitly states:
- "Pickup GPS must be within 5km of listing location"
- "Dropoff GPS must be within 5km of trip destination or buyer location"
- "Server validates proximity to registered locations"

However, the existing `ProcessDeliveryAsync` only validates GPS coordinate ranges (-90/90, -180/180) and NaN/Infinity. It does NOT:
1. Look up the crop listing location to validate pickup proximity
2. Look up the trip destination to validate dropoff proximity
3. Implement or call Haversine distance calculation

The implementation plan (§5.2) implements client-side proximity checking in `GpsCaptureService`, but the server is supposed to be the authoritative validator per the contract.

**Risk:** Without server-side proximity validation, a trucker could submit GPS coordinates from a different woreda entirely, and the server would accept them. Combined with GPS spoofing on cheap phones, this removes a key anti-fraud control.

**Recommended Fix:**

Add a `ValidateGpsProximity` method to the delivery sync handler that:
1. On pickup (status → `pickedUp`): Queries the crop listing by `listingId`, extracts its location, and validates Haversine distance ≤ 5km
2. On delivery (status → `delivered`): Queries the trip by `tripId`, extracts destination woreda center or buyer location, validates Haversine distance ≤ 5km
3. If GPS is not provided, flags the delivery as `requires_review` in metadata (graceful degradation)

The architecture doc (§3.1) already has the Haversine formula in C# for reference.

---

### F12-SEC-06 — Dispute Reason Length Mismatch: Contract Says 500, Server Allows 2000

| Field | Value |
|-------|-------|
| **ID** | F12-SEC-06 |
| **Severity** | SHOULD FIX (Medium) |
| **Affected Section** | §7.6 DisputeScreen, `SyncFunction.cs` lines 1662-1675 |
| **Category** | Input Validation / Contract Compliance |

**Description:**

Three different length limits exist for `disputeReason`:

| Source | Max Length |
|--------|-----------|
| API contract (§Data Models) | 500 chars |
| Implementation plan (§7.6, Appendix B) | 500 chars |
| Server `ValidateDeliveryFields` (line 1338) | 2000 chars |
| Server inline check (line 1666) | 2000 chars |

The server accepts dispute reasons up to 2000 characters, but the contract and implementation plan specify 500 characters.

**Risk:** An attacker could submit dispute reasons up to 4x the expected length, increasing storage costs and potentially degrading rendering performance on low-end devices displaying dispute details.

**Recommended Fix:**

Align server validation with the contract:
```csharp
if (string.IsNullOrWhiteSpace(drText) || drText.Length > 500)
{
    errors.Add("disputeReason must be 1-500 characters.");
}
```

Update both the `ValidateDeliveryFields` method AND the inline check in the update handler.

---

### F12-SEC-07 — Malicious QR Payload Handling Not Specified

| Field | Value |
|-------|-------|
| **ID** | F12-SEC-07 |
| **Severity** | SHOULD FIX (Medium) |
| **Affected Section** | §5.1 QrVerificationService |
| **Category** | Input Validation / Mobile Security (OWASP M1) |

**Description:**

The QR scan flow processes data from an external source (camera-scanned QR code). A malicious QR code could contain:

1. **Oversized payload**: A QR code with >500 bytes of data could cause memory issues on 2GB RAM devices during base64 decoding and JSON parsing.
2. **Malformed JSON**: Deeply nested JSON, extremely long string values, or invalid Unicode sequences could crash the JSON parser.
3. **Non-base64 data**: Data that isn't valid base64url would throw an unhandled exception.
4. **Embedded scripts/URLs**: While unlikely to execute in this context, the extracted string values could contain URLs or script tags that might be rendered unsafely elsewhere.

The plan (§5.1) lists error cases for malformed base64 and missing fields, but doesn't specify:
- A hard byte-length check BEFORE base64 decoding
- Try-catch around JSON parsing
- String length limits on extracted field values

**Risk:** A malicious QR code placed in a market area could crash the app or cause unexpected behavior when scanned by unsuspecting users.

**Recommended Fix:**

Add defensive validation at the top of `verifyQrToken()`:
```dart
QrVerificationResult verifyQrToken(String qrToken, Delivery delivery) {
  // 1. Hard size limit BEFORE any processing
  if (qrToken.length > 700) { // 500 bytes base64-encoded ≈ ~670 chars
    return QrVerificationResult.invalid('QR code too large');
  }

  // 2. Wrap all parsing in try-catch
  try {
    final decoded = utf8.decode(base64Url.decode(base64Url.normalize(qrToken)));
    final payload = jsonDecode(decoded) as Map<String, dynamic>;

    // 3. Validate all extracted strings are bounded
    final did = payload['did']?.toString() ?? '';
    if (did.length > 100) return QrVerificationResult.invalid('Invalid QR data');
    // ... similar for tid, lid, fid

    // 4. Continue with HMAC verification
    // ...
  } on FormatException {
    return QrVerificationResult.invalid('Invalid QR code format');
  } on TypeError {
    return QrVerificationResult.invalid('Malformed QR data');
  } catch (_) {
    return QrVerificationResult.invalid('Could not process QR code');
  }
}
```

---

### F12-SEC-08 — Photo Upload Endpoint Missing Server-Side Content Validation

| Field | Value |
|-------|-------|
| **ID** | F12-SEC-08 |
| **Severity** | SHOULD FIX (Medium) |
| **Affected Section** | §5.3 PhotoManager, contract §4 Upload Photo Evidence |
| **Category** | Input Validation / File Upload Security |

**Description:**

The implementation plan describes the photo upload flow (§5.3) but relies entirely on **client-side** validation:
- Client converts to WebP format
- Client enforces 200KB size limit
- Client enforces 800px max width

The existing `DeliveryFunction.cs` does NOT yet implement the photo upload handler (`POST /deliveries/{id}/photos`). When this is built, the server must NOT trust client claims about file format.

**Risk:** Without server-side validation, an attacker could upload non-WebP files (malware, oversized images, executables) by crafting a multipart request directly.

**Recommended Fix:**

The photo upload handler must validate:
1. **Content-Type header**: Must be `image/webp`
2. **Magic bytes**: WebP files start with `RIFF` (bytes 0-3) and `WEBP` (bytes 8-11). Validate these before storing.
3. **File size**: Reject files > 200KB at the server level (413 response)
4. **Max count**: Check existing photo count for the delivery/type before accepting
5. **Participant authorization**: Only delivery participants can upload
6. **Filename sanitization**: Do not use client-provided filenames; generate server-side keys

---

### F12-SEC-09 — Mobile Repository Lacks Client-Side Validation for Delivery Operations

| Field | Value |
|-------|-------|
| **ID** | F12-SEC-09 |
| **Severity** | SHOULD FIX (Medium) |
| **Affected Section** | §6.4 Repository Updates, existing `delivery_repository.dart` |
| **Category** | Client-Side Validation |

**Description:**

The same pattern identified in F-SEC-04 of the F-11 review applies here. The implementation plan adds `getDeliveriesByUser()` to the repository (§6.4) but the existing repository methods (`confirmPickup`, `confirmDelivery`, `raiseDispute`, `cancelDelivery`) are not shown with client-side validation.

The plan should specify that the repository validates BEFORE enqueuing to the sync queue:
- **confirmPickup**: Validate current status is `pending`, user is trucker
- **confirmDelivery**: Validate current status is `in_transit`, user is buyer
- **raiseDispute**: Validate current status is `delivered`, within 48-hour window, reason is 1-500 chars
- **cancelDelivery**: Validate current status allows cancellation, user is a participant
- **GPS coordinates**: Validate ranges if provided
- **deliveryId format**: Validate UUID format before constructing file paths (prevents path traversal in PhotoManager)

**Risk:** Invalid operations clog the sync queue, waste expensive mobile data bandwidth, and degrade UX (user sees local success but sync fails).

**Recommended Fix:**

Add a `_validateDeliveryOperation()` method to the repository that mirrors server-side checks. Throw a descriptive exception so the BLoC can show localized error messages. This is explicitly required by the contract: "Validate before enqueueing sync operation."

---

### NICE TO HAVE (Future Improvement)

---

### F12-SEC-10 — No GPS Spoofing Detection Heuristics

| Field | Value |
|-------|-------|
| **ID** | F12-SEC-10 |
| **Severity** | NICE TO HAVE (Low) |
| **Affected Section** | §5.2 GpsCaptureService |
| **Category** | Anti-Fraud / Ethiopian Context |

**Description:**

GPS spoofing is trivial on cheap Android 8+ devices using freely available apps. The plan acknowledges "GPS can be spoofed on cheap phones; GPS is evidence, not sole verification" but implements no detection heuristics.

**Risk:** Low for MVP (GPS is one of multiple evidence types). In the future, GPS spoofing could be used systematically to fabricate delivery evidence.

**Recommendation:**

Consider adding lightweight spoofing heuristics in a future iteration:
1. **Accuracy check**: Flag readings with `accuracy < 1.0m` (suspiciously perfect accuracy suggests spoofing)
2. **Mock location API**: Check `Position.isMocked` property from geolocator (detects basic spoofing apps)
3. **Velocity check**: If consecutive GPS readings indicate impossible travel speed (>200 km/h in rural Ethiopia), flag for review
4. **Store accuracy in sync data**: Include the `accuracy` field in the confirm-pickup/delivery sync operation so the server can flag low-accuracy readings

---

### F12-SEC-11 — No Audit Trail for QR Verification Attempts

| Field | Value |
|-------|-------|
| **ID** | F12-SEC-11 |
| **Severity** | NICE TO HAVE (Low) |
| **Affected Section** | §7.5 QrScanScreen |
| **Category** | Monitoring / Fraud Detection |

**Description:**

Failed QR verification attempts are shown to the user but not logged or tracked. This data would be valuable for fraud detection (F-14) — repeated failed scans could indicate someone attempting to forge QR codes.

**Recommendation:**

Store QR scan attempts locally with outcome (success/failure), timestamp, and error reason. Include in periodic analytics sync if the user consents. On the server side, this data feeds into the fraud scoring system (F-14).

---

### F12-SEC-12 — Photo EXIF Stripping Should Be Verified by Test

| Field | Value |
|-------|-------|
| **ID** | F12-SEC-12 |
| **Severity** | NICE TO HAVE (Low) |
| **Affected Section** | §5.3 PhotoManager |
| **Category** | Data Privacy |

**Description:**

The plan states: "EXIF data stripped by `flutter_image_compress` (no location metadata leak)." This is an assumption about the library's behavior during WebP transcoding. Some image compression libraries preserve EXIF data even when changing formats.

**Risk:** If EXIF is not stripped, photos uploaded to S3 could contain embedded GPS coordinates, camera serial numbers, and timestamps that extend beyond the delivery context.

**Recommendation:**

Add a unit test that:
1. Creates a test image with known EXIF data (GPS coords, camera model)
2. Runs it through the PhotoManager compression pipeline
3. Reads the output WebP file and asserts EXIF metadata is absent

If `flutter_image_compress` does NOT strip EXIF, add an explicit EXIF removal step before compression.

---

### F12-SEC-13 — Shared Device Data Isolation Could Be Stronger

| Field | Value |
|-------|-------|
| **ID** | F12-SEC-13 |
| **Severity** | NICE TO HAVE (Low) |
| **Affected Section** | §9 Security Mitigations |
| **Category** | Ethiopian Context / OWASP M2 |

**Description:**

The plan acknowledges shared device scenarios and states: "Each operation requires authenticated user context from BLoC." However, Isar databases are stored in plaintext in the app's data directory. On shared devices:
1. A user who logs in as User A sees all of User A's delivery data in Isar
2. If the app doesn't fully clear Isar on logout, User B could see User A's data
3. On rooted devices or via ADB, the Isar database is accessible

**Recommendation:**

For future hardening:
1. Clear delivery-related Isar data on logout (or at minimum, require re-authentication to view delivery details)
2. Consider encrypting Isar database with a device-bound key (Isar supports encryption)
3. Clear photo evidence files on logout
4. Do NOT store raw QR tokens in Isar longer than necessary

---

### F12-SEC-14 — QR Error Correction Level May Be Insufficient for Ethiopian Conditions

| Field | Value |
|-------|-------|
| **ID** | F12-SEC-14 |
| **Severity** | NICE TO HAVE (Low) |
| **Affected Section** | §7.4 QrGenerateScreen, Appendix B |
| **Category** | Reliability / Ethiopian Context |

**Description:**

The plan uses QR error correction level M (15% error tolerance). In Ethiopian rural conditions:
- Phone screens may be cracked or scratched
- Outdoor lighting (bright sunlight, dust) reduces camera quality
- Camera lenses on $50 phones are low-resolution and often dirty
- Market environments are dusty and chaotic

Level Q (25% error tolerance) would improve scan reliability at the cost of slightly larger QR codes. Given the 500-byte payload limit and 280x280dp display size, Level Q should still be scannable.

**Recommendation:**

Consider increasing to `QrErrorCorrectLevel.Q` (25% tolerance) and testing on target devices. If scan reliability is acceptable with Level M during device testing, this can remain as-is.

---

## Analysis: Existing Server Security Controls

The following server-side controls are already implemented and well-designed:

### DynamoDB Injection — SAFE
All delivery DynamoDB operations in `SyncFunction.cs` use parameterized expressions. User-supplied values flow through `ExpressionAttributeValues`. Field names use indexed aliases (`#f0`, `#f1`) mapped from the `AllowedDeliveryFields` whitelist. No injection vectors found.

### Role-Based Authorization — SOLID
The `ProcessDeliveryAsync` handler correctly enforces:
- Only truckers can create deliveries and confirm pickup
- Only buyers can confirm delivery
- Only participants can dispute or cancel
- Non-status updates require trucker ownership

### Status Machine — CORRECT
The `DeliveryTransitions` dictionary correctly models the lifecycle:
- `pending → pickedUp | cancelled`
- `pickedUp → inTransit | cancelled`
- `inTransit → delivered`
- `delivered → disputed`

Terminal state protection for `cancelled` and `deletedAt` is enforced.

### Optimistic Concurrency — WORKING
Version-based conflict detection uses DynamoDB condition expressions, preventing stale updates.

---

## Findings Summary

| ID | Severity | Finding | Blocks Implementation? |
|----|----------|---------|----------------------|
| F12-SEC-01 | MUST FIX (High) | QR HMAC secret derivable by all synced participants | Yes — implement nonce or document limitation |
| F12-SEC-02 | MUST FIX (Critical) | 48-hour dispute window not enforced server-side | Yes — affects escrow safety |
| F12-SEC-03 | SHOULD FIX | HMAC comparison not constant-time | No |
| F12-SEC-04 | SHOULD FIX | Phone validation uses StartsWith instead of regex | No |
| F12-SEC-05 | SHOULD FIX | No server-side GPS proximity validation | No |
| F12-SEC-06 | SHOULD FIX | disputeReason length mismatch (500 vs 2000) | No |
| F12-SEC-07 | SHOULD FIX | Malicious QR payload handling not specified | No |
| F12-SEC-08 | SHOULD FIX | Photo upload endpoint needs content validation | No |
| F12-SEC-09 | SHOULD FIX | Mobile repository lacks client-side validation | No |
| F12-SEC-10 | NICE TO HAVE | No GPS spoofing detection heuristics | No |
| F12-SEC-11 | NICE TO HAVE | No audit trail for QR verification attempts | No |
| F12-SEC-12 | NICE TO HAVE | EXIF stripping should be verified by test | No |
| F12-SEC-13 | NICE TO HAVE | Shared device data isolation could be stronger | No |
| F12-SEC-14 | NICE TO HAVE | QR error correction level may be insufficient | No |

**MUST FIX: 2** | **SHOULD FIX: 7** | **NICE TO HAVE: 5**

---

## Conditions for Approval

Implementation may proceed **after addressing the 2 MUST FIX items**:

1. **F12-SEC-01** (QR HMAC): Either implement the nonce-based approach, or at minimum:
   - Remove `qrToken` from the synced delivery fields (don't include in `AllowedDeliveryFields`)
   - Add a random component to the HMAC that only travels through the QR scan
   - Document in §9 that offline QR verification is a UI ritual, not a cryptographic guarantee

2. **F12-SEC-02** (Dispute window): Add the 48-hour check to `ProcessDeliveryAsync` in `SyncFunction.cs`. This is a ~15-line server code change.

The following SHOULD FIX items should be addressed during implementation (Tasks #3-#8):

3. **Server (Task #6):** Address F12-SEC-04 (phone regex), F12-SEC-05 (GPS proximity), F12-SEC-06 (dispute length), F12-SEC-08 (photo validation)
4. **Mobile (Task #4):** Address F12-SEC-03 (constant-time comparison), F12-SEC-07 (malicious QR handling), F12-SEC-09 (client-side validation)

---

**Last Updated:** 2026-02-18
**Plan Reference:** `specs/decisions/f12-implementation-plan.md` v1.0.0
**Contract Reference:** `specs/contracts/delivery-verification-api.md` v1.0.0
**Architecture Reference:** `specs/decisions/r4-2-delivery-verification-architecture.md` v1.0.0
