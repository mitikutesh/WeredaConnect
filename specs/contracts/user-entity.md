# User Entity Contract

## Version
1.1.0

## Parties
- **Mobile** (client) -- stores user profile in Isar for offline access, displays user info
- **Backend** (server) -- stores authoritative user record in DynamoDB, validates user data
- **Infrastructure** (CDK) -- provisions DynamoDB users table with GSIs

---

## Overview

The User entity represents a registered user of WeredaConnect. It is shared between the mobile client (Isar local database) and the server (DynamoDB). This contract defines the canonical field definitions, constraints, storage schemas, and synchronization rules.

---

## Entity Fields

| Field | Type | Required | Mutable | Transmitted | Description |
|-------|------|----------|---------|-------------|-------------|
| `userId` | string (UUID v4) | Yes | No | Yes | Primary key. Generated server-side during registration. |
| `phoneNumber` | string | Yes | No | Yes | Ethiopian phone number in E.164 format: `+251XXXXXXXXX` |
| `passwordHash` | string | Yes | Yes | **Never** | Server-side only. Cognito-managed. Never leaves the server. |
| `displayName` | string | No | Yes | Yes | User-visible name. Supports Amharic/Oromo Unicode. Max 100 chars. |
| `role` | enum | Yes | No | Yes | One of: `farmer`, `trucker`, `buyer`. Set at registration, immutable. |
| `woreda` | string | Yes | Yes | Yes | Administrative district (e.g., "Adama", "Jimma"). Max 100 chars. |
| `faydaId` | string | No | Yes | Yes | Ethiopian national Fayda ID. Optional at registration, required for financial transactions (escrow). |
| `preferredLanguage` | string | No | Yes | Yes | Enum: `en`, `am`, `om`. Default: `am`. |
| `isPhoneVerified` | boolean | Yes | Yes | Yes | Whether the user completed SMS OTP verification. |
| `profilePhotoRef` | string | No | Yes | Yes | S3 key for profile photo. Null if no photo uploaded. |
| `buyerType` | string | No | Yes | Yes | **Buyer only** (F-32). Enum: `private`, `company`. Null for farmers/truckers. |
| `companyName` | string | No | Yes | Yes | **Buyer only** (F-32). Required when `buyerType` is `company`. Max 100 chars. |
| `drivingLicensePath` | string | No | Yes | **Never** | **Trucker only** (F-32). Local device file path, stored in Isar only — not transmitted to the server until an image-upload pipeline exists. |
| `createdAt` | string (ISO 8601) | Yes | No | Yes | UTC timestamp of account creation. |
| `updatedAt` | string (ISO 8601) | Yes | Yes | Yes | UTC timestamp of last profile update. |
| `version` | integer | Yes | Yes | Yes | Monotonically increasing version number for sync conflict detection. Starts at 1. |

### Field Constraints

#### phoneNumber
- Format: `+251` followed by exactly 9 digits
- Regex: `^\+251[0-9]{9}$`
- Total length: 13 characters
- Unique across all users (enforced by DynamoDB GSI and Isar unique index)
- Immutable after registration (phone number change requires account migration, not supported in v1)

#### role
- Enum values: `farmer`, `trucker`, `buyer`
- Case-sensitive, lowercase
- Immutable after registration
- Stored as string in DynamoDB, as enum in Isar

#### woreda
- Non-empty string
- Maximum 100 characters
- Supports Unicode (Amharic/Oromo district names)
- Mutable (user can update if they relocate)

#### faydaId
- Ethiopian national ID format (validation rules TBD pending Fayda API integration)
- Optional at registration
- Required before initiating escrow transactions (enforced at transaction time, not at profile level)
- Once set, can be updated but not removed

#### version
- Integer, starts at 1 on creation
- Incremented on every server-side update
- Used for optimistic concurrency control in sync
- Client sends its `version` with updates; server rejects if `clientVersion < serverVersion`

---

## JSON Representation

### Full User (returned from auth endpoints and profile endpoint)

```json
{
  "userId": "550e8400-e29b-41d4-a716-446655440000",
  "phoneNumber": "+251912345678",
  "displayName": "Abebe Kebede",
  "role": "farmer",
  "woreda": "Adama",
  "faydaId": null,
  "preferredLanguage": "am",
  "isPhoneVerified": true,
  "profilePhotoRef": null,
  "createdAt": "2026-02-12T10:00:00Z",
  "updatedAt": "2026-02-12T10:00:00Z",
  "version": 1
}
```

**Note**: `passwordHash` is never included in any JSON representation. It exists only within Cognito and is never transmitted over the network or stored in DynamoDB directly.

### Minimal User (returned in sync responses, listings, etc.)

```json
{
  "userId": "550e8400-e29b-41d4-a716-446655440000",
  "displayName": "Abebe Kebede",
  "role": "farmer",
  "woreda": "Adama",
  "profilePhotoRef": null
}
```

Used when embedding user info in other entities (e.g., who created a crop listing). Minimizes payload size for 3G optimization.

---

## DynamoDB Schema

### Table Definition

| Property | Value |
|----------|-------|
| Table name | `wereda-connect-{env}-users` |
| Billing mode | On-demand (PAY_PER_REQUEST) |
| Encryption | AWS-managed (SSE with S3-managed key; upgrade to KMS in Phase R5) |
| Point-in-time recovery | Enabled |
| TTL | Not applicable (users are not auto-deleted) |

### Primary Key

| Key | Attribute | Type |
|-----|-----------|------|
| Partition Key (PK) | `userId` | String (UUID v4) |
| Sort Key (SK) | None | N/A (single-table per entity in v1) |

### Global Secondary Indexes

#### PhoneIndex

| Property | Value |
|----------|-------|
| Index name | `PhoneIndex` |
| Partition Key | `phoneNumber` (String) |
| Sort Key | None |
| Projection | `ALL` (project all attributes) |
| Purpose | Login lookup by phone number |

**Query pattern**: `GET user WHERE phoneNumber = "+251912345678"`

This GSI enables O(1) lookup by phone number during login. Since phone numbers are unique, this query always returns 0 or 1 result.

#### RoleWaredaIndex

| Property | Value |
|----------|-------|
| Index name | `RoleWaredaIndex` |
| Partition Key | `role` (String) |
| Sort Key | `woreda` (String) |
| Projection | `KEYS_ONLY` (userId, role, woreda) |
| Purpose | Query users by role and woreda for matching (e.g., find truckers in a woreda) |

**Query pattern**: `GET users WHERE role = "trucker" AND woreda = "Adama"`

### DynamoDB Item Structure

```json
{
  "userId": { "S": "550e8400-e29b-41d4-a716-446655440000" },
  "phoneNumber": { "S": "+251912345678" },
  "displayName": { "S": "Abebe Kebede" },
  "role": { "S": "farmer" },
  "woreda": { "S": "Adama" },
  "faydaId": { "NULL": true },
  "preferredLanguage": { "S": "am" },
  "isPhoneVerified": { "BOOL": true },
  "profilePhotoRef": { "NULL": true },
  "createdAt": { "S": "2026-02-12T10:00:00Z" },
  "updatedAt": { "S": "2026-02-12T10:00:00Z" },
  "version": { "N": "1" }
}
```

### DynamoDB Access Patterns

| Access Pattern | Key Condition | Index |
|---------------|---------------|-------|
| Get user by ID | `PK = userId` | Table (primary) |
| Get user by phone | `PK = phoneNumber` | PhoneIndex |
| List truckers in woreda | `PK = "trucker" AND SK = woreda` | RoleWaredaIndex |
| List farmers in woreda | `PK = "farmer" AND SK = woreda` | RoleWaredaIndex |
| List buyers in woreda | `PK = "buyer" AND SK = woreda` | RoleWaredaIndex |

### Conditional Writes (Optimistic Concurrency)

All updates to the users table must use DynamoDB conditional expressions to enforce version-based concurrency:

```
UpdateExpression: SET #v = :newVersion, #updatedAt = :now, ...
ConditionExpression: #v = :expectedVersion
```

If the condition fails, the update is rejected with `ConditionalCheckFailedException`, which the server translates to a sync conflict response.

---

## Isar Schema (Mobile)

### Schema Definition

```dart
@collection
class User {
  Id isarId = Isar.autoIncrement;

  @Index(unique: true)
  late String userId;

  @Index(unique: true)
  late String phoneNumber;

  String? displayName;

  @Enumerated(EnumType.name)
  late UserRole role;

  late String woreda;

  String? faydaId;

  @Enumerated(EnumType.name)
  late PreferredLanguage preferredLanguage;

  late bool isPhoneVerified;

  String? profilePhotoRef;

  late DateTime createdAt;

  late DateTime updatedAt;

  late int version;
}

enum UserRole {
  farmer,
  trucker,
  buyer,
}

enum PreferredLanguage {
  en,
  am,
  om,
}
```

### Isar Indexes

| Index | Fields | Unique | Purpose |
|-------|--------|--------|---------|
| `userId` | `userId` | Yes | Primary lookup by server-assigned ID |
| `phoneNumber` | `phoneNumber` | Yes | Login lookup and duplicate prevention |

### Isar Storage Notes

- `isarId` is the Isar-internal auto-increment ID. It is not used outside of Isar.
- `userId` is the server-assigned UUID v4. This is the cross-system identifier.
- `role` is stored as an Isar enum (mapped to string name, not ordinal, for forward compatibility).
- `preferredLanguage` is stored as an Isar enum.
- `DateTime` fields are stored as Isar `DateTime` (UTC, microsecond precision).
- `version` is an integer used for sync conflict detection, mirroring the DynamoDB version field.
- The `passwordHash` field does NOT exist in Isar. Cached password hashes are stored separately in `flutter_secure_storage`.

### Isar Encryption

The Isar database containing User records must be opened with encryption enabled:

```dart
final isar = await Isar.open(
  [UserSchema, ...],
  directory: dir.path,
  encryptionKey: await getEncryptionKey(),
);
```

The encryption key is device-bound and stored in Android Keystore via `flutter_secure_storage`. See `auth-api.md` for the offline auth contract details.

---

## Sync Behavior

### User Profile Sync

User profiles sync using the standard sync protocol defined in `sync-protocol.md`:

| Scenario | Behavior |
|----------|----------|
| Registration (online) | Server creates user in DynamoDB and Cognito. Mobile caches in Isar. |
| Profile update (offline) | Mobile updates Isar, enqueues sync operation. |
| Profile update (sync) | Sync engine sends update with `clientVersion`. Server applies if version matches. |
| Profile conflict | Server version wins. Mobile accepts server data and updates Isar. |
| Deletion | Self-service via `DELETE /user/account` (see `account-deletion.md`). Online only, never through `/sync`. Refused while the user has an active order, unreleased escrow, open delivery or open dispute. The row becomes a tombstone with no personal data and is never synced back to a client. |

### Sync Operation for User Updates

```json
{
  "operationId": "uuid-v4",
  "idempotencyKey": "uuid-v4",
  "entityType": "profile",
  "entityId": "550e8400-e29b-41d4-a716-446655440000",
  "action": "update",
  "clientVersion": 1,
  "data": {
    "displayName": "Abebe Kebede Geleta",
    "woreda": "Bishoftu"
  },
  "timestamp": "2026-02-12T14:30:00Z"
}
```

**Note**: Only mutable fields can be included in the `data` payload. Attempts to change `userId`, `phoneNumber`, or `role` via sync are rejected with `SYNC_VALIDATION`.

### Deletion fields (server only, 1.1.0)

| Field | Type | Meaning |
|-------|------|---------|
| `deletionState` | String | `in_progress`, `cognito_pending` or `completed`. Absent on a live account. Set by `DELETE /user/account`; every write endpoint returns `410 ACCOUNT_004` while it is present. |
| `deletionRequestedAt` | String (ISO-8601) | When the deletion started. The completion sweep uses it to bound retention to 24 hours. |
| `deletedAt` | String (ISO-8601) | When personal data was erased. Present only on a tombstone. |

A tombstone keeps only `userId`, `role`, these three fields, `createdAt`, `updatedAt` and `version`. `phoneNumber`, `faydaId` and `woreda` are removed, so the row drops out of `PhoneIndex`, `FaydaIdIndex` and `RoleWaredaIndex`. These fields are never accepted from a client and never appear in `UserDto`. Full rules: `account-deletion.md`.

---

## Server Model Notes (.NET)

### DTO (Data Transfer Object)

```csharp
public sealed class UserDto
{
    public required string UserId { get; init; }
    public required string PhoneNumber { get; init; }
    public string? DisplayName { get; init; }
    public required string Role { get; init; }
    public required string Woreda { get; init; }
    public string? FaydaId { get; init; }
    public string PreferredLanguage { get; init; } = "am";
    public required bool IsPhoneVerified { get; init; }
    public string? ProfilePhotoRef { get; init; }
    public required string CreatedAt { get; init; }
    public required string UpdatedAt { get; init; }
    public required int Version { get; init; }
}
```

### JSON Serialization (AOT-Compatible)

```csharp
[JsonSerializable(typeof(UserDto))]
[JsonSerializable(typeof(List<UserDto>))]
[JsonSerializable(typeof(MinimalUserDto))]
public partial class UserJsonContext : JsonSerializerContext { }
```

### Validation Rules (Server-Side)

| Field | Validation |
|-------|-----------|
| `phoneNumber` | Regex: `^\+251[0-9]{9}$`, unique (check PhoneIndex before insert) |
| `displayName` | Max 100 chars, sanitize HTML/script tags |
| `role` | Must be one of: `farmer`, `trucker`, `buyer` |
| `woreda` | Non-empty, max 100 chars |
| `faydaId` | Format validation TBD (Fayda API spec pending) |
| `preferredLanguage` | Must be one of: `en`, `am`, `om` |

---

## Invariants

These rules must always hold:

1. **Phone number is globally unique.** No two users can have the same phone number. Enforced by DynamoDB conditional write and Isar unique index.
2. **userId is immutable.** Once assigned, a user's ID never changes.
3. **role is immutable.** A user's role is set at registration and cannot be changed. (Role change requires new account in v1.)
4. **phoneNumber is immutable.** Phone number changes are not supported in v1.
5. **version only increases.** The version number is monotonically increasing. It is never decremented or reset.
6. **passwordHash never leaves the server.** It is managed by Cognito and is never included in any API response, sync payload, or Isar record.
7. **Server is authoritative.** On sync conflict, the server's version of the user profile wins.
8. **Offline profile edits are queued.** Profile changes made offline are stored in the sync queue and applied when connectivity returns.
9. **faydaId is optional but gated.** Users can register and use basic features without a Fayda ID, but escrow/payment features require it.
10. **Timestamps are UTC.** All `createdAt` and `updatedAt` values are ISO 8601 UTC strings. Local time conversion happens on the mobile client only.

---

## Data Transfer Optimization

Given Ethiopian mobile data costs:

1. **Minimal user in embedded contexts.** When a user is referenced in another entity (e.g., crop listing creator), use `MinimalUserDto` (userId, displayName, role, woreda, profilePhotoRef) instead of the full user.
2. **Delta sync for profile updates.** Only send changed fields in sync operations, not the full user object.
3. **Profile photo optimization.** Photos are compressed to WebP, max 200x200px, max 50KB. The `profilePhotoRef` is an S3 key, not a base64-encoded image.

---

## Changelog

| Version | Date | Change |
|---------|------|--------|
| 1.0.0 | 2026-02-12 | Initial entity definition: fields, DynamoDB schema, Isar schema, sync rules |
| 1.1.0 | 2026-10-06 | Deletion is now supported through `DELETE /user/account` (`account-deletion.md`); server-only `deletionState`, `deletionRequestedAt`, `deletedAt` and the tombstone shape documented (ACC-01) |

---

**Last Updated**: 2026-10-06
**Version**: 1.1.0
