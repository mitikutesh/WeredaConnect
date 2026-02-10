# Authentication & Encryption Implementation Plan

## Decision Summary

**Recommendation**: **Hybrid AWS Cognito + Custom JWT** with offline-first token caching  
**Status**: Proposed  
**Date**: 2026-02-10  
**Architect**: Planning Agent  
**Version**: 1.0.0

---

## Executive Summary

This plan addresses 12 critical security vulnerabilities identified in the security audit while maintaining WeredaConnect's offline-first architecture for Ethiopian rural users. The solution combines AWS Cognito for managed authentication with custom JWT handling for offline scenarios, plus comprehensive encryption and security hardening.

### Key Decisions
1. **Auth Strategy**: AWS Cognito with offline token caching (hybrid approach)
2. **Token Lifetime**: 15-min access tokens, 30-day refresh tokens (offline-friendly)
3. **Mobile Encryption**: Isar database encryption with device-bound keys
4. **Certificate Pinning**: Dio HTTP client with AWS certificate pinning
5. **Server Validation**: Custom JWT validator (AOT-compatible, no reflection)
6. **Ethiopian Context**: SIM swap protection, shared device support, low-literacy UX

---

## 1. Architecture Decision: AWS Cognito vs Custom JWT

### Decision: **Hybrid Approach** ✅

**Rationale**:
- **AWS Cognito** provides managed user pools, MFA, and compliance
- **Custom JWT caching** enables offline authentication
- **Best of both worlds**: Cloud security + offline-first UX

### Comparison Matrix

| Feature | AWS Cognito Only | Custom JWT Only | **Hybrid (Chosen)** |
|---------|------------------|-----------------|---------------------|
| Offline auth | ❌ Requires internet | ✅ Works offline | ✅ Works offline |
| MFA support | ✅ Built-in | ❌ Manual implementation | ✅ Built-in |
| Token refresh | ❌ Requires internet | ✅ Works offline | ✅ Cached refresh |
| Compliance | ✅ SOC2, HIPAA ready | ❌ Manual audit | ✅ SOC2, HIPAA ready |
| Cost | $0.0055/MAU (first 50k free) | $0 (DIY) | $0.0055/MAU |
| Maintenance | ✅ Managed | ❌ High burden | ✅ Managed |
| AOT compatible | ⚠️ Needs custom validator | ✅ Full control | ✅ Custom validator |

### Why Not Cognito-Only?
- **Offline-first requirement**: Users must authenticate without internet
- **Ethiopian context**: Intermittent 3G connectivity
- **User experience**: Can't block login when offline

### Why Not Custom JWT-Only?
- **Solo developer burden**: MFA, password reset, account recovery are complex
- **Security audit**: Managed service reduces attack surface
- **Compliance**: Cognito provides audit trails and compliance certifications

---

## 2. Offline-First Token Flow Design

### 2.1 Initial Registration (Online Required)

```
User → Mobile App → AWS Cognito → DynamoDB
  1. Enter phone number (+251912345678)
  2. Cognito sends SMS OTP
  3. Verify OTP
  4. Create password
  5. Receive tokens:
     - Access token (JWT, 15 min)
     - Refresh token (30 days)
     - ID token (user profile)
  6. Store encrypted in Keychain/Keystore
  7. Cache user profile in Isar (encrypted)
```

**Ethiopian Context**:
- Phone number as primary identifier (not email)
- SMS OTP via Ethio Telecom
- Amharic/Oromo UI for low-literacy users

### 2.2 Subsequent Login (Offline-Capable)

```
┌─────────────────────────────────────────────────────────────┐
│ SCENARIO A: Online Login                                    │
├─────────────────────────────────────────────────────────────┤
│ 1. User enters phone + password                             │
│ 2. Check connectivity → ONLINE                              │
│ 3. Call Cognito InitiateAuth API                            │
│ 4. Receive fresh tokens                                     │
│ 5. Update local cache (encrypted)                           │
│ 6. Proceed to app                                           │
└─────────────────────────────────────────────────────────────┘

┌─────────────────────────────────────────────────────────────┐
│ SCENARIO B: Offline Login (Cached Credentials)             │
├─────────────────────────────────────────────────────────────┤
│ 1. User enters phone + password                             │
│ 2. Check connectivity → OFFLINE                             │
│ 3. Hash password with PBKDF2                                │
│ 4. Compare with cached hash (Keychain/Keystore)             │
│ 5. If match:                                                │
│    - Load cached user profile from Isar                     │
│    - Generate local session token (device-bound)            │
│    - Mark session as "offline" (sync pending)               │
│ 6. Proceed to app (offline mode)                            │
│ 7. When online: Validate with Cognito, refresh tokens       │
└─────────────────────────────────────────────────────────────┘
```

### 2.3 Token Refresh (Background Sync)

```dart
// Background sync job (runs when connectivity detected)
class TokenRefreshService {
  Future<void> refreshIfNeeded() async {
    final accessToken = await _secureStorage.read('access_token');
    final refreshToken = await _secureStorage.read('refresh_token');

    // Check if access token expired
    if (_isTokenExpired(accessToken)) {
      try {
        // Call Cognito refresh endpoint
        final newTokens = await _cognitoClient.refreshToken(refreshToken);

        // Update secure storage
        await _secureStorage.write('access_token', newTokens.accessToken);
        await _secureStorage.write('refresh_token', newTokens.refreshToken);

        // Update Isar cache
        await _updateUserSession(newTokens);
      } catch (e) {
        // If refresh fails, continue with cached credentials
        // User will re-authenticate when online
      }
    }
  }
}
```

**Token Lifetimes** (Optimized for Ethiopian Context):
- **Access Token**: 15 minutes (short-lived for security)
- **Refresh Token**: 30 days (long-lived for offline scenarios)
- **Offline Session**: 7 days max (force re-auth for security)

---

## 3. Implementation Packages & Libraries

### 3.1 Mobile (Flutter/Dart)

#### Authentication
```yaml
dependencies:
  # AWS Cognito SDK
  amazon_cognito_identity_dart_2: ^3.8.0  # Cognito client

  # Secure Storage (Keychain/Keystore)
  flutter_secure_storage: ^9.0.0  # Encrypted key-value storage

  # JWT Handling
  dart_jsonwebtoken: ^2.14.0  # JWT parsing/validation (offline)

  # Cryptography
  cryptography: ^2.7.0  # PBKDF2, AES encryption
  pointycastle: ^3.9.0  # Additional crypto primitives
```

#### Database Encryption
```yaml
dependencies:
  # Isar with encryption support
  isar: ^4.0.0
  isar_flutter_libs: ^4.0.0  # Includes encryption
```

#### HTTPS Certificate Pinning
```yaml
dependencies:
  # Dio HTTP client with pinning
  dio: ^5.4.0  # Already in pubspec.yaml
  dio_certificate_pinning: ^1.0.0  # NEW: Certificate pinning
```

### 3.2 Server (.NET 10 AOT)

#### JWT Validation (AOT-Compatible)
```xml
<!-- ❌ BANNED: Microsoft.AspNetCore.Authentication.JwtBearer (uses reflection) -->
<!-- ✅ USE: Manual JWT validation with System.IdentityModel.Tokens.Jwt -->

<PackageReference Include="System.IdentityModel.Tokens.Jwt" Version="8.0.0" />
<PackageReference Include="Microsoft.IdentityModel.Tokens" Version="8.0.0" />
```

**AOT Compliance**: These libraries support source generators and explicit type registration.

#### AWS Cognito Integration
```xml
<!-- AWS SDK for Cognito (already approved in AOT_COMPLIANCE.md) -->
<PackageReference Include="AWSSDK.CognitoIdentityProvider" Version="3.7.400" />
```

#### Input Validation
```xml
<!-- Manual validation (no FluentValidation due to AOT concerns) -->
<!-- Use DataAnnotations with source generators -->
<PackageReference Include="System.ComponentModel.Annotations" Version="5.0.0" />
```

### 3.3 Infrastructure (AWS CDK)

```typescript
// AWS Cognito User Pool
import * as cognito from 'aws-cdk-lib/aws-cognito';

const userPool = new cognito.UserPool(this, 'WeredaConnectUserPool', {
  userPoolName: 'wereda-connect-users',
  selfSignUpEnabled: true,
  signInAliases: { phone: true },  // Phone number login
  autoVerify: { phone: true },     // SMS OTP verification

  // Password policy
  passwordPolicy: {
    minLength: 8,
    requireLowercase: true,
    requireUppercase: true,
    requireDigits: true,
    requireSymbols: false,  // Easier for low-literacy users
  },

  // MFA (optional, recommended for high-value accounts)
  mfa: cognito.Mfa.OPTIONAL,
  mfaSecondFactor: {
    sms: true,
    otp: false,  // TOTP not suitable for low-literacy users
  },

  // Account recovery
  accountRecovery: cognito.AccountRecovery.PHONE_ONLY_WITHOUT_MFA,

  // Token lifetimes
  accessTokenValidity: Duration.minutes(15),
  refreshTokenValidity: Duration.days(30),
  idTokenValidity: Duration.minutes(15),
});
```

---

## 4. Implementation Order (Priority-Based)

### Phase 1: Foundation (Week 1) - CRITICAL

#### 1.1 Server JWT Validation (P0)
**Files to create**:
- `server/src/Auth/JwtValidator.cs`
- `server/src/Auth/CognitoJwtValidator.cs`
- `server/src/Middleware/AuthenticationMiddleware.cs`

**Implementation**:
```csharp
// AOT-compatible JWT validator
public class CognitoJwtValidator
{
    private readonly string _userPoolId;
    private readonly string _region;
    private readonly HttpClient _httpClient;

    public async Task<ClaimsPrincipal?> ValidateTokenAsync(string token)
    {
        // 1. Download Cognito JWKS (JSON Web Key Set)
        var jwks = await GetJwksAsync();

        // 2. Validate token signature
        var tokenHandler = new JsonWebTokenHandler();
        var validationParams = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = $"https://cognito-idp.{_region}.amazonaws.com/{_userPoolId}",
            ValidateAudience = true,
            ValidAudience = _clientId,
            ValidateLifetime = true,
            IssuerSigningKeys = jwks.Keys,
        };

        var result = await tokenHandler.ValidateTokenAsync(token, validationParams);
        return result.IsValid ? result.ClaimsPrincipal : null;
    }
}
```

**Tests**:
- Valid token → returns claims
- Expired token → returns null
- Invalid signature → returns null
- Malformed token → returns null

#### 1.2 Mobile Database Encryption (P0)
**Files to modify**:
- `mobile/lib/data/local/database.dart`

**Implementation**:
```dart
// Generate encryption key (device-bound)
Future<Uint8List> _getEncryptionKey() async {
  const keyName = 'isar_encryption_key';

  // Try to read existing key
  final existingKey = await _secureStorage.read(key: keyName);
  if (existingKey != null) {
    return base64Decode(existingKey);
  }

  // Generate new 256-bit key
  final key = Uint8List.fromList(
    List.generate(32, (_) => Random.secure().nextInt(256))
  );

  // Store in Keychain/Keystore
  await _secureStorage.write(
    key: keyName,
    value: base64Encode(key),
  );

  return key;
}

// Open Isar with encryption
final isar = await Isar.open(
  [UserSchema, CropSchema, TripSchema, SyncOperationSchema],
  directory: dir.path,
  encryptionKey: await _getEncryptionKey(),  // ✅ Enable encryption
);
```

**Tests**:
- Database encrypted on disk
- Key persists across app restarts
- Key unique per device
- Cannot read DB without key

#### 1.3 Input Validation (P0)
**Files to create**:
- `server/src/Validation/RequestValidator.cs`
- `server/src/Models/ValidationResult.cs`

**Implementation**:
```csharp
// Manual validation (AOT-compatible)
public static class RequestValidator
{
    public static ValidationResult ValidatePhoneNumber(string phone)
    {
        // Ethiopian phone format: +251XXXXXXXXX (13 chars)
        if (string.IsNullOrWhiteSpace(phone))
            return ValidationResult.Fail("Phone number required");

        if (!phone.StartsWith("+251"))
            return ValidationResult.Fail("Must be Ethiopian number (+251)");

        if (phone.Length != 13)
            return ValidationResult.Fail("Invalid phone format");

        if (!phone[4..].All(char.IsDigit))
            return ValidationResult.Fail("Phone must contain only digits");

        return ValidationResult.Success();
    }

    public static ValidationResult ValidateCropListing(CreateCropDto dto)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(dto.CropType))
            errors.Add("Crop type required");

        if (dto.Quantity <= 0)
            errors.Add("Quantity must be positive");

        if (dto.PricePerKg < 0)
            errors.Add("Price cannot be negative");

        return errors.Any()
            ? ValidationResult.Fail(errors)
            : ValidationResult.Success();
    }
}
```

### Phase 2: Authentication Flow (Week 2) - HIGH PRIORITY

#### 2.1 AWS Cognito Setup (Infrastructure)
**Files to modify**:
- `infra/lib/stacks/auth-stack.ts` (NEW)
- `infra/bin/app.ts`

**CDK Implementation**:
```typescript
export class AuthStack extends cdk.Stack {
  public readonly userPool: cognito.UserPool;
  public readonly userPoolClient: cognito.UserPoolClient;

  constructor(scope: Construct, id: string, props?: cdk.StackProps) {
    super(scope, id, props);

    // User Pool
    this.userPool = new cognito.UserPool(this, 'UserPool', {
      userPoolName: 'wereda-connect-users',
      selfSignUpEnabled: true,
      signInAliases: { phone: true },
      autoVerify: { phone: true },

      // SMS configuration (Ethio Telecom)
      smsConfiguration: {
        snsCallerArn: this.createSnsRole().roleArn,
        externalId: 'wereda-connect-sms',
      },

      // Password policy
      passwordPolicy: {
        minLength: 8,
        requireLowercase: true,
        requireUppercase: true,
        requireDigits: true,
        requireSymbols: false,
      },

      // MFA
      mfa: cognito.Mfa.OPTIONAL,
      mfaSecondFactor: { sms: true, otp: false },

      // Account recovery
      accountRecovery: cognito.AccountRecovery.PHONE_ONLY_WITHOUT_MFA,

      // Custom attributes (Ethiopian context)
      customAttributes: {
        'faydaId': new cognito.StringAttribute({ mutable: false }),
        'woreda': new cognito.StringAttribute({ mutable: true }),
        'role': new cognito.StringAttribute({ mutable: false }),
      },
    });

    // App Client (mobile app)
    this.userPoolClient = this.userPool.addClient('MobileClient', {
      userPoolClientName: 'wereda-connect-mobile',
      authFlows: {
        userPassword: true,
        userSrp: true,  // Secure Remote Password
      },
      generateSecret: false,  // Public client (mobile)
      accessTokenValidity: cdk.Duration.minutes(15),
      refreshTokenValidity: cdk.Duration.days(30),
      idTokenValidity: cdk.Duration.minutes(15),
    });

    // Outputs
    new cdk.CfnOutput(this, 'UserPoolId', {
      value: this.userPool.userPoolId,
      exportName: 'WeredaConnect-UserPoolId',
    });

    new cdk.CfnOutput(this, 'UserPoolClientId', {
      value: this.userPoolClient.userPoolClientId,
      exportName: 'WeredaConnect-UserPoolClientId',
    });
  }
}
```

#### 2.2 Mobile Authentication Service
**Files to create**:
- `mobile/lib/features/auth/data/auth_service.dart`
- `mobile/lib/features/auth/data/cognito_client.dart`
- `mobile/lib/features/auth/data/offline_auth_cache.dart`

**Implementation**:
```dart
class AuthService {
  final CognitoClient _cognitoClient;
  final OfflineAuthCache _offlineCache;
  final ConnectivityService _connectivity;

  Future<AuthResult> login(String phone, String password) async {
    final isOnline = await _connectivity.isOnline();

    if (isOnline) {
      return await _onlineLogin(phone, password);
    } else {
      return await _offlineLogin(phone, password);
    }
  }

  Future<AuthResult> _onlineLogin(String phone, String password) async {
    try {
      // Call Cognito
      final tokens = await _cognitoClient.initiateAuth(phone, password);

      // Cache credentials for offline use
      await _offlineCache.cacheCredentials(
        phone: phone,
        passwordHash: _hashPassword(password),
        tokens: tokens,
      );

      return AuthResult.success(tokens);
    } catch (e) {
      return AuthResult.failure('Login failed: ${e.toString()}');
    }
  }

  Future<AuthResult> _offlineLogin(String phone, String password) async {
    // Verify against cached credentials
    final cachedHash = await _offlineCache.getPasswordHash(phone);
    final inputHash = _hashPassword(password);

    if (cachedHash == null || cachedHash != inputHash) {
      return AuthResult.failure('Invalid credentials (offline mode)');
    }

    // Load cached tokens
    final tokens = await _offlineCache.getTokens(phone);
    if (tokens == null) {
      return AuthResult.failure('No cached session (go online to login)');
    }

    return AuthResult.success(tokens, isOffline: true);
  }

  String _hashPassword(String password) {
    // PBKDF2 with 100,000 iterations
    final pbkdf2 = Pbkdf2(
      macAlgorithm: Hmac.sha256(),
      iterations: 100000,
      bits: 256,
    );

    final hash = pbkdf2.deriveKeyFromPassword(
      password: password,
      nonce: _getSalt(),  // Device-specific salt
    );

    return base64Encode(hash.bytes);
  }
}
```

#### 2.3 Server Lambda Authorizer
**Files to create**:
- `server/src/Functions/AuthorizerFunction.cs`
- `server/src/Auth/AuthorizationHandler.cs`

**Implementation**:
```csharp
public class AuthorizerFunction
{
    private readonly CognitoJwtValidator _jwtValidator;

    public async Task<APIGatewayCustomAuthorizerResponse> FunctionHandler(
        APIGatewayCustomAuthorizerRequest request,
        ILambdaContext context)
    {
        try
        {
            // Extract token from Authorization header
            var token = ExtractToken(request.AuthorizationToken);
            if (string.IsNullOrEmpty(token))
            {
                return GenerateDenyPolicy("anonymous", request.MethodArn);
            }

            // Validate JWT
            var principal = await _jwtValidator.ValidateTokenAsync(token);
            if (principal == null)
            {
                return GenerateDenyPolicy("invalid", request.MethodArn);
            }

            // Extract user ID and role
            var userId = principal.FindFirst("sub")?.Value;
            var role = principal.FindFirst("custom:role")?.Value ?? "user";

            // Generate allow policy
            return GenerateAllowPolicy(userId, request.MethodArn, role);
        }
        catch (Exception ex)
        {
            context.Logger.LogError($"Authorization failed: {ex.Message}");
            return GenerateDenyPolicy("error", request.MethodArn);
        }
    }

    private APIGatewayCustomAuthorizerResponse GenerateAllowPolicy(
        string principalId,
        string resource,
        string role)
    {
        return new APIGatewayCustomAuthorizerResponse
        {
            PrincipalId = principalId,
            PolicyDocument = new APIGatewayCustomAuthorizerPolicy
            {
                Statement = new List<APIGatewayCustomAuthorizerPolicy.IAMPolicyStatement>
                {
                    new()
                    {
                        Effect = "Allow",
                        Action = new HashSet<string> { "execute-api:Invoke" },
                        Resource = new HashSet<string> { resource }
                    }
                },
            },
            Context = new APIGatewayCustomAuthorizerContextOutput
            {
                ["userId"] = principalId,
                ["role"] = role,
            },
        };
    }
}
```

### Phase 3: Security Hardening (Week 3) - HIGH PRIORITY

#### 3.1 HTTPS Certificate Pinning (Mobile)
**Files to modify**:
- `mobile/lib/core/network/dio_client.dart`

**Implementation**:
```dart
class DioClient {
  late final Dio _dio;

  DioClient() {
    _dio = Dio(BaseOptions(
      baseUrl: Config.apiBaseUrl,
      connectTimeout: const Duration(seconds: 30),
      receiveTimeout: const Duration(seconds: 30),
    ));

    // Add certificate pinning
    (_dio.httpClientAdapter as IOHttpClientAdapter).createHttpClient = () {
      final client = HttpClient();

      client.badCertificateCallback = (cert, host, port) {
        // Pin AWS certificate (CloudFront/API Gateway)
        final pinnedCerts = [
          // SHA-256 fingerprint of AWS certificate
          'sha256/AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=',
          // Backup certificate
          'sha256/BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB=',
        ];

        final certSha256 = sha256.convert(cert.der).toString();
        return pinnedCerts.contains('sha256/$certSha256');
      };

      return client;
    };
  }
}
```

**Certificate Extraction** (for pinning):
```bash
# Get AWS API Gateway certificate
openssl s_client -connect api.weredaconnect.com:443 -showcerts \
  | openssl x509 -pubkey -noout \
  | openssl pkey -pubin -outform der \
  | openssl dgst -sha256 -binary \
  | base64
```

#### 3.2 API Gateway Rate Limiting (Infrastructure)
**Files to modify**:
- `infra/lib/stacks/api-stack.ts`

**Implementation**:
```typescript
// API Gateway with rate limiting
const api = new apigateway.RestApi(this, 'WeredaConnectApi', {
  restApiName: 'wereda-connect-api',

  // Default throttling (per account)
  deployOptions: {
    throttlingRateLimit: 100,      // 100 requests/second
    throttlingBurstLimit: 200,     // 200 concurrent requests
  },
});

// Per-method rate limiting (stricter for auth endpoints)
const authResource = api.root.addResource('auth');
const loginMethod = authResource.addResource('login').addMethod('POST', loginIntegration, {
  throttling: {
    rateLimit: 10,      // 10 requests/second (prevent brute force)
    burstLimit: 20,
  },
});

// WAF rules for additional protection
const webAcl = new wafv2.CfnWebACL(this, 'ApiWaf', {
  scope: 'REGIONAL',
  defaultAction: { allow: {} },
  rules: [
    {
      name: 'RateLimitRule',
      priority: 1,
      statement: {
        rateBasedStatement: {
          limit: 2000,  // 2000 requests per 5 minutes per IP
          aggregateKeyType: 'IP',
        },
      },
      action: { block: {} },
      visibilityConfig: {
        sampledRequestsEnabled: true,
        cloudWatchMetricsEnabled: true,
        metricName: 'RateLimitRule',
      },
    },
  ],
  visibilityConfig: {
    sampledRequestsEnabled: true,
    cloudWatchMetricsEnabled: true,
    metricName: 'WeredaConnectWaf',
  },
});
```

#### 3.3 Android Release Signing (Mobile)
**Files to modify**:
- `mobile/android/app/build.gradle`
- `mobile/android/key.properties` (NEW, gitignored)

**Implementation**:
```gradle
// android/app/build.gradle

def keystoreProperties = new Properties()
def keystorePropertiesFile = rootProject.file('key.properties')
if (keystorePropertiesFile.exists()) {
    keystoreProperties.load(new FileInputStream(keystorePropertiesFile))
}

android {
    // ... existing config

    signingConfigs {
        release {
            keyAlias keystoreProperties['keyAlias']
            keyPassword keystoreProperties['keyPassword']
            storeFile keystoreProperties['storeFile'] ? file(keystoreProperties['storeFile']) : null
            storePassword keystoreProperties['storePassword']
        }
    }

    buildTypes {
        release {
            signingConfig signingConfigs.release
            minifyEnabled true
            shrinkResources true
            proguardFiles getDefaultProguardFile('proguard-android-optimize.txt'), 'proguard-rules.pro'
        }
    }
}
```

**Key Generation**:
```bash
# Generate release keystore
keytool -genkey -v -keystore wereda-connect-release.jks \
  -keyalg RSA -keysize 2048 -validity 10000 \
  -alias wereda-connect

# Store in key.properties (gitignored)
echo "storePassword=<password>" > android/key.properties
echo "keyPassword=<password>" >> android/key.properties
echo "keyAlias=wereda-connect" >> android/key.properties
echo "storeFile=../wereda-connect-release.jks" >> android/key.properties
```

---

## 5. Ethiopian Context Adaptations

### 5.1 SIM Swap Protection

**Problem**: SIM swap attacks are common in Ethiopia (attacker gets new SIM with victim's number)

**Solution**: Multi-factor device binding

```dart
class DeviceBindingService {
  Future<String> getDeviceFingerprint() async {
    final deviceInfo = await DeviceInfoPlugin().androidInfo;

    // Generate device fingerprint (non-spoofable)
    final fingerprint = sha256.convert(utf8.encode([
      deviceInfo.id,              // Android ID
      deviceInfo.androidId,       // Unique device ID
      deviceInfo.model,           // Device model
      deviceInfo.manufacturer,    // Manufacturer
    ].join('|'))).toString();

    return fingerprint;
  }

  Future<void> bindDeviceToAccount(String userId) async {
    final fingerprint = await getDeviceFingerprint();

    // Store in DynamoDB
    await _api.post('/auth/bind-device', {
      'userId': userId,
      'deviceFingerprint': fingerprint,
      'timestamp': DateTime.now().toIso8601String(),
    });
  }

  Future<bool> verifyDeviceBinding(String userId) async {
    final fingerprint = await getDeviceFingerprint();

    // Check against server
    final response = await _api.post('/auth/verify-device', {
      'userId': userId,
      'deviceFingerprint': fingerprint,
    });

    return response['isValid'] == true;
  }
}
```

**Server-side validation**:
```csharp
public async Task<bool> VerifyDeviceBinding(string userId, string deviceFingerprint)
{
    // Get registered devices from DynamoDB
    var devices = await _deviceRepository.GetDevicesForUser(userId);

    // Check if device is registered
    if (!devices.Any(d => d.Fingerprint == deviceFingerprint))
    {
        // New device detected - require additional verification
        await _notificationService.SendSmsAlert(userId,
            "New device login detected. If this wasn't you, contact support.");

        return false;  // Require re-authentication with MFA
    }

    return true;
}
```

### 5.2 Shared Device Support

**Problem**: Multiple farmers may share one device (family/cooperative)

**Solution**: Fast user switching with biometric auth

```dart
class UserSwitchingService {
  Future<void> switchUser() async {
    // Show user selection screen
    final users = await _offlineCache.getCachedUsers();

    // Display list of users (with avatars/names)
    final selectedUser = await showUserSelectionDialog(users);

    // Authenticate with biometrics (if available)
    final canUseBiometrics = await _localAuth.canCheckBiometrics;

    if (canUseBiometrics) {
      final authenticated = await _localAuth.authenticate(
        localizedReason: 'Verify your identity',
        options: const AuthenticationOptions(
          biometricOnly: true,
          stickyAuth: true,
        ),
      );

      if (!authenticated) {
        // Fall back to password
        await _showPasswordDialog(selectedUser);
      }
    } else {
      // Password authentication
      await _showPasswordDialog(selectedUser);
    }
  }

  Future<void> logout() async {
    // Clear current session (but keep cached users)
    await _secureStorage.delete(key: 'current_user_id');
    await _secureStorage.delete(key: 'access_token');

    // Don't clear offline cache (allow fast switching)
    // await _offlineCache.clear();  // ❌ Don't do this
  }
}
```

**UX Design** (Low-Literacy):
- Large profile pictures (not just names)
- Voice prompts in Amharic/Oromo
- Color-coded user profiles
- Icon-based navigation

### 5.3 Low-Literacy Authentication UX

**Problem**: Many users cannot read complex password requirements

**Solution**: Visual password strength + voice guidance

```dart
class PasswordInputWidget extends StatelessWidget {
  @override
  Widget build(BuildContext context) {
    return Column(
      children: [
        // Password field with visual strength indicator
        TextField(
          obscureText: true,
          decoration: InputDecoration(
            labelText: l10n.password,
            suffixIcon: IconButton(
              icon: Icon(_showPassword ? Icons.visibility_off : Icons.visibility),
              onPressed: () => setState(() => _showPassword = !_showPassword),
            ),
          ),
          onChanged: (value) => _updatePasswordStrength(value),
        ),

        // Visual strength indicator (colors, not text)
        LinearProgressIndicator(
          value: _passwordStrength,
          backgroundColor: Colors.grey[300],
          color: _getStrengthColor(_passwordStrength),
        ),

        // Icon-based requirements (✓/✗)
        Row(
          children: [
            _RequirementIcon(
              met: _hasMinLength,
              icon: Icons.text_fields,
              label: '8+ ${l10n.characters}',
            ),
            _RequirementIcon(
              met: _hasUppercase,
              icon: Icons.arrow_upward,
              label: 'A-Z',
            ),
            _RequirementIcon(
              met: _hasLowercase,
              icon: Icons.arrow_downward,
              label: 'a-z',
            ),
            _RequirementIcon(
              met: _hasDigit,
              icon: Icons.numbers,
              label: '0-9',
            ),
          ],
        ),

        // Voice guidance button
        IconButton(
          icon: Icon(Icons.volume_up),
          onPressed: () => _speakPasswordRequirements(),
        ),
      ],
    );
  }

  Future<void> _speakPasswordRequirements() async {
    final tts = FlutterTts();
    await tts.setLanguage(_getLanguageCode());  // am/om/en

    final message = l10n.passwordRequirementsVoice;
    await tts.speak(message);
  }
}
```

---

## 6. Testing Strategy

### 6.1 Unit Tests

**Mobile (Dart)**:
```dart
// test/features/auth/auth_service_test.dart
void main() {
  group('AuthService', () {
    test('online login with valid credentials succeeds', () async {
      // Arrange
      final authService = AuthService(
        cognitoClient: mockCognitoClient,
        offlineCache: mockOfflineCache,
        connectivity: mockConnectivity,
      );

      when(mockConnectivity.isOnline()).thenAnswer((_) async => true);
      when(mockCognitoClient.initiateAuth(any, any))
          .thenAnswer((_) async => mockTokens);

      // Act
      final result = await authService.login('+251912345678', 'password123');

      // Assert
      expect(result.isSuccess, true);
      expect(result.tokens, mockTokens);
      verify(mockOfflineCache.cacheCredentials(any)).called(1);
    });

    test('offline login with cached credentials succeeds', () async {
      // Arrange
      when(mockConnectivity.isOnline()).thenAnswer((_) async => false);
      when(mockOfflineCache.getPasswordHash(any))
          .thenAnswer((_) async => 'hashed_password');
      when(mockOfflineCache.getTokens(any))
          .thenAnswer((_) async => mockTokens);

      // Act
      final result = await authService.login('+251912345678', 'password123');

      // Assert
      expect(result.isSuccess, true);
      expect(result.isOffline, true);
    });

    test('offline login with invalid credentials fails', () async {
      // Arrange
      when(mockConnectivity.isOnline()).thenAnswer((_) async => false);
      when(mockOfflineCache.getPasswordHash(any))
          .thenAnswer((_) async => 'different_hash');

      // Act
      final result = await authService.login('+251912345678', 'wrong_password');

      // Assert
      expect(result.isSuccess, false);
      expect(result.error, contains('Invalid credentials'));
    });
  });
}
```

**Server (C#)**:
```csharp
// tests/Auth/CognitoJwtValidatorTests.cs
public class CognitoJwtValidatorTests
{
    [Fact]
    public async Task ValidateToken_WithValidToken_ReturnsClaimsPrincipal()
    {
        // Arrange
        var validator = new CognitoJwtValidator(userPoolId, region, httpClient);
        var validToken = GenerateValidJwt();

        // Act
        var principal = await validator.ValidateTokenAsync(validToken);

        // Assert
        principal.Should().NotBeNull();
        principal.FindFirst("sub")?.Value.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ValidateToken_WithExpiredToken_ReturnsNull()
    {
        // Arrange
        var validator = new CognitoJwtValidator(userPoolId, region, httpClient);
        var expiredToken = GenerateExpiredJwt();

        // Act
        var principal = await validator.ValidateTokenAsync(expiredToken);

        // Assert
        principal.Should().BeNull();
    }

    [Fact]
    public async Task ValidateToken_WithInvalidSignature_ReturnsNull()
    {
        // Arrange
        var validator = new CognitoJwtValidator(userPoolId, region, httpClient);
        var tamperedToken = GenerateTamperedJwt();

        // Act
        var principal = await validator.ValidateTokenAsync(tamperedToken);

        // Assert
        principal.Should().BeNull();
    }
}
```

### 6.2 Integration Tests

**Mobile (Flutter Integration Tests)**:
```dart
// integration_test/auth_flow_test.dart
void main() {
  testWidgets('complete auth flow: register → login → logout', (tester) async {
    // 1. Launch app
    await tester.pumpWidget(WeredaConnectApp());

    // 2. Navigate to registration
    await tester.tap(find.text('Sign Up'));
    await tester.pumpAndSettle();

    // 3. Fill registration form
    await tester.enterText(find.byKey(Key('phone_input')), '+251912345678');
    await tester.enterText(find.byKey(Key('password_input')), 'Test1234');
    await tester.tap(find.text('Register'));
    await tester.pumpAndSettle();

    // 4. Verify OTP screen appears
    expect(find.text('Enter OTP'), findsOneWidget);

    // 5. Enter OTP (mock)
    await tester.enterText(find.byKey(Key('otp_input')), '123456');
    await tester.tap(find.text('Verify'));
    await tester.pumpAndSettle();

    // 6. Verify home screen
    expect(find.text('Welcome'), findsOneWidget);

    // 7. Logout
    await tester.tap(find.byIcon(Icons.logout));
    await tester.pumpAndSettle();

    // 8. Verify login screen
    expect(find.text('Login'), findsOneWidget);
  });
}
```

**Server (LocalStack Integration Tests)**:
```bash
# Start LocalStack with Cognito
docker-compose -f docker-compose.localstack.yml up -d

# Run integration tests
dotnet test --filter Category=Integration
```

### 6.3 Security Tests

**Penetration Testing Checklist**:
- [ ] SQL injection attempts (input validation)
- [ ] JWT token tampering
- [ ] Brute force login attempts (rate limiting)
- [ ] Session fixation attacks
- [ ] CSRF attacks
- [ ] XSS attacks (API responses)
- [ ] Certificate pinning bypass attempts
- [ ] Encrypted database extraction

**Tools**:
- OWASP ZAP (API security scanning)
- Burp Suite (mobile app traffic analysis)
- sqlmap (SQL injection testing)
- JWTool (JWT manipulation)

---

## 7. Rollback Plan

### If Authentication Breaks

**Scenario**: Cognito integration fails in production

**Rollback Steps**:
1. Revert to previous Lambda version (API Gateway stage rollback)
2. Disable Cognito authorizer temporarily
3. Enable temporary "maintenance mode" with cached credentials only
4. Fix issue in dev environment
5. Re-deploy with hotfix

**Data Safety**:
- User credentials stored in Cognito (managed service, no data loss)
- Offline cache persists on device (users can still login offline)
- DynamoDB user data unaffected

### If Database Encryption Breaks

**Scenario**: Encryption key lost or corrupted

**Prevention**:
- Keys stored in Keychain/Keystore (OS-managed, backed up)
- Users can re-authenticate to regenerate keys
- Server-side data remains accessible (not encrypted with device key)

**Recovery**:
1. User re-authenticates online
2. New encryption key generated
3. Local database re-encrypted with new key
4. Sync from server to restore data

---

## 8. Estimated Effort

### Time Breakdown (Solo Developer)

| Phase | Tasks | Estimated Time |
|-------|-------|----------------|
| **Phase 1: Foundation** | Server JWT validation, DB encryption, input validation | 5-7 days |
| **Phase 2: Auth Flow** | Cognito setup, mobile auth service, Lambda authorizer | 7-10 days |
| **Phase 3: Security Hardening** | Certificate pinning, rate limiting, release signing | 3-5 days |
| **Testing** | Unit tests, integration tests, security tests | 5-7 days |
| **Documentation** | API docs, user guides, deployment guides | 2-3 days |
| **Buffer** | Bug fixes, edge cases, Ethiopian context testing | 3-5 days |
| **TOTAL** | | **25-37 days** (~5-7 weeks) |

### Complexity Assessment

**Overall Complexity**: **HIGH**

**Reasons**:
- Multiple technologies (Flutter, .NET AOT, AWS Cognito, CDK)
- Offline-first adds complexity (token caching, conflict resolution)
- Ethiopian context requires custom UX (low-literacy, shared devices)
- Security critical (12 vulnerabilities to fix)
- AOT constraints limit library choices

**Risk Factors**:
- Cognito SMS delivery in Ethiopia (Ethio Telecom integration)
- Certificate pinning may break with AWS cert rotation
- Offline auth cache may have edge cases
- Low-literacy UX requires user testing

---

## 9. Success Criteria

### Functional Requirements
- [ ] Users can register with phone number + SMS OTP
- [ ] Users can login online with Cognito
- [ ] Users can login offline with cached credentials
- [ ] Tokens refresh automatically in background
- [ ] JWT validation works on server (AOT-compatible)
- [ ] Input validation prevents injection attacks
- [ ] Isar database encrypted at rest
- [ ] HTTPS certificate pinning prevents MITM
- [ ] API Gateway rate limiting prevents brute force
- [ ] Android release signing configured

### Non-Functional Requirements
- [ ] Cold start <2 seconds (Lambda with AOT)
- [ ] Login <3 seconds on 3G (online)
- [ ] Login <1 second (offline)
- [ ] Database encryption overhead <10%
- [ ] APK size increase <5MB (auth libraries)
- [ ] Zero data loss during offline/online transitions

### Security Requirements
- [ ] All 12 critical vulnerabilities resolved
- [ ] OWASP Mobile Top 10 compliance
- [ ] OWASP API Top 10 compliance
- [ ] Penetration test passed
- [ ] Security audit approved

### Ethiopian Context Requirements
- [ ] SIM swap protection implemented
- [ ] Shared device support tested
- [ ] Low-literacy UX validated with users
- [ ] Amharic/Oromo voice guidance works
- [ ] Works on 2GB RAM devices
- [ ] Works on 3G connectivity

---

## 10. Next Steps

### Immediate Actions (This Week)
1. **Review this plan** with stakeholders
2. **Provision AWS Cognito** user pool (dev environment)
3. **Set up development environment** (LocalStack for testing)
4. **Create GitHub issues** for each phase
5. **Update TASK.md** with implementation tasks

### Phase 1 Kickoff (Next Week)
1. Implement server JWT validator (AOT-compatible)
2. Enable Isar database encryption
3. Add input validation to all endpoints
4. Write unit tests for auth logic
5. Deploy to dev environment for testing

### Ongoing
- Weekly security reviews
- User testing with Ethiopian farmers (low-literacy UX)
- Performance monitoring (cold starts, login times)
- Compliance audits (OWASP, data privacy)

---

## 11. References

### Documentation
- [AWS Cognito Developer Guide](https://docs.aws.amazon.com/cognito/latest/developerguide/)
- [.NET Native AOT Deployment](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/)
- [Flutter Secure Storage](https://pub.dev/packages/flutter_secure_storage)
- [Isar Database Encryption](https://isar.dev/encryption.html)
- [OWASP Mobile Top 10](https://owasp.org/www-project-mobile-top-10/)

### Related Specs
- `qa/security_reviews/SECURITY_AUDIT_2026-02-10.md` - Security audit findings
- `qa/threat_models/THREAT_MODEL_v1.0.md` - Threat analysis
- `server/AOT_COMPLIANCE.md` - .NET AOT constraints
- `docs/architecture/system-architecture.md` - System architecture
- `docs/local_reality/README.md` - Ethiopian context

---

**Plan Version**: 1.0.0
**Last Updated**: 2026-02-10
**Next Review**: After Phase 1 completion
**Owner**: Architect Agent


