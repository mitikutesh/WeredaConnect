# F-05: Certificate Pinning Activation Assessment

**Status**: Superseded 2026-09-24 by CA pinning (see update below); original assessment kept for history
**Date**: 2026-02-17
**API Endpoint**: `https://s0om0hib4i.execute-api.me-south-1.amazonaws.com`
**Decision Record**: `specs/decisions/cert-pinning.md`


---

## 2026-09-24 update: pinning now uses CA pinning (Amazon Trust Services roots)

**Why the SPKI approach below was replaced.** The 2026-09-24 synthetic-fleet run (SIM-01) found every
Android build unable to reach the API: the leaf pin extracted 2026-04-07 was stale after AWS rotated
the API Gateway leaf on 2026-06-26, and with `SecurityContext(withTrustedRoots: false)` dart:io hands
`badCertificateCallback` only the certificate that failed verification (the untrusted ROOT), so the
leaf/intermediate pins were never even compared. Pinning a root SPKI inside that callback would be
bypassable because the callback never proves the leaf chains to it.

**What runs now** (`mobile/lib/core/network/cert_pinning_io.dart`):
- `SecurityContext(withTrustedRoots: false)` + `setTrustedCertificatesBytes(AmazonTrustRoots.pem)`.
  BoringSSL validates the full chain and hostname against the five Amazon Trust Services roots
  (Amazon Root CA 1-4, Starfield Services Root G2; expiry 2037-2040) bundled in
  `mobile/lib/core/network/amazon_trust_roots.dart`. Any other CA is rejected (fail closed).
- Leaf and intermediate rotations by AWS no longer require an app release.

**Rotation / incident procedure**
1. `./scripts/check_cert_pins.sh` (also runs in `mobile-tests.yml`) verifies the LIVE chains of the API
   and webapp hosts against the bundle. A FAIL means AWS moved a host to a CA outside the bundle.
2. Download the new root from https://www.amazontrust.com/repository/, verify its SPKI hash
   (`openssl x509 -pubkey -noout | openssl pkey -pubin -outform der | openssl dgst -sha256 -binary | base64`)
   against the value published by Amazon, append the PEM to `AmazonTrustRoots.pem` and the hash to
   `AmazonTrustRoots.spkiSha256`, run `flutter test test/core/network/cert_pinning_test.dart`.
3. Ship a mobile release. Emergency fallback: `ApiClient().initialize(enableCertPinning: false)` is a
   code-level kill switch only (no remote flag yet, see "What is missing" item 5 below).

Everything below this line is the original 2026-02-17 assessment, kept for history.

---

## Current Implementation Status

### What exists

| Component | File | Status |
|-----------|------|--------|
| ApiClient with pinning infrastructure | `mobile/lib/core/network/api_client.dart` | Done (disabled) |
| Placeholder certificate hashes | `ApiClient.certificatePins` | Placeholder values |
| `enableCertPinning` parameter | `ApiClient.initialize()` | Defaults to `false` |
| `badCertificateCallback` handler | `ApiClient.initialize()` | Stub only - returns `false` |
| Decision record | `specs/decisions/cert-pinning.md` | Complete |

### What is missing

1. **Real certificate hashes** - placeholders `AAA...` and `BBB...` must be replaced
2. **Actual SPKI verification logic** - the `badCertificateCallback` is a stub that always rejects
3. **SHA-256 hashing dependency** - needs `package:crypto` (or `dart:convert` + `dart:typed_data`)
4. **Integration test** - no test for pinning verification
5. **Feature flag** - no remote kill switch for emergency disable

---

## Certificate Chain Analysis (Extracted 2026-02-17)

The following SPKI (Subject Public Key Info) SHA-256 hashes were extracted from the live API endpoint using OpenSSL.

### Certificate 0: Leaf (API Gateway)

| Field | Value |
|-------|-------|
| Subject | `CN=*.execute-api.me-south-1.amazonaws.com` |
| Issuer | `C=US, O=Amazon, CN=Amazon RSA 2048 M03` |
| Valid From | 2025-07-28 |
| Valid Until | **2026-08-24** |
| SPKI Pin | `sha256/YmAL9HFVfF2eJQzG/+cPUiuxcVHBMQxOS28Z8J4H3Tw=` |

### Certificate 1: Intermediate CA

| Field | Value |
|-------|-------|
| Subject | `C=US, O=Amazon, CN=Amazon RSA 2048 M03` |
| Issuer | `C=US, O=Amazon, CN=Amazon Root CA 1` |
| Valid From | 2022-08-23 |
| Valid Until | **2030-08-23** |
| SPKI Pin | `sha256/vxRon/El5KuI4vx5ey1DgmsYmRY0nDd5Cg4GfJ8S+bg=` |

### Certificate 2: Root CA (cross-signed)

| Field | Value |
|-------|-------|
| Subject | `C=US, O=Amazon, CN=Amazon Root CA 1` |
| Issuer | `Starfield Services Root Certificate Authority - G2` |
| Valid From | 2015-05-25 |
| Valid Until | **2037-12-31** |
| SPKI Pin | `sha256/++MBgDH5WGvL9Bcn5Be30cRcL0f5O+NyoXuWtQdX1aI=` |

### Pinning Recommendation

Pin the **intermediate CA** (Certificate 1) as primary, and the **leaf** (Certificate 0) as backup:

```dart
static const List<String> certificatePins = [
  'sha256/vxRon/El5KuI4vx5ey1DgmsYmRY0nDd5Cg4GfJ8S+bg=', // Amazon RSA 2048 M03 (intermediate, expires 2030)
  'sha256/YmAL9HFVfF2eJQzG/+cPUiuxcVHBMQxOS28Z8J4H3Tw=', // Leaf cert (expires 2026-08-24)
];
```

**Why pin the intermediate?**
- The leaf certificate rotates frequently (AWS ACM auto-renews ~60 days before expiry)
- Pinning only the leaf would break the app every time AWS rotates the certificate
- The intermediate CA (`Amazon RSA 2048 M03`) is stable until 2030
- Including the leaf as a secondary pin provides defense-in-depth

---

## Steps to Activate Pinning

### Step 1: Add `crypto` dependency

```bash
cd mobile
flutter pub add crypto
```

This provides SHA-256 hashing needed for SPKI pin verification.

### Step 2: Update certificate pins in `api_client.dart`

Replace the placeholder hashes at `mobile/lib/core/network/api_client.dart:50-53`:

```dart
static const List<String> certificatePins = [
  'sha256/vxRon/El5KuI4vx5ey1DgmsYmRY0nDd5Cg4GfJ8S+bg=', // Amazon RSA 2048 M03 intermediate (expires 2030-08-23)
  'sha256/YmAL9HFVfF2eJQzG/+cPUiuxcVHBMQxOS28Z8J4H3Tw=', // Leaf *.execute-api.me-south-1 (expires 2026-08-24)
];
```

### Step 3: Implement the verification logic in `badCertificateCallback`

Replace the stub in `api_client.dart:95-106` with actual SPKI pin verification:

```dart
import 'dart:convert';
import 'dart:typed_data';
import 'package:crypto/crypto.dart';

// Inside badCertificateCallback:
client.badCertificateCallback = (X509Certificate cert, String host, int port) {
  // Only pin for our API host
  if (!host.endsWith('.execute-api.me-south-1.amazonaws.com')) {
    return false;
  }

  // Compute SPKI SHA-256 hash of the certificate
  final derBytes = cert.der;
  final digest = sha256.convert(derBytes);
  final pin = 'sha256/${base64.encode(digest.bytes)}';

  return certificatePins.contains(pin);
};
```

**Important note on Dart's X509Certificate**: Dart's `X509Certificate.der` returns the full certificate DER encoding, not just the SPKI. For true SPKI pinning, you need to extract the SubjectPublicKeyInfo field from the DER-encoded certificate. Consider using `package:asn1lib` to parse the DER and extract SPKI, or pin the full certificate hash instead. If pinning the full certificate DER hash (simpler but rotates more often), extract hashes accordingly:

```bash
# Full certificate DER hash (alternative to SPKI pinning)
openssl x509 -in cert.pem -outform der | openssl dgst -sha256 -binary | openssl enc -base64
```

### Step 4: Enable pinning in production builds

In the app initialization code, enable pinning for production only:

```dart
ApiClient().initialize(
  enableCertPinning: Environment.config.isProduction,
);
```

### Step 5: Add Android network security config (defense-in-depth)

Create `mobile/android/app/src/main/res/xml/network_security_config.xml`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<network-security-config>
    <domain-config cleartextTrafficPermitted="false">
        <domain includeSubdomains="true">execute-api.me-south-1.amazonaws.com</domain>
        <pin-set expiration="2026-08-24">
            <pin digest="SHA-256">vxRon/El5KuI4vx5ey1DgmsYmRY0nDd5Cg4GfJ8S+bg=</pin>
            <pin digest="SHA-256">YmAL9HFVfF2eJQzG/+cPUiuxcVHBMQxOS28Z8J4H3Tw=</pin>
        </pin-set>
    </domain-config>
</network-security-config>
```

Reference it in `AndroidManifest.xml`:

```xml
<application
    android:networkSecurityConfig="@xml/network_security_config"
    ...>
```

---

## Testing Plan

### Unit Tests

- [ ] Verify `ApiClient` initializes with `enableCertPinning: true` without crashing
- [ ] Verify `badCertificateCallback` accepts a certificate matching a pinned hash
- [ ] Verify `badCertificateCallback` rejects a certificate NOT matching any pinned hash

### Integration Tests

- [ ] Test GET request to production API with correct pins -- should succeed
- [ ] Test GET request with wrong pins -- should throw `DioExceptionType.badCertificate`
- [ ] Test with expired/self-signed certificate -- should reject

### Manual Tests

- [ ] Test on real 3G network in Ethiopia
- [ ] Test on WiFi with proxy (e.g., Charles Proxy) -- should fail (MitM detected)
- [ ] Test on low-end device (2GB RAM, Android 8+) -- no performance regression
- [ ] Verify pinning failure produces clear error message to user (not a crash)

### Regression

- [ ] Verify all existing API calls still work after enabling pinning
- [ ] Verify auth flow (login, token refresh) works with pinning enabled
- [ ] Verify sync engine works with pinning enabled

---

## Certificate Rotation Strategy

### Timeline

The current leaf certificate expires **2026-08-24**. AWS ACM auto-renews managed certificates ~60 days before expiry.

| When | Action |
|------|--------|
| 2026-06-24 (T-60 days) | AWS may auto-renew. Monitor ACM console. |
| 2026-07-01 (T-54 days) | Extract new leaf certificate SPKI hash |
| 2026-07-07 (T-48 days) | Release app update with old + new leaf pins |
| 2026-07-21 (T-34 days) | Allow 2 weeks for app update adoption |
| 2026-08-24 | Old leaf certificate expires (intermediate pin still valid) |

### Why the intermediate pin provides a safety net

Because we pin the intermediate CA (`Amazon RSA 2048 M03`, valid until 2030), even if the leaf certificate rotates and the app is not updated, the intermediate pin will still match. This prevents app breakage during leaf rotation.

The intermediate pin will only fail if:
- AWS changes their intermediate CA (rare, ~8-year cycle)
- The intermediate CA is compromised and revoked (extremely rare)

### Emergency Procedures

If certificate pinning causes widespread connection failures:

1. **Immediate**: Release hotfix app update with pinning disabled (`enableCertPinning: false`)
2. **Short-term**: Extract new hashes, update pins, release fix
3. **Consider**: Adding a remote feature flag to disable pinning without an app update (trade-off: the flag endpoint itself cannot be pinned)

---

## Hash Re-extraction Command Reference

To re-extract hashes at any time (e.g., after certificate rotation):

```bash
# Full chain with SPKI pins
API_HOST="s0om0hib4i.execute-api.me-south-1.amazonaws.com"

echo | openssl s_client -showcerts -connect ${API_HOST}:443 \
  -servername ${API_HOST} 2>/dev/null | \
  openssl x509 -pubkey -noout | \
  openssl pkey -pubin -outform der | \
  openssl dgst -sha256 -binary | \
  openssl enc -base64
# Outputs the leaf SPKI hash

# For certificate details
echo | openssl s_client -connect ${API_HOST}:443 \
  -servername ${API_HOST} 2>/dev/null | \
  openssl x509 -text -noout | head -20
```

---

## References

- Decision record: `specs/decisions/cert-pinning.md`
- Implementation: `mobile/lib/core/network/api_client.dart`
- OWASP Certificate Pinning Guide: https://owasp.org/www-community/controls/Certificate_and_Public_Key_Pinning
- AWS ACM Managed Renewal: https://docs.aws.amazon.com/acm/latest/userguide/managed-renewal.html
