# User Entity Contract

## Version
2.3.0

## Parties
- **Mobile** (client): caches the signed-in user in Isar (`core/database/schemas/user.dart`) for offline sign-in and display
- **Web** (client): keeps the same fields in memory (`core/models/web/user.dart`)
- **Backend** (server): owns the authoritative row in the DynamoDB users table (`server/src/Data/UserRepository.cs`, `Models/UserDto.cs`)
- **Admin**: reads users and applies the admin user actions; since 2.3.0 shows a user's profile edit history
- **Infrastructure** (CDK): provisions the users table and its three indexes (`infra/lib/stacks/database-stack.ts`)

---

## Overview

The users row is one DynamoDB item per account, keyed by `userId`. Version 2.0.0 rewrites this
contract from the code, attribute by attribute: who writes each attribute, whether the `user`
object returns it, how versions move, and what the device keeps. Up to 1.3.2 the document still
described the February 2026 design (a `passwordHash` field, a `profile` sync entity, an Isar
schema with `preferredLanguage` and `isPhoneVerified`, a `UserJsonContext`); none of that was
built. 2.0.0 also adds the reputation attributes of EPIC ESC P2, marked PLANNED.

### Reading this contract

| Part | State |
|---|---|
| Sections 1, 3 to 11 | as built on `main` (2026-11-07) |
| Section 14 (profile read and edit) | BUILT (2.2.0, SIM-56); the edit's audit row and the admin history BUILT (2.3.0, FUP-317) |
| Section 2 (`reputation`, `ratingSum`, `ratingCount`, `ratingsAggregatedAt`) | BUILT (2.1.0, ESC-28): written by the reputation stream, the order-cron's daily job and backfill, the rating create and registration; ESC-29 reads them (`reputation-api.md` 1.1.0, `rating-api.md` 1.3.0) |

---

## 1. The users row (as built)

"Returned" means the attribute is in the `user` object of the register, login and verify-otp
responses (`auth-api.md`) and of the admin user endpoints (refresh and `/user/fayda-link` return
no user object). That object is `UserDto` serialized with camelCase names; a property with no
value is left out (the serializer omits nulls).

| Attribute | DynamoDB type | Returned | Written by | Meaning |
|---|---|---|---|---|
| `userId` | S | yes | registration | Primary key. The Cognito `sub` (a UUID), set at registration, never changed |
| `phoneNumber` | S | yes | registration | E.164 `+251XXXXXXXXX`, unique (`PhoneIndex`); never changed by any path (`UpdateUserAsync` rewrites the stored value) |
| `displayName` | S | yes | registration; the profile edit (section 14) | The marketplace name, copied onto new listings as `farmerName` / `truckerName`; max 100 characters, no phone number (section 11) |
| `fullName` | S | yes | registration (from the Fayda token), `/user/fayda-link` (only when empty) | The Fayda legal name; never screened (section 11) |
| `role` | S | yes | registration; an admin's `change_role` | `farmer`, `trucker` or `buyer` (section 3) |
| `woreda` | S | yes | registration; the profile edit (section 14) | The district, max 100 characters, no phone number |
| `faydaId` | S | yes | registration, `/user/fayda-link` | The Fayda national ID; unique in practice (`FaydaIdIndex` duplicate check) |
| `faydaVerified` | BOOL | yes (`false` included) | registration, `/user/fayda-link`, admin `verify` | The account's Fayda identity is verified |
| `faydaVerifiedAt` | S | yes | registration, `/user/fayda-link` | When it was first verified (admin `verify` does not stamp it) |
| `faydaAuthenticatedAt` | S | never | `/user/fayda-link` | The last Fayda sign-in; payments above 10,000 ETB need it recent (`AUTH_041`) |
| `preferredLanguage` | S | yes | registration; the `/sync` `user` entity (section 9) | `en`, `am` or `om`; a row without it reads as `am` |
| `isPhoneVerified` | BOOL | yes (`false` included) | registration, `POST /auth/verify-otp`, `/user/fayda-link` (when the Fayda record proves the phone) | SMS OTP or Fayda proved the phone |
| `suspended` | BOOL | yes (`false` included) | admin `suspend`, `unsuspend` | Blocks login, OTP and refresh (`AUTH_025`) and, since 2.1.1 (ESC-29), `/sync` and the write endpoints of `reputation-api.md` 8.4. A row without it reads as `false` |
| `suspendReason` | S | never (admin detail shows it beside the user) | admin `suspend` (removed by `unsuspend`) | `fraud_confirmed` is the Fraud Review reject (FRD-05). Since 2.0.1 a later `suspend` with another reason keeps `fraud_confirmed`; only `unsuspend` clears it (FUP-103) |
| `knownDevices` | SS | yes (`[]` when none) | registration, login on a new install | Install ids (`X-Device-Id`), at most 10; a string set, so DynamoDB keeps no order; a new one triggers a sign-in notice; FRD-15 compares them |
| `profilePhotoRef` | S | yes | nothing today | Read and written back by `UpdateUserAsync`; no upload path exists, so it is never set |
| `buyerType` | S | yes | registration | Buyer only: `private` or `company` (F-32) |
| `companyName` | S | yes | registration; the profile edit of a company buyer (section 14) | Buyer only, required for a company buyer, max 100 characters, no phone number |
| `deletionState` | S | never | `DELETE /user/account` | `in_progress`, `cognito_pending`, `completed`; absent on a live account (section 7) |
| `deletionRequestedAt` | S | never | `DELETE /user/account` | When the deletion started |
| `deletedAt` | S | never | the tombstone write | When the personal data was erased |
| `createdAt` | S | yes | registration | ISO 8601 UTC |
| `updatedAt` | S | yes | every write path (section 6) | ISO 8601 UTC of the last write |
| `version` | N | yes | registration (1), `UpdateUserAsync` (+1), the tombstone (+1) | Concurrency for the profile write path; admin actions and the reputation writes (ESC-28) do not bump it (section 6) |

Not on the row (fixed in 2.0.0): `passwordHash` (Cognito holds the password; nothing about it
is stored in DynamoDB or sent anywhere), `drivingLicensePath` and the Fayda ID scan paths (local
file paths on the phone only, section 8).

An absent optional attribute may be stored either as a missing attribute (registration omits
it) or as a DynamoDB `NULL` (`UpdateUserAsync` writes `NULL` for an empty `displayName`,
`fullName`, `profilePhotoRef`, `faydaId`, `faydaVerifiedAt`, `faydaAuthenticatedAt` and
`knownDevices`). Readers treat both the same.

**`faydaId` and `FaydaIdIndex` (answered in 2.2.0).** `faydaId` is the partition key of
`FaydaIdIndex`, and DynamoDB refuses a write that gives an index key attribute a type other than
the index's (`S` here), `NULL` included ("Invalid attribute value type", reproduced against
LocalStack with the deployed indexes in SIM-56). Up to 2.1.2 `UpdateUserAsync` set `faydaId` to
`NULL` on a row without a Fayda ID, so every such write (verify-otp, the login device write, the
language change) was refused there. Since 2.2.0 it `REMOVE`s the attribute instead (a no-op when
absent) and sets it only when the row has one.

**`woreda` and `RoleWaredaIndex` (2.2.0).** `woreda` is the sort key of `RoleWaredaIndex`, and
DynamoDB refuses an empty string there. Rows without a `woreda` exist (FUP-162: farmers and
truckers the backfill found only through a `Scan`), and `FromItem` reads the missing value as
`""`, so up to 2.1.2 every `UpdateUserAsync` on such a row was refused too. Since 2.2.0 an
empty `woreda` is `REMOVE`d (a no-op when absent) and only a non-empty one is set. `phoneNumber`
and `role` are present on every live row (a tombstone has no phone, but no profile write reaches
it).

---

## 2. Reputation attributes (BUILT: ESC-28, 2.1.0)

Server-only. Never returned in the `user` object: the party reads them through
`GET /reputation/me`, buyers through the public summary and the copied owner fields
(`reputation-api.md` 1.0.0), admins through `GET /admin/users/{userId}/reputation`.

| Attribute | Type | Written by | Meaning |
|---|---|---|---|
| `reputation` | M | `ReputationService` (the stream Lambda, the daily job, the admin reputation actions) | The aggregate below; absent until the user's first event (an absent map reads as 0 points, `good`, no reliability) |
| `ratingSum` | N | the rating create transaction (`ADD`) and its heal of an unstamped ratee, the backfill, the role-change rebuild | Sum of the stars of the ratings received in the current role (`rating-api.md` 1.2.0) |
| `ratingCount` | N | the same | Their count |
| `ratingsAggregatedAt` | S | registration (new rows start at 0 with it), the one-time backfill, the rating create's heal of an unstamped ratee (`rating-api.md` 1.5.0) | Present once `ratingSum` and `ratingCount` are exact; until then reads fall back to the ratings index |

`ratingSum` and `ratingCount` sit at the top level, not inside `reputation` (the spec put them in
the map): `ADD` creates a missing top-level number, and a rating write then never races the
map's own version (proposed, architecture doc 3.4).

The `reputation` map:

| Key | Type | Meaning |
|---|---|---|
| `activePoints` | N | sum of `points - earnedBack` over active, unexpired, unexcused penalty events |
| `tier` | S | `good`, `warning`, `low` or `restricted` at the time of the write (reads recompute it, section 6 of the architecture doc) |
| `completedOrders90d`, `faultCancels90d`, `noShows90d` | N | counts over the last 90 days (`reputation-api.md` 1.4) |
| `reliabilityPct` | N | whole percent; absent below 3 orders |
| `effectiveRating` | N | one decimal; absent with no ratings; the value last copied onto listings |
| `restriction` | M | `{since, until?, cause}` while restricted; `cause` `points`, `delivery_overdue` or `admin`; `until` absent while an admin review is open |
| `override` | M | `{tag, reason, by, until}`, `tag` `low` or `clear`, while an admin tag is in force |
| `nextChangeAt` | S | the earliest of the next point expiry, `restriction.until` and `override.until`; a read past it recomputes from the events |
| `lastEventAt` | S | `occurredAt` of the newest event counted |
| `lastEventId`, `lastEventRecordedAt` | S | 2.1.0: the newest event row written with the map and when; a writer whose events Query does not return it yet reads again (the read-lag check, `reputation-architecture.md` 6.1) |
| `version` | N | the map's own compare-and-set counter, starting at 1 |
| `updatedAt` | S | when the map was last written |

Write rules (invariants 11 to 13):
- `ReputationService` writes only the `reputation` path, in the same `TransactWriteItems` as the
  event row that changes it, conditioned on `reputation.version` (or `attribute_not_exists
  (reputation)` for the first write) and on `attribute_not_exists(deletionState)`.
- It never bumps the row's `version` and never changes `updatedAt`, so a login's device write or
  a language change (both `UpdateUserAsync`, version-checked) never conflicts with a reputation
  write.
- `UpdateUserAsync` never names these attributes, so a profile write never reverts them.
- The tombstone removes them with every other attribute off its allow-list (section 7).

---

## 3. Field constraints

### phoneNumber
- `^\+251[0-9]{9}$` (13 characters), unique: registration checks `PhoneIndex` first.
- Never changed: there is no phone change flow.

### role
- `farmer`, `trucker` or `buyer`, lowercase.
- The user cannot change it: the `/sync` `user` entity ignores every key but `preferredLanguage`,
  `displayName`, `woreda` and `companyName` (section 14).
- An admin's `POST /admin/users/{id}/action` with `change_role` and `newRole` rewrites it, only to
  one of the three marketplace roles (`ADMIN_011` otherwise, never `admin`), together with the
  Cognito `custom:role` claim, and signs the user out (`auth-api.md`). Spec H5 says a role change
  waits until the user has no open orders; that check is not built (follow-up).
- Admin accounts live in the separate admin user pool and have no users row.

### woreda, displayName, companyName
- Non-empty (`woreda`), at most 100 characters, Amharic and Afan Oromo scripts allowed.
- No Ethiopian mobile number (section 11).
- The user changes them through the profile edit (section 14), where none may be empty and
  only a company buyer has a `companyName`.

### faydaId
- Set at registration or by `/user/fayda-link`; the duplicate check reads `FaydaIdIndex`.
- Required before escrow payments above the Fayda threshold (`KycPolicy`), not at the profile
  level.

### preferredLanguage
- `en`, `am` or `om`. Registration takes the app's choice; the `/sync` `user` entity changes it.

### version
- Starts at 1. `UpdateUserAsync` writes `version = expected + 1` under `version = expected`
  (and `attribute_not_exists(deletionState)`); a mismatch is `409 AUTH_019`, or
  `USER_PROFILE_CONFLICT` on `/sync`. The tombstone bumps it too.

---

## 4. JSON representation

### The `user` object (register, login, verify-otp, admin)

```json
{
  "userId": "550e8400-e29b-41d4-a716-446655440000",
  "phoneNumber": "+251912345678",
  "displayName": "Abebe Kebede",
  "fullName": "Abebe Kebede Tola",
  "role": "farmer",
  "woreda": "Adama",
  "faydaId": "1234567890123456",
  "faydaVerified": true,
  "faydaVerifiedAt": "2026-10-20T08:00:00.000Z",
  "preferredLanguage": "am",
  "isPhoneVerified": true,
  "suspended": false,
  "knownDevices": ["3f1c2b9e-6a1d-4f7e-9c2a-0b8d7e6f5a41"],
  "createdAt": "2026-02-12T10:00:00.000Z",
  "updatedAt": "2026-10-20T08:00:00.000Z",
  "version": 4
}
```

A buyer's object adds `buyerType` and, for a company, `companyName`. `faydaAuthenticatedAt`,
`suspendReason`, the deletion attributes and the section 2 attributes never appear in it.

### Embedded user data

Other entities do not embed a user object. Listings and orders carry copied names instead
(`farmerName`, `truckerName`, `buyerName`), stamped when the row is written, and since
`reputation-api.md` 1.0.0 (PLANNED) the owner's public reputation fields. `MinimalUserDto` is
registered in `AppJsonContext` but no endpoint returns it.

---

## 5. DynamoDB schema

### Table

| Property | Value |
|---|---|
| Name | `wereda-connect-{env}-users` |
| Key | partition key `userId` (S), no sort key |
| Billing | on-demand (`PAY_PER_REQUEST`) |
| Encryption | DynamoDB default encryption at rest (AWS owned key) |
| Point-in-time recovery | on |
| Removal policy | `RETAIN` in prod, `DESTROY` elsewhere |
| Stream | none |
| TTL | none (accounts are never auto-deleted) |

### Global secondary indexes

All three project `ALL`, so every write to the row is also written to each index the row is in
(a cost note for the PLANNED reputation writes, architecture doc 15).

| Index | Partition key | Sort key | Used by |
|---|---|---|---|
| `PhoneIndex` | `phoneNumber` | none | login and registration lookup (`Limit 1`) |
| `RoleWaredaIndex` | `role` | `woreda` | the rating backfill (ESC-28, order-cron `reputation_backfill_ratings`) walks the `farmer` and `trucker` partitions; the admin list scans |
| `FaydaIdIndex` | `faydaId` | none | the Fayda duplicate check at registration and link (`Limit 1`) |

A tombstone has no `phoneNumber`, `faydaId` or `woreda`, so it leaves all three indexes.

### Access patterns

| Pattern | Operation |
|---|---|
| User by id | `GetItem` on `userId`; strongly consistent for the ACC-02 deletion guard and the `/sync` language write |
| User by phone | `Query` `PhoneIndex`, `Limit 1` |
| User by Fayda ID | `Query` `FaydaIdIndex`, `Limit 1` |
| Phones or languages of many users | `BatchGetItem` projecting `userId` and the one attribute, 100 keys per call, up to 3 rounds for unprocessed keys |
| Admin user list and dashboard counts | `Scan` with filters (admin only, paged) |
| The rating backfill (ESC-28) | `Query` `RoleWaredaIndex` for `role = farmer` and `role = trucker`; then (FUP-162) one filtered `Scan` for the farmers and truckers without `ratingsAggregatedAt` (the rows without a `woreda`), in that manual order-cron action only, resumable with `scanFrom` |

### Item example

```json
{
  "userId": { "S": "550e8400-e29b-41d4-a716-446655440000" },
  "phoneNumber": { "S": "+251912345678" },
  "displayName": { "S": "Abebe Kebede" },
  "fullName": { "S": "Abebe Kebede Tola" },
  "role": { "S": "farmer" },
  "woreda": { "S": "Adama" },
  "faydaId": { "S": "1234567890123456" },
  "faydaVerified": { "BOOL": true },
  "faydaVerifiedAt": { "S": "2026-10-20T08:00:00.000Z" },
  "faydaAuthenticatedAt": { "S": "2026-10-20T08:00:00.000Z" },
  "preferredLanguage": { "S": "am" },
  "isPhoneVerified": { "BOOL": true },
  "knownDevices": { "SS": ["3f1c2b9e-6a1d-4f7e-9c2a-0b8d7e6f5a41"] },
  "profilePhotoRef": { "NULL": true },
  "createdAt": { "S": "2026-02-12T10:00:00.000Z" },
  "updatedAt": { "S": "2026-10-20T08:00:00.000Z" },
  "version": { "N": "4" }
}
```

ESC-28 additions on the same row (built in 2.1.0):
```json
{
  "ratingSum": { "N": "58" },
  "ratingCount": { "N": "12" },
  "ratingsAggregatedAt": { "S": "2026-11-20T00:00:00.000Z" },
  "reputation": { "M": {
    "activePoints": { "N": "4" },
    "tier": { "S": "warning" },
    "completedOrders90d": { "N": "10" },
    "faultCancels90d": { "N": "1" },
    "noShows90d": { "N": "1" },
    "reliabilityPct": { "N": "83" },
    "effectiveRating": { "N": "4.4" },
    "nextChangeAt": { "S": "2027-01-20T09:00:00.000Z" },
    "lastEventAt": { "S": "2026-11-01T06:00:00.000Z" },
    "version": { "N": "7" },
    "updatedAt": { "S": "2026-11-01T06:00:05.000Z" }
  } }
}
```

---

## 6. Writes and concurrency (as built)

| Writer | Attributes | Operation | `version` |
|---|---|---|---|
| `RegisterFunction` (`POST /auth/register`) | the whole row | `PutItem`, `attribute_not_exists(userId)` (`409 AUTH_005`) | 1 |
| `RegisterFunction`, replacing a registration whose phone was never verified | the old row | `DeleteItem` | |
| `VerifyOtpFunction` | `isPhoneVerified` | `UpdateUserAsync` (one retry on a conflict) | +1 |
| `LoginFunction`, a new install | `knownDevices` | `UpdateUserAsync`; a failure is logged and the login goes on | +1 |
| `FaydaVerifyFunction` (`/user/fayda-link`) | `faydaId`, `faydaVerified`, `faydaVerifiedAt`, `faydaAuthenticatedAt`, `fullName` when empty, `isPhoneVerified` | `UpdateUserAsync` (one retry) | +1 |
| `SyncFunction`, `user` entity | `preferredLanguage`; since 2.2.0 `displayName`, `woreda` and a company buyer's `companyName` (only the fields that change; no write when nothing does) | `UpdateUserAsync`; since 2.3.0 a change of the profile text is `UpdateUserWithAuditAsync`: the same update and its transaction-audit `profile_edited` row in one `TransactWriteItems` (section 14) | +1 |
| `AdminFunction` user action | `suspend`: `suspended`, `suspendReason`; `unsuspend`: `suspended: false`, removes `suspendReason`; `verify`: `faydaVerified`; `change_role`: `role`; each with `updatedAt` | `UpdateItem`, `attribute_exists(userId)`; audited (transaction-audit `user_{action}`) | not bumped |
| Account deletion | section 7 | `UpdateItem` | +1 on the tombstone |
| `ReputationService` (ESC-28) | `reputation` | section 2 | not bumped |
| Rating create, backfill (ESC-28); the create's heal of an unstamped ratee and the backfill's users Scan (FUP-162); role-change rebuild (PLANNED, ESC-32) | `ratingSum`, `ratingCount`, `ratingsAggregatedAt` | `rating-api.md` 1.5.0 | not bumped |

`UpdateUserAsync` is a read-modify-write: it `SET`s `phoneNumber`, `displayName`, `fullName`,
`role`, `preferredLanguage`, `isPhoneVerified`, `profilePhotoRef`,
`faydaVerified`, `faydaVerifiedAt`, `faydaAuthenticatedAt`, `knownDevices`, `updatedAt` and
`version` from the copy its caller read, under `version = expected AND
attribute_not_exists(deletionState)`. Since 2.2.0 it `SET`s `faydaId`, `woreda` and
`companyName` when the copy has them, and `REMOVE`s `faydaId` and `woreda` when it has none
(section 1). It never touches
`suspended`, `suspendReason`, `buyerType` or the deletion attributes, and never clears
`companyName`.

**Known gap (found in 2.0.0).** The admin `verify` and `change_role` actions do not bump
`version`. An `UpdateUserAsync` whose copy was read just before such an action passes its
version check and writes the old `role` or `faydaVerified` back (the Cognito claim keeps the new
role). The window is the few milliseconds between a login's read and its device write. Fix
(follow-up, not in ESC-27): the admin actions bump `version`, or `UpdateUserAsync` sets only the
attributes its caller changed.

---

## 7. Deletion (as built, `account-deletion.md`)

| Attribute | Meaning |
|---|---|
| `deletionState` | `in_progress`, `cognito_pending` or `completed`. Absent on a live account. Set by `DELETE /user/account`; every write endpoint answers `410 ACCOUNT_004` while it is present (`POST /sync`, `POST /escrow/*`, `POST /user/fayda-start`, `POST /user/fayda-link`, `POST /device-tokens`; ACC-02) |
| `deletionRequestedAt` | when the deletion started; the completion sweep bounds retention to 24 hours with it |
| `deletedAt` | when the personal data was erased; present only on a tombstone |

The tombstone keeps only `userId`, `role`, the three deletion attributes, `createdAt`,
`updatedAt` and `version` (`AccountDeletionPolicy.TombstoneAllowlist`); every other attribute is
removed, including any added later (the section 2 attributes too). A deletion that
aborts removes `deletionState` and `deletionRequestedAt`. These attributes are never accepted
from a client and never returned.

---

## 8. On the device (as built)

### Isar (mobile, `core/database/schemas/user.dart`)

```dart
@collection
class User {
  Id id = Isar.autoIncrement;
  @Index(unique: true) late String userId;
  @Index(unique: true) late String phoneNumber;
  late String name;                 // legacy name, the fallback for displayName
  String? fullName;
  String? displayName;
  String get effectiveDisplayName => displayName ?? name;
  String? faydaId;
  String? faydaIdFrontImagePath;    // local file path, never sent
  String? faydaIdBackImagePath;     // local file path, never sent
  String? drivingLicensePath;       // trucker, local file path, never sent
  String? buyerType;
  String? companyName;
  bool isVerified = false;          // mirrors faydaVerified (offline_auth_cache)
  bool faydaVerified = false;
  DateTime? faydaVerifiedAt;
  @Index() @Enumerated(EnumType.name) late UserRole role;
  @Index() late String woreda;
  late DateTime createdAt;
  late DateTime updatedAt;
  late int version;
}

enum UserRole { farmer, trucker, buyer }
```

- Isar 3.1 has no built-in encryption. `IsarService` encrypts the PII fields (phone number,
  names, Fayda ID) field by field with an AES key kept in `flutter_secure_storage` (Android
  Keystore), with an integrity tag on the key.
- Not on the device row: `preferredLanguage` (kept in the app's locale settings, SIM-37),
  `isPhoneVerified`, `suspended`, `knownDevices`, `profilePhotoRef`, `faydaAuthenticatedAt`.
  The section 2 attributes are not cached here either: the app caches `GET /reputation/me`
  separately (`reputation-api.md` section 13, ESC-31).
- The cached password hash for offline sign-in lives in `flutter_secure_storage`, not in Isar
  (`auth-api.md`).

### Web (`core/models/web/user.dart`)

The same fields without Isar annotations, in memory only (Isar does not run on web). It is the
platform pair of the Isar schema: a field added to one is added to the other.

---

## 9. Sync behaviour (as built)

- The device never pushes a user row. Profile fields come from registration and the server
  paths of section 6; the row reaches the device in the register, login and verify-otp
  responses.
- The only profile change through `/sync` is the `user` entity (`sync-protocol.md` 1.28.0,
  1.57.0): `action: update`, `entityId` = the caller, `data.preferredLanguage` in `en`, `am`,
  `om` and, since 2.2.0, `displayName`, `woreda` and `companyName` (section 14); every other
  key is ignored. It is version-less for the client (the server reads the row and writes with
  its version); an unchanged value is a no-op `success`. The `profile` entity shown up to 1.3.2
  was never served.
- The device reads the row back with `GET /user/profile` (section 14) besides the sign-in
  responses.
- Deletion is `DELETE /user/account`, online only, never through `/sync` (section 7).

---

## 10. Server model notes (.NET)

- `Models/UserDto.cs`: `UserDto` (a `sealed record`) carries every section 1 attribute.
  `FaydaAuthenticatedAt`, `SuspendReason`, `DeletionState`, `DeletionRequestedAt` and `DeletedAt`
  are `[JsonIgnore]`, so they never leave the server. The section 2 attributes are not on
  `UserDto`: `ReputationEventRepository` reads them with its own projection (ESC-28).
- `Data/UserRepository.cs`: manual `ToItem` / `FromItem` marshalling on the low-level client
  (AOT: no Document Model, no reflection). `FromItem` maps a tombstone's missing phone and woreda
  to empty strings.
- Serialization: `AppJsonContext` (`server/src/AppJsonContext.cs`), camelCase, nulls omitted.
  There is no separate `UserJsonContext`.

---

## 11. Validation (server)

| Field | Rule |
|---|---|
| `phoneNumber` | `^\+251[0-9]{9}$`, unique (`PhoneIndex` before insert) |
| `displayName` | optional, max 100 characters, not the deleted-account name (`AccountDeletionPolicy.IsSentinelName`), no phone number (`Validation/RequestValidator.cs`) |
| `role` | `farmer`, `trucker` or `buyer` |
| `woreda` | non-empty, max 100 characters, no phone number |
| `companyName` | buyer only, required for a company buyer, max 100 characters, no phone number |
| `preferredLanguage` | `en`, `am` or `om` |
| `faydaId` | from the Fayda verification result only |

**No phone number in profile text (FUP-52).** `displayName` reaches buyers as the `farmerName` or
`truckerName` of every new listing, and `woreda` as the listing's `woreda`, before contact opens
at escrow. Registration refuses a value holding an Ethiopian mobile number (the listing text
detector, `crop-listing-api.md` 1.4.0 rule 6b) with `400 AUTH_027` and writes nothing
(`auth-api.md` 2.12.0). The profile edit (section 14, 2.2.0) runs the same check on the fields
it changes, with the same code and text, and treats a value equal to the stored one as an
unchanged echo. Values stored before 1.2.0 are kept. `fullName` comes only from Fayda and is
never screened.

---

## 12. Invariants

1. **`phoneNumber` is unique and never changes.**
2. **`userId` never changes.**
3. **Only an admin changes `role`**, and only to a marketplace role.
4. **`version` only increases**, and only on the profile write path and the tombstone.
5. **No password data is stored on the row** or sent in any response.
6. **The server is authoritative.** Clients never write a user row; the only client-driven
   profile changes are `preferredLanguage`, `displayName`, `woreda` and a company buyer's
   `companyName` through the `/sync` `user` entity, checked by the server.
7. **Server-only attributes never leave the server**: `faydaAuthenticatedAt`, `suspendReason`,
   the deletion attributes and the section 2 attributes.
8. **A deleting or deleted account takes no profile write** (`attribute_not_exists
   (deletionState)` on `UpdateUserAsync`).
9. **A tombstone keeps only the allow-list** of section 7.
10. **Timestamps are ISO 8601 UTC**; local time is a display concern of the device.
11. **The `reputation` map changes only together with the event that changes it**, in
    one transaction, under its own `reputation.version`.
12. **Reputation and rating aggregate writes never bump `version` or `updatedAt`**,
    and `UpdateUserAsync` never writes them.
13. **Nothing writes reputation onto a deleting account** (`attribute_not_exists
    (deletionState)` on every reputation write).
14. **Every accepted change of `displayName`, `woreda` or `companyName` has exactly one
    transaction-audit `profile_edited` row**, written in the same transaction (2.3.0); a refused,
    unchanged or language-only edit has none.

---

## 13. Data transfer

1. The `user` object is sent only by the auth endpoints and the admin endpoints, never in sync
   pulls. `GET /user/profile` sends the caller a smaller profile view of it (section 14).
2. Other entities carry copied names, not user objects (section 4).
3. PLANNED: reputation reaches buyers as about 60 bytes of owner fields per listing or trip
   (`reputation-api.md` section 7), and the party's own view as one small `GET /reputation/me`
   at most every 30 minutes.

---

## 14. Profile read and edit (BUILT: SIM-56, 2.2.0)

Every role edits the same profile text: `displayName`, `woreda` and, for a company buyer
(`role = buyer`, `buyerType = company`), `companyName`. Nothing else on the row is reachable:
role, phone, Fayda identity, verification, reputation, suspension and the deletion attributes are
never read from a client payload.

### Read: `GET /user/profile`

- JWT authorizer (the ID token, like every app route). The sync Lambda serves it: it owns the edit
  and already reads the users table, so the route adds no Lambda, grant or integration.
- `200 { "user": { ... } }` for the caller, read strongly consistent. Not the sign-in `user`
  object: a profile view (`UserProfileDto`) with only what the profile and edit screens read:
  `userId`, `displayName`, `fullName`, `role`, `woreda`, `preferredLanguage`, `buyerType`,
  `companyName` and `version` (a property with no value is left out). `faydaId` (the Fayda
  subject, the `FaydaIdIndex` key), `knownDevices` (install ids), `phoneNumber`, the
  verification, suspension and photo fields are never in it, so a leaked ID token reads no more
  than the profile text with one call.

  ```json
  {
    "user": {
      "userId": "550e8400-e29b-41d4-a716-446655440000",
      "displayName": "Almaz",
      "fullName": "Almaz Tesfaye Bekele",
      "role": "buyer",
      "woreda": "Meki",
      "preferredLanguage": "am",
      "buyerType": "company",
      "companyName": "Meki Grain Traders",
      "version": 5
    }
  }
  ```
- `401 UNAUTHORIZED` without a caller, `404 AUTH_013` without a row, `410 ACCOUNT_004` for a
  deleting or deleted account and `403 AUTH_025` for a suspended one (the refusals `/sync`
  answers), `503 USER_PROFILE_UNAVAILABLE` when the row cannot be read.
- About 300 bytes. The app reads it when the edit screen opens online, so a change made on another
  device reaches this one; the device keeps its own copy while an edit of its own still waits in
  its queue.

### Edit: the `/sync` `user` entity (`sync-protocol.md` 1.57.0)

`action: update`, `entityId` = the caller, `data` with any of `displayName`, `woreda`,
`companyName` and `preferredLanguage` (at least one). Offline-first: the phone queues the
operation like any other change; web sends it straight to `/sync`.

1. A missing key or a JSON `null` is not sent; a value that is not a string is
   `USER_PROFILE_VALIDATION`. Values are trimmed.
2. A field whose trimmed value equals the stored one (trimmed) is an unchanged echo: neither
   checked nor written, so a value stored before 1.2.0 can be sent back.
3. Field rules on the fields that change, in the order `displayName`, `woreda`, `companyName`:
   not empty (a listing, trip or order takes the name from the profile, and the woreda is the
   `RoleWaredaIndex` sort key), at most 100 characters, the name not the deleted-account name, and
   `companyName` only for a company buyer. A failure is `USER_PROFILE_VALIDATION: <reasons>`.
4. Then the FUP-52 phone screen on the fields that change, with the registration's code and text:
   `AUTH_027: Remove the phone number from <fields>. Contact opens after payment is held in
   escrow.` (`<fields>` in the order above, comma separated).
5. Any refusal writes nothing, the language included. Otherwise one `UpdateUserAsync` writes the
   changed fields (and the language), `version` + 1; nothing changed is a no-op `success` with the
   stored version. A version race is `USER_PROFILE_CONFLICT` (retried).

### Copies on other rows

The name and the woreda are copied when a row is written, not read live:

| Row | Copy | After an edit |
|---|---|---|
| Crop listing | `farmerName` (and the listing's own `woreda`, which the farmer types) | new listings carry the new name; existing ones keep theirs |
| Trip | `truckerName` | the same |
| Order | `buyerName`, `dropoffPlace` (the buyer's woreda), `farmerName` and `truckerName` from the listing and trip | the same |
| Delivery | the party names and phones | the same |
| Message | `senderName`, `recipientName` | the same |
| Admin payout queue | the payee name | read live, shows the new name |

Existing copies are not rewritten (no backfill): a listing, trip or order already seen by a
buyer keeps the name it was made with. Rewriting every row a user ever wrote on each edit would
cost writes and make every device pull those rows again. The app says so on the edit screen.
Whether open listings and trips should take the new name is follow-up FUP-316.

### Clients

- Profile > Edit profile, the same row for farmer, trucker and buyer, on the phone (reached from
  the account sheet's Profile) and on the web (the sidebar's Profile).
- The form checks rules 3 and 4 with the same detector before anything is sent, on the fields
  that change only (an unchanged value is not checked, as on the server); en, am and om texts.
  It shows the company name field only when the role is `buyer` and `buyerType` is `company`
  (an account an admin moved to another role keeps its `buyerType`). A web refusal naming fields
  marks those fields; any other refusal says the details were not accepted, not that the
  connection failed.
- On save the device's copy (Isar on the phone, browser storage on web) and the signed-in session
  take the new values, so the account sheet and the profile show them at once. A server read that
  comes back after a save started is not applied.

### Audit row and admin history (BUILT: FUP-317, 2.3.0)

The users row keeps only the new values, so each accepted edit of the profile text leaves one
transaction-audit row, and a name changed around a disputed order or a fraud case can be traced.

- **When.** Rule 5's write, when at least one of `displayName`, `woreda` and `companyName`
  changes. A refused edit, an unchanged echo and a language-only change write no row (the language
  is a preference, not identity).
- **Atomic.** The users update and the row go in one `TransactWriteItems`
  (`UpdateUserWithAuditAsync`), so an edit never lands without its row and a row never exists for
  an edit that did not land. The update keeps its conditions (`version`, `attribute_not_exists
  (deletionState)`); the row is put with `attribute_not_exists(transactionId)` (the table is
  append-only). A failed condition or a concurrent transaction on either row is the version
  conflict (`USER_PROFILE_CONFLICT`, retried); a validation error is thrown as before. Without the
  `TRANSACTION_AUDIT_TABLE` variable (tests) the edit is the plain update. No new grant: sync
  already has `UpdateItem` on the users table and `PutItem` on the audit table.
- **The row** (transaction-audit table, `TransactionAuditWriter.BuildOnceEntry`):

  | Attribute | Value |
  |---|---|
  | `transactionId` | `profile-{userId}`: one partition per user holding only these rows (the admin action rows use `{userId}`, the deletion rows `account-{userId}`) |
  | `timestamp` | when the edit was saved, ISO 8601 UTC |
  | `auditId` | `txaudit-profile-{userId}-profile_edited-{timestamp digits}` |
  | `entityType`, `entityId` | `user`, the `userId` (`EntityIndex` finds it with the user's other rows) |
  | `action` | `profile_edited` |
  | `actor`, `actorRole` | the user and the role on the row (only the user edits their profile) |
  | `ipAddress`, `correlationId` | from the `/sync` request |
  | `metadata` | JSON array of the changed fields in the order `displayName`, `woreda`, `companyName`: `[{"field":"displayName","from":"Abebe","to":"Abebe K."}]`; `from` is left out when the row had no value |
  | `fromState`, `toState` | not set (the FRD-19 advisory reads only these and the action, never `metadata`, so the values never reach the reviewer) |

- **Personal data.** The values are names and places, not secrets. They are never logged: the
  CloudWatch line names the fields only. The table is admin-read only (the admin Lambda; the sync
  Lambda only writes) and has no TTL, like every audit row (`account-deletion.md`: retained, never
  rewritten). An account deletion does not remove these rows today, so a deleted account's old
  names stay in its `profile-{userId}` partition; whether deletion should remove that partition is
  open (TASK FUP-419). The admin never shows them for a deleting or deleted account (below).
- **Admin.** `GET /admin/users/{id}` (admin group only, like every admin route) adds, next to
  `user` and `suspendReason`:

  ```json
  {
    "profileHistory": [
      {
        "editedAt": "2026-11-13T09:00:00.0000000Z",
        "actor": "550e8400-e29b-41d4-a716-446655440000",
        "actorRole": "buyer",
        "changes": [
          { "field": "displayName", "from": "Almaz", "to": "Almaz T." },
          { "field": "companyName", "from": "Old Traders", "to": "Meki Grain Traders" }
        ]
      }
    ],
    "profileHistoryMore": false
  }
  ```

  One `Query` of `profile-{userId}`, newest first, at most 50 edits; `profileHistoryMore` is true
  when older ones exist (the route does not page further). An empty list means no edits. Both
  fields are absent when the audit table is not configured, when the read fails (a warning with
  the exception type only; the user detail still answers) and for an account with
  `deletionState` (the ACC-05 rule: a deleted account's names are not shown). In that last case
  the response says `"profileHistoryHidden": true` (absent otherwise), since the phone is still on
  the row while a deletion runs and the app cannot tell the account apart from a failed read. A
  row whose `metadata` cannot be read is listed with no changes.
- **Audit log.** `GET /admin/audit` lists `profile_edited` rows without `metadata`: the user detail
  above is the only read of the names, so the audit log never shows them, whatever the account's
  state. Every other row keeps its `metadata`.
- **Admin app.** User detail > "Profile history": each edit's time and, per field, the old value,
  an arrow and the new value (`(none)` for a missing one); texts for no edits, a history that
  could not be loaded, a deleted or deleting account (an empty phone or `profileHistoryHidden`)
  and the 50-edit cap, in en, am and om. The audit log labels the `profile_edited` action
  ("Profile edited") without the values.

---

## Changelog

| Version | Date | Change |
|---------|------|--------|
| 2.3.0 | 2026-11-13 | FUP-317 (section 14, "Audit row and admin history"): an accepted change of `displayName`, `woreda` or `companyName` writes one transaction-audit `profile_edited` row (partition `profile-{userId}`, the old and new values of the changed fields in `metadata`, actor, role, IP, correlation id) in the same `TransactWriteItems` as the users update; a refused, unchanged or language-only edit writes none; a failed condition on either row is `USER_PROFILE_CONFLICT`. `GET /admin/users/{id}` adds `profileHistory` (newest first, at most 50) and `profileHistoryMore`, left out when the table is unconfigured, the read fails or the account has `deletionState` (then `profileHistoryHidden: true`). `GET /admin/audit` sends `profile_edited` rows without `metadata`. Invariant 14. The values are never logged; account deletion keeps the rows (open: TASK FUP-419). No change for the apps |
| 2.2.0 | 2026-11-12 | Profile read and edit (SIM-56, section 14): `GET /user/profile` returns the caller's profile view (`userId`, `displayName`, `fullName`, `role`, `woreda`, `preferredLanguage`, `buyerType`, `companyName`, `version`; never `faydaId`, `knownDevices` or the phone) (`401`, `404 AUTH_013`, `410 ACCOUNT_004`, `403 AUTH_025`, `503 USER_PROFILE_UNAVAILABLE`); the `/sync` `user` entity also edits `displayName`, `woreda` and a company buyer's `companyName` (trimmed, unchanged echoes skipped, not empty, at most 100 characters, not the deleted-account name, then the FUP-52 screen with the registration's `AUTH_027` code and text; a refusal writes nothing); copies on listings, trips, orders, deliveries and messages are not rewritten. `UpdateUserAsync` writes `companyName` from the copy when present and `REMOVE`s `faydaId` and an empty `woreda` when the row has none (a `NULL` `FaydaIdIndex` key and an empty `RoleWaredaIndex` key were refused, answering the 2.0.0 open question) |
| 2.1.2 | 2026-11-12 | FUP-162: two more writers of `ratingSum`, `ratingCount` and `ratingsAggregatedAt` for the farmers and truckers the ESC-28 backfill skipped (no `woreda`): the rating create's heal of an unstamped ratee and the backfill's one-off users `Scan` (`rating-api.md` 1.5.0). Shape unchanged |
| 2.1.1 | 2026-11-11 | ESC-29: `suspended` also refuses `/sync` and the write endpoints of `reputation-api.md` 8.4 (`403 AUTH_025`); the section 2 attributes are now read by `GET /reputation/*`, `GET /ratings`, the `/sync` gates and the owner stamps (`reputation-api.md` 1.2.0, `rating-api.md` 1.4.0), a row without `ratingsAggregatedAt` contributing no rating. Shape unchanged, never returned in the `user` object |
| 2.1.0 | 2026-11-07 | Section 2 BUILT (ESC-28): `ReputationService` writes the `reputation` map (the stream, the daily job, the refresh after a rating), the rating create `ADD`s `ratingSum` and `ratingCount`, registration starts every new row at 0 with `ratingsAggregatedAt`, the backfill stamps older rows. The map gains `lastEventId` and `lastEventRecordedAt` (the read-lag check). Never returned in the `user` object; no wire change |
| 2.0.1 | 2026-11-07 | `suspendReason`: an admin's `suspend` with a free-text reason no longer replaces a stored `fraud_confirmed` (FUP-103); the account stays held as confirmed fraud until `unsuspend`. Shape unchanged |
| 2.0.0 | 2026-11-07 | Rewritten from the code (ESC-27): every attribute with its type, writer and whether the `user` object returns it; `passwordHash`, `drivingLicensePath` and the ID scan paths are not on the row; `profilePhotoRef` has no writer; the NULL-or-missing rule, with an open question on `UpdateUserAsync` writing `NULL` to the `FaydaIdIndex` key on rows without a Fayda ID; `knownDevices` is an unordered string set; table settings as built (default encryption, no stream, no TTL); access patterns; the writers table with version semantics (admin actions do not bump `version`) and the known `UpdateUserAsync` revert gap; the Isar schema and web model as built (field-level PII encryption; `preferredLanguage` not in Isar); sync is the `user` entity only; `UserJsonContext` and the embedded `MinimalUserDto` were never used. PLANNED (ESC-28): `reputation` map, top-level `ratingSum`, `ratingCount`, `ratingsAggregatedAt`, their write rules and invariants 11 to 13 (`reputation-api.md` 1.0.0, `rating-api.md` 1.2.0). No wire change for built clients |
| 1.3.2 | 2026-11-07 | Doc only (ESC-14, survey 9.5): the row as built. `role` is changed by an admin's `change_role` (marketplace roles only, Cognito claim rewritten, user signed out), not immutable; new table of server-written fields (`fullName`, `faydaVerified`, `faydaVerifiedAt`, `faydaAuthenticatedAt`, `suspended`, `suspendReason`, `knownDevices`) saying which the register and login `user` object returns; `RoleWaredaIndex` projects `ALL` (was documented `KEYS_ONLY`); `FaydaIdIndex` documented. The full rewrite with `reputation` stays ESC-27 (user-entity 2.0.0). No wire change |
| 1.3.1 | 2026-11-06 | `preferredLanguage` is read back: after a sign-in the app shows it unless the user picked a language on that device (`auth-api.md` 2.12.3, SIM-37). Shape unchanged |
| 1.3.0 | 2026-10-24 | `preferredLanguage` is written from the app's Settings through the `/sync` `user` entity (`sync-protocol.md` 1.28.0, SIM-37, #220) |
| 1.2.0 | 2026-10-23 | No phone number in `displayName`, `woreda` or `companyName` (FUP-52): refused at registration with `AUTH_027` (`auth-api.md` 2.12.0); any later profile update path must run the same check; stored values are not re-validated; the Fayda `fullName` is not screened; `companyName` added to the server validation table |
| 1.1.0 | 2026-10-06 | Deletion is now supported through `DELETE /user/account` (`account-deletion.md`); server-only `deletionState`, `deletionRequestedAt`, `deletedAt` and the tombstone shape documented (ACC-01) |
| 1.0.0 | 2026-02-12 | Initial entity definition: fields, DynamoDB schema, Isar schema, sync rules |

---

**Last Updated**: 2026-11-13
**Version**: 2.3.0
