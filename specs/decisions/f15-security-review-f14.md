# F-15 Security Review: F-14 Fraud Detection

## Version
1.0.0

## Status
Complete

## Date
2026-02-23

## Reviewed Document
`specs/decisions/f14-fraud-detection-architecture.md` (v1.0.0, 2026-02-21)

## Context Files Reviewed
- `server/src/Functions/SyncFunction.cs` — sync handler, auth extraction, field whitelist
- `server/src/Functions/EscrowFunction.cs` — escrow handler, auth patterns
- `server/src/Services/EscrowService.cs` — existing security patterns (idempotency, auth checks)
- `server/src/RateLimiting/RateLimitRepository.cs` — atomic counter implementation
- `server/src/RateLimiting/IRateLimitRepository.cs` — rate limit interface

---

## Summary

The F-14 fraud detection design is **well-structured overall**. Server-side enforcement, check ordering by cost, separation of hard/soft checks, and reuse of existing patterns are all sound decisions. However, there is **one HIGH severity issue** (TOCTOU race in velocity limits) that must be addressed before implementation, and several MEDIUM issues that should be addressed.

**Verdict**: Approved with required fixes for Finding S1. Findings S2-S5 are recommended but non-blocking.

---

## Findings

### S1 — TOCTOU Race Condition in Velocity Limits

- **Severity**: HIGH
- **Category**: race-condition
- **Affects**: Rule 3 (Velocity Limits) — both listing and payment velocity

**Description**: The velocity check uses a non-atomic check-then-act pattern. `CheckListingCreationAsync` calls `_rateLimitRepo.GetAsync(velocityKey)` to read the current count, then later (after listing creation succeeds) `RecordListingCreatedAsync` calls `_rateLimitRepo.IncrementAsync()` to bump the counter. Between the read and the increment, concurrent requests can all pass the check.

**Exploit scenario**:
```
Request A                          Request B
─────────                          ─────────
GetAsync(velocity_key) → count=4
                                   GetAsync(velocity_key) → count=4
4 < 5, proceed                     4 < 5, proceed
... create listing ...             ... create listing ...
IncrementAsync → count=5           IncrementAsync → count=6
```

With Lambda concurrency, a farmer (or attacker controlling a farmer account) could send multiple sync batches simultaneously. Each batch's velocity check reads the same counter value before any of them increment. With 5 concurrent requests, the effective limit becomes 5 + N - 1 (where N is concurrent requests).

For payment velocity, the same race exists in `CheckPaymentVelocityAsync` / `RecordPaymentInitiatedAsync` in the EscrowFunction.

**Recommendation**: Replace the two-step check-then-increment with a single atomic conditional increment. Change `CheckListingCreationAsync` to call `IncrementAsync` (which uses DynamoDB `ADD` — already atomic) and check the **returned** count against the limit:

```csharp
// Instead of: GetAsync → check → later IncrementAsync
// Do: IncrementAsync → check returned count
var newCount = await _rateLimitRepo.IncrementAsync(velocityKey, ListingVelocityTtlSeconds);
if (newCount > MaxListingsPerHour)
{
    // Already over limit. Optionally decrement, or just leave it
    // (the TTL will clean it up, and the count being slightly over is harmless).
    return FraudCheckResult.Rejected("FRAUD_VELOCITY_EXCEEDED", ...);
}
```

This eliminates the race: `ADD` is atomic in DynamoDB, and the returned count reflects the true post-increment value. Remove the separate `RecordListingCreatedAsync` / `RecordPaymentInitiatedAsync` methods — they become unnecessary.

**Trade-off**: If the listing creation subsequently fails, the counter is already incremented. This means a failed creation "uses up" a slot. Given the generous limits (5/hour for listings, 10/day for payments) and that creation failures are rare, this is acceptable. Alternatively, decrement on failure, but that adds complexity for minimal benefit.

---

### S2 — cropType Not Normalized (Case Sensitivity / Unicode Bypass)

- **Severity**: MEDIUM
- **Category**: bypass
- **Affects**: Rule 1 (Duplicate Detection), Rule 2 (Price Anomaly)

**Description**: Duplicate detection compares `cropType` with exact string equality (`ct.S != cropType`). The DynamoDB GSI `CropTypeCreatedAtIndex` also uses the raw `cropType` value as the partition key. An attacker can create duplicate listings by varying case ("teff" vs "Teff" vs "TEFF") or inserting zero-width Unicode characters.

Similarly, the price anomaly check queries by exact `cropType`. "teff" and "Teff" would be different partitions, so a listing with cropType "Teff" would not be checked against "teff" price history.

**Exploit scenario**: Farmer creates a listing for cropType "teff" at normal price. Immediately creates another listing for cropType "Teff" at the same price. Duplicate detection sees different cropTypes — bypass achieved.

**Recommendation**: Normalize `cropType` to lowercase (and optionally strip Unicode control characters) at the SyncFunction level, before the fraud check runs. This also ensures the GSI partitions are consistent:

```csharp
var cropType = (operation.Data.GetValueOrDefault("cropType")?.ToString() ?? "")
    .Trim().ToLowerInvariant();
```

If there's already an enum or whitelist of valid crop types in the system, validate against it. This simultaneously fixes the bypass and improves data quality.

---

### S3 — Hour-Bucket Boundary Allows Burst

- **Severity**: MEDIUM
- **Category**: bypass
- **Affects**: Rule 3 (Velocity Limits — listing creation)

**Description**: The listing velocity key uses hour bucketing (`yyyy-MM-ddTHH`). A farmer who times requests at the hour boundary (e.g., 5 listings at 13:59, 5 more at 14:00) can create 10 listings in ~2 minutes, double the intended limit.

**Exploit scenario**: Automated client sends 5 listing-creation sync batches at 13:59:58 UTC, then 5 more at 14:00:02 UTC. Each set falls in a different hour bucket, so both pass the velocity check.

**Recommendation**: Use a sliding-window approach. Instead of bucket-based counting, query the `FarmerIdCreatedAtIndex` GSI (which is already being created for duplicate detection) to count listings created in the past 60 minutes:

```csharp
// Reuse the same GSI query from duplicate detection
var recentListings = await QueryFarmerRecentListingsAsync(farmerId, sinceOneHourAgo);
if (recentListings.Count >= MaxListingsPerHour)
    return FraudCheckResult.Rejected(...);
```

This gives a true rolling window and eliminates the bucket boundary exploit. It also removes the need for a separate rate limit entry for listing velocity (payment velocity still uses the `RateLimitsTable` since there's no equivalent GSI for payments).

**Trade-off**: This adds a GSI query for velocity checking instead of a cheap GetItem. However, the duplicate detection query already hits the same GSI, so the marginal cost is zero if the velocity check reuses the same query result.

---

### S4 — Price Anomaly Statistics Poisoning

- **Severity**: MEDIUM
- **Category**: data-poisoning
- **Affects**: Rule 2 (Price Anomaly Flagging)

**Description**: The price anomaly check uses the 50 most recent listings for a crop type to compute mean and standard deviation. An attacker with multiple farmer accounts could gradually flood listings at a chosen price to shift the statistical baseline, then create a listing at an otherwise-anomalous price that now falls within 3 standard deviations.

**Exploit scenario**: Market price for teff is ~45 ETB/kg. Attacker creates 5 accounts, each posting 5 teff listings/hour at 100 ETB/kg. After 2 hours, the 50-item sample is dominated by 100 ETB/kg listings. Now the attacker's actual listing at 100 ETB/kg passes anomaly detection, while legitimate farmers listing at 45 ETB/kg get flagged.

**Mitigation context**: Velocity limits (5/hour/farmer) slow this attack significantly. Creating fake farmer accounts requires phone number verification (OTP). The price anomaly check is a soft flag (doesn't reject), so the impact is reduced to the flag not firing when it should. In the Ethiopian rural context, this attack requires resources (multiple SIM cards, multiple devices) that make it impractical for most threat actors.

**Recommendation**: For the initial implementation, accept this risk — the velocity limits provide adequate protection. For a future hardening phase, consider:
1. Exclude the requesting farmer's own listings from the statistical sample
2. Use median instead of mean (more resistant to outliers)
3. Weight older samples less (exponential decay)
4. Cap per-farmer contribution to the sample (e.g., max 3 listings per farmer in the 50-item sample)

---

### S5 — GPS Check Only Catches Trivial Spoofing

- **Severity**: LOW
- **Category**: bypass
- **Affects**: Rule 4 (GPS Consistency Check)

**Description**: The GPS check flags when pickup and dropoff are <1km apart for inter-woreda trips, or when coordinates are identical. An attacker who provides coordinates that are >1km apart (e.g., two arbitrary points in different towns) bypasses the check trivially. No server-side validation confirms the coordinates are genuine.

**Mitigation context**: The architecture doc explicitly acknowledges this limitation and defers a full bounding-box check. GPS is optional (many rural users don't have reliable GPS). The check is a soft flag. The existing F-12 5km delivery validation provides a baseline.

**Recommendation**: Acceptable for initial implementation. The check catches the most common/lazy spoofing pattern (identical coordinates or not moving between pickup and dropoff). For a future phase, consider:
1. Validate coordinates fall within Ethiopia's bounding box (3°N-15°N, 33°E-48°E)
2. Cross-reference with the woreda's approximate coordinates when that database becomes available

---

### S6 — Velocity Error Message Reveals Exact Limits

- **Severity**: LOW
- **Category**: info-leak
- **Affects**: Rule 3 error messages

**Description**: The error message `"Maximum 5 listings per hour exceeded."` and `"Maximum 10 payment initiations per day exceeded."` reveal the exact thresholds to the attacker. This allows precise limit-gaming (send exactly 4 listings, then the real one).

**Recommendation**: Use a generic message that doesn't reveal the limit:
```csharp
"Too many listings created recently. Please try again later."
"Too many payment requests. Please try again later."
```

The mobile localization keys already use generic phrasing (`fraudVelocityExceeded` = "Too many requests. Please wait a moment and try again."), so the server-side messages just need to match.

---

### S7 — No GPS Coordinate Range Validation

- **Severity**: LOW
- **Category**: input-validation
- **Affects**: Rule 4 (GPS Consistency Check)

**Description**: The GPS check accepts any `double` values for latitude and longitude without range validation. While the Haversine formula handles any input without crashing, nonsensical values (lat=99999) could produce unexpected distance calculations.

**Recommendation**: Add a guard clause at the top of `CheckGpsConsistency`:
```csharp
if (Math.Abs(pickupLat.Value) > 90 || Math.Abs(dropoffLat.Value) > 90 ||
    Math.Abs(pickupLng.Value) > 180 || Math.Abs(dropoffLng.Value) > 180)
{
    return FraudCheckResult.Flagged("GPS_ANOMALY", "Invalid GPS coordinates.");
}
```

---

### S8 — DynamoDB Query Pagination Not Handled

- **Severity**: LOW
- **Category**: bypass (edge case)
- **Affects**: Rule 1 (Duplicate Detection)

**Description**: `QueryFarmerRecentListingsAsync` does not handle DynamoDB pagination. If the result set exceeds 1MB (approximately thousands of items), only the first page is returned. A prolific farmer with many listings in the past hour could have duplicates missed on page 2+.

**Mitigation context**: The velocity limit (5/hour) means at most 5 active listings per farmer per hour. With the small item size from the `INCLUDE` projection, 1MB accommodates thousands of items. This is not a practical concern.

**Recommendation**: No action needed for initial implementation. The velocity limit makes this edge case impossible in practice.

---

## What's Secure and Well-Designed

1. **Server-side enforcement**: All fraud checks run server-side in Lambda. The mobile client has no way to bypass them — it just receives error codes or flagged responses.

2. **Check ordering by cost**: Velocity (single GetItem, ~5ms) → Duplicate (GSI query, ~15ms) → Price anomaly (GSI query + computation, ~25ms). Cheap checks run first; if they reject, expensive checks are skipped.

3. **Soft vs hard separation**: Flagging (price anomaly, GPS) never blocks operations, which is correct for a low-connectivity marketplace where false positives would damage farmer trust. Hard rejects are reserved for clear-cut cases (duplicates, velocity).

4. **Auth gating**: Fraud checks run after JWT authentication, using the verified `authenticatedUserId`. The `farmerId` in listing data is validated against the authenticated user (SyncFunction line 316-317). No additional auth surface is introduced.

5. **Field whitelist prevents injection**: The `AllowedCropFields` whitelist (line 42-46) runs before fraud checks. `isFlagged` and `flagReason` are not in the whitelist, so clients cannot inject these fields. The fraud service adds them after validation.

6. **No flag details returned to clients**: Price anomaly details (mean, stddev) are logged server-side and stored as metadata, but never returned in error responses to the client. The client only sees the generic `isFlagged: true` if it reads the listing data.

7. **Idempotency prevents replay**: The existing idempotency check at the `ProcessOperationAsync` level (before fraud checks) prevents replay attacks from triggering fraud checks repeatedly.

8. **Existing patterns reused safely**: Rate limit keys use the same `RateLimitsTable` pattern as the sync rate limiter (proven in F-11). DynamoDB queries follow the same low-level API patterns as `CropRepository`. AOT compliance is maintained.

9. **GPS gracefully optional**: Missing GPS data is handled with `SKIP` (return `Allowed()`), which is correct for the Ethiopian rural context where GPS may be unreliable or unavailable.

10. **Minimal GSI projections**: Both new GSIs use `INCLUDE` projection with only the attributes needed for fraud checks, minimizing read costs and data exposure surface.

---

## Required Actions Before Implementation

| # | Finding | Action | Severity |
|---|---------|--------|----------|
| S1 | TOCTOU velocity race | Use atomic increment-then-check instead of get-then-increment | HIGH |
| S2 | cropType normalization | Normalize to lowercase before fraud checks | MEDIUM |
| S3 | Hour-bucket boundary | Consider sliding window via GSI reuse (can defer) | MEDIUM |
| S4 | Price poisoning | Accept risk for v1; document for future hardening | MEDIUM |
| S5 | GPS trivial bypass | Accept risk; acknowledged in architecture doc | LOW |
| S6 | Limit info leak | Use generic error messages without numeric limits | LOW |
| S7 | GPS range validation | Add lat/lng range guard clause | LOW |
| S8 | Query pagination | No action needed (velocity limit prevents) | LOW |

**Blocking**: S1 must be fixed in the implementation.
**Recommended**: S2 and S6 should be incorporated (minimal effort, meaningful improvement).
**Non-blocking**: S3-S5, S7-S8 can be deferred.

---

## Changelog

| Version | Date | Change |
|---------|------|--------|
| 1.0.0 | 2026-02-23 | Initial security review of F-14 fraud detection architecture |

---

**Reviewer**: Security Review Agent
**Last Updated**: 2026-02-23
