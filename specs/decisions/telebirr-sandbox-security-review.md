# Telebirr Sandbox Integration -- Security Review

**Status**: APPROVED_WITH_CHANGES
**Date**: 2026-02-24
**Reviewer**: Security Agent
**Reviewed Document**: `specs/decisions/telebirr-sandbox-architecture.md` v1.0.0

---

## 1. Executive Summary

The Telebirr sandbox architecture describes a platform-managed escrow model where WeredaConnect collects buyer payments via Telebirr's H5 C2B API and holds them in its own business account until delivery is confirmed. The architecture introduces several new attack surfaces: a publicly accessible webhook endpoint without JWT authentication, RSA2048 cryptographic operations for payload encryption and signing, AWS Secrets Manager credential loading at Lambda cold start, and a NAT Gateway for static outbound IP. The design also acknowledges that release and refund operations are simulated (logged intent only) during the sandbox phase, deferring actual B2B disbursement to Phase 2.

Overall, the architecture demonstrates solid security awareness -- credentials in Secrets Manager rather than environment variables, idempotent webhook processing, source IP validation, and an append-only audit trail. However, the review identified **two critical (P0) findings** that must be addressed before any deployment: the `DecryptWebhookPayload` method incorrectly uses an RSA public key for decryption (cryptographically impossible for OAEP), and the SHA256 "signing" mechanism uses simple hash concatenation rather than HMAC, providing no asymmetric authentication guarantee. Additionally, **three high-severity (P1) findings** cover webhook authentication gaps, timestamp validation clock-skew risks, and the `toPayUrl` being stored as `TelebirrRef` (leaking a payment session URL into audit logs and API responses).

The Ethiopian deployment context introduces specific threats that are partially addressed: shared-device risks, SIM swap attacks against Telebirr phone-number-based identity, rural 3G network interception, and mobile money fraud patterns. The architecture's offline-first design and idempotency mechanisms are well-suited to intermittent connectivity, but the webhook endpoint and credential management require hardening before even sandbox testing with real Telebirr credentials.

---

## 2. Findings

### Summary Table

| ID | Title | Severity | Component | Status |
|----|-------|----------|-----------|--------|
| SEC-01 | RSA public key used for decryption (cryptographically invalid) | **P0 Critical** | TelebirrCryptoService | Open |
| SEC-02 | SHA256 signing uses hash concatenation, not HMAC or RSA signature | **P0 Critical** | TelebirrCryptoService | Open |
| SEC-03 | Webhook authentication relies solely on IP whitelisting | **P1 High** | TelebirrWebhookFunction | Open |
| SEC-04 | Timestamp validation vulnerable to clock skew and replay | **P1 High** | TelebirrSandboxGateway | Open |
| SEC-05 | toPayUrl stored as TelebirrRef leaks payment session URL | **P1 High** | TelebirrSandboxGateway | Open |
| SEC-06 | Cold-start secret loading has no retry or circuit breaker | **P2 Medium** | Credential Management | Open |
| SEC-07 | Webhook returns differentiated HTTP status codes (information leak) | **P2 Medium** | TelebirrWebhookFunction | Open |
| SEC-08 | No rate limiting on webhook endpoint | **P2 Medium** | TelebirrWebhookFunction | Open |
| SEC-09 | RSA encryption block size limit not addressed | **P2 Medium** | TelebirrCryptoService | Open |
| SEC-10 | AllowedSourceIps parsed from comma-delimited string in JSON | **P2 Medium** | TelebirrConfig | Open |
| SEC-11 | No certificate pinning for Telebirr API endpoints | **P3 Low** | TelebirrSandboxGateway | Open |
| SEC-12 | NAT Gateway single point of failure for payment operations | **P3 Low** | Infrastructure | Open |
| SEC-13 | Simulated release/refund returns predictable reference format | **P3 Low** | TelebirrSandboxGateway | Open |
| SEC-14 | GetTransactionStatusAsync always returns "unknown" | **P3 Low** | TelebirrSandboxGateway | Open |
| SEC-15 | Source IP logged in plaintext in webhook rejection | **P3 Low** | TelebirrWebhookFunction | Open |

---

### SEC-01: RSA public key used for decryption (cryptographically invalid)

**Severity**: P0 Critical
**Component**: `TelebirrCryptoService.DecryptWebhookPayload()`
**STRIDE**: Tampering, Information Disclosure

**Description**:

The architecture document shows `DecryptWebhookPayload` calling `_rsaPublicKey.Decrypt(...)`:

```csharp
public string DecryptWebhookPayload(string encryptedPayload)
{
    var encryptedBytes = Convert.FromBase64String(encryptedPayload);
    var decrypted = _rsaPublicKey.Decrypt(encryptedBytes, RSAEncryptionPadding.OaepSHA256);
    return Encoding.UTF8.GetString(decrypted);
}
```

This is **cryptographically impossible** with RSA OAEP. Decryption requires the **private key**, not the public key. With OAEP padding, `RSA.Decrypt()` will throw a `CryptographicException` at runtime because the public key cannot perform the OAEP unpadding operation.

There are two possible interpretations of how Telebirr's webhook encryption actually works:

1. **Telebirr encrypts with WeredaConnect's public key** -- then WeredaConnect must decrypt with its own **private key**. This means a separate RSA key pair must be generated by WeredaConnect and the public key shared with Telebirr.
2. **Telebirr signs with its own private key** -- then WeredaConnect verifies the signature with Telebirr's **public key**. This is signature verification, not decryption.
3. **Telebirr uses PKCS#1 v1.5 raw encryption with public key** -- some legacy payment APIs use RSA public key "encryption" in a non-standard way (essentially raw modular exponentiation). This would work with `RSAEncryptionPadding.Pkcs1` but NOT with OAEP.

**Recommendation**:
- **Before any implementation**, obtain Telebirr's actual webhook encryption specification. Verify the exact RSA operation (encryption vs signing), padding mode (OAEP vs PKCS1v15 vs raw), and which party's key is used.
- If interpretation (1): Generate a WeredaConnect RSA key pair, store the private key in Secrets Manager, share the public key with Telebirr during merchant onboarding.
- If interpretation (3): Change padding to `RSAEncryptionPadding.Pkcs1` and add a large warning comment about the non-standard usage.
- Never use `_rsaPublicKey.Decrypt()` with OAEP -- it will always fail.

---

### SEC-02: SHA256 signing uses hash concatenation, not HMAC or RSA signature

**Severity**: P0 Critical
**Component**: `TelebirrCryptoService.SignPayload()`
**STRIDE**: Tampering, Spoofing

**Description**:

The signing implementation concatenates sorted parameters with `appSecret` and hashes them:

```csharp
var dataToSign = sortedParams + _appSecret;
var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(dataToSign));
return Convert.ToHexStringLower(hashBytes);
```

This is a **keyed hash** (secret-suffix construction), not a proper HMAC. The secret-suffix pattern (`H(message || secret)`) is vulnerable to length-extension attacks with SHA256 because SHA256 is based on Merkle-Damgard construction. An attacker who knows `H(message || secret)` can compute `H(message || secret || padding || extension)` without knowing the secret.

While this may match Telebirr's specification (many payment APIs use this pattern), it is important to verify the exact signing algorithm Telebirr expects:

1. **Secret-suffix hash**: `SHA256(sorted_params + secret)` -- vulnerable to length extension, but may be what Telebirr requires.
2. **HMAC-SHA256**: `HMAC(secret, sorted_params)` -- cryptographically secure, resistant to length extension.
3. **Secret-prefix hash**: `SHA256(secret + sorted_params)` -- also vulnerable, but in a different way.

**Recommendation**:
- Confirm the exact signing algorithm from Telebirr's API documentation. If Telebirr specifies `SHA256(params + secret)`, document the length-extension risk and implement exactly as specified (do not deviate from the payment provider's spec).
- If there is flexibility, prefer `HMAC-SHA256` using `System.Security.Cryptography.HMACSHA256`.
- Add a code comment documenting why this pattern is used (Telebirr specification compliance) so future developers do not "improve" it into incompatibility.
- Regardless of Telebirr's spec, this signature only provides server-to-Telebirr authentication. It does not authenticate Telebirr-to-server (webhook direction). Webhook authentication is a separate concern (see SEC-03).

---

### SEC-03: Webhook authentication relies solely on IP whitelisting

**Severity**: P1 High
**Component**: `TelebirrWebhookFunction`
**STRIDE**: Spoofing

**Description**:

The webhook endpoint authenticates incoming requests using only source IP validation:

```csharp
if (!IsAllowedSourceIp(sourceIp))
{
    context.Logger.LogWarning($"Webhook rejected: unauthorized source IP {sourceIp}");
    return LambdaResponse.Error(403, "WEBHOOK_FORBIDDEN", "Unauthorized source.");
}
```

IP-based authentication has several weaknesses:

1. **API Gateway may report a proxy IP**: If there is a CDN, load balancer, or AWS infrastructure between Telebirr and the Lambda, the `SourceIp` field may contain an intermediary IP, not Telebirr's actual IP. The `X-Forwarded-For` header must be parsed carefully (and it can be spoofed by the original client).
2. **IP ranges change**: Telebirr may change their outbound IPs without notice, causing webhook delivery failures. There is no documented process for staying informed of IP changes.
3. **IP spoofing**: While TCP-level IP spoofing is difficult (requires completing the TCP handshake), it is not impossible in certain network configurations. More importantly, if Telebirr routes through a shared NAT or cloud provider, other tenants of that NAT may share the same outbound IP.
4. **IPv4 exhaustion**: In Ethiopia, carrier-grade NAT is common. Telebirr's outbound requests may traverse shared infrastructure.

**Recommendation**:
- **Layer 1 (keep)**: IP whitelisting as a first-pass filter. Parse `X-Forwarded-For` carefully (use the rightmost non-private IP, or rely on API Gateway's `requestContext.identity.sourceIp` which strips client-controlled headers).
- **Layer 2 (add)**: Validate the encrypted payload structure. If the payload cannot be decrypted/verified with Telebirr's key, reject it. This is the primary authentication mechanism -- only Telebirr (or someone with their private key) can produce valid encrypted payloads.
- **Layer 3 (add)**: Implement a webhook signature header check if Telebirr provides one (many payment APIs include a `X-Signature` or `X-Webhook-Signature` header).
- **Layer 4 (add)**: Add a nonce or timestamp check within the decrypted payload to prevent replay of old valid payloads (see SEC-04).
- Document the IP update procedure: who at WeredaConnect is responsible for updating `allowedSourceIps` in Secrets Manager when Telebirr communicates IP changes.

---

### SEC-04: Timestamp validation vulnerable to clock skew and replay

**Severity**: P1 High
**Component**: `TelebirrSandboxGateway.HoldFundsAsync()`
**STRIDE**: Spoofing, Tampering, Repudiation

**Description**:

The architecture specifies a 1-minute timestamp validation window for outbound requests:

> Timestamp validation (must be within 1 minute of server time)

Two issues:

1. **Outbound (server to Telebirr)**: Lambda instances use NTP-synchronized clocks, but if a Lambda instance experiences clock drift (rare but possible after long execution warm periods), requests will be rejected by Telebirr. The architecture does not address retry logic specific to timestamp rejection.

2. **Inbound (webhook)**: The webhook handler does not validate any timestamp in the decrypted payload. An attacker who obtains a valid historical webhook payload (e.g., from log files, network capture, or a compromised system) could replay it. The current idempotency check (`outTradeNo` lookup) prevents duplicate state transitions for the *same* transaction, but does not prevent:
   - Replaying a "payment failed" webhook for a transaction that actually succeeded (if the success webhook hasn't arrived yet).
   - Replaying a webhook for a transaction that has been manually reconciled.

**Recommendation**:
- For outbound requests: Generate timestamps immediately before sending. If Telebirr rejects with a timestamp error, regenerate the timestamp and retry once (not with the cached timestamp).
- For inbound webhooks: After decrypting the payload, validate that any timestamp field is within an acceptable window (e.g., 5 minutes). If Telebirr's payload does not include a timestamp, log this as a gap.
- Add a `processedWebhookIds` check using the combination of `outTradeNo + tradeNo + transactionStatus` to detect exact duplicate payloads (beyond just `outTradeNo` idempotency).
- Store the webhook reception timestamp in the audit trail for forensic analysis.

---

### SEC-05: toPayUrl stored as TelebirrRef leaks payment session URL

**Severity**: P1 High
**Component**: `TelebirrSandboxGateway.HoldFundsAsync()`
**STRIDE**: Information Disclosure

**Description**:

The `HoldFundsAsync` method stores the `toPayUrl` as the `TelebirrRef`:

```csharp
return new HoldFundsResponse
{
    Success = true,
    TelebirrRef = response.Data.ToPayUrl
    // Note: TelebirrRef here is the toPayUrl, not a transaction ID.
};
```

The `toPayUrl` is a session-specific payment URL (e.g., `https://h5pay.trade.pay/payId=RE9879T0972S`). Storing it in `TelebirrRef` causes it to:

1. **Appear in API responses** to the mobile client (via `GET /escrow/{id}` which returns `telebirrRef`).
2. **Be logged in the audit trail** (the audit entry includes `telebirrRef`).
3. **Be visible to admin users** viewing any escrow.
4. **Potentially be logged in CloudWatch** through standard request/response logging.

If the `toPayUrl` is a one-time-use session URL, exposure is limited. But if it can be reused or contains session tokens that reveal buyer identity or account details, this is a direct information disclosure.

The architecture document itself notes in section 15.5: "toPayUrl: Logged without query parameters" -- but the code stores the full URL including query parameters.

**Recommendation**:
- Do NOT store `toPayUrl` as `TelebirrRef`. Store it in a separate, short-lived field that is:
  - Returned to the mobile client exactly once (in the `InitiateEscrowResponse`).
  - Never persisted in the audit trail.
  - Never returned in subsequent `GET /escrow/{id}` calls.
- If a `TelebirrRef` is needed for reconciliation, wait for the webhook to provide the actual `tradeNo` (Telebirr's transaction reference).
- Until the webhook arrives, `TelebirrRef` should be `null` or a placeholder like `PENDING-WEBHOOK`.
- Ensure the `toPayUrl` is redacted from all CloudWatch log statements.

---

### SEC-06: Cold-start secret loading has no retry or circuit breaker

**Severity**: P2 Medium
**Component**: `CreateTelebirrGateway()` credential loading
**STRIDE**: Denial of Service

**Description**:

The `CreateTelebirrGateway()` method loads credentials synchronously at Lambda cold start:

```csharp
var secretResponse = secretsClient.GetSecretValueAsync(
    new GetSecretValueRequest { SecretId = secretArn }).GetAwaiter().GetResult();
```

If AWS Secrets Manager is temporarily unavailable (rare, but possible during regional incidents), this will throw an exception during Lambda initialization, causing the function to fail to start. Lambda will retry initialization, but:

1. **No backoff**: Lambda retries are immediate, potentially hammering Secrets Manager during an outage.
2. **No fallback**: If the secret cannot be loaded, the entire escrow system is down.
3. **Synchronous blocking**: `.GetAwaiter().GetResult()` blocks the thread. If Secrets Manager is slow (high latency during a degraded state), this blocks the Lambda init for up to the SDK default timeout (potentially 100s of seconds).
4. **Race condition**: If two concurrent Lambda instances cold-start simultaneously, they both hit Secrets Manager. This is normal behavior but should be noted for cost awareness (Secrets Manager charges per API call).

**Recommendation**:
- Add a timeout to the Secrets Manager call: `new AmazonSecretsManagerClient(new AmazonSecretsManagerConfig { Timeout = TimeSpan.FromSeconds(10) })`.
- Consider caching the secret in Lambda's `/tmp` filesystem (encrypted) with a TTL, reducing cold-start Secrets Manager calls for warm-start reuse. However, this introduces a cache invalidation concern -- acceptable for sandbox, needs review for production.
- Add a health check that logs whether credentials were successfully loaded (without logging the credential values).
- For production, consider using the Lambda Secrets Manager extension layer, which caches secrets transparently.

---

### SEC-07: Webhook returns differentiated HTTP status codes (information leak)

**Severity**: P2 Medium
**Component**: `TelebirrWebhookFunction`
**STRIDE**: Information Disclosure

**Description**:

The webhook handler returns different responses for different failure modes:

- `403` for unauthorized IP
- `200 OK` for successful processing
- `200 OK` for "no escrow found" (to prevent Telebirr retries)

The `403` response reveals to an attacker that:
1. The endpoint exists and is actively validating IPs.
2. Their specific IP is not whitelisted.

The architecture document (section 15.1) states: "Webhook returns 200 OK for all cases (no error details to external caller)" -- but the code contradicts this by returning `403`.

**Recommendation**:
- Return `200 OK` for ALL cases, including unauthorized IPs. The webhook should be a "black hole" to external observers.
- Log the rejection internally (as the architecture already does) for security monitoring.
- Differentiation can be done through CloudWatch metrics (e.g., `WebhookUnauthorizedIp` metric) rather than HTTP response codes.
- Update the code to match the stated design principle in section 15.1.

---

### SEC-08: No rate limiting on webhook endpoint

**Severity**: P2 Medium
**Component**: `TelebirrWebhookFunction`
**STRIDE**: Denial of Service

**Description**:

The architecture mentions API Gateway throttling (200 burst, 100 sustained) but this is the global API Gateway limit. There is no specific rate limiting for the webhook endpoint. An attacker who discovers the webhook URL could:

1. Send thousands of requests per second, consuming Lambda invocations and DynamoDB capacity.
2. Each request triggers IP validation, decryption attempts, and DynamoDB lookups -- even if ultimately rejected.
3. The `403` response (see SEC-07) would be returned quickly, but the Lambda invocation and CloudWatch logging still incur cost.

**Recommendation**:
- Add a route-specific throttle on `/webhooks/telebirr`: 10 requests/second sustained, 20 burst. Telebirr should not be sending more than a few webhooks per second in normal operation.
- Consider adding a WAF rule for the webhook endpoint that rate-limits by source IP.
- Ensure API Gateway's `429 Too Many Requests` response does not leak endpoint information.
- For production: add CloudWatch alarms on webhook invocation rate to detect anomalous traffic.

---

### SEC-09: RSA encryption block size limit not addressed

**Severity**: P2 Medium
**Component**: `TelebirrCryptoService.EncryptPayload()`
**STRIDE**: Denial of Service (self-inflicted)

**Description**:

RSA 2048 with OAEP-SHA256 padding can only encrypt payloads up to **190 bytes** (2048/8 - 2*32 - 2 = 190). The `TelebirrCreateOrderRequest` serialized to JSON will likely exceed this limit -- a typical request with all fields populated could be 400-600 bytes.

The code does:

```csharp
var json = SerializeForEncryption(request);
var jsonBytes = Encoding.UTF8.GetBytes(json);
var encrypted = _rsaPublicKey.Encrypt(jsonBytes, RSAEncryptionPadding.OaepSHA256);
```

This will throw a `CryptographicException: The message exceeds the maximum allowable length for the chosen options` at runtime.

Most payment APIs that use RSA for "encryption" actually use a hybrid approach:
1. Generate a random AES key.
2. Encrypt the payload with AES.
3. Encrypt the AES key with RSA.
4. Send both the RSA-encrypted key and AES-encrypted payload.

Alternatively, Telebirr may use RSA with PKCS#1 v1.5 padding (which allows 245 bytes for RSA-2048) and chunk the payload, or they may encrypt individual fields rather than the entire JSON.

**Recommendation**:
- Verify Telebirr's actual encryption specification. The payload almost certainly exceeds the RSA block size limit.
- Implement the exact encryption scheme Telebirr specifies (likely hybrid RSA+AES, or field-level encryption, or chunked RSA).
- Add a unit test that encrypts a realistic-size payload to catch this issue early.
- If PKCS#1 v1.5 is used instead of OAEP, document the security implications (PKCS#1 v1.5 is vulnerable to Bleichenbacher's attack, but this is acceptable for a payment API if Telebirr mandates it).

---

### SEC-10: AllowedSourceIps parsed from comma-delimited string in JSON

**Severity**: P2 Medium
**Component**: `TelebirrConfig.FromSecretJson()`
**STRIDE**: Tampering, Denial of Service

**Description**:

The IP whitelist is stored as a comma-delimited string in a `Dictionary<string, string>`:

```csharp
AllowedSourceIps = dict.ContainsKey("allowedSourceIps")
    ? dict["allowedSourceIps"].Split(',').ToList()
    : new List<string>()
```

Issues:
1. **No IP format validation**: Whitespace around IPs (e.g., `"1.2.3.4, 5.6.7.8"`) will produce entries like `" 5.6.7.8"` with a leading space, which will never match the source IP string.
2. **Empty fallback**: If `allowedSourceIps` is missing from the secret, the list is empty, meaning ALL webhook requests will be rejected (safe failure, but operationally surprising).
3. **No CIDR support**: If Telebirr provides an IP range (e.g., `10.0.0.0/24`), the simple string comparison will fail.
4. **JSON type mismatch**: The secret JSON shows `allowedSourceIps` as a JSON array `["x.x.x.x", "y.y.y.y"]`, but the code deserializes the entire secret as `Dictionary<string, string>`, which cannot represent arrays. The JSON array would be lost or cause a deserialization error.

**Recommendation**:
- Trim whitespace from each IP after splitting: `.Split(',').Select(ip => ip.Trim()).ToList()`.
- Add IP format validation (parse each entry as `System.Net.IPAddress` at config load time; reject invalid entries with a clear error log).
- Consider supporting CIDR notation using `System.Net.IPNetwork` or a simple prefix match.
- Fix the JSON deserialization: either store `allowedSourceIps` as a comma-separated string in the secret (matching the parse logic), or use a proper JSON model with an array field (requiring a custom deserialization approach since `Dictionary<string, string>` cannot represent arrays).
- Add an explicit check: if `AllowedSourceIps` is empty after parsing, log a warning at startup. Consider failing startup if no IPs are configured (fail-closed).

---

### SEC-11: No certificate pinning for Telebirr API endpoints

**Severity**: P3 Low
**Component**: `TelebirrSandboxGateway` (HttpClient)
**STRIDE**: Information Disclosure, Tampering

**Description**:

The `HttpClient` used for Telebirr API calls uses default TLS verification (system trust store). In Ethiopian rural networks, there is a risk of MitM attacks via compromised or rogue certificate authorities. The existing `cert-pinning.md` decision record addresses mobile-to-API-Gateway pinning but does not cover server-to-Telebirr pinning.

For sandbox, this is acceptable. The architecture document correctly notes certificate pinning as a Phase 3 (production) item.

**Recommendation**:
- Acceptable for sandbox. Track as a Phase 3 production requirement.
- For production: Pin the Telebirr API server certificate or its CA certificate in the `HttpClient` configuration using `HttpClientHandler.ServerCertificateCustomValidationCallback`.
- Document the certificate rotation procedure for the Telebirr endpoint.

---

### SEC-12: NAT Gateway single point of failure for payment operations

**Severity**: P3 Low
**Component**: Infrastructure (networking-stack)
**STRIDE**: Denial of Service

**Description**:

The architecture uses a single NAT Gateway for cost optimization. If the NAT Gateway fails or the AZ it resides in has an outage, all outbound Telebirr API calls will fail. This means:

- No new payments can be initiated.
- Reconciliation cron cannot check Telebirr status.
- Auto-release/auto-refund continues to work (operates on DynamoDB, no Telebirr call in Phase 1).

The architecture acknowledges this: "Single NAT is acceptable for sandbox; HA NAT for production."

**Recommendation**:
- Acceptable for sandbox.
- For production: Deploy NAT Gateways in at least 2 AZs. Estimated additional cost: ~$32/month.
- Add a CloudWatch alarm on NAT Gateway error metrics to detect failures quickly.
- Consider a NAT Gateway health check that triggers a SNS notification to the operations team.

---

### SEC-13: Simulated release/refund returns predictable reference format

**Severity**: P3 Low
**Component**: `TelebirrSandboxGateway.ReleaseFundsAsync()`, `RefundFundsAsync()`
**STRIDE**: Information Disclosure, Repudiation

**Description**:

The sandbox release and refund methods return predictable references:

```csharp
TelebirrRef = $"PAYOUT-PENDING-{request.TransactionRef}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
TelebirrRef = $"REFUND-PENDING-{request.TransactionRef}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"
```

These references:
1. Leak the `TransactionRef` (which is the idempotency key containing user IDs and timestamps).
2. Include a predictable timestamp.
3. Could be confused with real Telebirr references in reconciliation, since there is no clear marker that they are simulated.

**Recommendation**:
- Prefix simulated references with `SANDBOX-SIM-` to make it unambiguous that no real money movement occurred.
- Avoid including the full `TransactionRef` in the simulated reference. Use a hash or truncation instead.
- Add a `"simulated": true` field to the audit log entry for these operations.
- Ensure that reconciliation logic in Phase 2 does not accidentally treat sandbox references as real Telebirr references.

---

### SEC-14: GetTransactionStatusAsync always returns "unknown"

**Severity**: P3 Low
**Component**: `TelebirrSandboxGateway.GetTransactionStatusAsync()`
**STRIDE**: Repudiation

**Description**:

The method always returns `Status = "unknown"`, which means the reconciliation cron (which runs every 6 hours to check stuck "initiated" escrows) will never be able to reconcile transactions. During the sandbox phase, all reconciliation relies entirely on webhook delivery. If a webhook is missed, the escrow will remain stuck in "initiated" forever.

**Recommendation**:
- For sandbox: this is acceptable if the webhook is reliable. Document this limitation clearly.
- Add a secondary reconciliation mechanism: if an escrow has been in "initiated" state for more than 1 hour AND no webhook has been received, log an alert for manual investigation.
- For production: implement actual Telebirr status polling if the API supports it. If not, escalate stuck transactions to the operations team after a configurable threshold.

---

### SEC-15: Source IP logged in plaintext in webhook rejection

**Severity**: P3 Low
**Component**: `TelebirrWebhookFunction`
**STRIDE**: Information Disclosure (internal)

**Description**:

```csharp
context.Logger.LogWarning($"Webhook rejected: unauthorized source IP {sourceIp}");
```

Logging source IPs in CloudWatch is generally acceptable for security monitoring, but:
1. If CloudWatch logs are accessed by a broad team, the IP patterns of attempted attackers are visible.
2. Aggregated IP data could reveal Telebirr's infrastructure topology to anyone with log access.

This is low severity since CloudWatch access should be restricted by IAM.

**Recommendation**:
- Acceptable for sandbox. Ensure CloudWatch log groups have appropriate IAM restrictions.
- For production: consider logging a hash of the source IP for trend analysis, with the raw IP available only in a separate, more restricted log group.

---

## 3. STRIDE Analysis Matrix

### 3.1 TelebirrSandboxGateway

| Threat | Risk | Mitigation in Architecture | Gap |
|--------|------|---------------------------|-----|
| **Spoofing** | Attacker impersonates WeredaConnect to Telebirr | RSA encryption + SHA256 signing authenticates requests | Signing uses simple hash, not HMAC (SEC-02) |
| **Tampering** | Request parameters modified in transit | TLS 1.2+ transport encryption; RSA payload encryption | Payload encryption may fail due to block size (SEC-09) |
| **Repudiation** | WeredaConnect denies sending a payment request | Audit trail logs all Telebirr interactions | Audit trail records are adequate |
| **Information Disclosure** | Telebirr credentials leaked | Secrets Manager, never in env vars or logs | Cold-start loading lacks resilience (SEC-06) |
| **Denial of Service** | Telebirr API unavailable | 30-second timeout, retry with backoff | No circuit breaker; NAT Gateway SPOF (SEC-12) |
| **Elevation of Privilege** | Attacker escalates from payment initiation to fund release | Release/refund require system/admin JWT role | Sandbox simulated responses bypass Telebirr (by design for Phase 1) |

### 3.2 TelebirrCryptoService

| Threat | Risk | Mitigation in Architecture | Gap |
|--------|------|---------------------------|-----|
| **Spoofing** | Forged signed request accepted by Telebirr | SHA256 signing with appSecret | Hash-concatenation, not HMAC (SEC-02) |
| **Tampering** | Encrypted payload modified | RSA2048 encryption | Block size limit not addressed (SEC-09) |
| **Repudiation** | Dispute over whether payload was sent correctly | Signed payloads provide non-repudiation | SHA256 hash does not provide true non-repudiation (no asymmetric signature) |
| **Information Disclosure** | RSA key or appSecret leaked from memory | Keys loaded once at cold start, cached in memory | No memory protection (acceptable for Lambda's isolated execution model) |
| **Denial of Service** | Crypto operations fail | AOT-safe BCL crypto | Public key decrypt will always fail (SEC-01) |
| **Elevation of Privilege** | N/A | Crypto service has no authorization logic | -- |

### 3.3 TelebirrWebhookFunction

| Threat | Risk | Mitigation in Architecture | Gap |
|--------|------|---------------------------|-----|
| **Spoofing** | Attacker sends fake webhook to manipulate escrow state | IP whitelisting + encrypted payload validation | IP-only auth is weak (SEC-03); payload validation depends on fixing SEC-01 |
| **Tampering** | Webhook payload modified in transit | TLS 1.2+ transport; RSA-encrypted payload | Depends on correct crypto implementation |
| **Repudiation** | Telebirr denies sending webhook | Audit trail with source IP and payload hash | No webhook receipt acknowledgment protocol with Telebirr |
| **Information Disclosure** | HTTP status codes reveal endpoint behavior | Architecture states "200 for all" | Code returns 403 for unauthorized IP (SEC-07) |
| **Denial of Service** | Flood webhook endpoint | API Gateway throttling (200 burst) | No route-specific rate limit (SEC-08) |
| **Elevation of Privilege** | Webhook manipulates escrow state to "escrowed" or "cancelled" | State machine enforces valid transitions | Race condition: if a legitimate webhook and a spoofed webhook arrive simultaneously, DynamoDB conditional write prevents double-transition (good) |

### 3.4 Credential Management

| Threat | Risk | Mitigation in Architecture | Gap |
|--------|------|---------------------------|-----|
| **Spoofing** | Attacker uses stolen credentials to impersonate WeredaConnect | IAM-restricted Secrets Manager access | Credentials are not rotatable without redeployment |
| **Tampering** | Secret values modified in Secrets Manager | IAM write restrictions, CloudTrail audit | No integrity check on loaded secret values |
| **Repudiation** | Unauthorized secret access goes undetected | CloudTrail logs Secrets Manager API calls | Adequate for audit |
| **Information Disclosure** | Credentials leaked via logs, error messages, or memory dump | Never logged; error messages use generic text | Verify that no SDK or framework auto-logs the secret value |
| **Denial of Service** | Secrets Manager unavailable at cold start | Lambda retries initialization | No explicit retry/backoff for Secrets Manager calls (SEC-06) |
| **Elevation of Privilege** | Lambda role has excessive permissions | `secretsmanager:GetSecretValue` only | Verify IAM policy is scoped to the specific secret ARN, not `*` |

---

## 4. Positive Findings

The architecture demonstrates strong security design in several areas:

1. **Credentials in Secrets Manager**: All Telebirr credentials (RSA keys, appSecret, merchant IDs) are stored in AWS Secrets Manager rather than environment variables or source code. This is the correct approach for sensitive credentials and allows IAM-scoped access control.

2. **Idempotent webhook processing**: The webhook handler checks `outTradeNo` against existing escrows and processes each webhook exactly once. This prevents duplicate state transitions from Telebirr's retry mechanism and is critical for financial correctness.

3. **Append-only audit trail**: The `EscrowAuditTable` is designed as an append-only log with no update/delete operations. This provides a tamper-evident record of all escrow state changes, essential for financial compliance and dispute resolution.

4. **State machine enforcement**: The escrow state machine with explicit valid transitions prevents illegal state changes (e.g., directly from "initiated" to "refunded"). This is implemented server-side, making it impossible for a compromised mobile client to manipulate escrow states.

5. **Gateway abstraction**: The `ITelebirrGateway` interface with `MockTelebirrGateway` and `TelebirrSandboxGateway` implementations allows clean separation of payment logic from Telebirr-specific integration. The `TELEBIRR_MOCK` feature flag enables instant rollback.

6. **PII redaction awareness**: The architecture explicitly documents PII handling: phone numbers masked, RSA keys never logged, Fayda IDs excluded from Telebirr calls. This demonstrates a privacy-by-design approach.

7. **AOT compliance**: All cryptographic operations use `System.Security.Cryptography` BCL classes, which are fully AOT-compatible. No reflection-based libraries are introduced.

8. **Offline-first escrow intent**: The mobile architecture correctly separates "payment intent" (can be queued offline) from "payment execution" (requires server + Telebirr connectivity), with idempotency keys ensuring safe retries on flaky 3G networks.

9. **Platform-managed escrow model**: Choosing Option A (platform-managed) over Option B (direct payment) is the correct security decision. It gives the platform control over fund disbursement, enables dispute resolution, and prevents irreversible losses from failed deliveries.

10. **Minimal Lambda VPC placement**: Only Telebirr-calling Lambdas are placed in the VPC (for static IP), while other Lambdas stay outside VPC. This minimizes cold-start overhead and reduces the blast radius of VPC networking issues.

---

## 5. Open Questions for Architecture Team

| # | Question | Security Impact |
|---|----------|----------------|
| 1 | What is Telebirr's exact RSA operation for webhook payloads? Encryption with WeredaConnect's public key, or signing with Telebirr's private key, or something else? | **Critical**: Determines whether SEC-01 is a design flaw or a documentation error. Implementation will fail if the wrong operation is used. |
| 2 | What is Telebirr's exact signing algorithm? `SHA256(params + secret)`, `SHA256(secret + params)`, or `HMAC-SHA256(secret, params)`? | **Critical**: Wrong algorithm = all API calls rejected. |
| 3 | Does Telebirr use OAEP or PKCS#1 v1.5 padding? And is the payload encrypted as a single block or chunked/hybrid (RSA+AES)? | **Critical**: Wrong padding or block handling = `CryptographicException` at runtime (SEC-09). |
| 4 | Does Telebirr provide a webhook signature header (e.g., `X-Signature`) in addition to the encrypted payload? | **High**: Would significantly strengthen webhook authentication (SEC-03). |
| 5 | What are Telebirr's exact outbound IP addresses for sandbox webhook delivery? Are they static or dynamic? | **High**: IP whitelist accuracy directly affects webhook reliability. |
| 6 | Does Telebirr's webhook payload include a timestamp or nonce for replay protection? | **Medium**: Affects replay attack mitigation (SEC-04). |
| 7 | What is Telebirr's retry policy for failed webhook deliveries (how many retries, interval, exponential backoff)? | **Medium**: Affects idempotency and reconciliation design. |
| 8 | Is the `toPayUrl` a one-time-use URL, or can it be reused? Does it contain session tokens? | **Medium**: Determines severity of SEC-05. |
| 9 | What regulatory requirements apply to a platform holding buyer funds in its Telebirr business account? (Ethiopian financial services regulations) | **High** (business risk): Custodial fund holding may require a financial services license. |
| 10 | Does Telebirr offer a transaction status query endpoint (not just webhooks)? | **Medium**: Would enable active reconciliation instead of passive webhook-only (SEC-14). |

---

## 6. Ethiopian-Specific Security Considerations

### 6.1 SIM Swap Attacks

Telebirr is phone-number-based. Ethiopian SIM swap fraud patterns include:
- Attacker obtains victim's SIM card through social engineering at Ethio Telecom retail shops.
- Attacker gains access to victim's Telebirr account and completes payments.
- In WeredaConnect's context: a compromised buyer's Telebirr account could authorize escrow payments the real buyer did not intend.

**Mitigation**: WeredaConnect's escrow is initiated via the app (requiring Cognito JWT authentication), not directly via Telebirr. A SIM swap alone does not grant access to the WeredaConnect app. However, if the attacker also has the victim's WeredaConnect credentials (or the refresh token on a shared device), the combination is dangerous.

**Recommendation**: For high-value transactions (> 10,000 ETB), consider requiring a secondary confirmation (e.g., in-app PIN or biometric) before redirecting to Telebirr's payment page. The Fayda ID check already provides a partial barrier.

### 6.2 Shared Device Risks

Rural Ethiopian users commonly share mobile devices. Security concerns:
- Multiple users may access the same WeredaConnect app.
- The `toPayUrl` opens in a browser/WebView -- if the device is shared, another user could complete the payment or extract the payment session.
- Payment history may be visible to other users of the shared device.

**Mitigation**: The architecture correctly relies on Cognito JWT authentication (per-user sessions). Shared devices still require each user to log in separately. However, if the user does not log out, the next user inherits the session.

**Recommendation**: The mobile app should require authentication (PIN or biometric) before displaying payment screens or opening `toPayUrl`. The existing offline session TTL (from the auth architecture) helps limit exposure.

### 6.3 Rural Network Interception

3G networks in rural Ethiopia have limited security infrastructure. Potential risks:
- Base station spoofing (IMSI catchers) could intercept HTTP traffic, though HTTPS/TLS mitigates this.
- DNS hijacking could redirect API calls to attacker-controlled servers.
- The `toPayUrl` is transmitted over the network -- if intercepted, an attacker could potentially complete the payment before the buyer.

**Mitigation**: TLS 1.2+ is enforced for all communications. Certificate pinning (Phase 3) will provide additional protection. The `toPayUrl` is session-scoped and requires Telebirr app authentication to complete.

### 6.4 Mobile Money Fraud Patterns

Common mobile money fraud in Ethiopia:
- **Fake payment confirmations**: Attacker shows a fabricated screenshot of payment confirmation. The platform-managed escrow model protects against this -- the server verifies the actual webhook from Telebirr, not a client-provided screenshot.
- **Social engineering**: Attacker contacts farmer claiming to be from WeredaConnect, requesting them to release goods before escrow confirms. The 48-hour dispute window provides a buffer.
- **Payment reversal fraud**: In some mobile money systems, payments can be reversed by the sender. Telebirr's C2B payment is merchant-initiated, which makes buyer-side reversal more difficult. However, this should be confirmed with Telebirr.

---

## 7. Recommended Architecture Changes (Summary)

### Must Fix Before Any Deployment (P0)

1. **SEC-01**: Determine the correct RSA operation for webhook decryption. Do not use `publicKey.Decrypt()` with OAEP. Either use a private key for decryption or use `publicKey.VerifyData()` for signature verification.
2. **SEC-02**: Confirm Telebirr's exact signing algorithm and document it. If possible, use HMAC-SHA256 instead of hash concatenation.

### Must Fix Before Production (P1)

3. **SEC-03**: Add payload-based authentication to webhooks (encrypted payload verification) as the primary auth mechanism; keep IP whitelisting as secondary.
4. **SEC-04**: Add timestamp validation for webhook payloads and implement replay detection beyond idempotency-key-only checks.
5. **SEC-05**: Do not store `toPayUrl` as `TelebirrRef`. Return it once to the mobile client and do not persist it in audit trails or API responses.

### Should Fix (P2)

6. **SEC-06**: Add timeout and retry logic for Secrets Manager cold-start loading.
7. **SEC-07**: Return `200 OK` for all webhook responses (match the stated design in section 15.1).
8. **SEC-08**: Add route-specific rate limiting for the webhook endpoint.
9. **SEC-09**: Verify RSA payload size and implement the correct encryption scheme (likely hybrid RSA+AES).
10. **SEC-10**: Fix `AllowedSourceIps` parsing (trim whitespace, validate IP format, handle JSON array vs string mismatch).

### Advisory (P3)

11. **SEC-11**: Track certificate pinning for Telebirr as a Phase 3 requirement.
12. **SEC-12**: Plan HA NAT Gateway for production.
13. **SEC-13**: Make sandbox-simulated references clearly distinguishable from real Telebirr references.
14. **SEC-14**: Document the limitation of always-unknown transaction status and plan for Phase 2 resolution.
15. **SEC-15**: Restrict CloudWatch log access for webhook rejection logs.

---

## Changelog

| Version | Date | Change |
|---------|------|--------|
| 1.0.0 | 2026-02-24 | Initial security review of telebirr-sandbox-architecture.md v1.0.0 |

---

**Last Updated**: 2026-02-24
**Reviewer**: Security Agent
**Reviewed Document Version**: 1.0.0
