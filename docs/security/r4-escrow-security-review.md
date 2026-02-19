# R4 Escrow Security Review

**Date**: 2026-02-19
**Reviewer**: server-repos agent
**Scope**: All escrow implementation files (Services, Functions, Data, Models)
**Status**: Review complete

---

## MUST FIX (Critical / High)

### M1. Idempotency Store lacks conditional write — double-charge risk

**File**: `server/src/Data/EscrowIdempotencyRepository.cs:63-69`
**Severity**: Critical

`StoreAsync` uses `PutItemRequest` without a `ConditionExpression`. If two concurrent requests arrive with the same idempotency key:
1. Request A checks idempotency → not found
2. Request B checks idempotency → not found
3. Request A stores idempotency key with status "processing"
4. Request B overwrites with its own "processing" record
5. Both proceed to call Telebirr HoldFunds → **double charge**

**Fix**: Add `ConditionExpression = "attribute_not_exists(idempotencyKey)"` to the `PutItemRequest` and catch `ConditionalCheckFailedException` to return the existing record instead of overwriting.

```csharp
var request = new PutItemRequest
{
    TableName = _tableName,
    Item = item,
    ConditionExpression = "attribute_not_exists(idempotencyKey)"
};
```

---

### M2. Release and Refund endpoints have no authorization check

**File**: `server/src/Services/EscrowService.cs:218-321` (ReleaseAsync), `472-561` (RefundAsync)
**Severity**: High

`ReleaseAsync` and `RefundAsync` accept any `authenticatedUserId` and use it only for audit logging. There is no check that the caller is authorized (buyer, farmer, or admin) to perform the operation. Any authenticated user can release or refund any escrow by knowing the escrowId.

Compare with `InitiateAsync` (line 67-71) which validates `buyerId == authenticatedUserId`, and `DisputeAsync` (line 355-371) which validates `disputedBy` matches JWT and is a party to the escrow.

**Fix**: Add authorization checks to both methods. Suggested approach:
- Release: only allow the buyer (delivery confirmed), admin, or system (cron).
- Refund: only allow admin or system (cron).
- Pass a `role` or `isSystemCall` flag from the cron function to bypass JWT checks for automated operations.

---

### M3. Repository UpdateStatusAsync has no optimistic concurrency — race condition

**File**: `server/src/Data/EscrowRepository.cs:62-116`
**Severity**: High

`UpdateStatusAsync` does not include a `ConditionExpression` to validate the current status or version before updating. This creates a TOCTOU (time-of-check-time-of-use) race:
1. Request A reads escrow with status "escrowed", version 2
2. Request B reads escrow with status "escrowed", version 2
3. Request A updates status to "released", version 3
4. Request B updates status to "refunded", version 3 — **overwrites release**

This could result in funds being both released to farmer AND refunded to buyer.

**Fix**: Add a `ConditionExpression` to verify the expected status and/or version:

```csharp
request.ConditionExpression = "#currentStatus = :expectedStatus";
request.ExpressionAttributeNames["#currentStatus"] = "status";
request.ExpressionAttributeValues[":expectedStatus"] = new AttributeValue { S = expectedCurrentStatus };
```

Pass the expected current status from EscrowService into the update call. Catch `ConditionalCheckFailedException` to handle conflicts.

---

### M4. Fayda ID verification threshold defined but never enforced

**File**: `server/src/Services/EscrowService.cs:26`
**Severity**: High

`FaydaThreshold = 10_000.0m` is defined but never referenced in any method. The escrow-protocol.md requires that transactions above 10,000 ETB verify both buyer and farmer Fayda IDs. Currently:
- `buyerFaydaVerified` and `farmerFaydaVerified` are hardcoded to `false` at creation (line 145-146)
- No conditional check like `if (amount > FaydaThreshold && !buyer.FaydaVerified)` exists

**Fix**: Before creating the escrow record in `InitiateAsync`, if `request.Amount > FaydaThreshold`, look up both buyer and farmer user profiles and verify their Fayda status. Return `ESCROW_007` if unverified. This requires access to `IUserRepository` or the user profile service.

---

## SHOULD FIX (Medium)

### S1. Missing audit entries for Telebirr gateway failures

**Files**:
- `server/src/Services/EscrowService.cs:208-212` (InitiateAsync hold failure)
- `server/src/Services/EscrowService.cs:274-279` (ReleaseAsync release failure)
- `server/src/Services/EscrowService.cs:526-531` (RefundAsync refund failure)

When Telebirr calls fail, the idempotency record is updated to "failed" but no audit entry is appended. This creates gaps in the audit trail for financial operations — if an investigation occurs, there is no record of attempted-but-failed Telebirr interactions.

**Fix**: Append an audit entry with action "hold_failed" / "release_failed" / "refund_failed" before returning the failure result. Include the Telebirr error code in the audit metadata.

---

### S2. Dispute description and evidence refs not validated server-side

**File**: `server/src/Services/EscrowService.cs:326-467` (DisputeAsync)
**Severity**: Medium

The `DisputeEscrowRequest` DTO documents "0-1000 chars" for description and "max 5" for evidence refs, but EscrowService does not enforce these limits:
- `request.Description` is stored directly without length validation
- `request.EvidenceRefs` has no count limit check (could pass 100+ refs)
- No format validation on evidence refs (should be S3 key patterns)

A malicious user could:
- Submit a multi-megabyte description string, causing DynamoDB write throttling
- Submit thousands of evidence refs in a single comma-joined string

**Fix**: Add validation before the idempotency store:
```csharp
if (request.Description?.Length > 1000)
    return Failure(400, "ESCROW_017", "Description must be 1000 characters or fewer.", "description");
if (request.EvidenceRefs?.Count > 5)
    return Failure(400, "ESCROW_017", "Maximum 5 evidence references allowed.", "evidenceRefs");
```

---

### S3. Number formatting in ToAttributeValue may be locale-dependent

**File**: `server/src/Data/CropRepository.cs:246-248`
**Severity**: Medium

`ToAttributeValue` converts numbers using `.ToString()` without `CultureInfo.InvariantCulture`. If the Lambda runtime thread culture uses a comma decimal separator (e.g., `de-DE`), the value `1234.56` would be stored as `"1234,56"` in DynamoDB, which would fail on read.

While AWS Lambda typically runs with invariant culture, this is not guaranteed.

**Fix**: Use `ToString(CultureInfo.InvariantCulture)` for all numeric conversions:
```csharp
int n => new AttributeValue { N = n.ToString(CultureInfo.InvariantCulture) },
long n => new AttributeValue { N = n.ToString(CultureInfo.InvariantCulture) },
double d => new AttributeValue { N = d.ToString(CultureInfo.InvariantCulture) },
float f => new AttributeValue { N = f.ToString(CultureInfo.InvariantCulture) },
```

---

### S4. Exception messages in logs may leak PII

**File**: `server/src/Functions/EscrowFunction.cs:124,172,220,268,320,356`
**Severity**: Medium

All catch blocks in EscrowFunction log `ex.Message` directly to CloudWatch:
```csharp
context.Logger.LogError($"Error initiating escrow: {ex.Message}");
```

If downstream services (future real Telebirr gateway) include phone numbers, Fayda IDs, or account details in exception messages, these would be written to CloudWatch logs in plaintext.

**Fix**: Log only the exception type and a sanitized message. Store the full exception in a structured field that can be filtered by log retention policies:
```csharp
context.Logger.LogError($"Error initiating escrow: {ex.GetType().Name}");
```

---

### S5. EscrowCronFunction action parsing accepts arbitrary strings

**File**: `server/src/Functions/EscrowCronFunction.cs:87-92`
**Severity**: Medium

If the body JSON deserialization fails, the code falls back to treating the raw body as a plain string action:
```csharp
catch
{
    action = request.Body.Trim().Trim('"');
}
```

This is overly permissive. A malformed payload could inject unexpected action strings. The fallback should either be removed or should only accept known action values.

**Fix**: Remove the catch fallback. If JSON parsing fails, return a 400 error:
```csharp
catch (JsonException)
{
    return LambdaResponse.Error(400, "ESCROW_CRON_VALIDATION", "Invalid JSON in request body.");
}
```

---

## CONSIDER (Low)

### C1. ValidTransitions map is defined but never used

**File**: `server/src/Services/EscrowService.cs:30-36, 672-673`
**Severity**: Low

The `ValidTransitions` dictionary and `IsValidTransition` method exist but are never called. Each operation method does its own inline status validation. While the inline checks are correct, having an unused validation map creates a maintenance risk — if someone adds a new state, they might update the map but not the inline checks, or vice versa.

**Consider**: Refactor to use `IsValidTransition` in each method instead of inline status checks, creating a single source of truth for valid transitions.

---

### C2. Cron function creates AmazonDynamoDBClient per invocation

**File**: `server/src/Functions/EscrowCronFunction.cs:45`
**Severity**: Low

The default constructor creates a new `AmazonDynamoDBClient()` instance. In Lambda, the constructor runs once per cold start and is reused across warm invocations, so this is acceptable. However, if the function is ever instantiated multiple times (e.g., in tests without DI), it would create multiple HTTP clients.

**Consider**: No action needed for current architecture. Note for future refactoring if DI is introduced.

---

### C3. Audit repository has no size limit on GetAuditTrailAsync

**File**: `server/src/Data/EscrowAuditRepository.cs:50-79`
**Severity**: Low

`GetAuditTrailAsync` paginates through ALL audit entries for an escrow with no limit. A heavily-disputed escrow could accumulate hundreds of audit entries. While this is unlikely in practice, unbounded reads could cause Lambda timeouts.

**Consider**: Add an optional `limit` parameter (e.g., default 1000) to prevent runaway reads.

---

### C4. Escrow ID format is predictable

**File**: `server/src/Services/EscrowService.cs:129`
**Severity**: Low

Escrow IDs use `$"escrow-{Guid.NewGuid():N}"` format. While GUIDs are sufficiently random, the `escrow-` prefix makes the format predictable. In a DynamoDB NoSQL context this is acceptable since the primary security boundary is the authorization check, not ID obscurity.

**Consider**: No action needed. Authorization checks (M2 fix) are the correct defense.

---

## PASS (Items that look good)

| # | Check | Assessment |
|---|-------|------------|
| P1 | **Double-charge: idempotency check ordering** | Idempotency key is checked BEFORE Telebirr call in all three operations (Initiate, Release, Refund). Correct sequence. (Conditional write M1 still needed.) |
| P2 | **JWT validation on all endpoints** | All 6 EscrowFunction handlers call `IsAuthenticated()` before processing. Auth extraction uses API Gateway authorizer claims. |
| P3 | **Self-trading prevention** | `InitiateAsync` explicitly checks `buyerId != farmerId` (line 115-121). Returns ESCROW_001. |
| P4 | **Amount range validation** | Server validates 100-500,000 ETB (line 90-96). Commission computed server-side, not from client input. |
| P5 | **Stale escrow handling** | Auto-refund queries expired escrows (expiresAt < now). Reconciliation handles stuck "initiated" items older than 30 min. |
| P6 | **Audit trail coverage** | Every successful state transition writes an audit entry: initiate, hold_success, release, complete, dispute, refund, reconcile_confirm, reconcile_cancel. |
| P7 | **Currency validation** | Only "ETB" accepted (line 99-103). Hard-coded server-side. |
| P8 | **DI-friendly constructors** | Both EscrowFunction and EscrowCronFunction have test constructors accepting mocked dependencies. |
| P9 | **Escrow create idempotency** | `EscrowRepository.CreateAsync` uses `ConditionExpression = "attribute_not_exists(escrowId)"` preventing duplicate escrow records. |
| P10 | **Audit repository is append-only** | `EscrowAuditRepository` only implements `AppendAsync` (PutItem) and `GetAuditTrailAsync` (Query). No update or delete methods exist. |
| P11 | **Idempotency TTL** | 30-day TTL on idempotency records via DynamoDB TTL (`expiresAt` as Unix epoch). Prevents unbounded table growth. |
| P12 | **Dispute reason validation** | `DisputeAsync` validates against a fixed set of 6 enum values (line 397-403). Rejects unknown reasons. |
| P13 | **Dispute window enforcement** | Validates that dispute is within 48 hours of delivery confirmation (line 384-394). |
| P14 | **Duplicate dispute prevention** | Checks `disputeId` already exists on escrow (line 406-410). Returns ESCROW_020. |
| P15 | **AOT compatibility** | No reflection, no Document Model, no EF Core. All marshalling is manual via `FromItem`/`ToAttributeValue`. |

---

## Summary

| Severity | Count | Action Required |
|----------|-------|----------------|
| **MUST FIX** | 4 | M1 (idempotency race), M2 (authz on release/refund), M3 (optimistic concurrency), M4 (Fayda threshold) |
| **SHOULD FIX** | 5 | S1 (audit gaps), S2 (input validation), S3 (locale numbers), S4 (PII in logs), S5 (cron parsing) |
| **CONSIDER** | 4 | C1-C4 (low-risk improvements) |
| **PASS** | 15 | Verified secure |

**Recommendation**: Address all MUST FIX items before any production deployment. SHOULD FIX items should be resolved before beta testing with real Telebirr integration.
