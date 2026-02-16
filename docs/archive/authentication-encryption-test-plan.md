# 🧪 Authentication & Encryption Test Plan

**Date**: 2026-02-10  
**Owner**: Testing Agent  
**Scope**: Authentication, Encryption, Input Validation, Security Hardening  
**Prerequisites**: Implementation of `specs/decisions/authentication-encryption-plan.md` complete

---

## Purpose

This test plan defines comprehensive testing for the authentication and encryption implementation. The Testing Agent will execute these tests after Phase 1-3 are complete to validate all security requirements.

---

## Test Scope

### In Scope ✅
- Server JWT validation
- Mobile database encryption (Isar)
- Input validation (all endpoints)
- Android release signing
- AWS Cognito integration
- Mobile auth service (online/offline)
- Lambda authorizer
- Registration/login flows
- HTTPS certificate pinning
- API Gateway rate limiting
- SIM swap protection
- Shared device support
- Low-literacy UX

### Out of Scope ❌
- Feature functionality (crop listings, trips, etc.)
- Performance testing (separate plan)
- Load testing (separate plan)

---

## Test Environments

### 1. **Local Development**
- LocalStack for AWS services
- Flutter on Android emulator (2GB RAM)
- .NET 10 with AOT compilation

### 2. **Dev Environment**
- AWS Cognito (dev pool)
- Lambda functions (dev stage)
- API Gateway (dev stage)
- Real Android device (2GB RAM, Android 8+)

### 3. **Staging Environment**
- Production-like configuration
- Real AWS services
- Multiple test devices

---

## Test Categories

### 1. Unit Tests (Automated)
### 2. Integration Tests (Automated)
### 3. Security Tests (Manual + Automated)
### 4. OWASP Compliance Tests (Manual)
### 5. Ethiopian Context Tests (Manual)
### 6. Regression Tests (Automated)

---

## 1. Unit Tests (Automated)

### 1.1 Server JWT Validator Tests

**File**: `server/tests/Auth/JwtValidatorTests.cs`

| Test ID | Test Case | Expected Result |
|---------|-----------|-----------------|
| UT-JWT-001 | Valid Cognito token | Token validated, claims extracted |
| UT-JWT-002 | Expired token | Validation fails with 401 |
| UT-JWT-003 | Invalid signature | Validation fails with 401 |
| UT-JWT-004 | Malformed token | Validation fails with 400 |
| UT-JWT-005 | Missing required claims | Validation fails with 401 |
| UT-JWT-006 | Token from wrong issuer | Validation fails with 401 |
| UT-JWT-007 | Token with tampered payload | Validation fails with 401 |

**Success Criteria**: All 7 tests pass

---

### 1.2 Mobile Database Encryption Tests

**File**: `mobile/test/features/auth/data/encryption_test.dart`

| Test ID | Test Case | Expected Result |
|---------|-----------|-----------------|
| UT-ENC-001 | Initialize encrypted Isar | Database opens successfully |
| UT-ENC-002 | Write encrypted data | Data persisted encrypted |
| UT-ENC-003 | Read encrypted data | Data decrypted correctly |
| UT-ENC-004 | Wrong encryption key | Database fails to open |
| UT-ENC-005 | Key rotation | Old data readable with new key |
| UT-ENC-006 | Encryption key storage | Key stored in secure storage |
| UT-ENC-007 | Database file inspection | File is encrypted (not plaintext) |

**Success Criteria**: All 7 tests pass

---

### 1.3 Input Validation Tests

**File**: `server/tests/Validation/RequestValidatorTests.cs`

| Test ID | Test Case | Expected Result |
|---------|-----------|-----------------|
| UT-VAL-001 | Valid input | Validation passes |
| UT-VAL-002 | SQL injection attempt | Validation fails, sanitized |
| UT-VAL-003 | XSS attempt | Validation fails, sanitized |
| UT-VAL-004 | Command injection | Validation fails, sanitized |
| UT-VAL-005 | Oversized input | Validation fails with 400 |
| UT-VAL-006 | Missing required fields | Validation fails with 400 |
| UT-VAL-007 | Invalid data types | Validation fails with 400 |
| UT-VAL-008 | Special characters | Properly escaped/sanitized |

**Success Criteria**: All 8 tests pass

---

### 1.4 Mobile Auth Service Tests

**File**: `mobile/test/features/auth/data/auth_service_test.dart`

| Test ID | Test Case | Expected Result |
|---------|-----------|-----------------|
| UT-AUTH-001 | Login online (valid) | Returns user session |
| UT-AUTH-002 | Login online (invalid) | Returns error |
| UT-AUTH-003 | Login offline (cached) | Returns cached session |
| UT-AUTH-004 | Login offline (no cache) | Returns offline error |
| UT-AUTH-005 | Token refresh online | New token returned |
| UT-AUTH-006 | Token refresh offline | Uses cached token |
| UT-AUTH-007 | Logout | Clears session, cache |
| UT-AUTH-008 | Session expiry | Auto-logout after 7 days |

**Success Criteria**: All 8 tests pass

---

## 2. Integration Tests (Automated)

### 2.1 End-to-End Auth Flow Tests

**File**: `server/tests/Integration/AuthFlowTests.cs`

| Test ID | Test Case | Expected Result |
|---------|-----------|-----------------|
| IT-E2E-001 | Register new user | User created in Cognito |
| IT-E2E-002 | Verify phone (SMS OTP) | Phone verified |
| IT-E2E-003 | Login with credentials | JWT token returned |
| IT-E2E-004 | Access protected endpoint | 200 OK with data |
| IT-E2E-005 | Access without token | 401 Unauthorized |
| IT-E2E-006 | Refresh expired token | New token returned |
| IT-E2E-007 | Logout | Token invalidated |

**Success Criteria**: All 7 tests pass with LocalStack

---

### 2.2 Offline Auth Flow Tests

**File**: `mobile/integration_test/auth_offline_test.dart`

| Test ID | Test Case | Expected Result |
|---------|-----------|-----------------|
| IT-OFF-001 | Login online, go offline | Cached session works |
| IT-OFF-002 | Offline for 1 day | Session still valid |
| IT-OFF-003 | Offline for 7 days | Session expires, re-auth required |
| IT-OFF-004 | Create data offline | Queued for sync |
| IT-OFF-005 | Go online, auto-sync | Data synced to server |
| IT-OFF-006 | Token refresh offline | Uses cached token |

**Success Criteria**: All 6 tests pass

---

### 2.3 Lambda Authorizer Tests

**File**: `server/tests/Integration/AuthorizerTests.cs`

| Test ID | Test Case | Expected Result |
|---------|-----------|-----------------|
| IT-AUT-001 | Valid token in header | Policy allows access |
| IT-AUT-002 | Invalid token | Policy denies access |
| IT-AUT-003 | Missing token | Policy denies access |
| IT-AUT-004 | Expired token | Policy denies access |
| IT-AUT-005 | User context injection | User ID in Lambda event |

**Success Criteria**: All 5 tests pass

---

## 3. Security Tests (Manual + Automated)

### 3.1 HTTPS Certificate Pinning Tests

| Test ID | Test Case | Expected Result | Method |
|---------|-----------|-----------------|--------|
| ST-PIN-001 | Valid certificate | Connection succeeds | Manual |
| ST-PIN-002 | Invalid certificate | Connection fails | Manual (proxy) |
| ST-PIN-003 | MITM attack | Connection fails | Manual (mitmproxy) |
| ST-PIN-004 | Certificate rotation | Backup cert works | Manual |

**Tools**: mitmproxy, Charles Proxy  
**Success Criteria**: All 4 tests pass

---

### 3.2 Rate Limiting Tests

| Test ID | Test Case | Expected Result | Method |
|---------|-----------|-----------------|--------|
| ST-RATE-001 | 50 req/sec | All succeed | Automated (k6) |
| ST-RATE-002 | 100 req/sec | All succeed | Automated (k6) |
| ST-RATE-003 | 150 req/sec | Some throttled (429) | Automated (k6) |
| ST-RATE-004 | 500 req/sec | Most throttled (429) | Automated (k6) |

**Tools**: k6 load testing  
**Success Criteria**: Rate limiting enforced at 100 req/sec

---

### 3.3 Android Release Signing Tests

| Test ID | Test Case | Expected Result | Method |
|---------|-----------|-----------------|--------|
| ST-SIGN-001 | Build release APK | Uses production key | Manual |
| ST-SIGN-002 | Verify APK signature | Production key verified | Manual (apksigner) |
| ST-SIGN-003 | Attempt to re-sign | Fails (signature mismatch) | Manual |

**Tools**: `apksigner verify`  
**Success Criteria**: All 3 tests pass

---

## 4. OWASP Compliance Tests (Manual)

### 4.1 OWASP Mobile Top 10

| OWASP ID | Vulnerability | Test | Status |
|----------|---------------|------|--------|
| M1 | Improper Platform Usage | Auth uses secure storage | ⏳ Pending |
| M2 | Insecure Data Storage | Isar encrypted | ⏳ Pending |
| M3 | Insecure Communication | HTTPS pinning | ⏳ Pending |
| M4 | Insecure Authentication | Cognito + MFA | ⏳ Pending |
| M5 | Insufficient Cryptography | AES-256 encryption | ⏳ Pending |
| M6 | Insecure Authorization | JWT validation | ⏳ Pending |
| M7 | Client Code Quality | Linting, static analysis | ⏳ Pending |
| M8 | Code Tampering | ProGuard enabled | ⏳ Pending |
| M9 | Reverse Engineering | Obfuscation enabled | ⏳ Pending |
| M10 | Extraneous Functionality | No debug code in release | ⏳ Pending |

**Success Criteria**: All 10 vulnerabilities mitigated

---

### 4.2 OWASP API Top 10

| OWASP ID | Vulnerability | Test | Status |
|----------|---------------|------|--------|
| API1 | Broken Object Level Authorization | User can't access other's data | ⏳ Pending |
| API2 | Broken User Authentication | JWT validation enforced | ⏳ Pending |
| API3 | Excessive Data Exposure | Minimal data in responses | ⏳ Pending |
| API4 | Lack of Resources & Rate Limiting | Rate limiting enforced | ⏳ Pending |
| API5 | Broken Function Level Authorization | Role-based access | ⏳ Pending |
| API6 | Mass Assignment | Input validation | ⏳ Pending |
| API7 | Security Misconfiguration | Secure defaults | ⏳ Pending |
| API8 | Injection | Input sanitization | ⏳ Pending |
| API9 | Improper Assets Management | API versioning | ⏳ Pending |
| API10 | Insufficient Logging & Monitoring | CloudWatch logs | ⏳ Pending |

**Success Criteria**: All 10 vulnerabilities mitigated

---

## 5. Ethiopian Context Tests (Manual)

### 5.1 SIM Swap Protection

| Test ID | Test Case | Expected Result |
|---------|-----------|-----------------|
| ET-SIM-001 | Login from new device | SMS alert sent |
| ET-SIM-002 | Login after SIM swap | Re-verification required |
| ET-SIM-003 | Device fingerprint mismatch | Additional auth required |

**Success Criteria**: All 3 tests pass

---

### 5.2 Shared Device Support

| Test ID | Test Case | Expected Result |
|---------|-----------|-----------------|
| ET-DEV-001 | Switch between 3 users | <1 second switch time |
| ET-DEV-002 | Biometric per user | Each user has own biometric |
| ET-DEV-003 | Visual user selection | Photos displayed, not text |

**Success Criteria**: All 3 tests pass

---

### 5.3 Low-Literacy UX

| Test ID | Test Case | Expected Result |
|---------|-----------|-----------------|
| ET-LIT-001 | Password strength indicator | Visual color bars |
| ET-LIT-002 | Requirement checklist | Icons (✓/✗), not text |
| ET-LIT-003 | Voice guidance | Plays in Amharic/Oromo |

**Success Criteria**: All 3 tests pass, validated with users

---

## Test Execution Schedule

| Phase | Duration | Responsible |
|-------|----------|-------------|
| Unit Tests | 1 day | Testing Agent (automated) |
| Integration Tests | 2 days | Testing Agent (automated) |
| Security Tests | 2 days | Testing Agent (manual + automated) |
| OWASP Compliance | 1 day | Security Agent + Testing Agent |
| Ethiopian Context | 1 day | Testing Agent + User validation |
| Regression Tests | 1 day | Testing Agent (automated) |
| **TOTAL** | **8 days** | Testing Agent |

---

## Success Criteria

### Overall
- ✅ All unit tests pass (30+ tests)
- ✅ All integration tests pass (18+ tests)
- ✅ All security tests pass (11+ tests)
- ✅ OWASP Mobile Top 10: 10/10 mitigated
- ✅ OWASP API Top 10: 10/10 mitigated
- ✅ Ethiopian context tests: 9/9 pass
- ✅ Zero critical vulnerabilities remaining

### Performance
- ✅ Offline login: <1 second
- ✅ Online login: <3 seconds on 3G
- ✅ Database encryption overhead: <5%
- ✅ User switching: <1 second

---

## Test Report Template

After testing, Testing Agent will create:

**File**: `qa/test_reports/AUTH_ENCRYPTION_TEST_REPORT_[DATE].md`

**Contents**:
1. Executive Summary
2. Test Results (pass/fail counts)
3. Defects Found (severity, status)
4. OWASP Compliance Status
5. Performance Metrics
6. Recommendations
7. Sign-off

---

**Test Plan Owner**: Testing Agent  
**Next Review**: After Phase 1-3 implementation complete  
**Version**: 1.0.0

