# Security Review: R4 Infrastructure

Date: 2026-02-18
Reviewer: infra-security
Version: 1.0
Status: PASS WITH FINDINGS

## Scope

- **Files reviewed**:
  - `infra/lib/stacks/api-stack.ts` (555 lines)
  - `infra/lib/stacks/database-stack.ts` (368 lines)
  - `mobile/lib/core/sync/sync_queue.dart` (270 lines)
  - `infra/lib/constructs/dynamodb-construct.ts` (33 lines)
  - `infra/lib/constructs/lambda-construct.ts` (88 lines)
  - `specs/contracts/delivery-verification-api.md` (cross-reference)
  - `specs/contracts/trip-listing-api.md` (cross-reference)
- **Lines of code reviewed**: ~1,314 (infrastructure and mobile sync)
- **Commits covered**: Branch `trips-delivery-escrow`, commits `f5d664f` through `3044ff9`

---

## Checklist (from qa/README.md)

### Authentication & Authorization
- [x] JWT tokens validated and not expired -- Cognito JWT authorizer configured with 15-minute access/ID token validity. HttpUserPoolAuthorizer bound to UserPool and client. (api-stack.ts:351-353)
- [x] Authorization checks on all API endpoints -- All mutation endpoints and sensitive read endpoints use `jwtAuthorizer`. Public browse endpoints (/crops, /trips, /health) are intentionally unauthenticated per API contracts. (api-stack.ts:387-517)
- [x] No hardcoded credentials in code -- Cognito pool IDs and table names are passed via CDK references. SSM parameters used for escrow configuration. No secrets in source. (api-stack.ts:87-99, 224-240)

### Data Privacy
- [x] Sensitive data encrypted at rest and in transit -- DynamoDB uses AWS-managed encryption (default). S3 uses S3_MANAGED encryption. API Gateway enforces HTTPS. **Note**: Not using customer-managed KMS keys (see Finding F-03).
- [x] Minimal data collection; user consent obtained -- Phone numbers hidden from unauthenticated users on /trips. Delivery details restricted to participants. GPS coordinates stored only for verification. (Per API contracts)

### Input Validation
- [x] All user input validated (length, type, format) -- Server-side validation rules documented in both API contracts: GPS bounds, capacity limits, woreda name lengths, E.164 phone format, UUID format checks.
- [x] Injection prevention (SQL, XSS) -- DynamoDB is NoSQL (no SQL injection vector). Lambda handlers receive structured JSON via API Gateway. No HTML rendering on server side.
- [x] File upload validation (type, size, content) -- Photo uploads constrained to WebP format, max 200KB, max 800px width, max 3 per type per delivery. (delivery-verification-api.md, Photo Evidence section)

### API Security
- [x] Rate limiting enabled -- API Gateway DefaultRouteSettings: 100 req/sec rate, 200 burst. Rate limits table with TTL for application-level brute-force protection. (api-stack.ts:521-527, database-stack.ts:60-72)
- [x] Idempotency keys for write operations -- Sync protocol uses deterministic idempotency keys. Escrow idempotency table with 30-day TTL prevents double-charges. (sync_queue.dart:70-77, database-stack.ts:249-261)
- [ ] Error messages don't leak sensitive info -- **Cannot verify from infrastructure code alone**. Depends on Lambda handler implementation. API contracts specify generic error codes but actual error messages are in server code (not reviewed here).

### Mobile Security
- [x] No sensitive data in logs -- sync_queue.dart does not log sensitive data. Idempotency keys contain entity type and user ID but not PII.
- [ ] Secure storage for tokens -- **Not in scope of reviewed files**. Token storage is in the auth layer, not sync_queue.dart. Requires separate mobile security review.
- [ ] Obfuscation enabled in release builds -- **Not in scope of reviewed files**. Build configuration not reviewed.

### Infrastructure
- [x] IAM roles follow least privilege -- Each Lambda function is granted only the specific DynamoDB tables it needs. Read-only functions get grantReadData; write functions get grantReadWriteData. Cognito grants are scoped to specific actions. (api-stack.ts:108-314)
- [x] Secrets in AWS Secrets Manager -- Escrow configuration parameters stored in SSM Parameter Store (commission rate, dispute window, escrow timeout). No secrets exposed as environment variables. Cognito pool IDs are not secrets. (api-stack.ts:224-240)
- [ ] CloudWatch alarms for suspicious activity -- **No CloudWatch alarms configured.** No WAF rules. No GuardDuty integration. (See Finding F-06)

---

## Threat Model Cross-Reference

| Finding | Threat ID | Description |
|---------|-----------|-------------|
| F-01 | T7.1 | MFA disabled -- SIM swap attacks unmitigated |
| F-02 | T5.1 | No per-user rate limiting on /sync -- global throttle only |
| F-03 | T4.2 | AWS-managed encryption, not customer-managed KMS |
| F-04 | T4.3 | Isar database not encrypted at rest on device |
| F-05 | T1.1 | Weak password policy -- no uppercase or symbols required |
| F-06 | T5.1, T6.2 | No CloudWatch alarms or WAF for anomaly detection |
| F-07 | T5.1 | CORS wildcard origin -- acceptable for mobile-only, risk if web client added |

---

## Findings

### F-01 [HIGH]: MFA Disabled on Cognito User Pool

- **File**: `infra/lib/stacks/api-stack.ts:52`
- **OWASP**: A07:2021 - Identification and Authentication Failures
- **Description**: Cognito User Pool has `mfa: cognito.Mfa.OFF`. Combined with phone-only authentication and SMS OTP for verification, the system is vulnerable to SIM swap attacks (T7.1). SIM swapping is a well-documented attack vector in Ethiopia, where telecom employees can be socially engineered into transferring phone numbers.
- **Current mitigation**: Phone number verification via OTP at registration. `preventUserExistenceErrors: true` prevents username enumeration. 15-minute access token validity limits session hijacking window.
- **Recommendation**: Enable `Mfa.OPTIONAL` with TOTP (authenticator app) as a second factor. For the Ethiopian context where TOTP adoption may be low, consider device binding as an alternative -- store a device fingerprint at first login and require re-verification when logging in from a new device. This is more practical than mandating MFA for low-literacy rural users.
- **Risk**: DEFERRED -- Acceptable for initial launch given the user population. Must be addressed before escrow handles significant transaction volumes.

---

### F-02 [MEDIUM]: No Per-User Rate Limiting on /sync Endpoint

- **File**: `infra/lib/stacks/api-stack.ts:521-527`
- **OWASP**: A04:2021 - Insecure Design
- **Description**: API Gateway throttling is configured globally at 100 req/sec with 200 burst. There is no per-user or per-IP rate limiting. A single malicious user could consume the entire API quota, denying service to all other users. The /sync endpoint is particularly sensitive because it performs write operations across multiple DynamoDB tables.
- **Current mitigation**: Application-level rate limits table (database-stack.ts:60-72) with TTL exists, suggesting Lambda-level rate limiting is planned or implemented in server code. API Gateway global throttle provides baseline protection.
- **Recommendation**: (1) Configure per-route throttling on /sync with a lower limit (e.g., 10 req/sec per user) using API Gateway usage plans or Lambda-level enforcement via the rate limits table. (2) Add per-IP throttling on public endpoints (/crops, /trips). (3) Consider AWS WAF rate-based rules for IP-level blocking.
- **Risk**: ACCEPTED -- Global throttle at 100 req/sec is adequate for current traffic levels. The rate limits DynamoDB table indicates server-side per-user limiting is in the design. Revisit when user base grows.

---

### F-03 [LOW]: DynamoDB and S3 Use AWS-Managed Encryption (Not Customer-Managed KMS)

- **File**: `infra/lib/constructs/dynamodb-construct.ts:24-31`, `infra/lib/stacks/database-stack.ts:264-305`
- **OWASP**: A02:2021 - Cryptographic Failures
- **Description**: DynamoDB tables use default AWS-managed encryption (AES-256). S3 buckets use `BucketEncryption.S3_MANAGED`. Neither uses customer-managed KMS keys (CMK). This means AWS manages the encryption keys and the customer cannot audit key usage, enforce key rotation schedules, or revoke access at the key level. For the escrow tables containing financial transaction data, this is a concern.
- **Current mitigation**: AWS-managed encryption still provides AES-256 encryption at rest. TLS in transit is enforced by API Gateway HTTPS endpoints. S3 `BlockPublicAccess.BLOCK_ALL` prevents public access.
- **Recommendation**: For production deployment, create a KMS CMK and configure `encryption: dynamodb.TableEncryptionV2.customerManagedKey(key)` on sensitive tables (escrows, escrow-audit, payments, users). For S3, switch uploadsBucket to `BucketEncryption.KMS` with the same CMK. This enables key usage auditing via CloudTrail and allows key revocation in a breach scenario. Estimated cost impact: ~$1/month per CMK + $0.03/10k API calls.
- **Risk**: ACCEPTED -- AWS-managed encryption is sufficient for launch. Upgrade to CMK before handling real financial transactions.

---

### F-04 [HIGH]: Isar Database Not Encrypted at Rest on Mobile Device

- **File**: `mobile/lib/core/sync/sync_queue.dart:1-31`
- **OWASP**: A02:2021 - Cryptographic Failures
- **Description**: The SyncQueue stores all pending sync operations in Isar database without encryption. This includes delivery data, crop listings, GPS coordinates, user IDs, and serialized JSON payloads. On a stolen or rooted device, this data is readable in plaintext. Maps directly to threat T4.3 (Unencrypted Mobile Database).
- **Current mitigation**: None for data at rest. The sync queue does implement bounded size (maxQueueSize=1000) and automatic purging of synced operations after 30 days, which limits the exposure window.
- **Recommendation**: (1) Enable Isar encryption by passing an encryption key to `Isar.open(encryptionKey: key)`. Derive the key from the user's authentication credential using PBKDF2 or store it in the platform keystore (Android Keystore / iOS Keychain). (2) Add root/jailbreak detection to warn users. (3) Implement a data wipe after N failed authentication attempts.
- **Risk**: MUST FIX -- Device theft is a realistic scenario in the target market. Rural Ethiopian users may share devices, and stolen phones are common. This should be addressed before production launch.

---

### F-05 [LOW]: Weak Cognito Password Policy

- **File**: `infra/lib/stacks/api-stack.ts:63-70`
- **OWASP**: A07:2021 - Identification and Authentication Failures
- **Description**: Password policy requires minLength=8, lowercase letters, and digits, but does not require uppercase letters or symbols (`requireUppercase: false`, `requireSymbols: false`). This reduces the entropy of user passwords and makes them more susceptible to brute-force attacks.
- **Current mitigation**: Application-level rate limiting via rate limits table. Cognito native brute-force protection. `preventUserExistenceErrors: true` prevents enumeration.
- **Recommendation**: This is an intentional design decision for the Ethiopian context -- low-literacy users typing on phone keyboards struggle with uppercase and symbol requirements. The 8-character minimum with lowercase + digits is acceptable given the additional rate limiting. Consider increasing minimum length to 10 characters as a compensating control.
- **Risk**: ACCEPTED -- Justified by user population. Rate limiting and account lockout provide compensating controls.

---

### F-06 [MEDIUM]: No CloudWatch Alarms or WAF Configuration

- **File**: `infra/lib/stacks/api-stack.ts` (entire file -- no alarm resources defined)
- **OWASP**: A09:2021 - Security Logging and Monitoring Failures
- **Description**: No CloudWatch alarms are configured for any suspicious activity patterns. No AWS WAF rules are attached to the API Gateway. There is no alerting for: (1) unusual API error rates, (2) authentication failure spikes, (3) DynamoDB throttling events, (4) Lambda error rates, (5) S3 access anomalies. This means a security incident could go undetected for an extended period.
- **Current mitigation**: S3 access logging is enabled on the uploads bucket with a dedicated access logs bucket and 365-day retention (database-stack.ts:263-277). This provides forensic capability after the fact but no real-time alerting.
- **Recommendation**: (1) Add CloudWatch alarms for: 4xx/5xx error rate > 5% on API Gateway, Lambda error count > 10/min, DynamoDB throttled requests > 0, failed Cognito auth attempts > 50/hour. (2) Attach AWS WAF to the HTTP API with rate-based rules (block IPs exceeding 500 req/5min) and managed rule groups (AWSManagedRulesCommonRuleSet). (3) Enable AWS GuardDuty for DynamoDB threat detection. Estimated cost: WAF ~$5/month + $0.60/million requests.
- **Risk**: MUST FIX -- Monitoring is essential for production. At minimum, add error rate alarms and authentication failure alerts before launch.

---

### F-07 [LOW]: CORS Wildcard Origin

- **File**: `infra/lib/stacks/api-stack.ts:341-346`
- **OWASP**: A05:2021 - Security Misconfiguration
- **Description**: CORS is configured with `allowOrigins: ['*']`. For a mobile-only API where the Flutter app communicates directly via HTTP (not browser-based), CORS headers are largely irrelevant because CORS is a browser-enforced mechanism. However, if a web client (admin dashboard, partner portal) is ever added, this wildcard would allow any website to make authenticated cross-origin requests.
- **Current mitigation**: The API is mobile-only. CORS is only relevant to browser-based clients. The Flutter HTTP client does not send Origin headers.
- **Recommendation**: (1) For now, this is acceptable. (2) Add a comment in the code documenting that CORS must be tightened before any web client is deployed. (3) When a web client is added, restrict origins to the specific domain(s). (4) Consider removing `allowOrigins` entirely since mobile clients do not need it.
- **Risk**: ACCEPTED -- No browser client exists. Document the constraint and add to the pre-web-launch checklist.

---

### F-08 [INFO]: userPassword Auth Flow Enabled on Cognito Client

- **File**: `infra/lib/stacks/api-stack.ts:76-79`
- **OWASP**: A07:2021 - Identification and Authentication Failures
- **Description**: The Cognito client has both `userSrp: true` and `userPassword: true` auth flows enabled. The USER_PASSWORD_AUTH flow sends the password in plaintext to the Cognito endpoint (protected by TLS). SRP (Secure Remote Password) is the more secure option because the password never leaves the client, even in encrypted form.
- **Current mitigation**: All communication is over HTTPS/TLS. The password is encrypted in transit.
- **Recommendation**: If USER_PASSWORD_AUTH is only needed for testing or a specific use case, disable it in production and use SRP exclusively. If it is needed (e.g., for simplified auth on very low-end devices), document the justification.
- **Risk**: ACCEPTED -- TLS provides adequate protection. SRP is used by default in the mobile client.

---

## Positive Security Patterns

The following security practices are well-implemented and should be maintained:

1. **Least-privilege IAM grants** (api-stack.ts:108-314): Each Lambda function receives granular permissions. Read-only functions (cropFn, tripFn) get `grantReadData`. The escrow function only gets write access to escrow-related tables, not user or crop tables. Cognito grants are scoped to specific API actions (e.g., loginFn only gets `InitiateAuth`, not `AdminDeleteUser`).

2. **Point-in-time recovery on all DynamoDB tables** (dynamodb-construct.ts:30): The `WeredaDynamoTable` construct enables PITR by default, providing continuous backups for all tables. This is critical for financial data (escrows, payments).

3. **S3 public access fully blocked** (database-stack.ts:267, 283): Both the uploads bucket and access logs bucket have `BlockPublicAccess.BLOCK_ALL`. No accidental public exposure of delivery photos or audit logs.

4. **S3 versioning on uploads bucket** (database-stack.ts:286): Object versioning prevents accidental or malicious deletion of delivery photo evidence.

5. **S3 access logging with dedicated bucket** (database-stack.ts:263-277, 287-288): The uploads bucket logs all access to a dedicated access logs bucket with 365-day retention. This provides an audit trail for photo evidence access.

6. **S3 lifecycle policies** (database-stack.ts:289-304): Temporary files in `temp/` are auto-deleted after 7 days. Photos transition to Glacier after 365 days. This balances cost with evidence retention requirements.

7. **TTL-based cleanup on rate limits, sync metadata, and idempotency tables** (database-stack.ts:69-72, 118-121, 258-261): Automatic expiration of transient data prevents table bloat and reduces storage costs.

8. **Idempotency key design** (sync_queue.dart:70-77): Deterministic idempotency keys based on user+entity+action+timestamp prevent duplicate operations. This is critical for the flaky 3G network context.

9. **Bounded sync queue** (sync_queue.dart:23, 60-66): The 1000-operation limit with clear exception handling prevents unbounded growth and app crashes (mitigates T5.2).

10. **Sync operation purge policy** (sync_queue.dart:29, 210-227): 30-day retention for synced operations limits data exposure on device while retaining enough history for debugging.

11. **Cognito preventUserExistenceErrors** (api-stack.ts:83): Enabled, preventing username enumeration attacks on the registration and login endpoints.

12. **Production retention policies** (dynamodb-construct.ts:29, database-stack.ts:268, 284): Production resources use `RemovalPolicy.RETAIN` to prevent accidental deletion via CDK operations. Dev environments use `DESTROY` for easy cleanup.

13. **Short-lived access tokens** (api-stack.ts:80): 15-minute access/ID token validity limits the window for stolen token abuse. 30-day refresh token validity is reasonable for the mobile context where frequent re-authentication would frustrate low-literacy users.

14. **Escrow audit table design** (database-stack.ts:241-247): Separate immutable append-only audit table for escrow operations. Lambda only gets `grantWriteData` (not read-write), reinforcing append-only semantics.

15. **EventBridge-driven escrow cron** (api-stack.ts:317-334): Escrow auto-release and reconciliation are server-driven on fixed schedules, preventing client manipulation of payment release timing.

---

## Summary

### Overall Risk Assessment: MODERATE

The R4 infrastructure demonstrates a solid security foundation with well-designed IAM permissions, data lifecycle management, and offline-first safeguards. The architecture follows security best practices for a serverless AWS deployment.

### Critical Path Items

| Priority | Finding | Action |
|----------|---------|--------|
| MUST FIX | F-04: Isar database encryption | Enable Isar encryption before production launch |
| MUST FIX | F-06: No CloudWatch alarms/WAF | Add monitoring and alerting before production launch |
| DEFERRED | F-01: MFA disabled | Enable before escrow handles significant volume |
| ACCEPTED | F-02: No per-user rate limiting | Global throttle adequate for current scale |
| ACCEPTED | F-03: AWS-managed encryption | Upgrade to CMK before financial production |
| ACCEPTED | F-05: Weak password policy | Justified by user context |
| ACCEPTED | F-07: CORS wildcard | Tighten before web client deployment |
| ACCEPTED | F-08: userPassword auth flow | Document justification |

### Production Readiness

The infrastructure is **conditionally ready for a limited beta** with the following prerequisites:

1. **Before beta**: Resolve F-06 (add CloudWatch alarms for error rates and auth failures)
2. **Before production**: Resolve F-04 (Isar encryption) and F-06 (full monitoring suite)
3. **Before escrow goes live with real money**: Resolve F-01 (MFA or device binding) and F-03 (KMS CMK for financial tables)

### Open Items

- Lambda handler error message content not reviewed (requires server code audit)
- Mobile token storage mechanism not reviewed (requires mobile auth layer audit)
- Release build obfuscation not verified (requires build configuration audit)
- AWS WAF rule set selection pending (requires traffic pattern analysis post-beta)

---

**Next Review**: After F-04 and F-06 are resolved, or before escrow production launch, whichever comes first.
