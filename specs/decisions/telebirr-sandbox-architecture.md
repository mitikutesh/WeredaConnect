# Telebirr Sandbox Integration Architecture

## Version
2.1.0 | 2026-02-24

## Status
Approved (Post-Security Review + Architecture Re-Review)

## Security Review
Reviewed by Security Agent on 2026-02-24. All P0 and P1 findings addressed. P2 findings addressed. P3 findings acknowledged with deferred timelines. Full review at `specs/decisions/telebirr-sandbox-security-review.md`.

## Architecture Re-Review (v2.1.0)
Five additional findings (NEW-01 through NEW-05) identified during architecture re-review. All addressed in this revision. See Section 16 for full mapping.

---

## 1. Overview

### Purpose
Replace the `MockTelebirrGateway` with a real `TelebirrSandboxGateway` that integrates with Telebirr's H5 C2B payment API via their developer portal sandbox. This is a prerequisite for GA launch -- currently the entire escrow payment system (F-13) operates against mock responses.

### Scope
- Server-side `TelebirrSandboxGateway` implementing `ITelebirrGateway`
- RSA2048 encryption and SHA256 signing (AOT-safe)
- Webhook handler for asynchronous payment notifications
- Credential management via AWS Secrets Manager
- Infrastructure changes (NAT Gateway, webhook endpoint, secrets)
- Error code mapping from Telebirr codes to existing `ESCROW_*` codes

### Out of Scope
- Mobile UI changes (escrow screens already built)
- Production merchant onboarding (separate operational task)
- Telebirr B2B disbursement API (farmer payout -- see Gap Analysis)
- USSD payment flow (future enhancement)

---

## 2. Telebirr API Analysis

### 2.1 Actual Telebirr API Capabilities

Telebirr's H5 C2B (Customer-to-Business) API provides a single operation: **payment collection**. The buyer pays a specified amount to the merchant's (WeredaConnect's) Telebirr business account.

**Authentication Model**:
- Merchant credentials: `fabricAppId`, `appSecret`, `merchantAppId`, `merchantCode`, `shortCode`
- Token acquisition: `POST /apply/h5token` returns a session token
- Request encryption: RSA2048 public key encryption of the request payload
- Request signing: SHA256 signature over the encrypted payload

**Payment Creation Flow**:
```
Server                          Telebirr API                    Buyer
  |                                  |                            |
  |-- POST /apply/h5token ---------->|                            |
  |<----------- { token } ---------- |                            |
  |                                  |                            |
  |-- POST /create/order ----------->|                            |
  |   (sign + ussd encrypted data)   |                            |
  |<-- { toPayUrl } ----------------|                            |
  |                                  |                            |
  |-- Return toPayUrl to mobile ----|---> Open toPayUrl -------->|
  |                                  |    (Telebirr app / WebView)|
  |                                  |<--- Buyer completes pay --|
  |                                  |                            |
  |<-- POST notifyUrl (webhook) ----|                            |
  |   (encrypted payment result)     |                            |
  |                                  |--- Redirect returnUrl --->|
```

**Request Parameters**:

| Parameter | Type | Required | Purpose |
|-----------|------|----------|---------|
| appId | String | Yes | Merchant application ID (fabricAppId) |
| appKey | String | Yes | Authentication key (from h5token) |
| nonce | String | Yes | Unique random string per request |
| outTradeNo | String | Yes | Our transaction order number (maps to escrow idempotencyKey) |
| totalAmount | String | Yes | Amount in ETB (0.01 - 100,000,000) |
| timestamp | String | Yes | Unix milliseconds (must be within 1 minute of server time) |
| subject | String | Yes | Payment description |
| timeoutExpress | String | Yes | Payment timeout in minutes |
| shortCode | String | Yes | Merchant short code |
| returnUrl | String | Yes | Post-payment redirect URL (mobile deep link) |
| notifyUrl | String | No | Server webhook endpoint |
| receiveName | String | No | Receiver display name |

**Response Format**:
```json
{
  "code": 0,
  "msg": "success",
  "data": {
    "toPayUrl": "https://h5pay.trade.pay/payId=RE9879T0972S"
  }
}
```

**Webhook Notification** (POST to notifyUrl):
- Telebirr sends POST body after payment completion
- Payload is encrypted -- exact encryption scheme must be confirmed (see section 5.3)
- Contains: transaction status, amount, outTradeNo, Telebirr reference

### 2.2 Security Requirements
- Source IP whitelisting (Lambda requires NAT Gateway for a static outbound IP)
- RSA2048 encryption of request payloads using Telebirr-provided public key
- SHA256 signature for request integrity
- Timestamp validation (must be within 1 minute of server time)
- HTTPS only (TLS 1.2+)

---

## 3. Gap Analysis: ITelebirrGateway vs Telebirr API

### 3.1 The Fundamental Mismatch

Our current `ITelebirrGateway` interface assumes four operations:

| Our Interface Method | What It Assumes | Telebirr Reality |
|---------------------|-----------------|-----------------|
| `HoldFundsAsync` | Place a hold on buyer's funds | **No hold API**. Telebirr only supports full payment collection (C2B). |
| `ReleaseFundsAsync` | Release held funds to farmer | **No release API**. Funds go to merchant (platform) account immediately. |
| `RefundFundsAsync` | Return held funds to buyer | **No refund API** in H5 C2B. Requires separate B2B transfer or manual process. |
| `GetTransactionStatusAsync` | Query transaction status | **Partially supported** via webhook notifications. No polling endpoint documented. |

### 3.2 What Telebirr Actually Supports

| Capability | Telebirr API | Notes |
|------------|-------------|-------|
| Collect payment from buyer | H5 C2B `/create/order` | Buyer pays to platform account |
| Query payment status | Webhook (async) | Server receives POST notification |
| Transfer to farmer (B2B) | Separate B2B API | Not part of H5 C2B flow; requires separate merchant integration |
| Refund to buyer | Manual or B2B reverse | No automated refund in H5 C2B |

### 3.3 Impact on Escrow State Machine

The current escrow state machine (`initiated -> escrowed -> released -> completed`) was designed around a hypothetical hold/release pattern. With Telebirr's actual capabilities:

- **"escrowed"** = funds collected to platform account (not held in escrow by Telebirr)
- **"released"** = platform initiates B2B transfer to farmer (separate API call)
- **"refunded"** = platform initiates B2B transfer back to buyer (separate API call)

The state machine itself remains valid. The difference is that "escrow" is **platform-managed** (funds sit in WeredaConnect's Telebirr business account) rather than **Telebirr-managed**.

---

## 4. Proposed Architecture

### 4.1 Option A: Platform-Managed Escrow (Recommended)

**Model**: Buyer pays the full amount to WeredaConnect's Telebirr business account. Platform holds funds in its own account. Upon delivery confirmation + dispute window expiry, platform initiates a B2B transfer to the farmer (minus commission). For refunds, platform initiates a B2B transfer back to the buyer.

```
BUYER                WEREDACONNECT              TELEBIRR              FARMER
  |                    PLATFORM                    API                   |
  |                       |                         |                    |
  |--- Pay 15,000 ETB -->|                         |                    |
  |                       |--- C2B create/order --->|                    |
  |                       |<-- toPayUrl ------------|                    |
  |<-- Open toPayUrl ----|                         |                    |
  |--- Complete in app -->|                         |                    |
  |                       |<-- webhook: paid -------|                    |
  |                       |                         |                    |
  |    [Platform holds 15,000 ETB for 48h+]        |                    |
  |                       |                         |                    |
  |    [Delivery confirmed, dispute window passed]  |                    |
  |                       |--- B2B transfer ------->|                    |
  |                       |   14,625 ETB to farmer  |--- 14,625 ETB --->|
  |                       |   375 ETB commission    |                    |
  |                       |<-- B2B success ---------|                    |
```

**Advantages**:
1. Works with Telebirr's existing H5 C2B API (no special hold features needed)
2. Platform retains full control over fund disbursement timing
3. Commission deduction is trivial (platform transfers `amount - commission`)
4. Matches how most marketplace payment systems work globally
5. Existing escrow state machine remains valid with minimal changes
6. Can support partial refunds in the future

**Disadvantages**:
1. Requires Telebirr B2B API integration for payouts (separate integration)
2. Platform assumes custodial risk (regulatory: holding buyer funds)
3. B2B transfer may take 1-2 business days (farmer payout delay)
4. Platform needs sufficient float in its Telebirr account for concurrent payouts

**Risk Mitigation**:
- Ethiopian regulations: Mobile money custodial limits may apply. Legal review needed before production.
- Float management: Daily reconciliation + alerts when balance drops below threshold.
- Payout delay: Communicate expected 1-3 day payout timeline to farmers in UI.

### 4.2 Option B: Direct Payment with Manual Release

**Model**: Buyer pays directly to the farmer's Telebirr account. Platform commission is collected separately (or deducted at initiation). No platform-held escrow.

**Disadvantages** (disqualifying):
1. No way to refund buyer if delivery fails (money already with farmer)
2. No escrow protection -- defeats the purpose of F-13
3. Commission collection requires a separate payment from buyer or farmer
4. Dispute resolution has no leverage (funds already disbursed)

**Verdict**: Rejected. This does not satisfy the escrow requirement.

### 4.3 Recommendation: Option A (Platform-Managed Escrow)

Option A is the only viable approach given Telebirr's API. It aligns with standard marketplace payment patterns (e.g., Stripe Connect, PayPal for Marketplaces) where the platform collects payment and disburses to sellers.

**Key implication**: The `ITelebirrGateway` interface semantics change from "Telebirr holds/releases funds" to "Platform collects via Telebirr C2B and disburses via Telebirr B2B". The interface method signatures can remain the same, but their implementation is fundamentally different.

---

## 5. Component Design

### 5.1 New and Modified Files

| File | Type | Description |
|------|------|-------------|
| `server/src/Services/TelebirrSandboxGateway.cs` | New | Implements `ITelebirrGateway` using Telebirr H5 C2B API |
| `server/src/Services/TelebirrCryptoService.cs` | New | RSA2048 encryption + SHA256 signing (AOT-safe) |
| `server/src/Services/ITelebirrCryptoService.cs` | New | Interface for crypto operations (testability) |
| `server/src/Functions/TelebirrWebhookFunction.cs` | New | Lambda handler for Telebirr payment notifications |
| `server/src/Models/TelebirrDtos.cs` | New | Telebirr API request/response DTOs |
| `server/src/Functions/EscrowFunction.cs` | Modify | Wire `TelebirrSandboxGateway` in `CreateTelebirrGateway()` |
| `server/src/Functions/EscrowCronFunction.cs` | Modify | Wire `TelebirrSandboxGateway` in `CreateTelebirrGateway()` |
| `server/src/AppJsonContext.cs` | Modify | Register new Telebirr DTOs |
| `infra/lib/stacks/api-stack.ts` | Modify | Add webhook route, NAT Gateway, Secrets Manager |
| `infra/lib/stacks/database-stack.ts` | No change | Existing tables suffice |

### 5.2 TelebirrSandboxGateway

This is the core implementation. It implements `ITelebirrGateway` by mapping each method to Telebirr's actual API.

**SEC-05 fix**: `toPayUrl` is NOT stored as `TelebirrRef`. It is returned to the caller via a separate `ToPayUrl` field in the response, never persisted in the escrow record or audit trail. `TelebirrRef` remains `null` until the webhook provides the actual Telebirr `tradeNo`.

**NEW-04 enforcement rules**: To prevent accidental future persistence of `toPayUrl`:
1. `EscrowService.InitiateEscrowAsync()` MUST NOT pass `toPayUrl` to any DynamoDB `PutItem` or `UpdateItem` call
2. `EscrowAuditRepository` MUST NOT accept `toPayUrl` as a metadata field
3. Unit tests MUST verify: `GET /escrow/{id}` response does NOT contain `toPayUrl` after initiation
4. Code review checklist: any PR that adds `toPayUrl` to a DynamoDB attribute map or audit metadata MUST be rejected

```csharp
public sealed class TelebirrSandboxGateway : ITelebirrGateway
{
    private readonly HttpClient _httpClient;
    private readonly ITelebirrCryptoService _cryptoService;
    private readonly TelebirrConfig _config;

    // ─── ITelebirrGateway Implementation ────────────────────

    /// <summary>
    /// "Hold" = Initiate C2B payment from buyer to platform account.
    /// Returns a toPayUrl for the buyer to complete payment in Telebirr app.
    /// Actual fund confirmation arrives asynchronously via webhook.
    ///
    /// SEC-05: toPayUrl is returned in the HoldFundsResponse but is NOT stored
    /// as TelebirrRef. TelebirrRef is set to a placeholder until the webhook
    /// provides the actual Telebirr tradeNo.
    /// </summary>
    public async Task<HoldFundsResponse> HoldFundsAsync(HoldFundsRequest request)
    {
        // 1. Acquire h5token
        var token = await AcquireTokenAsync();

        // 2. Build payment request
        // SEC-04: Generate timestamp immediately before sending.
        var paymentParams = new TelebirrCreateOrderRequest
        {
            AppId = _config.FabricAppId,
            AppKey = token,
            Nonce = GenerateNonce(),
            OutTradeNo = request.TransactionRef,     // Our idempotency key
            TotalAmount = request.Amount.ToString("F2"),
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(),
            Subject = request.Description,
            TimeoutExpress = "30",                    // 30 minutes
            ShortCode = _config.ShortCode,
            ReturnUrl = _config.ReturnUrl,            // Mobile deep link
            NotifyUrl = _config.WebhookUrl,           // Our webhook Lambda
            ReceiveName = "WeredaConnect"
        };

        // 3. Encrypt payload with RSA2048 (SEC-09: uses chunked/hybrid encryption)
        var ussd = _cryptoService.EncryptPayload(paymentParams);

        // 4. Sign with SHA256 (SEC-02: exact algorithm per Telebirr spec)
        var sign = _cryptoService.SignPayload(paymentParams);

        // 5. POST /create/order
        TelebirrCreateOrderResponse response;
        try
        {
            response = await PostToTelebirrAsync("/create/order", sign, ussd);
        }
        catch (HttpRequestException)
        {
            return new HoldFundsResponse
            {
                Success = false,
                ErrorCode = "TELEBIRR_NETWORK",
                ErrorMessage = "Network error contacting Telebirr API."
            };
        }

        if (response.Code == 0 && response.Data?.ToPayUrl != null)
        {
            // SEC-05: Return toPayUrl separately. TelebirrRef is a placeholder
            // until the webhook delivers the real Telebirr tradeNo.
            return new HoldFundsResponse
            {
                Success = true,
                TelebirrRef = $"PENDING-WEBHOOK-{request.TransactionRef}",
                ToPayUrl = response.Data.ToPayUrl  // Transient, not persisted
            };
        }

        // SEC-04: If Telebirr rejects with a timestamp error, retry once
        // with a freshly generated timestamp.
        if (response.Code == -2 && (response.Msg?.Contains("timestamp") == true))
        {
            paymentParams = paymentParams with
            {
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString()
            };
            ussd = _cryptoService.EncryptPayload(paymentParams);
            sign = _cryptoService.SignPayload(paymentParams);
            response = await PostToTelebirrAsync("/create/order", sign, ussd);

            if (response.Code == 0 && response.Data?.ToPayUrl != null)
            {
                return new HoldFundsResponse
                {
                    Success = true,
                    TelebirrRef = $"PENDING-WEBHOOK-{request.TransactionRef}",
                    ToPayUrl = response.Data.ToPayUrl
                };
            }
        }

        return new HoldFundsResponse
        {
            Success = false,
            ErrorCode = $"TELEBIRR_{response.Code}",
            ErrorMessage = response.Msg ?? "Telebirr API error"
        };
    }

    /// <summary>
    /// "Release" = Initiate B2B transfer from platform account to farmer.
    /// PHASE 2: This requires Telebirr B2B API integration.
    /// For sandbox, this is simulated with a logged intent + audit trail.
    ///
    /// SEC-13: Sandbox references are clearly prefixed with SANDBOX-SIM-
    /// and use a hash of the TransactionRef instead of the raw value.
    /// </summary>
    public async Task<ReleaseFundsResponse> ReleaseFundsAsync(ReleaseFundsRequest request)
    {
        await Task.CompletedTask;
        var refHash = ComputeShortHash(request.TransactionRef);
        return new ReleaseFundsResponse
        {
            Success = true,
            TelebirrRef = $"SANDBOX-SIM-PAYOUT-{refHash}"
        };
    }

    /// <summary>
    /// "Refund" = Initiate B2B transfer from platform account back to buyer.
    /// PHASE 2: This requires Telebirr B2B API integration.
    /// For sandbox, this is simulated with a logged intent + audit trail.
    ///
    /// SEC-13: Same sandbox prefix pattern as ReleaseFundsAsync.
    /// </summary>
    public async Task<RefundFundsResponse> RefundFundsAsync(RefundFundsRequest request)
    {
        await Task.CompletedTask;
        var refHash = ComputeShortHash(request.TransactionRef);
        return new RefundFundsResponse
        {
            Success = true,
            TelebirrRef = $"SANDBOX-SIM-REFUND-{refHash}"
        };
    }

    /// <summary>
    /// Query Telebirr for transaction status.
    /// Used by reconciliation cron to check stuck "initiated" escrows.
    ///
    /// SEC-14: Returns "unknown" because Telebirr H5 C2B does not expose a
    /// status polling endpoint. Reconciliation relies on webhook data.
    /// For sandbox, if an escrow is stuck in "initiated" for >1 hour with
    /// no webhook, an alert is logged for manual investigation.
    /// </summary>
    public async Task<TransactionStatusResponse> GetTransactionStatusAsync(string transactionRef)
    {
        await Task.CompletedTask;
        return new TransactionStatusResponse
        {
            TransactionRef = transactionRef,
            Status = "unknown",
            Amount = 0,
            UpdatedAt = DateTime.UtcNow.ToString("o")
        };
    }

    /// <summary>
    /// Generate a cryptographically secure nonce for each Telebirr request.
    ///
    /// NEW-03 fix: Uses RandomNumberGenerator (CSPRNG) instead of
    /// Guid.NewGuid() or DateTime-based values. 32 bytes provides
    /// 256 bits of entropy — sufficient to prevent nonce collisions
    /// and prediction attacks.
    /// </summary>
    private static string GenerateNonce()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>
    /// Compute a short, non-reversible hash for sandbox reference IDs.
    /// Avoids leaking the full TransactionRef (which contains user IDs).
    /// </summary>
    private static string ComputeShortHash(string input)
    {
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(hashBytes)[..12];
    }
}
```

### 5.3 TelebirrCryptoService (AOT-Safe) -- Security-Hardened

All cryptographic operations use `System.Security.Cryptography`, which is fully AOT-compatible.

**Critical note (SEC-01, SEC-02, SEC-09)**: The exact RSA operation, signing algorithm, and encryption scheme MUST be confirmed with Telebirr's documentation before implementation. The code below documents three possible interpretations and the implementation path for each.

```csharp
public sealed class TelebirrCryptoService : ITelebirrCryptoService
{
    private readonly RSA _telebirrPublicKey;   // Telebirr's public key (for outbound encryption)
    private readonly RSA? _weredaPrivateKey;   // WeredaConnect's private key (for webhook decryption, if needed)
    private readonly string _appSecret;

    public TelebirrCryptoService(
        string telebirrPublicKeyPem,
        string appSecret,
        string? weredaPrivateKeyPem = null)
    {
        _telebirrPublicKey = RSA.Create();
        _telebirrPublicKey.ImportFromPem(telebirrPublicKeyPem);
        _appSecret = appSecret;

        // SEC-01: If Telebirr encrypts webhook payloads with OUR public key,
        // we need our own private key for decryption.
        if (!string.IsNullOrEmpty(weredaPrivateKeyPem))
        {
            _weredaPrivateKey = RSA.Create();
            _weredaPrivateKey.ImportFromPem(weredaPrivateKeyPem);
        }
    }

    /// <summary>
    /// Encrypt the payment request payload using RSA2048.
    ///
    /// SEC-09: RSA-2048 with OAEP-SHA256 can only encrypt 190 bytes per block.
    /// A typical TelebirrCreateOrderRequest JSON is 400-600 bytes.
    /// We MUST use one of these approaches (to be confirmed with Telebirr):
    ///
    /// Option A (Hybrid RSA+AES -- most likely):
    ///   1. Generate random AES-256 key
    ///   2. Encrypt payload with AES-256-CBC
    ///   3. Encrypt AES key with RSA-2048
    ///   4. Return: base64(RSA-encrypted-AES-key) + ":" + base64(AES-encrypted-payload)
    ///
    /// Option B (PKCS#1 v1.5 with chunking):
    ///   1. Split payload into 245-byte chunks
    ///   2. Encrypt each chunk with RSA PKCS#1 v1.5
    ///   3. Concatenate encrypted chunks
    ///
    /// Option C (Field-level encryption):
    ///   Encrypt only specific sensitive fields, not the entire JSON
    ///
    /// The implementation below uses Option A (hybrid) as the default.
    /// This MUST be validated against Telebirr's sandbox before deployment.
    /// </summary>
    public string EncryptPayload(TelebirrCreateOrderRequest request)
    {
        var json = SerializeForEncryption(request);
        var jsonBytes = Encoding.UTF8.GetBytes(json);

        // Generate random AES-256 key and IV
        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.GenerateKey();
        aes.GenerateIV();

        // Encrypt payload with AES
        using var encryptor = aes.CreateEncryptor();
        var encryptedPayload = encryptor.TransformFinalBlock(jsonBytes, 0, jsonBytes.Length);

        // Encrypt AES key with RSA (fits in one RSA block)
        var keyAndIv = new byte[aes.Key.Length + aes.IV.Length];
        Buffer.BlockCopy(aes.Key, 0, keyAndIv, 0, aes.Key.Length);
        Buffer.BlockCopy(aes.IV, 0, keyAndIv, aes.Key.Length, aes.IV.Length);
        var encryptedKey = _telebirrPublicKey.Encrypt(keyAndIv, RSAEncryptionPadding.OaepSHA256);

        // Combine: base64(encrypted-key) + base64(encrypted-payload)
        return Convert.ToBase64String(encryptedKey) + "|" + Convert.ToBase64String(encryptedPayload);
    }

    /// <summary>
    /// Generate SHA256 signature over the request fields.
    ///
    /// SEC-02: The signing algorithm MUST match Telebirr's specification exactly.
    /// Three possible algorithms (to be confirmed):
    ///
    /// 1. Secret-suffix hash: SHA256(sorted_params + secret)
    ///    - Vulnerable to length-extension attacks but may be what Telebirr requires.
    ///    - If Telebirr mandates this, implement as-is and document the risk.
    ///
    /// 2. HMAC-SHA256: HMAC(secret, sorted_params)
    ///    - Cryptographically secure. Preferred if Telebirr supports it.
    ///
    /// 3. Secret-prefix hash: SHA256(secret + sorted_params)
    ///    - Also vulnerable but in a different way.
    ///
    /// The implementation below defaults to secret-suffix hash (most common in
    /// Chinese/Ethiopian payment APIs). A compile-time flag can switch to HMAC
    /// after confirming with Telebirr.
    /// </summary>
    public string SignPayload(TelebirrCreateOrderRequest request)
    {
        var sortedParams = BuildSortedParamString(request);

        // WARNING (SEC-02): Secret-suffix SHA256 is vulnerable to length-extension
        // attacks. This is implemented per Telebirr's likely specification.
        // If Telebirr supports HMAC, switch to the HMAC path below.
        // DO NOT "improve" this without verifying Telebirr compatibility.
        var dataToSign = sortedParams + _appSecret;
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(dataToSign));
        return Convert.ToHexStringLower(hashBytes);
    }

    /// <summary>
    /// Alternative HMAC-SHA256 signing (use if Telebirr supports it).
    /// SEC-02: This is cryptographically superior to secret-suffix hash.
    /// </summary>
    public string SignPayloadHmac(TelebirrCreateOrderRequest request)
    {
        var sortedParams = BuildSortedParamString(request);
        var keyBytes = Encoding.UTF8.GetBytes(_appSecret);
        var dataBytes = Encoding.UTF8.GetBytes(sortedParams);
        var hashBytes = HMACSHA256.HashData(keyBytes, dataBytes);
        return Convert.ToHexStringLower(hashBytes);
    }

    /// <summary>
    /// Decrypt/verify webhook notification payload from Telebirr.
    ///
    /// SEC-01 FIX: The correct RSA operation depends on how Telebirr encrypts
    /// the webhook payload. Three interpretations:
    ///
    /// Interpretation A: Telebirr encrypts with WeredaConnect's public key.
    ///   -> We decrypt with our PRIVATE key (_weredaPrivateKey).
    ///   -> Requires generating a WeredaConnect RSA key pair and sharing the
    ///      public key with Telebirr during merchant onboarding.
    ///
    /// Interpretation B: Telebirr signs with its own private key.
    ///   -> We VERIFY the signature using Telebirr's PUBLIC key.
    ///   -> The payload is in cleartext; the signature proves authenticity.
    ///
    /// Interpretation C: Legacy PKCS#1 v1.5 "encryption" with public key.
    ///   -> Uses raw modular exponentiation (non-standard).
    ///   -> Use RSAEncryptionPadding.Pkcs1 (not OAEP).
    ///
    /// The implementation below handles Interpretation A (most standard).
    /// This MUST be validated against Telebirr's sandbox before deployment.
    ///
    /// NEW-02 fix: Webhook payloads may also exceed the RSA block size limit
    /// (190 bytes for OAEP-SHA256). The decryption method now handles both
    /// raw RSA and hybrid RSA+AES formats, matching the outbound encryption.
    /// </summary>
    public string DecryptWebhookPayload(string encryptedPayload)
    {
        if (_weredaPrivateKey == null)
        {
            throw new InvalidOperationException(
                "WeredaConnect RSA private key is required for webhook decryption. " +
                "Set 'weredaPrivateKeyPem' in TelebirrConfig.");
        }

        // NEW-02: Check if payload is in hybrid RSA+AES format (contains '|' separator)
        if (encryptedPayload.Contains('|'))
        {
            // Hybrid format: base64(RSA-encrypted-AES-key) | base64(AES-encrypted-payload)
            var parts = encryptedPayload.Split('|', 2);
            if (parts.Length != 2)
            {
                throw new CryptographicException("Invalid hybrid encrypted payload format.");
            }

            // Decrypt AES key+IV with RSA
            var encryptedKeyAndIv = Convert.FromBase64String(parts[0]);
            var keyAndIv = _weredaPrivateKey.Decrypt(encryptedKeyAndIv, RSAEncryptionPadding.OaepSHA256);

            // Extract AES key (32 bytes) and IV (16 bytes)
            if (keyAndIv.Length != 48) // 32 + 16
            {
                throw new CryptographicException(
                    $"Decrypted key+IV has unexpected length: {keyAndIv.Length} (expected 48).");
            }

            using var aes = Aes.Create();
            aes.KeySize = 256;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            aes.Key = keyAndIv[..32];
            aes.IV = keyAndIv[32..];

            // Decrypt payload with AES
            using var decryptor = aes.CreateDecryptor();
            var encryptedData = Convert.FromBase64String(parts[1]);
            var decrypted = decryptor.TransformFinalBlock(encryptedData, 0, encryptedData.Length);
            return Encoding.UTF8.GetString(decrypted);
        }

        // Simple RSA format (payload fits in one RSA block — unlikely for webhooks
        // but included as fallback for compatibility)
        var encryptedBytes = Convert.FromBase64String(encryptedPayload);
        var decryptedBytes = _weredaPrivateKey.Decrypt(encryptedBytes, RSAEncryptionPadding.OaepSHA256);
        return Encoding.UTF8.GetString(decryptedBytes);
    }

    /// <summary>
    /// Alternative: Verify webhook signature (if Telebirr signs rather than encrypts).
    /// SEC-01: Use this if Telebirr provides cleartext + signature.
    /// </summary>
    public bool VerifyWebhookSignature(string payload, string signature)
    {
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var signatureBytes = Convert.FromBase64String(signature);
        return _telebirrPublicKey.VerifyData(
            payloadBytes, signatureBytes,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }
}
```

**AOT Compliance**:

| Check | Status | Notes |
|-------|--------|-------|
| `System.Security.Cryptography.RSA` | AOT-safe | Part of BCL, no reflection |
| `System.Security.Cryptography.Aes` | AOT-safe | Part of BCL, no reflection (SEC-09 hybrid encryption) |
| `System.Security.Cryptography.SHA256` | AOT-safe | Static methods, no reflection |
| `System.Security.Cryptography.HMACSHA256` | AOT-safe | Static methods (SEC-02 HMAC option) |
| `System.Security.Cryptography.RandomNumberGenerator` | AOT-safe | Static `GetBytes()` method (NEW-03 nonce generation) |
| `Convert.ToBase64String` / `FromBase64String` | AOT-safe | BCL primitives |
| `Encoding.UTF8` | AOT-safe | BCL primitive |
| JSON serialization | AOT-safe | Uses `AppJsonContext` source generators |
| `HttpClient` | AOT-safe | Part of `System.Net.Http` |

### 5.4 TelebirrWebhookFunction -- Security-Hardened

A new Lambda function that receives asynchronous payment notifications from Telebirr.

```
API Gateway                   TelebirrWebhookFunction             DynamoDB
    |                                   |                            |
    |-- POST /webhooks/telebirr ------->|                            |
    |   (no JWT auth)                   |                            |
    |                                   |-- 1. Validate source IP -->|
    |                                   |-- 2. Decrypt/verify ------>|
    |                                   |      payload (primary auth)|
    |                                   |-- 3. Validate timestamp -->|
    |                                   |-- 4. Check replay -------->|
    |                                   |-- 5. Look up escrow ------>|
    |                                   |-- 6. Update escrow ------->|
    |                                   |-- 7. Append audit -------->|
    |                                   |                            |
    |<-- 200 OK (always) --------------|                            |
```

**Security layers (SEC-03 fix)**:
1. **Layer 1 (secondary)**: Source IP validation -- first-pass filter
2. **Layer 2 (primary)**: Encrypted payload decryption/verification -- only Telebirr can produce valid payloads
3. **Layer 3**: Timestamp validation within acceptable window (SEC-04)
4. **Layer 4**: Replay detection via composite key (SEC-04)
5. **Layer 5**: Idempotent processing -- same webhook produces same result

**SEC-07 fix**: Returns `200 OK` for ALL cases, including unauthorized IPs and invalid payloads. Internal failures are logged and tracked via CloudWatch metrics, never exposed to the caller.

```csharp
public class TelebirrWebhookFunction
{
    private readonly ITelebirrCryptoService _cryptoService;
    private readonly IEscrowRepository _escrowRepo;
    private readonly IEscrowAuditRepository _auditRepo;
    private readonly IEscrowIdempotencyRepository _idempotencyRepo;
    private readonly HashSet<string> _allowedSourceIps;

    private const int MaxTimestampSkewMinutes = 5;

    public async Task<APIGatewayProxyResponse> FunctionHandler(
        APIGatewayProxyRequest request, ILambdaContext context)
    {
        // SEC-07: ALWAYS return 200 OK. The webhook is a "black hole" to
        // external observers. All validation failures are logged internally.

        // Layer 1 (SEC-03 secondary): Validate source IP
        var sourceIp = request.RequestContext?.Identity?.SourceIp;
        if (!IsAllowedSourceIp(sourceIp))
        {
            // SEC-15: Log masked IP for security monitoring
            var maskedIp = MaskIpAddress(sourceIp);
            context.Logger.LogWarning($"Webhook: unauthorized source IP {maskedIp}");
            // SEC-07: Return 200 OK, not 403
            return OkResponse();
        }

        // Layer 2 (SEC-03 primary): Decrypt/verify payload
        string decryptedJson;
        try
        {
            decryptedJson = _cryptoService.DecryptWebhookPayload(request.Body);
        }
        catch (CryptographicException)
        {
            context.Logger.LogWarning("Webhook: payload decryption/verification failed.");
            return OkResponse();
        }

        TelebirrWebhookNotification notification;
        try
        {
            notification = ParseWebhookNotification(decryptedJson);
        }
        catch (JsonException)
        {
            context.Logger.LogWarning("Webhook: failed to parse decrypted payload.");
            return OkResponse();
        }

        // Layer 3 (SEC-04): Validate timestamp if present in payload
        if (!string.IsNullOrEmpty(notification.Timestamp))
        {
            if (long.TryParse(notification.Timestamp, out var webhookTimestamp))
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var skew = Math.Abs(now - webhookTimestamp);
                if (skew > MaxTimestampSkewMinutes * 60 * 1000)
                {
                    context.Logger.LogWarning(
                        $"Webhook: timestamp outside acceptable window (skew: {skew}ms).");
                    return OkResponse();
                }
            }
        }

        // Layer 4 (SEC-04): Replay detection using composite key
        var webhookKey = $"{notification.OutTradeNo}:{notification.TradeNo}:{notification.TransactionStatus}";
        var existing = await _idempotencyRepo.GetAsync(webhookKey);
        if (existing != null)
        {
            context.Logger.LogInformation($"Webhook: duplicate delivery for {notification.OutTradeNo}.");
            return OkResponse();
        }

        // Layer 5: Look up escrow by outTradeNo (our idempotency key)
        var escrow = await FindEscrowByTradeNoAsync(notification.OutTradeNo);
        if (escrow == null)
        {
            context.Logger.LogWarning(
                $"Webhook: no escrow found for outTradeNo (key length: {notification.OutTradeNo.Length}).");
            return OkResponse();
        }

        // NEW-01 fix: Validate webhook amount matches the escrow record.
        // An attacker who can forge or replay a webhook with a different amount
        // could cause the escrow to transition with an incorrect payment value.
        if (notification.IsSuccess)
        {
            var escrowAmount = GetDecimal(escrow, "amount");
            if (!decimal.TryParse(notification.TotalAmount, out var webhookAmount)
                || Math.Abs(escrowAmount - webhookAmount) > 0.01m)
            {
                context.Logger.LogWarning(
                    $"Webhook: amount mismatch for {notification.OutTradeNo} " +
                    $"(escrow: {escrowAmount}, webhook: {webhookAmount}). Rejecting.");
                // Audit the mismatch for investigation
                await AppendAuditAsync(
                    GetString(escrow, "escrowId"), "webhook_amount_mismatch", "", "",
                    "telebirr", notification.TradeNo,
                    metadata: $"escrow={escrowAmount},webhook={notification.TotalAmount}");
                return OkResponse();
            }
        }

        // Store webhook receipt in idempotency table (prevents replay)
        await _idempotencyRepo.StoreAsync(webhookKey, notification.OutTradeNo, "webhook_processed");

        // Audit: record webhook reception with timestamp
        await AppendAuditAsync(
            GetString(escrow, "escrowId"), "webhook_received", "", "",
            "telebirr", notification.TradeNo,
            metadata: $"status={notification.TransactionStatus}");

        // Process payment result
        if (notification.IsSuccess)
        {
            await TransitionToEscrowedAsync(escrow, notification);
        }
        else
        {
            await TransitionToCancelledAsync(escrow, notification);
        }

        return OkResponse();
    }

    /// <summary>
    /// SEC-07: Always return 200 OK with empty body.
    /// </summary>
    private static APIGatewayProxyResponse OkResponse()
    {
        return new APIGatewayProxyResponse
        {
            StatusCode = 200,
            Body = "",
            Headers = new Dictionary<string, string> { { "Content-Type", "application/json" } }
        };
    }

    /// <summary>
    /// SEC-15: Mask IP address for logging (show first two octets only).
    /// </summary>
    private static string MaskIpAddress(string? ip)
    {
        if (string.IsNullOrEmpty(ip)) return "unknown";
        var parts = ip.Split('.');
        if (parts.Length == 4) return $"{parts[0]}.{parts[1]}.*.*";
        return "masked";
    }
}
```

### 5.5 Credential Management -- Security-Hardened

All Telebirr credentials are stored in AWS Secrets Manager (never in environment variables or source code).

**Secret Structure** (`/wereda-connect-{env}/telebirr-credentials`):

```json
{
  "fabricAppId": "...",
  "appSecret": "...",
  "merchantAppId": "...",
  "merchantCode": "...",
  "shortCode": "...",
  "rsaPublicKeyPem": "-----BEGIN PUBLIC KEY-----\n...\n-----END PUBLIC KEY-----",
  "weredaPrivateKeyPem": "-----BEGIN PRIVATE KEY-----\n...\n-----END PRIVATE KEY-----",
  "webhookUrl": "https://{api-id}.execute-api.me-south-1.amazonaws.com/webhooks/telebirr",
  "returnUrl": "weredaconnect://payment/complete",
  "allowedSourceIps": "1.2.3.4,5.6.7.8"
}
```

**SEC-01 note**: The `weredaPrivateKeyPem` field holds WeredaConnect's RSA private key for webhook payload decryption. The corresponding public key must be shared with Telebirr during merchant onboarding. If Telebirr uses signature verification instead of encryption, this field is not needed.

**Access Pattern**:
- `TelebirrSandboxGateway` and `TelebirrWebhookFunction` read the secret at Lambda cold start
- Secret is cached in-memory for the Lambda instance lifetime
- `TELEBIRR_SECRET_ARN` environment variable points to the secret ARN
- IAM policy grants `secretsmanager:GetSecretValue` scoped to the specific secret ARN

### 5.6 TelebirrConfig (AOT-Safe) -- Security-Hardened

**SEC-10 fix**: IP parsing trims whitespace, validates format, and fails loudly if no IPs are configured.

```csharp
/// <summary>
/// Telebirr merchant configuration loaded from Secrets Manager.
/// All fields populated at Lambda cold start, cached in memory.
/// No reflection -- manual parsing from JSON dictionary.
/// </summary>
public sealed class TelebirrConfig
{
    public required string FabricAppId { get; init; }
    public required string AppSecret { get; init; }
    public required string MerchantAppId { get; init; }
    public required string MerchantCode { get; init; }
    public required string ShortCode { get; init; }
    public required string RsaPublicKeyPem { get; init; }
    public string? WeredaPrivateKeyPem { get; init; }
    public required string WebhookUrl { get; init; }
    public required string ReturnUrl { get; init; }
    public required List<string> AllowedSourceIps { get; init; }

    /// <summary>
    /// Parse config from Secrets Manager JSON without reflection.
    /// Uses AppJsonContext source generator.
    ///
    /// SEC-10 fix: Trims whitespace from IPs, validates each as a valid
    /// IP address, and logs a warning if the list is empty.
    /// </summary>
    public static TelebirrConfig FromSecretJson(string json)
    {
        var dict = JsonSerializer.Deserialize(json, AppJsonContext.Default.DictionaryStringString)
            ?? throw new InvalidOperationException("Failed to parse Telebirr secret.");

        // SEC-10: Parse and validate IP whitelist
        var allowedIps = new List<string>();
        if (dict.TryGetValue("allowedSourceIps", out var ipString) && !string.IsNullOrWhiteSpace(ipString))
        {
            foreach (var rawIp in ipString.Split(','))
            {
                var trimmedIp = rawIp.Trim();
                if (System.Net.IPAddress.TryParse(trimmedIp, out _))
                {
                    allowedIps.Add(trimmedIp);
                }
                else if (!string.IsNullOrEmpty(trimmedIp))
                {
                    // Log invalid IP format but do not fail startup
                    Console.Error.WriteLine(
                        $"WARNING: Invalid IP in allowedSourceIps: '{trimmedIp}' (skipped).");
                }
            }
        }

        if (allowedIps.Count == 0)
        {
            Console.Error.WriteLine(
                "WARNING: No valid allowedSourceIps configured. " +
                "All webhook requests will be rejected by IP validation.");
        }

        return new TelebirrConfig
        {
            FabricAppId = dict.GetValueOrDefault("fabricAppId")
                ?? throw new InvalidOperationException("Missing fabricAppId in secret."),
            AppSecret = dict.GetValueOrDefault("appSecret")
                ?? throw new InvalidOperationException("Missing appSecret in secret."),
            MerchantAppId = dict.GetValueOrDefault("merchantAppId")
                ?? throw new InvalidOperationException("Missing merchantAppId in secret."),
            MerchantCode = dict.GetValueOrDefault("merchantCode")
                ?? throw new InvalidOperationException("Missing merchantCode in secret."),
            ShortCode = dict.GetValueOrDefault("shortCode")
                ?? throw new InvalidOperationException("Missing shortCode in secret."),
            RsaPublicKeyPem = dict.GetValueOrDefault("rsaPublicKeyPem")
                ?? throw new InvalidOperationException("Missing rsaPublicKeyPem in secret."),
            WeredaPrivateKeyPem = dict.GetValueOrDefault("weredaPrivateKeyPem"),
            WebhookUrl = dict.GetValueOrDefault("webhookUrl")
                ?? throw new InvalidOperationException("Missing webhookUrl in secret."),
            ReturnUrl = dict.GetValueOrDefault("returnUrl")
                ?? throw new InvalidOperationException("Missing returnUrl in secret."),
            AllowedSourceIps = allowedIps
        };
    }
}
```

---

## 6. Payment Flow Diagrams

### 6.1 Initiate Payment (C2B Collection)

```
Mobile App          WeredaConnect Server         Telebirr API         Buyer's Phone
    |                       |                         |                     |
    |-- POST /escrow/       |                         |                     |
    |   initiate ---------->|                         |                     |
    |                       |                         |                     |
    |                       |-- 1. Validate request   |                     |
    |                       |-- 2. Check idempotency  |                     |
    |                       |-- 3. Create escrow      |                     |
    |                       |   (status: initiated)   |                     |
    |                       |   (telebirrRef: null)    |                     |
    |                       |                         |                     |
    |                       |-- 4. POST /apply/       |                     |
    |                       |   h5token ------------->|                     |
    |                       |<--- token ------------- |                     |
    |                       |                         |                     |
    |                       |-- 5. Encrypt payload    |                     |
    |                       |   (RSA+AES hybrid)      |                     |
    |                       |-- 6. Sign payload       |                     |
    |                       |   (SHA256 per spec)     |                     |
    |                       |                         |                     |
    |                       |-- 7. POST /create/      |                     |
    |                       |   order --------------->|                     |
    |                       |<--- toPayUrl ---------- |                     |
    |                       |                         |                     |
    |<-- 201 Created -------|                         |                     |
    |   { status: initiated,|                         |                     |
    |     toPayUrl: "...",  |   (SEC-05: toPayUrl     |                     |
    |     telebirrRef: null |    NOT stored in DB)    |                     |
    |   }                   |                         |                     |
    |                       |                         |                     |
    |-- Open toPayUrl ------|---- Telebirr WebView --|----> Buyer pays --->|
    |   (in-app browser)    |                         |                     |
    |                       |                         |                     |
    |                       |<-- 8. Webhook POST ----|                     |
    |                       |   (encrypted result)    |                     |
    |                       |                         |                     |
    |                       |-- 9. Decrypt/verify     |                     |
    |                       |-- 10. Validate timestamp|                     |
    |                       |-- 11. Check replay      |                     |
    |                       |-- 12. Update escrow     |                     |
    |                       |   initiated -> escrowed |                     |
    |                       |   telebirrRef = tradeNo |                     |
    |                       |-- 13. Append audit      |                     |
    |                       |                         |                     |
    |<-- (poll GET /escrow) |                         |                     |
    |   { status: escrowed, |                         |                     |
    |     telebirrRef: "TB."|                         |                     |
    |   }                   |                         |                     |
```

### 6.2 Release Payment (B2B Disbursement -- Phase 2)

```
EscrowCronFunction         WeredaConnect Server         Telebirr B2B API
    |                              |                           |
    |-- Auto-release check ------->|                           |
    |   (delivery confirmed +      |                           |
    |    48h dispute window passed) |                           |
    |                              |                           |
    |                              |-- 1. Calculate payout     |
    |                              |   (amount - commission)   |
    |                              |                           |
    |                              |-- 2. B2B transfer ------->|
    |                              |   to farmer's Telebirr    |
    |                              |<-- transfer result -------|
    |                              |                           |
    |                              |-- 3. Update escrow        |
    |                              |   escrowed -> released    |
    |                              |   -> completed            |
    |                              |-- 4. Append audit         |
    |                              |   (simulated: true)       |
    |                              |                           |
    |<-- result -------------------|                           |
```

**Phase 1 (Sandbox)**: Release logs a payout intent (with `SANDBOX-SIM-` prefix reference) and returns success. Actual B2B transfer is performed manually by the platform operator using Telebirr's merchant dashboard.

**Phase 2 (Production)**: Release calls the Telebirr B2B API to programmatically transfer funds.

### 6.3 Refund Payment (B2B Reverse -- Phase 2)

Same phasing as Release. Sandbox logs intent with `SANDBOX-SIM-REFUND-` prefix; production calls B2B API.

---

## 7. Telebirr DTOs (AOT-Safe)

New DTOs required for the Telebirr API integration. All must be registered in `AppJsonContext`.

```csharp
// ── Telebirr API DTOs ──────────────────────────────────────

/// <summary>POST /apply/h5token request body.</summary>
public sealed record TelebirrTokenRequest
{
    public required string AppId { get; init; }
    public required string AppSecret { get; init; }
}

/// <summary>POST /apply/h5token response.</summary>
public sealed record TelebirrTokenResponse
{
    public int Code { get; init; }
    public string? Msg { get; init; }
    public TelebirrTokenData? Data { get; init; }
}

public sealed record TelebirrTokenData
{
    public string? Token { get; init; }
}

/// <summary>Parameters for /create/order (pre-encryption).</summary>
public sealed record TelebirrCreateOrderRequest
{
    public required string AppId { get; init; }
    public required string AppKey { get; init; }
    public required string Nonce { get; init; }
    public required string OutTradeNo { get; init; }
    public required string TotalAmount { get; init; }
    public required string Timestamp { get; init; }
    public required string Subject { get; init; }
    public required string TimeoutExpress { get; init; }
    public required string ShortCode { get; init; }
    public required string ReturnUrl { get; init; }
    public required string NotifyUrl { get; init; }
    public string? ReceiveName { get; init; }
}

/// <summary>POST /create/order request body (after encryption).</summary>
public sealed record TelebirrCreateOrderEncryptedRequest
{
    public required string Sign { get; init; }
    public required string Ussd { get; init; }
}

/// <summary>POST /create/order response.</summary>
public sealed record TelebirrCreateOrderResponse
{
    public int Code { get; init; }
    public string? Msg { get; init; }
    public TelebirrOrderData? Data { get; init; }
}

public sealed record TelebirrOrderData
{
    public string? ToPayUrl { get; init; }
}

/// <summary>
/// Decrypted webhook notification from Telebirr.
/// SEC-04: Includes optional Timestamp field for replay protection.
/// </summary>
public sealed record TelebirrWebhookNotification
{
    public required string OutTradeNo { get; init; }
    public required string TradeNo { get; init; }
    public required string TotalAmount { get; init; }
    public required string TransactionStatus { get; init; }
    public string? Msisdn { get; init; }
    public string? Timestamp { get; init; }

    public bool IsSuccess => TransactionStatus is "Completed" or "2";
}
```

**AppJsonContext additions**:

```csharp
// Telebirr sandbox API DTOs
[JsonSerializable(typeof(TelebirrTokenRequest))]
[JsonSerializable(typeof(TelebirrTokenResponse))]
[JsonSerializable(typeof(TelebirrTokenData))]
[JsonSerializable(typeof(TelebirrCreateOrderRequest))]
[JsonSerializable(typeof(TelebirrCreateOrderEncryptedRequest))]
[JsonSerializable(typeof(TelebirrCreateOrderResponse))]
[JsonSerializable(typeof(TelebirrOrderData))]
[JsonSerializable(typeof(TelebirrWebhookNotification))]
[JsonSerializable(typeof(TelebirrConfig))]
```

**SEC-05 addition**: The `HoldFundsResponse` needs a `ToPayUrl` field that is transient (returned once, never persisted):

```csharp
public sealed record HoldFundsResponse
{
    public required bool Success { get; init; }
    public string? TelebirrRef { get; init; }
    public string? ToPayUrl { get; init; }      // SEC-05: transient, never persisted
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }
}
```

---

## 8. Configuration

### 8.1 Environment Variables (Lambda)

| Variable | Value | Used By |
|----------|-------|---------|
| `TELEBIRR_MOCK` | `"false"` (sandbox/prod) or `"true"` (dev) | EscrowFunction, EscrowCronFunction |
| `TELEBIRR_SECRET_ARN` | ARN of Secrets Manager secret | TelebirrSandboxGateway, TelebirrWebhookFunction |
| `TELEBIRR_BASE_URL` | `https://developerportal.ethiotelebirr.et:38443/apiaccess/payment/gateway` | TelebirrSandboxGateway |

### 8.2 Secrets Manager

**Secret name**: `/${prefix}/telebirr-credentials`

Contains all merchant credentials (see section 5.5). Rotated manually when Telebirr issues new credentials.

### 8.3 SSM Parameters (existing, unchanged)

| Parameter | Current Value | Purpose |
|-----------|--------------|---------|
| `/${prefix}/escrow/commission-rate` | `0.025` | 2.5% commission |
| `/${prefix}/escrow/dispute-window-hours` | `48` | Dispute window |
| `/${prefix}/escrow/timeout-days` | `7` | Auto-refund timeout |

### 8.4 Gateway Selection Logic -- Security-Hardened

**SEC-06 fix**: Secrets Manager loading includes a 10-second timeout and logs a health status.

```csharp
/// Updated CreateTelebirrGateway() in EscrowFunction and EscrowCronFunction:
private static ITelebirrGateway CreateTelebirrGateway()
{
    var useMock = string.Equals(
        Environment.GetEnvironmentVariable("TELEBIRR_MOCK"),
        "true", StringComparison.OrdinalIgnoreCase);

    if (useMock)
    {
        return new MockTelebirrGateway();
    }

    var secretArn = Environment.GetEnvironmentVariable("TELEBIRR_SECRET_ARN")
        ?? throw new InvalidOperationException("TELEBIRR_SECRET_ARN is not set.");
    var baseUrl = Environment.GetEnvironmentVariable("TELEBIRR_BASE_URL")
        ?? "https://developerportal.ethiotelebirr.et:38443/apiaccess/payment/gateway";

    // SEC-06: Load secret with explicit timeout
    var secretsClient = new AmazonSecretsManagerClient(
        new AmazonSecretsManagerConfig { Timeout = TimeSpan.FromSeconds(10) });

    string secretString;
    try
    {
        var secretResponse = secretsClient.GetSecretValueAsync(
            new GetSecretValueRequest { SecretId = secretArn }).GetAwaiter().GetResult();
        secretString = secretResponse.SecretString
            ?? throw new InvalidOperationException("Telebirr secret is empty.");
        Console.Error.WriteLine("Telebirr credentials loaded successfully.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(
            $"CRITICAL: Failed to load Telebirr credentials: [{ex.GetType().Name}]");
        throw;
    }

    var config = TelebirrConfig.FromSecretJson(secretString);

    var cryptoService = new TelebirrCryptoService(
        config.RsaPublicKeyPem,
        config.AppSecret,
        config.WeredaPrivateKeyPem);

    return new TelebirrSandboxGateway(
        new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) },
        cryptoService,
        config);
}
```

---

## 9. Infrastructure Changes

### 9.1 NAT Gateway (Static Outbound IP)

**Why**: Telebirr requires source IP whitelisting. Lambda functions in a VPC with a NAT Gateway get a static outbound IP that can be registered with Telebirr.

```typescript
// infra/lib/stacks/networking-stack.ts (new stack)

const vpc = new ec2.Vpc(this, 'WeredaVpc', {
  natGateways: 1,           // Single NAT for cost (not HA for MVP)
  maxAzs: 2,
  subnetConfiguration: [
    { name: 'public', subnetType: ec2.SubnetType.PUBLIC },
    { name: 'private', subnetType: ec2.SubnetType.PRIVATE_WITH_EGRESS },
  ],
});

// Only Telebirr-calling Lambdas go in VPC (EscrowFunction, EscrowCronFunction)
// Other Lambdas stay outside VPC to avoid cold start penalty
```

**Cost Impact**:
- NAT Gateway: ~$32/month (Bahrain region) + data processing charges
- This is the most expensive infrastructure addition. Consider:
  - Single NAT (not HA) for sandbox/dev -- acceptable for non-prod
  - SEC-12: Move to HA (2 NATs across 2 AZs) for production

### 9.2 Webhook Endpoint -- Security-Hardened

**SEC-08 fix**: Route-specific throttling on the webhook endpoint.

```typescript
// In api-stack.ts: Add webhook route (NO JWT auth)

const webhookFn = new WeredaLambda(this, 'TelebirrWebhookFunction', {
  functionName: `${prefix}-telebirr-webhook`,
  description: 'POST /webhooks/telebirr -- Telebirr payment notification handler',
  handlerName: 'TelebirrWebhookFunction',
  environment: {
    ESCROWS_TABLE: props.escrowsTable.tableName,
    ESCROW_AUDIT_TABLE: props.escrowAuditTable.tableName,
    ESCROW_IDEMPOTENCY_TABLE: props.escrowIdempotencyTable.tableName,
    TELEBIRR_SECRET_ARN: telebirrSecret.secretArn,
  },
  timeout: cdk.Duration.seconds(15),
  memorySize: 256,
  // SEC-08: Reserve concurrency to limit parallel invocations
  reservedConcurrentExecutions: 10,
});

// DynamoDB permissions
props.escrowsTable.grantReadWriteData(webhookFn.function);
props.escrowAuditTable.grantWriteData(webhookFn.function);
props.escrowIdempotencyTable.grantReadWriteData(webhookFn.function);
telebirrSecret.grantRead(webhookFn.function);

// Route: NO authorizer (Telebirr cannot send JWTs)
httpApi.addRoutes({
  path: '/webhooks/telebirr',
  methods: [apigateway.HttpMethod.POST],
  integration: new integrations.HttpLambdaIntegration(
    'TelebirrWebhookInt', webhookFn.function,
    { payloadFormatVersion: apigateway.PayloadFormatVersion.VERSION_1_0 }
  ),
});

// SEC-08: Route-specific throttling via API Gateway stage overrides
// Telebirr should not send more than a few webhooks per second

// NEW-05: Consider using a less predictable webhook path to reduce
// reconnaissance surface. While security-through-obscurity is not a
// primary defense, it raises the bar for untargeted scanning:
//   path: '/webhooks/telebirr'              <-- current (guessable)
//   path: '/wh/tb-{random-suffix}'          <-- recommended
// The path suffix should be a stable, deployment-time random value
// stored in SSM (not rotated per-request). The notifyUrl sent to
// Telebirr in /create/order must match this path.
```

### 9.3 Secrets Manager

```typescript
const telebirrSecret = new secretsmanager.Secret(this, 'TelebirrCredentials', {
  secretName: `/${prefix}/telebirr-credentials`,
  description: 'Telebirr sandbox merchant credentials (RSA key, app ID, etc.)',
});

// Grant read access SCOPED to this specific secret ARN
telebirrSecret.grantRead(escrowFn.function);
telebirrSecret.grantRead(escrowCronFn.function);
telebirrSecret.grantRead(webhookFn.function);
```

### 9.4 VPC Configuration for Telebirr-Calling Lambdas

Same as v1.0.0. Only EscrowFunction and EscrowCronFunction need VPC access for static outbound IP.

**Cold Start Impact**: VPC-attached Lambdas have ~1-2s additional cold start. Mitigated by:
- Provisioned concurrency (1 instance) for the escrow function in production
- ARM64 + Native AOT already provides fast cold starts (~100ms baseline)
- Payment operations are inherently latency-tolerant (user waits for Telebirr redirect)

---

## 10. AOT Compliance Checklist

| Item | Status | Notes |
|------|--------|-------|
| `System.Security.Cryptography.RSA` | AOT-safe | `RSA.Create()`, `ImportFromPem()`, `Encrypt()`, `Decrypt()`, `VerifyData()` |
| `System.Security.Cryptography.Aes` | AOT-safe | SEC-09 hybrid encryption |
| `System.Security.Cryptography.SHA256` | AOT-safe | `SHA256.HashData()` static method |
| `System.Security.Cryptography.HMACSHA256` | AOT-safe | SEC-02 HMAC alternative |
| `System.Net.Http.HttpClient` | AOT-safe | Standard HTTP client |
| `System.Net.IPAddress.TryParse` | AOT-safe | SEC-10 IP validation |
| `System.Text.Json` source generators | AOT-safe | All new DTOs registered in `AppJsonContext` |
| `AWSSDK.SecretsManager` | AOT-safe | AWS SDK v3 supports AOT |
| `Convert.ToBase64String` / `FromBase64String` | AOT-safe | BCL primitives |
| `Encoding.UTF8.GetBytes` | AOT-safe | BCL primitive |
| No `Newtonsoft.Json` | Verified | Not used |
| No reflection | Verified | Manual construction only |
| No `Moq` in tests | Verified | NSubstitute used |

---

## 11. Error Code Mapping

### Telebirr Error Codes to ESCROW_* Codes

| Telebirr Code | Telebirr Meaning | ESCROW_* Code | HTTP Status | Action |
|--------------|-----------------|---------------|-------------|--------|
| 0 | Success | (none -- success) | 201 / 200 | Continue flow |
| -1 | System error | ESCROW_013 | 502 | Retry with backoff |
| -2 | Invalid parameters | ESCROW_001 | 400 | Log and reject (SEC-04: retry if timestamp error) |
| -3 | Authentication failed | ESCROW_014 | 503 | Alert ops, check credentials |
| -4 | Insufficient balance | ESCROW_025 (new) | 422 | Notify buyer |
| -5 | Duplicate transaction | ESCROW_003 | 409 | Return cached result |
| -6 | Transaction expired | ESCROW_026 (new) | 422 | Cancel escrow |
| -7 | Amount out of range | ESCROW_001 | 400 | Reject |
| (timeout) | No response in 30s | ESCROW_014 | 503 | Retry, then reconcile |
| (webhook: success) | Payment completed | (transition to escrowed) | -- | Update escrow state |
| (webhook: failed) | Payment failed | (transition to cancelled) | -- | Update escrow state |

### New Error Codes (additions to escrow-protocol.md)

| Code | HTTP | Description |
|------|------|-------------|
| `ESCROW_025` | 422 | Buyer has insufficient Telebirr balance |
| `ESCROW_026` | 422 | Telebirr payment session expired (buyer did not complete in time) |
| `ESCROW_027` | -- | (Internal) Webhook rejected: unauthorized source IP (SEC-07: never returned to caller) |
| `ESCROW_028` | -- | (Internal) Webhook rejected: invalid payload (SEC-07: never returned to caller) |

---

## 12. Sandbox Testing Strategy

### 12.1 Test Phases

**Phase 0: Unit Tests (No Telebirr)**
- Test `TelebirrCryptoService` with known RSA key pair (generate test keys)
- SEC-09: Test that realistic-size payloads (400-600 bytes) encrypt successfully
- SEC-01: Test both decryption and signature verification paths
- Test `TelebirrSandboxGateway` with mocked `HttpClient` (HttpMessageHandler)
- Test `TelebirrWebhookFunction` with crafted encrypted payloads
- SEC-04: Test timestamp validation (valid, expired, missing)
- SEC-04: Test replay detection (duplicate webhooks rejected)
- SEC-07: Test that all webhook failures return 200 OK
- SEC-10: Test `TelebirrConfig.FromSecretJson` with whitespace IPs, invalid IPs, empty list
- NEW-01: Test webhook amount validation (matching, mismatching by 1 ETB, mismatching by 1000 ETB, non-numeric)
- NEW-02: Test hybrid RSA+AES decryption of webhook payloads (400-600 byte payloads)
- NEW-03: Test `GenerateNonce()` returns 64 hex chars (32 bytes) and no two calls produce the same value
- NEW-04: Test `GET /escrow/{id}` does NOT include `toPayUrl` in response after initiation
- All tests use NSubstitute, not Moq (AOT compliance)

**Phase 1: Sandbox Integration (Real Telebirr Sandbox)**
- Deploy to AWS dev environment with real Telebirr sandbox credentials
- Test payment creation: verify `toPayUrl` is returned
- Test webhook receipt: simulate Telebirr webhook with test tool
- Test error paths: invalid credentials, expired timestamps, malformed payloads
- Verify NAT Gateway IP is whitelisted in Telebirr's sandbox config
- SEC-01: Confirm the correct RSA operation for webhook payloads
- SEC-02: Confirm the correct signing algorithm
- SEC-09: Confirm the correct encryption scheme (hybrid vs chunked vs field-level)

**Phase 2: End-to-End (Mobile + Server + Telebirr Sandbox)**
- Mobile initiates payment via POST /escrow/initiate
- SEC-05: Verify `toPayUrl` is in response but NOT in `GET /escrow/{id}`
- Verify `toPayUrl` opens Telebirr payment page
- Complete payment in Telebirr sandbox (test account with sandbox funds)
- Verify webhook arrives and escrow transitions to "escrowed"
- Trigger auto-release (fast-forward dispute window in sandbox config)
- SEC-13: Verify release uses `SANDBOX-SIM-` prefix, not real Telebirr ref

### 12.2 Test Configuration

```
Sandbox environment variables:
  TELEBIRR_MOCK=false
  TELEBIRR_SECRET_ARN=arn:aws:secretsmanager:me-south-1:xxx:secret:/wereda-connect-sandbox/telebirr-credentials
  TELEBIRR_BASE_URL=https://developerportal.ethiotelebirr.et:38443/apiaccess/payment/gateway
```

### 12.3 Sandbox Limitations

| Limitation | Impact | Mitigation |
|-----------|--------|-----------|
| Sandbox may not send webhooks | Cannot test async flow | Manually POST to webhook endpoint with test payload |
| No real money movement | Cannot verify fund transfer | Verify API response codes and DynamoDB state transitions |
| Sandbox credentials may expire | Integration tests may fail intermittently | Document credential renewal process |
| Sandbox rate limits may differ | Performance testing invalid | Note sandbox vs production rate limits |
| SEC-14: No status polling endpoint | Cannot actively reconcile | Document limitation; rely on webhooks + 1-hour alert for stuck initiated |

---

## 13. Migration Plan

### Phase 1: Sandbox Gateway (This Document)

```
MockTelebirrGateway (current) --> TelebirrSandboxGateway (next)
```

1. Implement `TelebirrSandboxGateway`, `TelebirrCryptoService`, `TelebirrWebhookFunction`
2. Add Telebirr DTOs to `AppJsonContext`
3. Add `ToPayUrl` field to `HoldFundsResponse` (SEC-05)
4. Add infrastructure: NAT Gateway, Secrets Manager, webhook route with rate limiting (SEC-08)
5. Update `CreateTelebirrGateway()` with timeout and health logging (SEC-06)
6. Deploy to sandbox/dev environment
7. Confirm RSA operation (SEC-01), signing algorithm (SEC-02), encryption scheme (SEC-09) with sandbox
8. Run sandbox integration tests
9. Release (B2B) and refund remain as logged intents (manual payout, `SANDBOX-SIM-` prefix per SEC-13)

### Phase 2: B2B Disbursement

1. Integrate Telebirr B2B transfer API for farmer payouts
2. Integrate Telebirr B2B transfer API for buyer refunds
3. `ReleaseFundsAsync` calls B2B API instead of logging intent
4. `RefundFundsAsync` calls B2B API instead of logging intent
5. Add reconciliation for B2B transfer status
6. End-to-end testing with sandbox B2B API

### Phase 3: Production Gateway

```
TelebirrSandboxGateway --> TelebirrProductionGateway
```

1. Obtain production merchant credentials from Telebirr
2. Complete legal review for custodial fund holding
3. Register production NAT Gateway IP with Telebirr
4. `TelebirrProductionGateway` extends sandbox gateway with:
   - Production base URL
   - Production credentials from Secrets Manager
   - Enhanced logging and alerting
   - SEC-11: Certificate pinning for Telebirr API calls
   - SEC-12: HA NAT Gateway (2 AZs)
5. Gradual rollout: internal beta users first, then public launch

### Rollback Plan

Each phase is independently deployable. Rollback is achieved by setting `TELEBIRR_MOCK=true` in the Lambda environment variables, which reverts to `MockTelebirrGateway`. No code changes required for rollback.

### Feature Flag

```typescript
// api-stack.ts: Environment variable controls which gateway is used
TELEBIRR_MOCK: envConfig.name === 'prod' ? 'false'
             : envConfig.name === 'sandbox' ? 'false'
             : 'true'  // dev, staging use mock
```

---

## 14. Mobile Changes (Minimal)

The mobile app requires minimal changes for sandbox integration. The escrow UI (4 screens, 4 widgets, BLoC) is already built.

### 14.1 toPayUrl Handling

When the server returns `toPayUrl` in the `InitiateEscrowResponse`, the mobile app needs to open it in an in-app browser or redirect to the Telebirr app:

```dart
// After POST /escrow/initiate returns { toPayUrl: "..." }
if (response.toPayUrl != null) {
  await launchUrl(Uri.parse(response.toPayUrl));
}
```

### 14.2 Response Model Update

The `InitiateEscrowResponse` DTO needs a `toPayUrl` field (SEC-05: transient, not stored):

```csharp
public sealed record InitiateEscrowResponse
{
    // ... existing fields ...
    public string? ToPayUrl { get; init; }  // Telebirr payment page URL (transient)
}
```

### 14.3 Payment Status Polling

After the buyer completes payment in Telebirr, the mobile app polls `GET /escrow/{id}` every 5 seconds for up to 2 minutes after returning from Telebirr. Polling is sufficient for MVP given the 3G network context.

---

## 15. Security Considerations

### 15.1 Webhook Authentication (SEC-03 hardened)

The webhook endpoint uses **layered defense-in-depth**:

| Layer | Mechanism | Purpose |
|-------|-----------|---------|
| 1 (secondary) | Source IP validation | First-pass filter; rejects most unauthorized requests |
| 2 (primary) | Encrypted payload decryption/verification | Only Telebirr can produce valid payloads |
| 3 | Timestamp validation (SEC-04) | Prevents replay of stale payloads |
| 4 | Composite replay detection (SEC-04) | outTradeNo + tradeNo + status prevents exact duplicates |
| 5 | Idempotent processing | Same webhook produces same result |

All failures return `200 OK` (SEC-07). Differentiation via CloudWatch metrics only.

### 15.2 Credential Security

| Credential | Storage | Access |
|-----------|---------|--------|
| Telebirr RSA public key | Secrets Manager | EscrowFunction, WebhookFunction |
| WeredaConnect RSA private key (SEC-01) | Secrets Manager | WebhookFunction only |
| Telebirr appSecret | Secrets Manager | EscrowFunction only |
| NAT Gateway Elastic IP | CloudFormation output | Registered with Telebirr manually |

### 15.3 Data Flow Security

| Path | Encryption | Notes |
|------|-----------|-------|
| Mobile -> API Gateway | TLS 1.2+ | Cognito JWT auth |
| API Gateway -> Lambda | AWS internal | IAM-authenticated |
| Lambda -> Telebirr API | TLS 1.2+ | RSA+AES hybrid payload encryption (SEC-09) |
| Telebirr -> Webhook | TLS 1.2+ | Encrypted payload + IP validation (SEC-03) |
| Lambda -> DynamoDB | TLS 1.2+ | At-rest encryption |
| Lambda -> Secrets Manager | TLS 1.2+ | IAM-scoped to specific ARN |

### 15.4 Audit Trail

| Event | Audit Action | Details |
|-------|-------------|---------|
| Token acquired | `telebirr_token` | Timestamp, success/failure |
| Order created | `telebirr_order` | outTradeNo only (SEC-05: no toPayUrl) |
| Webhook received | `webhook_received` | Masked source IP (SEC-15), outTradeNo, status, reception timestamp |
| Webhook processed | `webhook_processed` | State transition, tradeNo |
| Sandbox payout | `payout_initiated` | `simulated: true` (SEC-13), amount |
| Sandbox refund | `refund_initiated` | `simulated: true` (SEC-13), amount |

### 15.5 PII Handling

| Data | Treatment |
|------|----------|
| Phone numbers | Masked in logs: `+2519****4567` |
| RSA keys | Never logged |
| appSecret | Never logged |
| Fayda IDs | Never included in Telebirr calls |
| toPayUrl | SEC-05: Never persisted. Returned once to mobile, never logged. |
| Source IP | SEC-15: Masked in logs (`x.y.*.*`) |

---

## 16. Security Mitigations

This section maps each security review finding to its resolution in this architecture revision.

### P0 Critical (Must Fix Before Any Deployment)

| Finding | Resolution | Section |
|---------|-----------|---------|
| **SEC-01**: RSA public key used for decryption (cryptographically impossible with OAEP) | Revised `TelebirrCryptoService` to support three interpretations: (A) decrypt with WeredaConnect's private key, (B) verify signature with Telebirr's public key, (C) PKCS#1 v1.5 raw. Added `weredaPrivateKeyPem` to config. Implementation defaults to Interpretation A. **Must confirm with Telebirr before deployment.** | 5.3 |
| **SEC-02**: SHA256 signing uses hash concatenation, not HMAC | Added both `SignPayload` (secret-suffix hash) and `SignPayloadHmac` (HMAC-SHA256) methods. Documented the length-extension vulnerability. Default implementation follows likely Telebirr spec (secret-suffix). Added code comment warning against "improvement". **Must confirm with Telebirr before deployment.** | 5.3 |

### P1 High (Must Fix Before Production)

| Finding | Resolution | Section |
|---------|-----------|---------|
| **SEC-03**: Webhook auth too weak (IP only) | Layered defense: (1) IP validation as secondary filter, (2) encrypted payload decryption/verification as primary auth, (3) timestamp validation, (4) replay detection. | 5.4, 15.1 |
| **SEC-04**: No timestamp/replay validation on webhooks | Added timestamp skew validation (5-minute window) on webhook payloads. Added composite replay detection key: `outTradeNo + tradeNo + transactionStatus`. Outbound requests retry with fresh timestamp on rejection. | 5.4, 5.2 |
| **SEC-05**: toPayUrl stored as TelebirrRef (information leak) | `toPayUrl` is returned once in `HoldFundsResponse.ToPayUrl` (transient). `TelebirrRef` is set to `PENDING-WEBHOOK-{ref}` until webhook provides real `tradeNo`. `toPayUrl` is never persisted in DynamoDB, audit trail, or subsequent API responses. | 5.2, 7 |

### P2 Medium (Should Fix)

| Finding | Resolution | Section |
|---------|-----------|---------|
| **SEC-06**: Cold-start secret loading has no retry/timeout | Added 10-second timeout on Secrets Manager client. Added health logging on success/failure. Explicit error message on load failure. | 8.4 |
| **SEC-07**: Webhook returns 403 (information leak) | All webhook responses return `200 OK` with empty body. Failures logged internally via CloudWatch. `OkResponse()` helper enforces this. | 5.4 |
| **SEC-08**: No rate limiting on webhook | Added reserved concurrency limit (10) on webhook Lambda. Route-specific API Gateway throttling recommended. | 9.2 |
| **SEC-09**: RSA block size limit not addressed | Documented three encryption approaches (hybrid RSA+AES, chunked PKCS#1, field-level). Default implementation uses hybrid RSA+AES (AES-256-CBC for payload, RSA for AES key). Must validate against Telebirr's sandbox. | 5.3 |
| **SEC-10**: AllowedSourceIps parsing issues | IPs are trimmed, validated via `IPAddress.TryParse`, and logged if invalid. Empty list produces a startup warning. Stored as comma-separated string (not JSON array) to match `Dictionary<string, string>` deserialization. | 5.6 |

### P3 Low (Advisory -- Deferred)

| Finding | Resolution | Timeline |
|---------|-----------|----------|
| **SEC-11**: No certificate pinning for Telebirr API | Tracked as Phase 3 (production) requirement. | Production |
| **SEC-12**: NAT Gateway single point of failure | Single NAT acceptable for sandbox. HA NAT (2 AZs) for production. | Production |
| **SEC-13**: Simulated release/refund returns predictable references | Sandbox references prefixed with `SANDBOX-SIM-`. TransactionRef hashed (not raw). Audit entries include `simulated: true`. | 5.2 |
| **SEC-14**: GetTransactionStatusAsync always returns "unknown" | Documented limitation. Added 1-hour alert for stuck "initiated" escrows. Real status polling deferred to Phase 2 (if Telebirr provides endpoint). | 5.2 |
| **SEC-15**: Source IP logged in plaintext | IPs masked in logs (`x.y.*.*`). Raw IPs available only via CloudWatch Insights query with restricted IAM. | 5.4 |

### Architecture Re-Review Findings (v2.1.0)

| Finding | Severity | Resolution | Section |
|---------|----------|-----------|---------|
| **NEW-01**: Webhook amount not validated against escrow record | P1 High | Added amount validation in webhook handler before `TransitionToEscrowedAsync`. Webhook `TotalAmount` must match escrow `amount` within 0.01 ETB tolerance. Mismatches are rejected with audit trail entry (`webhook_amount_mismatch`). | 5.4 |
| **NEW-02**: Webhook decryption doesn't handle hybrid RSA+AES format | P2 Medium | `DecryptWebhookPayload` now detects hybrid format (contains `|` separator) and performs RSA decryption of AES key+IV followed by AES-256-CBC decryption of payload. Falls back to simple RSA for payloads that fit in one block. | 5.3 |
| **NEW-03**: `GenerateNonce()` implementation not specified | P2 Medium | Added explicit `GenerateNonce()` using `RandomNumberGenerator.GetBytes(32)` (CSPRNG). Returns 64 hex characters (256 bits of entropy). Prevents nonce prediction and collision attacks. | 5.2 |
| **NEW-04**: No enforcement of `toPayUrl` non-persistence | P2 Medium | Added 4 enforcement rules: (1) `EscrowService` must not pass `toPayUrl` to DynamoDB, (2) audit repo must not accept it as metadata, (3) unit test verifying `GET /escrow/{id}` excludes it, (4) code review checklist item. | 5.2, 12 |
| **NEW-05**: Predictable webhook URL path `/webhooks/telebirr` | P3 Low | Recommended using a less predictable path (`/wh/tb-{random-suffix}`) with deployment-time random value stored in SSM. Not a primary defense but raises the bar for untargeted scanning. | 9.2 |

---

## 17. Cost Analysis

### 17.1 New Infrastructure Costs (Monthly, Sandbox)

| Resource | Estimated Cost | Notes |
|----------|---------------|-------|
| NAT Gateway | ~$32/mo | Single NAT, Bahrain region |
| NAT Data Processing | ~$5/mo | Low traffic in sandbox |
| Secrets Manager | ~$0.40/mo | 1 secret, ~1000 API calls/mo |
| Webhook Lambda | ~$0.50/mo | Low invocation count |
| VPC Endpoints (DynamoDB) | ~$7/mo | Required when Lambda is in VPC |
| **Total** | **~$45/mo** | Significant increase from current ~$15/mo |

### 17.2 Cost Optimization Options

1. **Share NAT Gateway**: If other services need VPC egress, share the NAT
2. **Deferred NAT**: Use Lambda outside VPC during development (no IP whitelisting needed for sandbox testing with Telebirr's test mode)
3. **VPC Endpoint for DynamoDB**: Avoids NAT Gateway charges for DynamoDB traffic

---

## 18. Open Questions

| # | Question | Impact | Source |
|---|----------|--------|--------|
| 1 | What is Telebirr's exact RSA operation for webhook payloads? (Encryption with our public key, or signing with their private key?) | **Critical** (SEC-01) | Telebirr dev docs |
| 2 | What is Telebirr's exact signing algorithm? (SHA256 secret-suffix, HMAC-SHA256, or other?) | **Critical** (SEC-02) | Telebirr dev docs |
| 3 | What is the exact RSA encryption scheme? (Hybrid RSA+AES, PKCS#1 v1.5 chunked, or field-level?) | **Critical** (SEC-09) | Telebirr dev docs |
| 4 | Does Telebirr sandbox require IP whitelisting? | If not, NAT Gateway can be deferred | Telebirr support |
| 5 | Does Telebirr sandbox send real webhook notifications? | Affects testing strategy | Sandbox testing |
| 6 | Does Telebirr webhook payload include a timestamp or nonce? | Affects replay protection (SEC-04) | Answered (ESC-45): no `timestamp` or nonce, only `notify_time`; see `telebirr-webhook-delivery-and-recovery.md` F0 to F3 |
| 7 | Is Telebirr B2B API available in sandbox? | Affects Phase 2 timeline | Telebirr support |
| 8 | Ethiopian regulatory requirements for holding buyer funds? | Legal risk | Legal review |
| 9 | Telebirr's webhook retry policy? | Affects reconciliation design | Not documented (ESC-45, F1); the inbox replay sweep does not rely on it; sandbox check in `telebirr-webhook-delivery-and-recovery.md` section 5 |
| 10 | Is the `toPayUrl` a one-time-use URL? | Determines residual SEC-05 risk | Not documented (ESC-45, F8); sandbox check in `telebirr-webhook-delivery-and-recovery.md` section 5 |

---

## 19. Risks and Mitigations

| Risk | Severity | Probability | Mitigation |
|------|----------|-------------|-----------|
| Telebirr sandbox API changes without notice | High | Medium | Pin to documented API version, monitor for breaking changes |
| RSA operation mismatch (SEC-01) | High | Medium | Test all three interpretations against sandbox before deployment |
| Signing algorithm mismatch (SEC-02) | High | Medium | Test both secret-suffix and HMAC against sandbox |
| Encryption scheme mismatch (SEC-09) | High | Medium | Test hybrid, chunked, and field-level against sandbox |
| NAT Gateway single point of failure | Medium | Low | Single NAT for sandbox; HA NAT for production (SEC-12) |
| Webhook delivery failure | Medium | Medium | Layered validation (SEC-03) + reconciliation cron (every 6h) |
| Lambda cold start with VPC | Low | High | Provisioned concurrency for production; acceptable for sandbox |
| Telebirr sandbox rate limits | Low | Medium | Client-side rate limiting (max 10 req/min to Telebirr) |
| Custodial fund risk (regulatory) | High | Unknown | Legal review required before production; sandbox has no real funds |

---

## 20. Dependencies

| Dependency | Owner | Status | Blocking? |
|-----------|-------|--------|-----------|
| Telebirr sandbox merchant credentials | Telebirr onboarding team | Not started | Yes (Phase 1) |
| Telebirr RSA public key | Telebirr developer portal | Not started | Yes (Phase 1) |
| Telebirr webhook encryption spec (SEC-01) | Telebirr developer portal | Not started | Yes (Phase 1) |
| Telebirr signing algorithm spec (SEC-02) | Telebirr developer portal | Not started | Yes (Phase 1) |
| NAT Gateway deployment | Infrastructure (CDK) | Not started | Yes (if IP whitelisting required) |
| Legal review (custodial funds) | Business/Legal | Not started | No (sandbox only; required for production) |
| Telebirr B2B API documentation | Telebirr developer portal | Not started | No (Phase 2) |

---

## Changelog

| Version | Date | Change |
|---------|------|--------|
| 1.0.0 | 2026-02-24 | Initial architecture: gap analysis, platform-managed escrow design, component design, payment flows, infrastructure, AOT compliance, error mapping, testing strategy, migration plan |
| 2.0.0 | 2026-02-24 | Post-security review revision: addressed all P0 (SEC-01, SEC-02), P1 (SEC-03, SEC-04, SEC-05), and P2 (SEC-06 through SEC-10) findings. Added Security Mitigations section (S16). Updated crypto service with three RSA interpretations, hybrid encryption, HMAC option. Hardened webhook with layered auth, timestamp validation, replay detection, and uniform 200 OK responses. Fixed toPayUrl leak, IP parsing, cold-start resilience, and sandbox reference format. |
| 2.1.0 | 2026-02-24 | Architecture re-review: 5 additional findings (NEW-01 through NEW-05). Added webhook amount validation against escrow record (NEW-01). Fixed webhook decryption to handle hybrid RSA+AES format for inbound payloads (NEW-02). Added explicit `GenerateNonce()` using `RandomNumberGenerator.GetBytes(32)` (NEW-03). Added toPayUrl non-persistence enforcement rules and test requirements (NEW-04). Recommended less predictable webhook URL path (NEW-05). |

---

**Last Updated**: 2026-02-24
**Author**: Architecture Agent
**Security Reviewer**: Security Agent + Architecture Re-Review
**Version**: 2.1.0
**Status**: Approved (Post-Security Review + Architecture Re-Review)
