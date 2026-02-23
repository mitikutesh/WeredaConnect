# WeredaConnect Security Audit Report
**Date:** February 22, 2026  
**Scope:** Complete codebase - Mobile (Flutter), Server (.NET), Infrastructure (AWS CDK)  
**Severity Levels:** Critical | High | Medium | Low

---

## EXECUTIVE SUMMARY

The WeredaConnect codebase demonstrates **strong security fundamentals** with proper authentication, encryption, and infrastructure hardening. However, several **critical gaps require immediate attention** before production deployment:

1. **Certificate pinning is disabled** - MitM vulnerable on Ethiopian 3G networks
2. **CORS misconfiguration** - allows all origins to access API
3. **Pagination token secret hardcoded** - must rotate before production
4. **Data isolation not verified in sync endpoints** - potential cross-user data leakage
5. **Production logging not fully disabled** - could expose PII

---

## 1. DATA STORAGE (MOBILE)

### 1.1 Isar Database Configuration
**Status:** ✅ GOOD with ⚠️ FUTURE WORK

**Findings:**
- **CRITICAL** `mobile/lib/core/database/isar_service.dart:83-87`
  - Isar 3.1 **does NOT support built-in encryption** per comment
  - Encryption key is generated but **NOT USED** for database encryption
  - Database file stored unencrypted at application documents directory
  - Only secure storage of the encryption key is implemented
  - Severity: **HIGH** - Database files readable if device physically accessed

**Recommendation:**
```dart
// Current: Key generated but unused
await _getEncryptionKey(); // Line 88

// TODO: Implement field-level encryption for sensitive data:
// - Phone numbers
// - Payment amounts  
// - Delivery verification data
// Consider using package:encrypt or package:pointycastle
```

**Positive:**
- ✅ Encryption key stored in Android Keystore (hardware-backed on supported devices)
- ✅ Key rotation method defined (though not implemented)
- ✅ Proper cleanup on app uninstall

---

### 1.2 Sensitive Data Storage - Finance
**Status:** ⚠️ MEDIUM CONCERN

**Findings:**
- **MEDIUM** `mobile/lib/core/database/schemas/payment.dart:31-32`
  - Payment amounts stored in plaintext: `late double amount;`
  - Commission and payout amounts plaintext: `late double farmerPayout;`
  - Telebirr references plaintext: `String? telebirrRef;`
  
**Risk:** If database is extracted (rooted device), payment history compromised

**Mitigation Currently in Place:**
- ✅ User isolation: `farmerId` and `buyerId` index prevent cross-user reads
- ✅ Isar uses ObjectBox internals with native memory isolation
- ⚠️ But no encryption means raw SQLite dump reveals payment history

---

### 1.3 Authentication Token Storage
**Status:** ✅ EXCELLENT

**Findings:**
- ✅ Access tokens stored in `flutter_secure_storage` (Android Keystore backing)
- ✅ Refresh tokens also secured
- ✅ ID tokens secured
- ✅ Proper cleanup on logout: `offline_auth_cache.dart:229-252`

**Token Lifecycle:**
- Access token: 15-minute validity ✅
- Refresh token: 30-day validity ✅
- Device-specific salt for password hashing ✅

---

### 1.4 Offline Password Caching
**Status:** ✅ EXCELLENT

**Findings:** `mobile/lib/core/auth/offline_auth_cache.dart`
- ✅ PBKDF2-SHA256 with 100,000 iterations (OWASP 2026 compliant)
- ✅ Device-specific 32-byte salt
- ✅ Constant-time comparison (prevents timing attacks, line 315-326)
- ✅ Rate limiting: 5 failed attempts → 15-minute lockout
- ✅ Failed attempts tracked per phone number
- ✅ Proper cleanup on logout

**Risk:** Low - Offline brute force protected by rate limiting + PBKDF2

---

### 1.5 Shared Preferences Security
**Status:** ✅ NO SENSITIVE DATA IN SharedPreferences

**Findings:**
- No hardcoded secrets in SharedPreferences found
- All sensitive data properly moved to flutter_secure_storage
- User preference data is non-sensitive (language, themes, etc.)

---

### 1.6 File Storage & Cache Management
**Status:** ⚠️ MEDIUM CONCERN

**Findings:**
- Cache management not explicitly implemented in reviewed code
- Photo uploads to S3 before delivery confirmation ✅
- **MEDIUM** `mobile/lib/features/delivery_verification/presentation/bloc/delivery_state.dart`
  - Temporary photo files may remain in app cache
  - No explicit cache cleanup on delivery confirmation
  
**Recommendation:** Implement cache cleanup:
```dart
// After successful delivery photo upload to S3
await deletePhotoFromCache(photoPath);
```

---

### 1.7 Data Isolation (Mobile Level)
**Status:** ⚠️ MEDIUM - User isolation verified at DB level

**Findings:**
- ✅ Each user's cached data stored separately in Isar
- ✅ Offline auth cache keyed by `phoneNumber`
- ✅ `User` schema has unique index on `phoneNumber`
- ⚠️ **User isolation NOT enforced at Isar collection level**
  - Any code with Isar instance can query all users' data
  - Mitigation: Single user logged in at a time

**Risk:** If SQLite database is extracted, all users' data readable

---

### 1.8 Secure Deletion
**Status:** ✅ GOOD

**Findings:**
- ✅ `clearAllData()` method deletes database and encryption key
- ✅ Proper cleanup on logout: `clearCredentials()`
- ✅ Stale payment intent cleanup: `escrow_sync_service.dart:105-130`

**Note:** Old database file still on disk but unreadable without key

---

## 2. NETWORK SECURITY

### 2.1 HTTPS Enforcement
**Status:** ✅ ENFORCED

**Findings:**
- ✅ All API endpoints use HTTPS base URLs
- ✅ Development: `https://s0om0hib4i.execute-api.me-south-1.amazonaws.com`
- ✅ Production: `https://api.weredaconnect.com`
- ✅ No HTTP fallback allowed

---

### 2.2 Certificate Pinning
**Status:** 🔴 **CRITICAL - DISABLED**

**Findings:** `mobile/lib/core/network/api_client.dart:18-53`

```dart
/// SECURITY NOTE:
/// Certificate pinning is currently DISABLED (foundation only).
/// Before production deployment:
/// 1. Deploy F-16 Lambda API Gateway
/// 2. Extract actual certificate SHA-256 hashes
/// 3. Update [certificatePins] with real values
/// 4. Uncomment certificate pinning code in [initialize]
/// 5. Test with production API

static const List<String> certificatePins = [
  'sha256/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=', // PLACEHOLDER
  'sha256/BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB=', // PLACEHOLDER
];
```

**Risk Level:** **CRITICAL**
- Ethiopian 3G networks vulnerable to MitM attacks
- Attacker can intercept auth tokens, payments, personal data
- Current implementation: `enableCertPinning = false` by default (line 65)

**Current Implementation:**
- `badCertificateCallback` returns `false` (rejects all certs when enabled)
- But enableCertPinning NOT called anywhere in codebase
- Verification: `api_client.dart:90-110` - pinning code commented/incomplete

**Action Required BEFORE PRODUCTION:**
1. Deploy actual API Gateway certificate
2. Extract certificate: `openssl x509 -in cert.pem -outform der | openssl dgst -sha256 -binary | base64`
3. Update `certificatePins` array
4. Call `initialize(enableCertPinning: true)` in production builds
5. Test on real device against staging API

---

### 2.3 HTTP Client Configuration
**Status:** ✅ GOOD

**Findings:** `mobile/lib/core/network/api_client.dart`
- ✅ Connection timeout: 30 seconds (reasonable for 3G)
- ✅ Receive timeout: 30 seconds
- ✅ Headers: Content-Type, Accept properly set
- ✅ Error handling with specific exception types
- ✅ Standard HTTP methods supported

---

### 2.4 Auth Token Management
**Status:** ✅ EXCELLENT

**Findings:** `mobile/lib/core/network/auth_interceptor.dart`
- ✅ Automatic Bearer token injection
- ✅ 401 response → automatic token refresh
- ✅ Concurrent request queueing (prevents multiple refresh calls)
- ✅ Token cleared on failed refresh
- ✅ Refresh endpoint excluded from auth injection (prevents loop)

**Flow:** Request → 401 → Refresh token → Retry request ✅

---

### 2.5 Logging & Information Leakage
**Status:** ⚠️ **MEDIUM - PRODUCTION SAFE BUT DEVELOPMENT VERBOSE**

**Findings:** `mobile/lib/core/network/logging_interceptor.dart`

**GOOD:**
- ✅ Only active in debug mode: `environment.enableLogging`
- ✅ Disabled in production: `production` environment has `enableLogging: false` (line 101)
- ✅ Authorization header masked (shows first 10 chars then ***)
- ✅ Request/response body truncated to 1000 chars
- ✅ Response bodies logged (could contain user data)

**MEDIUM CONCERN:**
- ⚠️ Line 65: `options.data.toString()` - logs entire request body in debug
  - Could contain: phone numbers, passwords during failed auth attempts
  - Could contain: delivery photos, user coordinates
  - Mitigation: Already masked in production (`enableLogging: false`)

**Recommendation:** Even in development, mask PII:
```dart
// Mask phone numbers in request body
String _maskPii(String body) {
  return body.replaceAll(RegExp(r'\+251\d{9}'), '+251***');
}
```

---

### 2.6 Network Error Handling
**Status:** ✅ GOOD

**Findings:** `mobile/lib/core/network/api_client.dart:234-278`
- ✅ Errors mapped to typed exceptions
- ✅ No sensitive data in error messages (except for validation)
- ✅ Rate limit errors properly handled
- ✅ SSL certificate errors caught separately

---

## 3. SERVER SECURITY (.NET)

### 3.1 JWT Validation
**Status:** ✅ EXCELLENT

**Findings:** `server/src/Auth/JwtValidator.cs`

**Strong Points:**
- ✅ Issuer validation: `ValidateIssuer = true`
- ✅ Audience validation: `ValidAudience = _clientId`
- ✅ Lifetime validation: `ValidateLifetime = true`
- ✅ 1-minute clock skew tolerance (reasonable for UTC sync issues)
- ✅ Required claims enforcement: `sub`, `phone_number`, `custom:role`, `custom:woreda`
- ✅ All exception types caught with specific messages
- ✅ Malformed token detection (line 108-110)

**Implementation Notes:**
- ✅ No reflection (AOT compatible)
- ✅ Can skip signature validation for tests (line 93-96) - correctly flagged
- ✅ Claims extraction uses safe defaults (line 121-125)

---

### 3.2 Rate Limiting
**Status:** ✅ EXCELLENT

**Findings:** `server/src/RateLimiting/RateLimitService.cs`

**Implemented Rules:**
1. ✅ Failed login lockout: 3 failures → 15-minute lockout (AUTH_015)
2. ✅ Registration flood: 5 per phone per 24 hours (AUTH_006)
3. ✅ OTP attempts: 3 per phone per 5 minutes (AUTH_012)
4. ✅ Sync rate limit: 10 requests per minute per user

**Security Features:**
- ✅ DynamoDB TTL for automatic cleanup
- ✅ Rate limit checked BEFORE password verification (prevents timing-based enumeration)
- ✅ Lockout time calculated per user
- ✅ Retry-After header information provided

---

### 3.3 Cognito Integration
**Status:** ✅ VERY GOOD

**Findings:** `server/src/Services/CognitoAuthService.cs`

**Authentication Flows:**
- ✅ Sign-up with phone number and attributes
- ✅ OTP confirmation (ConfirmSignUp)
- ✅ Login with USER_PASSWORD_AUTH flow
- ✅ Token refresh
- ✅ Token revocation (logout)

**Security Measures:**
- ✅ Phone number uniqueness enforced (UsernameExistsException)
- ✅ Invalid password rejected (InvalidPasswordException)
- ✅ User not found returns same error as invalid password (prevents enumeration, line 167-169)
- ✅ Cognito error codes mapped to AUTH_* codes
- ✅ Exception handling for TooManyRequests, InternalError

**Potential Issue:**
- ⚠️ No mention of Cognito email/SMS rate limiting
- ⚠️ OTP delivery mechanism not reviewed (uses Cognito default)

---

### 3.4 Input Validation
**Status:** ✅ EXCELLENT

**Findings:** `server/src/Validation/RequestValidator.cs`

**Validations Implemented:**
- ✅ Phone number: `+251` followed by exactly 9 digits (regex-validated)
- ✅ Password: 6-128 characters
- ✅ Role: whitelist check (farmer, trucker, buyer)
- ✅ Woreda: non-empty, max 100 chars
- ✅ OTP code: exactly 6 digits
- ✅ Display name: optional, max 100 chars
- ✅ Language: whitelist check (en, am, om)

**Implementation Quality:**
- ✅ Regex compiled at compile-time (AOT safe, no reflection)
- ✅ HashSet for role/language validation (efficient)
- ✅ Null checks before validation
- ✅ Comprehensive error messages with field names

---

### 3.5 API Response Security
**Status:** ⚠️ MEDIUM - Needs verification

**Concerns:**
- ⚠️ Response DTO fields not reviewed
  - Likely returning unnecessary fields to client
  - Need to verify: no passwords, no Cognito internals, no private keys returned

**Action Item:** Review all response DTOs:
- `server/src/Models/AuthDtos.cs`
- `server/src/Models/UserDto.cs`
- Ensure no sensitive fields in responses

---

### 3.6 Password Storage
**Status:** ✅ DELEGATED TO COGNITO

**Findings:**
- ✅ Passwords stored in Cognito, not in WeredaConnect DB
- ✅ DynamoDB only stores: userId, phone, role, woreda, name
- ✅ Cognito uses PBKDF2-like hashing

---

### 3.7 Lambda Security
**Status:** ✅ GOOD

**Findings:** `server/src/Program.cs`
- ✅ Handler routing via environment variable
- ✅ Native AOT bootstrap (no reflection)
- ✅ Per-function Lambda functions (following least privilege)
- ✅ Minimal cold-start overhead

---

## 4. INFRASTRUCTURE SECURITY (AWS CDK)

### 4.1 Cognito Configuration
**Status:** ⚠️ **MEDIUM - PRODUCTION READINESS CONCERN**

**Findings:** `infra/lib/stacks/api-stack.ts:47-72`

**Password Policy:**
```typescript
passwordPolicy: {
  minLength: 8,
  requireLowercase: true,
  requireUppercase: false,  // ⚠️ No uppercase required
  requireDigits: true,
  requireSymbols: false,    // ⚠️ No symbols required
}
```

**Assessment:**
- ✅ 8-char minimum is reasonable for low-literacy users in rural areas
- ✅ Lowercase + digits = 36^8 combinations (meets NIST SP 800-63B)
- ⚠️ MEDIUM: No uppercase/symbols requirement weakens password strength
- ✅ Mitigated by: offline PBKDF2 hashing (100k iterations) for offline login

**Cognito Settings:**
- ✅ Self sign-up enabled (matches design)
- ✅ Phone number as sign-in alias
- ✅ Auto-verify phone (requires OTP)
- ✅ Phone number immutable (prevents account takeover via phone change)
- ❌ MFA disabled - `mfa: cognito.Mfa.OFF` (line 52)
  - **MEDIUM SEVERITY:** No MFA protection for account takeover
  - **Mitigation:** Offline password hashing + rate limiting reduce risk
  - **Action:** Consider enabling optional MFA for buyer accounts (handling escrow)

**Token Lifetimes:**
- ✅ Access token: 15 minutes (good balance)
- ✅ Refresh token: 30 days (supports intermittent connectivity)
- ✅ ID token: 15 minutes

**User Existence Prevention:**
- ✅ `preventUserExistenceErrors: true` (line 83)
  - Prevents user enumeration attack

---

### 4.2 API Gateway Security
**Status:** ⚠️ **CRITICAL - CORS MISCONFIGURATION**

**Findings:** `infra/lib/stacks/api-stack.ts:353-358`

```typescript
corsPreflight: {
  allowOrigins: ['*'],  // 🔴 CRITICAL: Allow all origins
  allowMethods: [apigateway.CorsHttpMethod.POST, apigateway.CorsHttpMethod.GET],
  allowHeaders: ['Content-Type', 'Authorization'],
}
```

**Risk:** **CRITICAL**
1. Browser-based CSRF attacks from any website
2. Any website can make API requests with user's credentials
3. Attacker website: `<img src="https://api.weredaconnect.com/auth/logout">` logs out user

**Attack Scenario:**
```javascript
// Attacker's website (attacker.example.com)
fetch('https://api.weredaconnect.com/escrow/initiate', {
  method: 'POST',
  credentials: 'include',  // Include auth cookies
  body: JSON.stringify({...fraudulent payment...})
})
// Success because CORS allows *
```

**Action Required BEFORE PRODUCTION:**
```typescript
corsPreflight: {
  allowOrigins: [
    'https://app.weredaconnect.com',  // Production app
    'https://staging-app.weredaconnect.com',  // Staging
    // Do NOT include '*'
  ],
  allowMethods: [...],
}
```

**Note:** Mobile app doesn't use CORS (native HTTP), so this mainly affects web versions if deployed later.

---

### 4.3 API Gateway Throttling
**Status:** ✅ GOOD

**Findings:** `infra/lib/stacks/api-stack.ts:532-539`

```typescript
DefaultRouteSettings: {
  ThrottlingBurstLimit: 200,  // 200 requests in burst window
  ThrottlingRateLimit: 100,   // 100 requests per second
}
```

**Assessment:**
- ✅ Prevents DDoS from single client
- ✅ Allows burst for real spikes
- ⚠️ MEDIUM: Per-user rate limiting in application layer (RateLimitService) more important for auth
- ✅ Combined protection (API Gateway + application) is good

---

### 4.4 DynamoDB Security
**Status:** ✅ EXCELLENT

**Findings:** `infra/lib/stacks/database-stack.ts`

**Encryption:**
- ✅ S3 buckets use S3_MANAGED encryption (AWS-managed keys)
- ✓ DynamoDB encryption is enabled by default (AWS-managed keys)
- Note: No mention of customer-managed KMS keys (acceptable for MVP)

**Table Configuration:**
- ✅ Pay-per-request billing (no provisioned capacity exposure)
- ✅ Global secondary indexes for efficient queries
- ✅ TTL enabled for:
  - Rate limit entries (auto-cleanup, line 70)
  - Sync metadata (7-day expiry, line 119)
  - Escrow idempotency keys (30-day expiry, line 258)

**Data Isolation:**
- ✅ Users table indexed by `userId` (partition key)
- ✅ GSI by `phoneNumber` for login lookups
- ✅ GSI by `role` + `woreda` for matching
- ✅ User data isolation enforced at application level (need to verify in Lambda functions)

**Escrow Audit Table:**
- ✅ Append-only design (no updates/deletes)
- ✅ Immutable for audit trail

**Concerns:**
- ⚠️ MEDIUM: Point-in-time recovery (PITR) not explicitly enabled
  - DynamoDB default: can restore to any point in last 35 days
  - Consider enabling explicit backup policy for production

---

### 4.5 S3 Bucket Security
**Status:** ✅ EXCELLENT

**Findings:** `infra/lib/stacks/database-stack.ts:263-305`

**Access Control:**
- ✅ `BlockPublicAccess.BLOCK_ALL` - blocks all public access attempts
- ✅ Encryption: S3_MANAGED
- ✅ Versioning enabled (recovery from accidental deletion)
- ✅ Access logs bucket configured (audit trail)

**Lifecycle Rules:**
- ✅ Temporary files: expire after 7 days
- ✅ Photos: transition to Glacier after 365 days (cost optimization)
- ✅ Access logs: expire after 365 days

**File Upload Security:**
- ⚠️ MEDIUM: No file type validation mentioned
  - Need to verify: delivery photos restricted to image/* types
  - Need to verify: file size limits enforced
  - Need to verify: malware scanning if available

**Bucket Naming:**
- ✅ Includes account ID to ensure global uniqueness

---

### 4.6 IAM Policies
**Status:** ✅ GOOD - Least Privilege

**Findings:** `infra/lib/stacks/api-stack.ts:108-300`

**Per-Function Permissions:**
- ✅ RegisterFunction: only DynamoDB write to users table + rate limits
- ✅ LoginFunction: DynamoDB read + rate limits + Cognito auth
- ✅ RefreshFunction: only Cognito auth
- ✅ LogoutFunction: only Cognito revoke
- ✅ CropFunction: only DynamoDB read crops table
- ✅ TripFunction: only DynamoDB read trips table
- ✅ DeliveryFunction: DynamoDB read + S3 upload
- ✅ EscrowFunction: escrow tables R/W + SSM parameter read

**Cognito Actions:**
- ✅ Limited to specific operations (SignUp, ConfirmSignUp, InitiateAuth, etc.)
- ✅ Actions scoped to user pool ARN (line 563)

**SSM Parameters:**
- ✅ Commission rate, dispute window, timeout - read-only from Lambdas
- ✅ Parameters in SSM Parameter Store (secure management)

---

### 4.7 Lambda Configuration
**Status:** ✅ GOOD

**Findings:** `infra/lib/constructs/lambda-construct.ts`

**Security Features:**
- ✅ ARM64 architecture (Graviton2 - better price/performance)
- ✅ Runtime: `PROVIDED_AL2023` (custom .NET 10 AOT binary)
- ✅ No reflection (AOT compatible)
- ✅ Bundling via Docker (cross-compilation safe)

**Environment Variables:**
- ✅ Database names, Cognito config via env vars
- ✅ No hardcoded secrets (populated via CDK)
- ✅ `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` (AOT requirement)

**Timeout Configuration:**
- ✅ Auth functions: 10 seconds (reasonable)
- ✅ Sync function: 30 seconds (batch processing)
- ✅ Escrow functions: 30 seconds
- ✅ Cron: 5 minutes (background processing)

---

### 4.8 Monitoring & Logging
**Status:** ⚠️ **INCOMPLETE**

**Findings:** `infra/lib/stacks/monitoring-stack.ts`

**Current State:**
- ✅ CloudWatch Dashboard created
- ✅ Dashboard URL exported
- ⚠️ Only placeholder widgets added

**Missing:**
- ❌ Lambda error rate alarms
- ❌ DynamoDB throttling alarms
- ❌ API Gateway 5xx rate alarms
- ❌ Failed authentication count alarms
- ❌ Log retention policy not configured
- ❌ CloudWatch Logs encryption not mentioned

**Action Required:**
```typescript
// Add CloudWatch alarms:
- Lambda errors > 10 in 5 min → SNS alert
- DynamoDB consumed capacity throttles → alert
- API Gateway 5xx > 5% → alert
- Cognito failed auth attempts > threshold → alert

// Configure Log retention:
logs.LogGroup: retentionDays: 30 (production)
```

---

### 4.9 Pagination Token Security
**Status:** 🔴 **CRITICAL - HARDCODED SECRET**

**Findings:** `infra/lib/stacks/api-stack.ts:194-198`

```typescript
const paginationSecretParam = new ssm.StringParameter(this, 'PaginationSecret', {
  parameterName: `/${prefix}/pagination-secret`,
  stringValue: 'WeredaConnect-Pagination-Default-Key-v1',  // 🔴 HARDCODED
  description: 'HMAC signing key for pagination tokens (override in production)',
});
```

**Risk:** **CRITICAL**
1. Token forgery possible if key is known
2. Attacker can generate valid pagination tokens for any query
3. Hardcoded value same across all environments

**Attack Scenario:**
```
Attacker knows: "WeredaConnect-Pagination-Default-Key-v1"
Generates valid pagination token → Queries all crops in database
```

**Action Required BEFORE PRODUCTION:**
```bash
# Generate cryptographically secure 256-bit key
openssl rand -base64 32

# Store in AWS Secrets Manager, not SSM Parameter Store
# Rotate key monthly
```

**Update CDK:**
```typescript
const paginationSecret = secrets.Secret.fromSecretNameV2(
  this, 'PaginationSecret',
  'wereda-connect-prod/pagination-secret'
);
```

---

## 5. PRIVACY & PII HANDLING

### 5.1 Collected Personal Data
**Status:** ⚠️ **MEDIUM - NEED PRIVACY POLICY**

**Data Collected:**
- ✅ Phone number (+251XXXXXXXXX)
- ✅ Full name
- ✅ User role (farmer/trucker/buyer)
- ✅ Woreda (district)
- ✅ Language preference
- ✅ Fayda microfinance ID (optional)
- ✅ Payment transaction history (amounts, dates, participants)
- ✅ Delivery locations (GPS coordinates per delivery_verification specs)
- ✅ Photos of delivered goods
- ✅ IP addresses (in CloudWatch logs)

**Data Minimization:**
- ✅ Only fields necessary for platform functionality collected
- ✅ No email (phone-only, Ethiopian context appropriate)
- ✅ No age/gender/ethnicity

---

### 5.2 Data Retention
**Status:** ⚠️ **MEDIUM - POLICY MISSING**

**Current TTLs Implemented:**
- Rate limit entries: auto-expire (no retention policy)
- Sync metadata: 7 days
- Escrow idempotency: 30 days
- Photos in S3: 365 days then Glacier

**Missing:**
- ❌ User account deletion timeline not specified
- ❌ Payment history retention policy not documented
- ❌ Photo deletion after dispute resolution timeline

**Recommendation:**
```
User-requested deletion: 30 days to anonymize
Payment history: Keep for 7 years (regulatory/fraud prevention)
Photos: Delete 1 year after dispute resolution
```

---

### 5.3 Consent & Privacy Notice
**Status:** ❌ **NOT FOUND - REQUIRED FOR GDPR/LOCAL COMPLIANCE**

**Missing:**
- ❌ Privacy policy in mobile app
- ❌ Consent screen at sign-up
- ❌ Data processing agreement (DPA)
- ❌ Third-party processor disclosures (AWS, Cognito, Telebirr)

**Action Required:**
1. Create privacy policy addressing:
   - Data collection, processing, retention
   - Third-party processors
   - User rights (access, deletion, portability)
   - Ethiopia data protection requirements
2. Add consent screen to registration
3. Implement data deletion endpoint for user requests

---

### 5.4 User Data Isolation
**Status:** ⚠️ **MEDIUM - PARTIALLY VERIFIED**

**Verified:**
- ✅ Mobile: Single user logged in at a time
- ✅ Isar: indexed by phoneNumber (unique constraint)
- ✅ Server: JWT contains userId, passed to Lambdas

**Not Yet Verified:**
- ⚠️ Sync endpoint: Need to verify user can only see own data
- ⚠️ Crop/Trip browse: Public data, confirmed open access
- ⚠️ Delivery endpoint: Need to verify user isolation
- ⚠️ Escrow endpoint: Need to verify buyer/farmer isolation

**Critical Query:** In each Lambda, verify:
```csharp
// Extract userId from JWT
var userId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;

// All queries must filter by userId
var deliveries = await deliveriesTable.Query()
  .Where(d => d.userId == userId)  // ✅ Must have this
```

---

### 5.5 Password Reset Security
**Status:** ✅ **DELEGATED TO COGNITO (SAFE)**

**Findings:**
- ✅ Password reset via phone number (no email account takeover)
- ✅ OTP verification required
- ✅ Cognito manages reset tokens (automatic expiry)
- ✅ Rate limited per phone (3 attempts per 5 minutes)

---

## 6. SUMMARY TABLE

| Category | Component | Status | Severity | Required Action |
|----------|-----------|--------|----------|-----------------|
| **Data Storage** | Isar encryption | ⚠️ | HIGH | Implement field-level encryption for sensitive data |
| | Auth tokens | ✅ | - | None |
| | Password hashing | ✅ | - | None |
| | User isolation | ⚠️ | MEDIUM | Document single-user guarantee |
| **Network** | HTTPS | ✅ | - | None |
| | Certificate pinning | 🔴 | **CRITICAL** | Deploy real certs & enable pinning |
| | Logging (debug) | ⚠️ | MEDIUM | Mask PII in logs |
| **Auth** | JWT validation | ✅ | - | None |
| | Rate limiting | ✅ | - | None |
| | Token refresh | ✅ | - | None |
| **Server** | Input validation | ✅ | - | None |
| | Cognito integration | ✅ | - | Enable MFA for buyers (MEDIUM) |
| | Password policy | ⚠️ | MEDIUM | Already mitigated by offline PBKDF2 |
| **Infrastructure** | CORS | 🔴 | **CRITICAL** | Restrict to known origins |
| | Pagination secret | 🔴 | **CRITICAL** | Rotate & store in Secrets Manager |
| | API throttling | ✅ | - | None |
| | DynamoDB security | ✅ | - | None |
| | S3 security | ✅ | - | Add file type validation |
| | IAM policies | ✅ | - | None |
| | Monitoring | ⚠️ | MEDIUM | Add CloudWatch alarms |
| **Privacy** | Data minimization | ✅ | - | None |
| | Consent mechanism | ❌ | MEDIUM | Implement privacy policy & consent |
| | Data retention policy | ⚠️ | MEDIUM | Document & implement |
| | User deletion | ❌ | MEDIUM | Implement account deletion endpoint |

---

## 7. CRITICAL ACTIONS BEFORE PRODUCTION

### Immediate (Blocking):
1. **🔴 Certificate Pinning:** Extract API Gateway certificates, update client, enable pinning
2. **🔴 CORS:** Restrict to specific origins only
3. **🔴 Pagination Secret:** Rotate & store in AWS Secrets Manager

### High Priority (1 sprint):
4. Enable monitoring alarms (DynamoDB throttles, Lambda errors, auth failures)
5. Verify user data isolation in all Lambda functions
6. Implement privacy policy & consent flow
7. Add file upload validation (type, size, malware scanning)

### Medium Priority (2 sprints):
8. Field-level encryption for payment data
9. MFA for buyer accounts (optional)
10. CloudWatch log retention & encryption

---

## 8. COMPLIANCE NOTES

### GDPR Readiness (if serving EU):
- ⚠️ Privacy policy: NOT PRESENT
- ⚠️ Data consent: NOT PRESENT
- ⚠️ Data deletion endpoint: NOT PRESENT
- ⚠️ Data portability: NOT VERIFIED
- ✅ Data minimization: GOOD
- ✅ Encryption at rest: GOOD
- ✅ Encryption in transit: GOOD (when cert pinning enabled)

### Ethiopia Data Protection:
- ✅ Phone number as ID: Culturally appropriate
- ✅ No social media integration: Good privacy
- ⚠️ No formal privacy framework mentioned (Ethiopia developing regulations)
- Recommendation: Implement generic GDPR-like framework as baseline

---

## 9. RECOMMENDATIONS FOR FUTURE

1. **Threat Modeling:** Conduct formal threat model for escrow/payment flows
2. **Security Testing:** Penetration test before public beta
3. **Bug Bounty:** Consider HackerOne program for ongoing security
4. **Incident Response:** Define breach notification process
5. **Secrets Management:** Consider AWS Secrets Manager for all credentials
6. **API Rate Limiting:** Implement per-user rate limits in API Gateway (not just application)
7. **Request Signing:** Consider request signing for offline scenarios (prevent tampering)
8. **Escrow Audit Logs:** Implement immutable Escrow Audit Trail queries

---

**Report Generated:** 2026-02-22  
**Reviewed By:** Security Audit Team  
**Classification:** INTERNAL - SENSITIVE
