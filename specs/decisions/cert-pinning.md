# Certificate Pinning - Decision Record

**Status**: Foundation Implemented (Not Active)
**Date**: 2026-02-14
**Sprint**: Sprint 3
**Related**: F-16 (Lambda Deployment), F-04 (Security)

## Context

WeredaConnect mobile app communicates with backend APIs over HTTPS in Ethiopian rural areas with 3G connectivity. These networks are particularly vulnerable to Man-in-the-Middle (MitM) attacks due to:
- Limited infrastructure security
- Potential carrier-level interception
- Public WiFi hotspots with poor security
- Sensitive data (auth tokens, user credentials, Telebirr payment info)

Certificate pinning provides an additional security layer beyond standard HTTPS by validating that the server's certificate matches a known, trusted certificate hash.

## Decision

Implement certificate pinning using Dio's `IOHttpClientAdapter` with hardcoded SHA-256 certificate hashes. The implementation follows a phased approach:

### Phase 1: Foundation (Current - Sprint 3)
- Create `ApiClient` singleton with certificate pinning infrastructure
- Create `ApiConfig` for centralized endpoint management
- Integrate `ApiClient` into `AuthService`
- **Certificate pinning DISABLED by default** (placeholder hashes only)
- Document extraction and rotation procedures

### Phase 2: Activation (Sprint 4 - after F-16 deployment)
- Deploy Lambda API Gateway
- Extract actual certificate SHA-256 hashes
- Update `ApiClient.certificatePins` with real values
- Enable certificate pinning in production builds
- Test with production API

## Implementation Details

### Certificate Extraction Procedure

After F-16 Lambda deployment, extract certificate hashes using OpenSSL:

```bash
# Method 1: Direct connection
openssl s_client -connect api.weredaconnect.et:443 -servername api.weredaconnect.et < /dev/null 2>/dev/null | \
  openssl x509 -pubkey -noout | \
  openssl rsa -pubin -outform der 2>/dev/null | \
  openssl dgst -sha256 -binary | \
  openssl enc -base64

# Method 2: From certificate file
openssl x509 -in cert.pem -pubkey -noout | \
  openssl rsa -pubin -outform der 2>/dev/null | \
  openssl dgst -sha256 -binary | \
  openssl enc -base64
```

The output is a Base64-encoded SHA-256 hash (e.g., `AAAA...BBB=`). Add it to `ApiClient.certificatePins` with `sha256/` prefix.

### Certificate Rotation Strategy

SSL certificates expire (typically 1 year for AWS ACM). To prevent app breakage during rotation:

1. **Dual Pinning**: Always pin BOTH current and next certificate
   - Primary cert: Currently active certificate
   - Backup cert: Next certificate (obtained before renewal)

2. **Rotation Timeline**:
   - 30 days before expiry: Obtain new certificate hash from AWS
   - 21 days before expiry: Release app update with both old + new pins
   - 7 days after release: Rotate server certificate
   - Old certificate remains pinned for 60 days (backward compatibility)

3. **Emergency Bypass**:
   - In case of emergency rotation, release hotfix app update
   - Monitor crash analytics for certificate pinning failures
   - Use feature flag to temporarily disable pinning if needed

### Current Implementation Status

**Files Created**:
- `/mobile/lib/core/network/api_client.dart` - Dio client with cert pinning infrastructure
- `/mobile/lib/core/network/api_config.dart` - Centralized API configuration
- `/specs/decisions/cert-pinning.md` - This document

**Files Modified**:
- `/mobile/lib/core/auth/auth_service.dart` - Integrated ApiClient
- `/mobile/pubspec.yaml` - Updated connectivity_plus to 7.0.0

**Certificate Pinning Status**: DISABLED
- `ApiClient.initialize()` defaults to `enableCertPinning: false`
- Placeholder certificate hashes are used
- Actual pinning verification is TODO (see code comments)

**ConnectivityResult API**: FIXED
- Updated from `ConnectivityResult result` to `List<ConnectivityResult> results`
- Fixed 6 occurrences in `auth_service.dart`
- Upgraded `connectivity_plus` from 5.0.2 to 7.0.0

## Testing Strategy

### Phase 1 (Current)
- ✅ Verify `flutter analyze` passes (no ConnectivityResult errors)
- ✅ Verify ApiClient initializes without errors
- ✅ Verify auth service uses ApiConfig endpoints
- ⏸️ Certificate pinning tests skipped (not active yet)

### Phase 2 (After F-16 Deployment)
- [ ] Test with correct certificate pins
- [ ] Test with incorrect pins (should reject connection)
- [ ] Test certificate rotation scenario
- [ ] Test on real 3G networks in Ethiopia
- [ ] Monitor crash analytics for pinning failures

## Security Considerations

**Threats Mitigated**:
- MitM attacks on public WiFi
- Carrier-level traffic interception
- Rogue certificate authorities
- DNS hijacking + fake certificates

**Threats NOT Mitigated**:
- Compromised device (rooted/jailbroken)
- Malware with root access
- SSL stripping attacks (if user ignores warnings)
- Attacks before initial pin establishment

**Additional Recommendations**:
- Use HTTPS-only (no HTTP fallback)
- Implement certificate transparency monitoring
- Add network security config for Android (backup)
- Monitor AWS ACM certificate expiration dates

## Ethiopian Context

**3G Network Risks**:
- Limited infrastructure monitoring
- Potential carrier-level interception for government surveillance
- Farmers/truckers often use public WiFi at cafes/markets
- Low digital literacy = users may ignore security warnings

**User Impact**:
- Certificate pinning adds ~50ms latency (negligible on 3G)
- Failed pinning = app refuses connection (safe failure mode)
- Requires app update for certificate rotation (acceptable trade-off)

## References

- [OWASP Mobile Security Guide - Certificate Pinning](https://owasp.org/www-community/controls/Certificate_and_Public_Key_Pinning)
- [Dio Documentation - HttpClientAdapter](https://pub.dev/documentation/dio/latest/dio/HttpClientAdapter-class.html)
- [AWS ACM Certificate Renewal](https://docs.aws.amazon.com/acm/latest/userguide/managed-renewal.html)

## Next Steps

1. **Sprint 4**: Complete F-16 Lambda deployment
2. **Sprint 4**: Extract real certificate hashes
3. **Sprint 4**: Update `ApiClient.certificatePins`
4. **Sprint 4**: Implement actual SHA-256 verification (use package:crypto)
5. **Sprint 4**: Enable pinning in production builds
6. **Sprint 5**: Add monitoring and alerting for pinning failures
