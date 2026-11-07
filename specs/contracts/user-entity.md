# User Entity Contract

## Version
2.0.1

## Parties
- **Mobile** (client): caches the signed-in user in Isar (`core/database/schemas/user.dart`) for offline sign-in and display
- **Web** (client): keeps the same fields in memory (`core/models/web/user.dart`)
- **Backend** (server): owns the authoritative row in the DynamoDB users table (`server/src/Data/UserRepository.cs`, `Models/UserDto.cs`)
- **Admin**: reads users and applies the admin user actions
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
| Section 2 (`reputation`, `ratingSum`, `ratingCount`, `ratingsAggregatedAt`) | PLANNED: ESC-28 writes them, ESC-29 reads them (`reputation-api.md` 1.0.0, `rating-api.md` 1.2.0) |

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
| `displayName` | S | yes | registration | The marketplace name, copied onto new listings as `farmerName` / `truckerName`; max 100 characters, no phone number (section 11) |
| `fullName` | S | yes | registration (from the Fayda token), `/user/fayda-link` (only when empty) | The Fayda legal name; never screened (section 11) |
| `role` | S | yes | registration; an admin's `change_role` | `farmer`, `trucker` or `buyer` (section 3) |
| `woreda` | S | yes | registration | The district, max 100 characters, no phone number |
| `faydaId` | S | yes | registration, `/user/fayda-link` | The Fayda national ID; unique in practice (`FaydaIdIndex` duplicate check) |
| `faydaVerified` | BOOL | yes (`false` included) | registration, `/user/fayda-link`, admin `verify` | The account's Fayda identity is verified |
| `faydaVerifiedAt` | S | yes | registration, `/user/fayda-link` | When it was first verified (admin `verify` does not stamp it) |
| `faydaAuthenticatedAt` | S | never | `/user/fayda-link` | The last Fayda sign-in; payments above 10,000 ETB need it recent (`AUTH_041`) |
| `preferredLanguage` | S | yes | registration; the `/sync` `user` entity (section 9) | `en`, `am` or `om`; a row without it reads as `am` |
| `isPhoneVerified` | BOOL | yes (`false` included) | registration, `POST /auth/verify-otp`, `/user/fayda-link` (when the Fayda record proves the phone) | SMS OTP or Fayda proved the phone |
| `suspended` | BOOL | yes (`false` included) | admin `suspend`, `unsuspend` | Blocks login, OTP and refresh (`AUTH_025`); ESC-29 extends it to the write endpoints (`reputation-api.md` 8.4). A row without it reads as `false` |
| `suspendReason` | S | never (admin detail shows it beside the user) | admin `suspend` (removed by `unsuspend`) | `fraud_confirmed` is the Fraud Review reject (FRD-05). Since 2.0.1 a later `suspend` with another reason keeps `fraud_confirmed`; only `unsuspend` clears it (FUP-103) |
| `knownDevices` | SS | yes (`[]` when none) | registration, login on a new install | Install ids (`X-Device-Id`), at most 10; a string set, so DynamoDB keeps no order; a new one triggers a sign-in notice; FRD-15 compares them |
| `profilePhotoRef` | S | yes | nothing today | Read and written back by `UpdateUserAsync`; no upload path exists, so it is never set |
| `buyerType` | S | yes | registration | Buyer only: `private` or `company` (F-32) |
| `companyName` | S | yes | registration | Buyer only, required for a company buyer, max 100 characters, no phone number |
| `deletionState` | S | never | `DELETE /user/account` | `in_progress`, `cognito_pending`, `completed`; absent on a live account (section 7) |
| `deletionRequestedAt` | S | never | `DELETE /user/account` | When the deletion started |
| `deletedAt` | S | never | the tombstone write | When the personal data was erased |
| `createdAt` | S | yes | registration | ISO 8601 UTC |
| `updatedAt` | S | yes | every write path (section 6) | ISO 8601 UTC of the last write |
| `version` | N | yes | registration (1), `UpdateUserAsync` (+1), the tombstone (+1) | Concurrency for the profile write path; admin actions and the PLANNED reputation writes do not bump it (section 6) |

Not on the row (fixed in 2.0.0): `passwordHash` (Cognito holds the password; nothing about it
is stored in DynamoDB or sent anywhere), `drivingLicensePath` and the Fayda ID scan paths (local
file paths on the phone only, section 8).

An absent optional attribute may be stored either as a missing attribute (registration omits
it) or as a DynamoDB `NULL` (`UpdateUserAsync` writes `NULL` for an empty `displayName`,
`fullName`, `profilePhotoRef`, `faydaId`, `faydaVerifiedAt`, `faydaAuthenticatedAt` and
`knownDevices`). Readers treat both the same.

**Open question (found in 2.0.0).** `faydaId` is the partition key of `FaydaIdIndex`, and
DynamoDB refuses a write that gives an index key attribute a type other than the index's (`S`
here), `NULL` included. Registration allows a null `faydaId` (`RegisterFunction` simply omits
it), but `UpdateUserAsync` always sets `faydaId`, as `NULL` when the user has none. On a row
without a Fayda ID every `UpdateUserAsync` (verify-otp, the login device write, the language
change) may therefore be refused with a validation error. Follow-up: test `UpdateUserAsync` on
a row without `faydaId` against DynamoDB Local and, if refused, `REMOVE` the attribute instead
of setting `NULL` (the same for any other index key).

---

## 2. Reputation attributes (PLANNED: ESC-28)

Server-only. Never returned in the `user` object: the party reads them through
`GET /reputation/me`, buyers through the public summary and the copied owner fields
(`reputation-api.md` 1.0.0), admins through `GET /admin/users/{userId}/reputation`.

| Attribute | Type | Written by | Meaning |
|---|---|---|---|
| `reputation` | M | `ReputationService` (the stream Lambda, the daily job, the admin reputation actions) | The aggregate below; absent until the user's first event (an absent map reads as 0 points, `good`, no reliability) |
| `ratingSum` | N | the rating create transaction (`ADD`), the backfill, the role-change rebuild | Sum of the stars of the ratings received in the current role (`rating-api.md` 1.2.0) |
| `ratingCount` | N | the same | Their count |
| `ratingsAggregatedAt` | S | registration (new rows start at 0 with it), the one-time backfill | Present once `ratingSum` and `ratingCount` are exact; until then reads fall back to the ratings index |

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
- The user cannot change it: the `/sync` `user` entity ignores every key but `preferredLanguage`.
- An admin's `POST /admin/users/{id}/action` with `change_role` and `newRole` rewrites it, only to
  one of the three marketplace roles (`ADMIN_011` otherwise, never `admin`), together with the
  Cognito `custom:role` claim, and signs the user out (`auth-api.md`). Spec H5 says a role change
  waits until the user has no open orders; that check is not built (follow-up).
- Admin accounts live in the separate admin user pool and have no users row.

### woreda, displayName, companyName
- Non-empty (`woreda`), at most 100 characters, Amharic and Afan Oromo scripts allowed.
- No Ethiopian mobile number (section 11).

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
| `RoleWaredaIndex` | `role` | `woreda` | no server code queries it today (the admin list scans); PLANNED: the rating backfill walks the `farmer` and `trucker` partitions |
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
| PLANNED: the rating backfill | `Query` `RoleWaredaIndex` for `role = farmer` and `role = trucker` |

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

PLANNED (ESC-28) additions on the same row:
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
| `SyncFunction`, `user` entity | `preferredLanguage` (no write when unchanged) | `UpdateUserAsync` | +1 |
| `AdminFunction` user action | `suspend`: `suspended`, `suspendReason`; `unsuspend`: `suspended: false`, removes `suspendReason`; `verify`: `faydaVerified`; `change_role`: `role`; each with `updatedAt` | `UpdateItem`, `attribute_exists(userId)`; audited (transaction-audit `user_{action}`) | not bumped |
| Account deletion | section 7 | `UpdateItem` | +1 on the tombstone |
| PLANNED: `ReputationService` | `reputation` | section 2 | not bumped |
| PLANNED: rating create, backfill, role-change rebuild | `ratingSum`, `ratingCount`, `ratingsAggregatedAt` | `rating-api.md` 1.2.0 | not bumped |

`UpdateUserAsync` is a read-modify-write: it `SET`s `phoneNumber`, `displayName`, `fullName`,
`role`, `woreda`, `preferredLanguage`, `isPhoneVerified`, `profilePhotoRef`, `faydaId`,
`faydaVerified`, `faydaVerifiedAt`, `faydaAuthenticatedAt`, `knownDevices`, `updatedAt` and
`version` from the copy its caller read, under `version = expected AND
attribute_not_exists(deletionState)`. It never touches `suspended`, `suspendReason`,
`buyerType`, `companyName` or the deletion attributes.

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
removed, including any added later (the PLANNED section 2 attributes too). A deletion that
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
- The only profile change through `/sync` is the `user` entity (`sync-protocol.md` 1.28.0):
  `action: update`, `entityId` = the caller, `data.preferredLanguage` in `en`, `am`, `om`; every
  other key is ignored. It is version-less for the client (the server reads the row and writes
  with its version); an unchanged value is a no-op `success`. The `profile` entity shown up to
  1.3.2 was never served.
- Deletion is `DELETE /user/account`, online only, never through `/sync` (section 7).

---

## 10. Server model notes (.NET)

- `Models/UserDto.cs`: `UserDto` (a `sealed record`) carries every section 1 attribute.
  `FaydaAuthenticatedAt`, `SuspendReason`, `DeletionState`, `DeletionRequestedAt` and `DeletedAt`
  are `[JsonIgnore]`, so they never leave the server. PLANNED: the section 2 attributes stay off
  `UserDto` (or are `[JsonIgnore]`); `ReputationService` reads them with its own projection.
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
(`auth-api.md` 2.12.0). No profile edit path exists for these fields; any later one must run the
same check on the fields it changes and treat a value equal to the stored one as an unchanged
echo. Values stored before 1.2.0 are kept. `fullName` comes only from Fayda and is never
screened.

---

## 12. Invariants

1. **`phoneNumber` is unique and never changes.**
2. **`userId` never changes.**
3. **Only an admin changes `role`**, and only to a marketplace role.
4. **`version` only increases**, and only on the profile write path and the tombstone.
5. **No password data is stored on the row** or sent in any response.
6. **The server is authoritative.** Clients never write a user row; the only client-driven
   profile change is `preferredLanguage` through `/sync`.
7. **Server-only attributes never leave the server**: `faydaAuthenticatedAt`, `suspendReason`,
   the deletion attributes and the section 2 attributes.
8. **A deleting or deleted account takes no profile write** (`attribute_not_exists
   (deletionState)` on `UpdateUserAsync`).
9. **A tombstone keeps only the allow-list** of section 7.
10. **Timestamps are ISO 8601 UTC**; local time is a display concern of the device.
11. **PLANNED: the `reputation` map changes only together with the event that changes it**, in
    one transaction, under its own `reputation.version`.
12. **PLANNED: reputation and rating aggregate writes never bump `version` or `updatedAt`**,
    and `UpdateUserAsync` never writes them.
13. **PLANNED: nothing writes reputation onto a deleting account** (`attribute_not_exists
    (deletionState)` on every reputation write).

---

## 13. Data transfer

1. The `user` object is sent only by the auth endpoints and the admin endpoints, never in sync
   pulls.
2. Other entities carry copied names, not user objects (section 4).
3. PLANNED: reputation reaches buyers as about 60 bytes of owner fields per listing or trip
   (`reputation-api.md` section 7), and the party's own view as one small `GET /reputation/me`
   at most every 30 minutes.

---

## Changelog

| Version | Date | Change |
|---------|------|--------|
| 2.0.1 | 2026-11-07 | `suspendReason`: an admin's `suspend` with a free-text reason no longer replaces a stored `fraud_confirmed` (FUP-103); the account stays held as confirmed fraud until `unsuspend`. Shape unchanged |
| 2.0.0 | 2026-11-07 | Rewritten from the code (ESC-27): every attribute with its type, writer and whether the `user` object returns it; `passwordHash`, `drivingLicensePath` and the ID scan paths are not on the row; `profilePhotoRef` has no writer; the NULL-or-missing rule, with an open question on `UpdateUserAsync` writing `NULL` to the `FaydaIdIndex` key on rows without a Fayda ID; `knownDevices` is an unordered string set; table settings as built (default encryption, no stream, no TTL); access patterns; the writers table with version semantics (admin actions do not bump `version`) and the known `UpdateUserAsync` revert gap; the Isar schema and web model as built (field-level PII encryption; `preferredLanguage` not in Isar); sync is the `user` entity only; `UserJsonContext` and the embedded `MinimalUserDto` were never used. PLANNED (ESC-28): `reputation` map, top-level `ratingSum`, `ratingCount`, `ratingsAggregatedAt`, their write rules and invariants 11 to 13 (`reputation-api.md` 1.0.0, `rating-api.md` 1.2.0). No wire change for built clients |
| 1.3.2 | 2026-11-07 | Doc only (ESC-14, survey 9.5): the row as built. `role` is changed by an admin's `change_role` (marketplace roles only, Cognito claim rewritten, user signed out), not immutable; new table of server-written fields (`fullName`, `faydaVerified`, `faydaVerifiedAt`, `faydaAuthenticatedAt`, `suspended`, `suspendReason`, `knownDevices`) saying which the register and login `user` object returns; `RoleWaredaIndex` projects `ALL` (was documented `KEYS_ONLY`); `FaydaIdIndex` documented. The full rewrite with `reputation` stays ESC-27 (user-entity 2.0.0). No wire change |
| 1.3.1 | 2026-11-06 | `preferredLanguage` is read back: after a sign-in the app shows it unless the user picked a language on that device (`auth-api.md` 2.12.3, SIM-37). Shape unchanged |
| 1.3.0 | 2026-10-24 | `preferredLanguage` is written from the app's Settings through the `/sync` `user` entity (`sync-protocol.md` 1.28.0, SIM-37, #220) |
| 1.2.0 | 2026-10-23 | No phone number in `displayName`, `woreda` or `companyName` (FUP-52): refused at registration with `AUTH_027` (`auth-api.md` 2.12.0); any later profile update path must run the same check; stored values are not re-validated; the Fayda `fullName` is not screened; `companyName` added to the server validation table |
| 1.1.0 | 2026-10-06 | Deletion is now supported through `DELETE /user/account` (`account-deletion.md`); server-only `deletionState`, `deletionRequestedAt`, `deletedAt` and the tombstone shape documented (ACC-01) |
| 1.0.0 | 2026-02-12 | Initial entity definition: fields, DynamoDB schema, Isar schema, sync rules |

---

**Last Updated**: 2026-11-07
**Version**: 2.0.0
