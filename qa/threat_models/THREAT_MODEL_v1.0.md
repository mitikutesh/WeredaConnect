# 🛡️ WeredaConnect Threat Model v1.0

**Date**: 2026-02-10  
**Version**: 1.0.0  
**Methodology**: STRIDE (Spoofing, Tampering, Repudiation, Information Disclosure, Denial of Service, Elevation of Privilege)  
**Scope**: Mobile App, Server API, Infrastructure

---

## System Overview

**WeredaConnect** is an offline-first agritech logistics platform connecting:
- **Farmers** (rural Ethiopia, low-literacy, 2GB Android devices)
- **Transport Providers** (truckers with smartphones)
- **Urban Wholesalers** (buyers with better connectivity)

**Key Assets**:
1. Farmer personal data (name, phone, location)
2. Crop listings (type, quantity, price, photos)
3. Trip data (routes, schedules, payments)
4. Payment information (Telebirr escrow)
5. Location data (GPS coordinates)

---

## Trust Boundaries

```
┌─────────────────────────────────────────────────────────────┐
│ INTERNET (Untrusted)                                        │
│  ┌──────────────┐         ┌──────────────┐                 │
│  │ 3G Network   │────────▶│ API Gateway  │                 │
│  │ (Untrusted)  │         │ (AWS)        │                 │
│  └──────────────┘         └──────┬───────┘                 │
│                                   │                          │
│                          ┌────────▼────────┐                │
│                          │ Lambda Functions│                │
│                          │ (Trusted)       │                │
│                          └────────┬────────┘                │
│                                   │                          │
│                    ┌──────────────┼──────────────┐          │
│                    │              │              │          │
│              ┌─────▼─────┐  ┌────▼────┐  ┌──────▼──────┐  │
│              │ DynamoDB  │  │   S3    │  │ Secrets Mgr │  │
│              │ (Trusted) │  │(Trusted)│  │  (Trusted)  │  │
│              └───────────┘  └─────────┘  └─────────────┘  │
└─────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────┐
│ MOBILE DEVICE (Semi-Trusted)                                │
│  ┌──────────────┐         ┌──────────────┐                 │
│  │ Flutter App  │────────▶│ Isar Database│                 │
│  │ (Trusted)    │         │ (Trusted)    │                 │
│  └──────────────┘         └──────────────┘                 │
│                                                              │
│  ⚠️ Device may be rooted, shared, or stolen                │
└─────────────────────────────────────────────────────────────┘
```

---

## Threat Analysis (STRIDE)

### 1. Spoofing (Identity Threats)

#### T1.1: Attacker Impersonates Farmer
**Severity**: 🔴 **CRITICAL**  
**Likelihood**: High  
**Impact**: High

**Scenario**: Attacker creates fake farmer account to:
- Post fraudulent crop listings
- Steal payment information
- Damage platform reputation

**Current Mitigation**: ❌ NONE  
**Required Mitigation**:
- Phone number verification (SMS OTP)
- Government ID verification (Ethiopian Kebele ID)
- Multi-factor authentication
- Device fingerprinting

---

#### T1.2: Session Hijacking
**Severity**: 🔴 **CRITICAL**  
**Likelihood**: Medium  
**Impact**: High

**Scenario**: Attacker intercepts session token on 3G network

**Current Mitigation**: ❌ NONE  
**Required Mitigation**:
- HTTPS with certificate pinning
- Short-lived JWT tokens (15 min)
- Refresh token rotation
- IP address validation

---

### 2. Tampering (Data Integrity Threats)

#### T2.1: Crop Listing Manipulation
**Severity**: 🔴 **CRITICAL**  
**Likelihood**: High  
**Impact**: High

**Scenario**: Attacker modifies crop prices or quantities

**Current Mitigation**: ❌ NONE  
**Required Mitigation**:
- Digital signatures on listings
- Blockchain-style audit trail
- Server-side validation
- Conflict resolution with timestamps

---

#### T2.2: Local Database Tampering
**Severity**: 🟠 **HIGH**  
**Likelihood**: Medium (rooted devices)  
**Impact**: High

**Scenario**: Attacker modifies Isar database on rooted device

**Current Mitigation**: ❌ NONE  
**Required Mitigation**:
- Database encryption
- Integrity checks (HMAC)
- Root detection
- Server-side validation of all data

---

### 3. Repudiation (Non-Repudiation Threats)

#### T3.1: Farmer Denies Creating Listing
**Severity**: 🟠 **HIGH**  
**Likelihood**: Medium  
**Impact**: Medium

**Scenario**: Farmer claims they didn't create a crop listing

**Current Mitigation**: ❌ NONE  
**Required Mitigation**:
- Audit logs with timestamps
- Digital signatures
- CloudWatch Logs retention (1 year)
- Non-repudiation receipts

---

### 4. Information Disclosure (Privacy Threats)

#### T4.1: Farmer Location Tracking
**Severity**: 🔴 **CRITICAL**  
**Likelihood**: High  
**Impact**: High

**Scenario**: Attacker tracks farmer GPS coordinates

**Current Mitigation**: ❌ NONE  
**Required Mitigation**:
- Location data encryption
- Fuzzy location (village-level, not exact GPS)
- User consent for location sharing
- Data minimization (delete old locations)

---

#### T4.2: Database Breach
**Severity**: 🔴 **CRITICAL**  
**Likelihood**: Low  
**Impact**: Critical

**Scenario**: Attacker gains access to DynamoDB

**Current Mitigation**: ⚠️ PARTIAL (PITR enabled)  
**Required Mitigation**:
- Encryption at rest (AWS KMS)
- Encryption in transit (TLS 1.3)
- IAM least privilege
- VPC endpoints (no public access)
- AWS GuardDuty monitoring

---

#### T4.3: Unencrypted Mobile Database
**Severity**: 🔴 **CRITICAL**  
**Likelihood**: High (device theft)  
**Impact**: High

**Scenario**: Stolen device exposes farmer data

**Current Mitigation**: ❌ NONE  
**Required Mitigation**:
- Isar database encryption
- Biometric authentication
- Remote wipe capability
- Data expiration (30 days offline)

---

### 5. Denial of Service (Availability Threats)

#### T5.1: API DDoS Attack
**Severity**: 🟠 **HIGH**  
**Likelihood**: Medium  
**Impact**: High

**Scenario**: Attacker floods API with requests

**Current Mitigation**: ❌ NONE  
**Required Mitigation**:
- API Gateway rate limiting (100 req/sec)
- AWS WAF rules
- CloudFront DDoS protection
- Lambda concurrency limits

---

#### T5.2: Sync Queue Overflow
**Severity**: 🟡 **MEDIUM**  
**Likelihood**: Low  
**Impact**: Medium

**Scenario**: Offline queue grows too large, crashes app

**Current Mitigation**: ❌ NONE  
**Required Mitigation**:
- Queue size limits (1000 items)
- Batch processing
- Exponential backoff
- User notification

---

### 6. Elevation of Privilege (Authorization Threats)

#### T6.1: Farmer Accesses Other Farmer's Data
**Severity**: 🔴 **CRITICAL**  
**Likelihood**: High  
**Impact**: High

**Scenario**: Farmer modifies API request to access other user's crops

**Current Mitigation**: ❌ NONE  
**Required Mitigation**:
- Row-level security (DynamoDB)
- User ID validation on every request
- Attribute-based access control (ABAC)
- API Gateway authorizer

---

#### T6.2: Lambda Function Privilege Escalation
**Severity**: 🟠 **HIGH**  
**Likelihood**: Low  
**Impact**: High

**Scenario**: Compromised Lambda accesses unrelated resources

**Current Mitigation**: ❌ NONE  
**Required Mitigation**:
- Separate IAM roles per function
- Least privilege policies
- Resource-based policies
- VPC isolation

---

## Ethiopian Context-Specific Threats

### T7.1: SIM Swap Attack
**Severity**: 🔴 **CRITICAL**  
**Likelihood**: High (common in Ethiopia)  
**Impact**: High

**Scenario**: Attacker swaps farmer's SIM card to bypass SMS OTP

**Mitigation**:
- Multi-factor authentication (not just SMS)
- Device binding
- Behavioral analytics
- Telebirr integration (trusted payment)

---

### T7.2: Shared Device Access
**Severity**: 🟠 **HIGH**  
**Likelihood**: High (rural context)  
**Impact**: Medium

**Scenario**: Multiple farmers share one device

**Mitigation**:
- Biometric authentication
- Session timeout (5 min)
- Explicit logout
- User switching UI

---

### T7.3: Low-Literacy Phishing
**Severity**: 🟠 **HIGH**  
**Likelihood**: High  
**Impact**: Medium

**Scenario**: Farmers tricked by fake SMS/calls

**Mitigation**:
- Voice-based authentication
- Visual verification (photos)
- In-app warnings
- Community education

---

## Risk Matrix

| Threat ID | Severity | Likelihood | Risk Score | Status |
|-----------|----------|------------|------------|--------|
| T1.1 | Critical | High | 🔴 9.8 | ❌ Not Mitigated |
| T1.2 | Critical | Medium | 🔴 8.5 | ❌ Not Mitigated |
| T2.1 | Critical | High | 🔴 9.1 | ❌ Not Mitigated |
| T2.2 | High | Medium | 🟠 7.5 | ❌ Not Mitigated |
| T3.1 | High | Medium | 🟠 6.8 | ❌ Not Mitigated |
| T4.1 | Critical | High | 🔴 9.5 | ❌ Not Mitigated |
| T4.2 | Critical | Low | 🟠 7.2 | ⚠️ Partial |
| T4.3 | Critical | High | 🔴 9.2 | ❌ Not Mitigated |
| T5.1 | High | Medium | 🟠 7.8 | ❌ Not Mitigated |
| T5.2 | Medium | Low | 🟡 4.5 | ❌ Not Mitigated |
| T6.1 | Critical | High | 🔴 9.8 | ❌ Not Mitigated |
| T6.2 | High | Low | 🟠 6.5 | ❌ Not Mitigated |
| T7.1 | Critical | High | 🔴 9.0 | ❌ Not Mitigated |
| T7.2 | High | High | 🟠 8.0 | ❌ Not Mitigated |
| T7.3 | High | High | 🟠 7.5 | ❌ Not Mitigated |

**Overall Risk**: 🔴 **CRITICAL** - 8 Critical, 7 High, 0 Mitigated

---

**Threat Model Owner**: Security Agent  
**Next Review**: After authentication implementation  
**Version History**: v1.0 (2026-02-10) - Initial threat model

