# Spike: End-to-End Audit Logging & Observability

**Status:** Spike (Research Complete)
**Date:** 2026-03-05
**Author:** AI-assisted research
**Scope:** `@server` `@infra` `@mobile`

---

## 1. Problem Statement

WeredaConnect handles financial transactions (escrow payments via Telebirr) for Ethiopian farmers, truckers, and buyers. When something goes wrong — a disputed delivery, a stuck payment, a fraud alert — we need to answer: **what happened, when, to whom, and why?**

Today we can partially answer this for escrow operations (append-only `escrow-audit` table), but we lack:
- A way to trace a single transaction from mobile app through API Gateway to Lambda to DynamoDB
- Structured logs that can be searched programmatically
- Dashboards and alerts for real-time operational awareness
- Tamper-evident audit trails for regulatory compliance
- Audit coverage beyond escrow (auth, sync, listings, deliveries)

### Who Benefits

| Stakeholder | Need |
|---|---|
| **Developer (you)** | "Show me everything that happened for transaction X in the last hour" |
| **Support** | "A farmer says their payment is stuck — what's the state?" |
| **Compliance/Sales** | "Prove to NBE/enterprise customers that our audit trail is tamper-proof" |
| **Incident Response** | "Something broke at 3 AM — what was the blast radius?" |

---

## 2. Current State

### What Exists (Good Foundation)

| Component | Status | Location |
|---|---|---|
| Escrow audit table | Append-only DynamoDB, PK=escrowId, SK=timestamp | `database-stack.ts:283-290` |
| Audit entries | Who/what/when/from/to/telebirrRef/metadata | `EscrowAuditRepository.cs` |
| Sync idempotency table | Tracks processed sync operations, 7-day TTL | `database-stack.ts:143-161` |
| Rate limits table | Auth attempt tracking with TTL | `database-stack.ts:74-89` |
| S3 access logging | Upload bucket → access-logs bucket, 365-day retention | `database-stack.ts:381-427` |
| CloudWatch log groups | Per-function, 3-month default, 1-year for escrow | `lambda-construct.ts:37-41` |
| Error codes | Standardized `AUTH_*`, `ESCROW_*`, `SYNC_*` codes | `LambdaResponse.cs` |
| Security headers | HSTS, X-Frame-Options, nosniff on all responses | `LambdaResponse.cs` |
| Mobile logging | Dio interceptor with header masking (debug only) | `logging_interceptor.dart` |
| CORS allows X-Request-Id | Header permitted but not used | `api-stack.ts:653` |

### What's Missing (Gaps)

| Gap | Impact |
|---|---|
| **No correlation IDs** | Cannot trace a request across Lambda invocations |
| **Plain text logs** | Cannot query structured fields in CloudWatch Insights |
| **No dashboards/alarms** | Monitoring stack is a placeholder (`monitoring-stack.ts:27-39`) |
| **No API Gateway logging** | Request/response bodies not captured |
| **No X-Ray/tracing** | No distributed trace visualization |
| **No hash chain on audit** | Audit trail is append-only but not tamper-evident |
| **Audit only on escrow** | Auth, sync, listings, deliveries lack audit entries |
| **No correlation ID in responses** | Mobile app can't reference server-side trace |
| **No custom CloudWatch metrics** | No business metrics (transaction volume, dispute rate) |
| **No log export/archival** | No 10-year retention for NBE compliance |

---

## 3. Proposed Architecture

### 3.1 Correlation ID Flow

Every request gets a unique `correlationId` that flows through the entire stack:

```
┌─────────────┐     ┌──────────────┐     ┌──────────────┐     ┌──────────┐
│ Mobile App  │────▶│ API Gateway  │────▶│   Lambda     │────▶│ DynamoDB │
│             │     │              │     │              │     │          │
│ Generate    │     │ Forward      │     │ Extract from │     │ Store in │
│ X-Corr-Id  │     │ header       │     │ header or    │     │ audit    │
│ + store in  │     │ to Lambda    │     │ fallback to  │     │ entries  │
│ Isar DB     │     │              │     │ AwsRequestId │     │          │
│             │◀────│              │◀────│              │     │          │
│ Log server  │     │              │     │ Return       │     │          │
│ correlId    │     │              │     │ X-Corr-Id    │     │          │
│ locally     │     │              │     │ in response  │     │          │
└─────────────┘     └──────────────┘     └──────────────┘     └──────────┘
                                               │
                                               ▼
                                        ┌──────────────┐
                                        │ CloudWatch   │
                                        │ Structured   │
                                        │ JSON Logs    │
                                        │ {correlId,   │
                                        │  userId,     │
                                        │  action...}  │
                                        └──────────────┘
```

**Mobile generates the ID** (UUID v4) so that even if the request fails at the network level, the mobile app has a reference for support tickets. The server echoes it back and uses it in all logs and audit entries.

### 3.2 Structured Log Format

Replace all plain-text `context.Logger.Log*()` calls with JSON-structured output:

```json
{
  "level": "INFO",
  "timestamp": "2026-03-05T14:32:01.123Z",
  "correlationId": "a1b2c3d4-e5f6-7890-abcd-ef1234567890",
  "requestId": "ZxOE4iRShAcEPhg=",
  "function": "EscrowFunction",
  "action": "initiate",
  "userId": "usr-456",
  "role": "farmer",
  "escrowId": "esc-789",
  "status": "success",
  "durationMs": 234,
  "sourceIp": "196.188.x.x",
  "metadata": { "amount": 5000, "deliveryId": "del-123" }
}
```

**AOT-compatible implementation** — manual JSON construction (no reflection, no Serilog):

```csharp
public static class AuditLogger
{
    public static void Log(ILambdaLogger logger, string level,
        string correlationId, string function, string action,
        string? userId = null, string? entityId = null,
        string? status = null, long? durationMs = null,
        Dictionary<string, object>? metadata = null)
    {
        var sb = new StringBuilder(256);
        sb.Append('{');
        sb.Append($"\"level\":\"{level}\"");
        sb.Append($",\"ts\":\"{DateTime.UtcNow:O}\"");
        sb.Append($",\"cid\":\"{correlationId}\"");
        sb.Append($",\"fn\":\"{function}\"");
        sb.Append($",\"act\":\"{action}\"");
        if (userId != null) sb.Append($",\"uid\":\"{userId}\"");
        if (entityId != null) sb.Append($",\"eid\":\"{entityId}\"");
        if (status != null) sb.Append($",\"st\":\"{status}\"");
        if (durationMs.HasValue) sb.Append($",\"ms\":{durationMs}");
        // metadata fields...
        sb.Append('}');

        if (level == "ERROR") logger.LogError(sb.ToString());
        else logger.LogInformation(sb.ToString());
    }
}
```

**Why not Serilog/PowerTools?** Serilog uses reflection. AWS Lambda Powertools for .NET works with AOT as of v2 but adds ~3MB to deployment size and cold-start latency. At our scale, a lightweight custom logger keeps bundles small and cold starts fast.

### 3.3 Tamper-Evident Hash Chain

Each audit entry includes SHA-256 hashes that create a verifiable chain:

```
Entry 1 (genesis):
  content  = "esc-001|initiated|2026-03-05T10:00:00Z|GENESIS"
  hash     = SHA256(content) = "a3f2..."
  prevHash = null

Entry 2:
  content  = "esc-001|escrowed|2026-03-05T10:00:05Z|a3f2..."
  hash     = SHA256(content) = "b7c1..."
  prevHash = "a3f2..."

Entry 3:
  content  = "esc-001|released|2026-03-05T10:30:00Z|b7c1..."
  hash     = SHA256(content) = "d9e4..."
  prevHash = "b7c1..."
```

If any entry is modified, every subsequent hash breaks. Verification is O(n) — read the full trail and recompute.

**Implementation** (`System.Security.Cryptography.SHA256` works with AOT):

```csharp
public static string ComputeHash(string escrowId, string action,
    string timestamp, string? previousHash)
{
    var input = $"{escrowId}|{action}|{timestamp}|{previousHash ?? "GENESIS"}";
    var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
    return Convert.ToHexStringLower(bytes);
}
```

**New audit entry fields:**

| Field | Type | Purpose |
|---|---|---|
| `entryHash` | String | SHA-256 of this entry's content |
| `previousHash` | String (null for first) | SHA-256 of previous entry |
| `correlationId` | String | Links to CloudWatch logs |
| `sourceIp` | String | Client IP from API Gateway context |
| `userAgent` | String | Device fingerprint |
| `amount` | Number | Financial amount at time of action |
| `reason` | String | Business justification (disputes, refunds) |

### 3.4 Expanded Audit Coverage

Currently only escrow has an audit trail. Extend to all state-changing operations:

| Entity | Auditable Actions | Table |
|---|---|---|
| **Escrow** | initiate, hold, release, dispute, refund, auto-release, auto-refund | `escrow-audit` (exists) |
| **Auth** | register, login, logout, token-refresh, failed-login, account-locked | `auth-audit` (new) |
| **Delivery** | create, update-status, upload-photo, verify-qr, verify-gps | `delivery-audit` (new) |
| **Listing** | create, update, deactivate, fraud-flagged | `listing-audit` (new) |
| **Admin** | view-user, view-escrow, force-release, force-refund | `admin-audit` (new) |

**Cost concern:** Each new table adds ~$0.25/month at low volume (on-demand DynamoDB). Auth-audit will be highest volume. Use TTL aggressively:

| Table | TTL | Archival |
|---|---|---|
| `escrow-audit` | None (permanent) | S3 Glacier after 1 year |
| `auth-audit` | 90 days | S3 Standard-IA |
| `delivery-audit` | 1 year | S3 Glacier after 1 year |
| `listing-audit` | 1 year | S3 Glacier after 1 year |
| `admin-audit` | None (permanent) | S3 Glacier after 1 year |

### 3.5 CloudWatch Dashboard

Replace the placeholder in `monitoring-stack.ts` with real widgets:

```
┌─────────────────────────────────────────────────────────┐
│          WeredaConnect Production Dashboard              │
├──────────────────────┬──────────────────────────────────┤
│ Transaction Volume   │ Error Rate by Function           │
│ (escrows/hour)       │ (4xx, 5xx per function)          │
│ ▁▂▃▅▇█▇▅▃▂▁         │ ▁▁▁▂▁▁▁▁▁▁▃▁▁                   │
├──────────────────────┼──────────────────────────────────┤
│ Cold Start Duration  │ P50/P90/P99 Latency             │
│ (ms, by function)    │ (ms, by endpoint)               │
│ ▃▂▁▁▁▂▃▁▁▁▁         │ P50:120  P90:450  P99:1200      │
├──────────────────────┼──────────────────────────────────┤
│ Fraud Blocks         │ Dispute Rate                     │
│ (per hour)           │ (% of completed escrows)         │
│ ▁▁▁▁▂▁▁▁▁▁▁         │ 2.1% ──────────── threshold: 10%│
├──────────────────────┼──────────────────────────────────┤
│ Auth Activity        │ DynamoDB Throttles               │
│ (login/register/hr)  │ (should be 0)                   │
│ ▂▃▅▇▅▃▂▁▁▁▁         │ 0 ✓                              │
├──────────────────────┴──────────────────────────────────┤
│ Active Alarms:  ✅ All clear                            │
└─────────────────────────────────────────────────────────┘
```

**Alarms:**

| Alarm | Condition | Action |
|---|---|---|
| EscrowErrorRate | >5% 5xx in 5 min | SNS → Email |
| CronFailure | EscrowCron Errors > 0 | SNS → Email |
| DynamoThrottle | ThrottledRequests > 0 for any table | SNS → Email |
| ColdStartSpike | Init duration P99 > 3000ms | SNS → Email |
| HighDisputeRate | >10% disputes in 24h | SNS → Email |
| LoginBruteForce | >50 AUTH_015 lockouts in 1h | SNS → Email |

### 3.6 CloudWatch Logs Insights Saved Queries

Pre-built queries for common debugging scenarios:

**Trace a transaction by correlation ID:**
```sql
fields @timestamp, @message
| filter @message like /"cid":"CORRELATION-ID-HERE"/
| sort @timestamp asc
| limit 200
```

**Find all actions by a user:**
```sql
fields @timestamp, @message
| filter @message like /"uid":"USER-ID-HERE"/
| sort @timestamp desc
| limit 100
```

**Escrow error breakdown:**
```sql
filter @message like /ESCROW_/
| parse @message '"act":"*"' as action
| parse @message '"st":"*"' as status
| stats count() by action, status
```

**Cold start analysis:**
```sql
filter @type = "REPORT"
| filter ispresent(@initDuration)
| stats avg(@initDuration) as avgColdStart,
        max(@initDuration) as maxColdStart,
        count() as coldStarts
  by bin(1h)
```

**Slowest requests:**
```sql
filter @message like /"ms":/
| parse @message '"ms":*}' as duration
| parse @message '"fn":"*"' as function
| sort duration desc
| limit 20
```

**Failed logins by IP (fraud detection):**
```sql
filter @message like /"act":"login_failed"/
| parse @message '"sourceIp":"*"' as ip
| stats count() as attempts by ip
| sort attempts desc
| limit 20
```

### 3.7 Log Archival for NBE Compliance

```
CloudWatch Logs ──(subscription filter)──▶ Kinesis Firehose ──▶ S3 Bucket
                                                                    │
                                                         Lifecycle Rules:
                                                         0-90 days: Standard
                                                         90-365 days: Standard-IA
                                                         1-2 years: Glacier
                                                         2-10 years: Glacier Deep Archive
                                                         10 years: Delete
                                                                    │
                                                         Object Lock: Governance Mode
                                                         (prevents deletion during retention)
```

**Alternative (simpler, cheaper):** Weekly Lambda that calls `CreateExportTask` API to batch-export CloudWatch logs to S3. Less real-time but significantly cheaper for low-volume workloads.

### 3.8 DynamoDB Streams for Real-Time Audit Events

```
EscrowsTable ──(DynamoDB Stream)──▶ EventBridge Pipe ──▶ EventBridge Bus
                                                              │
                                                    ┌─────────┼─────────┐
                                                    ▼         ▼         ▼
                                              Log Archive  Alerts   Metrics
                                              (S3)        (SNS)    (CloudWatch)
```

**Event routing rules:**
- `escrow.disputed` → SNS → Admin email immediately
- `escrow.high_value` (>50,000 ETB) → Enhanced logging + fraud check
- `fraud.velocity_block` → SNS → Ops alert
- All state changes → S3 archive

---

## 4. X-Ray / OpenTelemetry Decision

### Recommendation: Do NOT adopt X-Ray or ADOT now

| Factor | Assessment |
|---|---|
| **X-Ray SDK + .NET AOT** | NOT compatible (confirmed on AWS re:Post) |
| **X-Ray SDK lifecycle** | Maintenance mode since Feb 25, 2026; end-of-support Feb 2027 |
| **ADOT Lambda Layer** | Adds 200-500ms cold start overhead; conflicts with 3G performance targets |
| **OpenTelemetry .NET AOT** | Early maturity; GA announced but Lambda integration still evolving |
| **WeredaConnect scale** | <15 Lambda functions; structured logging + correlation IDs sufficient |

### When to Revisit

- When function count exceeds 20+ and cross-service tracing becomes complex
- When AWS ships native OTel .NET AOT Lambda layer with <50ms overhead
- When customer contracts require formal distributed trace exports

### What to Do Instead

Correlation IDs + structured JSON logs + CloudWatch Logs Insights gives you 90% of X-Ray's value at 0% of the cold-start cost. You can reconstruct any transaction trace with a single Logs Insights query.

---

## 5. Business & Compliance Value

### 5.1 Sales Talking Points

For enterprise customers (NGOs, government agencies, large agribusinesses):

> "Every financial transaction in WeredaConnect has a cryptographically verifiable audit trail. We can reconstruct the complete lifecycle of any payment — from initiation through delivery verification to fund release — within minutes. Our audit logs are tamper-evident using SHA-256 hash chains, and archived for 10 years in compliance with NBE regulations."

### 5.2 Dispute Resolution Workflow

When a farmer disputes a payment:

1. **Support gets correlationId** from the farmer's app (Settings → Transaction History → tap transaction → "Copy Reference ID")
2. **Query CloudWatch** with the correlationId to see the full server-side timeline
3. **Query escrow-audit table** by escrowId to see state machine transitions
4. **Check delivery photos** via S3 presigned URLs (proof of delivery)
5. **Verify hash chain** to prove audit trail is unmodified
6. **Resolution**: evidence-based decision with full paper trail

### 5.3 Ethiopian Regulatory Requirements (NBE)

| NBE Requirement | How We Meet It |
|---|---|
| 6-month security audits | CloudTrail + audit tables provide evidence |
| Monthly reports (volume, fraud, incidents) | CloudWatch metrics + Logs Insights queries |
| 10-year record retention | S3 Glacier Deep Archive with Object Lock |
| 72-hour breach notification | CloudWatch alarms → SNS → email/SMS within minutes |
| Transaction monitoring | FraudDetectionService + audit logging of all checks |
| Data access auditing | S3 access logs + DynamoDB audit tables |
| AML/KYC compliance | Fayda ID verification + velocity limits |

### 5.4 Certifications Enabled

| Certification | Relevance | Audit Logging Role |
|---|---|---|
| **SOC 2 Type II** | Enterprise trust | Immutable logs, access controls, monitoring, 1-year retention |
| **ISO 27001** | International standard | Audit trail, incident response, access reviews |
| **NBE Compliance** | Required for Ethiopian financial services | Full transaction audit, fraud reporting |
| **PCI DSS** | Only if adding card payments (Telebirr = mobile money, not cards) | Requirement 10: comprehensive logging |

---

## 6. Implementation Plan

### Phase 1: Correlation IDs + Structured Logging (1-2 days)

**Effort:** ~8-12 hours | **Impact:** HIGH | **Risk:** LOW

| Task | Scope | Files |
|---|---|---|
| Create `AuditLogger` helper class | `@server` | New: `server/src/Helpers/AuditLogger.cs` |
| Create `CorrelationIdHelper` to extract from headers | `@server` | New: `server/src/Helpers/CorrelationIdHelper.cs` |
| Add `X-Correlation-Id` to `LambdaResponse` headers | `@server` | Edit: `LambdaResponse.cs` |
| Replace plain-text logs in EscrowFunction | `@server` | Edit: `EscrowFunction.cs` |
| Replace plain-text logs in LoginFunction | `@server` | Edit: `LoginFunction.cs` |
| Replace plain-text logs in RegisterFunction | `@server` | Edit: `RegisterFunction.cs` |
| Replace plain-text logs in SyncFunction | `@server` | Edit: `SyncFunction.cs` |
| Replace plain-text logs in EscrowCronFunction | `@server` | Edit: `EscrowCronFunction.cs` |
| Add correlationId generation to mobile HTTP client | `@mobile` | Edit: `api_client.dart` |
| Log server correlationId in mobile Isar records | `@mobile` | Edit: relevant BLoC files |

**Validation:** Deploy to dev → make an escrow request → query CloudWatch Logs Insights with correlationId → confirm full trace visible.

### Phase 2: Tamper-Evident Audit Trail (1 day)

**Effort:** ~4-6 hours | **Impact:** HIGH (compliance) | **Risk:** LOW

| Task | Scope | Files |
|---|---|---|
| Add `ComputeEntryHash` method | `@server` | Edit: `EscrowAuditRepository.cs` |
| Add `entryHash`, `previousHash`, `correlationId`, `sourceIp`, `amount`, `reason` fields to audit entries | `@server` | Edit: `EscrowAuditRepository.cs`, `EscrowService.cs` |
| Fetch previous entry hash on append | `@server` | Edit: `EscrowAuditRepository.cs` |
| Add admin endpoint `GET /admin/escrows/{id}/verify-chain` | `@server` | Edit: `AdminFunction.cs` |
| Add CDK route for verify-chain | `@infra` | Edit: `api-stack.ts` |
| Unit tests for hash chain computation and verification | `@server` | New test file |

**Validation:** Create an escrow → go through full lifecycle → call verify-chain → confirm chain is valid. Manually modify an audit entry in DynamoDB console → call verify-chain → confirm chain break detected.

### Phase 3: CloudWatch Dashboard + Alarms (1 day)

**Effort:** ~4-6 hours | **Impact:** MEDIUM | **Risk:** LOW

| Task | Scope | Files |
|---|---|---|
| Add Lambda error rate metrics to dashboard | `@infra` | Edit: `monitoring-stack.ts` |
| Add cold start duration widget | `@infra` | Edit: `monitoring-stack.ts` |
| Add DynamoDB throttle widget | `@infra` | Edit: `monitoring-stack.ts` |
| Create SNS topic for alerts | `@infra` | Edit: `monitoring-stack.ts` |
| Add EscrowErrorRate alarm | `@infra` | Edit: `monitoring-stack.ts` |
| Add CronFailure alarm | `@infra` | Edit: `monitoring-stack.ts` |
| Add DynamoThrottle alarm | `@infra` | Edit: `monitoring-stack.ts` |
| Save CloudWatch Logs Insights queries (manual, via console) | `@infra` | Documentation |

**Validation:** `cdk synth` → deploy → verify dashboard shows real metrics → trigger a test alarm.

### Phase 4: Expanded Audit Tables (1-2 days)

**Effort:** ~8-12 hours | **Impact:** MEDIUM | **Risk:** LOW

| Task | Scope | Files |
|---|---|---|
| Create `auth-audit` DynamoDB table in CDK | `@infra` | Edit: `database-stack.ts` |
| Create `AuthAuditRepository` | `@server` | New file |
| Add audit logging to LoginFunction, RegisterFunction, LogoutFunction | `@server` | Edit function files |
| Create `delivery-audit` DynamoDB table in CDK | `@infra` | Edit: `database-stack.ts` |
| Create `DeliveryAuditRepository` | `@server` | New file |
| Add audit to delivery state changes | `@server` | Edit: `DeliveryFunction.cs` |
| Add TTL configuration per table | `@infra` | Edit: `database-stack.ts` |

### Phase 5: Log Archival + Compliance (2-3 days)

**Effort:** ~12-16 hours | **Impact:** HIGH (compliance) | **Risk:** MEDIUM

| Task | Scope | Files |
|---|---|---|
| Create S3 audit archive bucket with Object Lock | `@infra` | Edit: `database-stack.ts` |
| Add lifecycle rules (Standard → IA → Glacier → Deep Archive) | `@infra` | Edit: `database-stack.ts` |
| Create weekly Lambda for CloudWatch log export | `@server` `@infra` | New function + CDK |
| Enable DynamoDB Streams on escrow-audit table | `@infra` | Edit: `database-stack.ts` |
| Create EventBridge Pipe for real-time audit archival | `@infra` | New construct |
| Configure CloudTrail data events for escrow tables | `@infra` | Edit: `monitoring-stack.ts` |

---

## 7. Cost Estimate

At current WeredaConnect scale (low traffic, dev/staging):

| Component | Monthly Cost |
|---|---|
| CloudWatch Logs ingestion | ~$1-2 |
| CloudWatch Logs storage (after free tier) | ~$0.50 |
| CloudWatch Dashboard | $3 (per dashboard) |
| CloudWatch Alarms (6 alarms) | $0.60 |
| SNS notifications | ~$0.01 |
| New DynamoDB audit tables (on-demand) | ~$1-2 |
| S3 audit archive | ~$0.10 |
| **Total** | **~$6-9/month** |

At production scale (1,000 transactions/day):

| Component | Monthly Cost |
|---|---|
| CloudWatch Logs | ~$5-10 |
| DynamoDB audit tables | ~$5-10 |
| S3 archival | ~$1-2 |
| Dashboard + Alarms | ~$4 |
| **Total** | **~$15-26/month** |

### Cost Optimization Levers

- Set health check log retention to **7 days** (saves ~30% of log storage)
- Set read-only endpoint logs (GET /crops, /trips) to **30 days**
- Use `LOG_LEVEL=WARN` in production for non-financial functions
- Batch CloudWatch exports weekly rather than real-time streaming

---

## 8. Recommended Log Retention Strategy

| Function Category | CloudWatch | S3 Tier | Total Retention |
|---|---|---|---|
| **Financial** (Escrow, EscrowCron) | 1 year | Glacier Deep Archive | 10 years |
| **Admin** (AdminFunction) | 1 year | Glacier Deep Archive | 10 years |
| **Auth** (Login, Register, Logout) | 90 days | Standard-IA → Glacier | 3 years |
| **Transactional** (Sync, Delivery) | 90 days | Standard-IA → Glacier | 3 years |
| **Read-only** (Crops, Trips, Orders) | 30 days | None | 30 days |
| **Health Check** | 7 days | None | 7 days |
| **Dev/Staging (all)** | 7 days | None | 7 days |

---

## 9. Developer Runbook Template

For each error code, create a runbook following this pattern:

```
## Runbook: ESCROW_012 (Internal Server Error)

### Symptoms
- User sees "Something went wrong" when initiating payment
- CloudWatch shows ESCROW_012 errors

### Investigation Steps

1. Get correlationId from user's app (Settings → Transactions → Copy Reference)

2. Query CloudWatch Logs Insights (select all Lambda log groups):
   fields @timestamp, @message
   | filter @message like /"cid":"<CORRELATION-ID>"/
   | sort @timestamp asc

3. Check escrow audit trail:
   aws dynamodb query \
     --table-name wereda-connect-prod-escrow-audit \
     --key-condition-expression "escrowId = :eid" \
     --expression-attribute-values '{":eid":{"S":"<ESCROW-ID>"}}'

4. Check DynamoDB throttling:
   CloudWatch → DynamoDB → ThrottledRequests metric

### Resolution Tree
- DynamoDB ConditionalCheckFailed → Idempotency conflict, safe to retry
- DynamoDB ProvisionedThroughputExceeded → Scale up table or wait
- Telebirr timeout → Check mock vs. real gateway, manual reconciliation
- Validation error → Bug in server validation, patch and deploy

### Escalation
- If financial impact: notify finance for manual reconciliation
- If data inconsistency: check hash chain, enable point-in-time recovery
```

---

## 10. Decision Record

| Decision | Rationale |
|---|---|
| **Custom AuditLogger over Serilog/PowerTools** | Serilog uses reflection (breaks AOT). PowerTools adds ~3MB + cold start. Custom is <100 lines, zero overhead. |
| **No X-Ray/ADOT** | X-Ray SDK incompatible with .NET AOT. ADOT adds 200-500ms cold start. Correlation IDs give 90% of the value. |
| **SHA-256 hash chain over blockchain** | Simple, AOT-compatible, zero external dependencies. Legally sufficient for Ethiopian courts. |
| **DynamoDB over dedicated audit DB** | Already using DynamoDB. Append-only pattern works well. No new infrastructure. |
| **Weekly batch export over real-time streaming** | Kinesis Firehose adds cost/complexity. Weekly export is sufficient for compliance. |
| **Correlation ID generated by mobile** | Survives network failures. Mobile keeps reference even if request never reaches server. |

---

## 11. Open Questions

1. **NBE data localization:** AWS has no Ethiopian region. Is me-south-1 (Bahrain) acceptable? Need legal opinion.
2. **Audit trail export format:** Should we support CSV/PDF export for regulators, or is DynamoDB query access sufficient?
3. **Real-time alerts channel:** Email only, or also SMS/Telegram for critical alerts?
4. **Hash chain verification frequency:** On every admin view, or scheduled (daily/weekly)?
5. **Auth audit granularity:** Log every token refresh, or only login/logout/failures?

---

## 12. References

- [AWS Lambda Observability Best Practices](https://aws-observability.github.io/observability-best-practices/guides/serverless/aws-native/lambda-based-observability/)
- [X-Ray .NET AOT Incompatibility (AWS re:Post)](https://repost.aws/questions/QU5Jy8IjyLTBi2BTZ7XuJSew/is-x-ray-on-lambda-compatible-with-net-ahead-of-time-compilation)
- [AWS X-Ray SDK End-of-Support (Feb 2026)](https://aws.amazon.com/blogs/mt/announcing-aws-x-ray-sdks-daemon-end-of-support-and-opentelemetry-migration/)
- [Tamper-Evident Audit Trails with Hash Chaining](https://www.sachith.co.uk/audit-trails-and-tamper-evidence-scaling-strategies-practical-guide-feb-22-2026/)
- [EventBridge CDK Audit Service Sample (AWS)](https://github.com/aws-samples/amazon-eventbridge-cdk-audit-service-sample)
- [CloudWatch Logs Insights Query Syntax](https://docs.aws.amazon.com/AmazonCloudWatch/latest/logs/CWL_QuerySyntax.html)
- [National Bank of Ethiopia Directives](https://nbe.gov.et/mandates/directives/)
- [Ethiopia Fintech Licensing Requirements](https://kiyalaw.com/legal-update8.html)

---

**Next Step:** Review this spike, resolve open questions, then start Phase 1 (Correlation IDs + Structured Logging).
