# PR Scaffold — S2-04: Encrypt CropListing PII

> **Status:** Design ready · **Depends on:** S2-01 merged
> **Sprint:** HS-2 · **Epic:** E1 — Encrypt at rest
> **Story catalog:** `specs/features/isar-hardening/sprints.md#s2-04`
> **Reviewer needed:** security + 1 mobile dev · **Story size:** 2 pts

The smaller sibling of S2-01. Same pattern (centralize PII write through
a repository, wrap with `FieldEncryptor`) applied to `CropListing`. The
crop_listing_repository already exists, so the scope is narrower.

---

## 0. TL;DR

Encrypt `CropListing.farmerName` (and optionally `description`) using
the AAD-bound `FieldEncryptor`. Keep `locationName` and `cropType`
plaintext — they're indexed and filtered on.

The existing `CropListingRepository` at
`mobile/lib/features/crop_listing/data/repositories/crop_listing_repository.dart`
gets the encrypt/decrypt hooks added.

---

## 1. How to ship this

```bash
git fetch origin
git log origin/main --oneline | grep S2-01      # confirm S2-01 merged

git checkout -b hs2/s2-04-encrypt-crop-pii origin/main

# Apply the changes in §5:
#   - mobile/lib/features/crop_listing/data/repositories/crop_listing_repository.dart
#   - mobile/lib/features/crop_listing/data/repositories/web_crop_listing_repository.dart
#       (no encrypt — web has no Isar; ensure signature still matches base)
#   - mobile/test/features/crop_listing/data/repositories/crop_listing_repository_test.dart

cd mobile
flutter analyze
flutter test test/features/crop_listing/
flutter build web --no-tree-shake-icons 2>&1 | tail -5
cd ..

git add ...
git commit -m "feat(mobile): encrypt CropListing.farmerName at rest"
git push -u origin hs2/s2-04-encrypt-crop-pii
gh pr create --title "feat(mobile): encrypt CropListing PII [S2-04]"
```

---

## 2. PR description

### Summary

Wraps `CropListingRepository.createListing/update` to encrypt
`farmerName` before `put`, and the read paths to decrypt before
returning to BLoCs. `locationName` and `cropType` stay plaintext —
both are `@Index` columns used in browse/filter queries.

### Why

`CropListing.farmerName` is a denormalized copy of the farmer's
display name, embedded in every listing so buyers see who created the
crop without doing a join. Today it sits plaintext alongside the
listing. Encrypting it closes one more PII surface on the offline-first
DB.

`description` is a free-text field. Some farmers include phone numbers
or other contact info in it. Encrypting `description` is **optional** —
the story leaves it as a PO decision. Default: encrypt. If we encrypt,
buyer-side search-by-description breaks; but no current UI offers that
search, so the trade-off is acceptable.

### What changes

- **`mobile/lib/features/crop_listing/data/repositories/crop_listing_repository.dart`**
  Constructor takes `FieldEncryptor`. `createListing` encrypts
  `farmerName` (and `description` if opt-in) before `put`. `getById` and
  the watch streams decrypt on read.

- **`mobile/lib/features/crop_listing/data/repositories/web_crop_listing_repository.dart`**
  No-op — web reads from API where the server returns plaintext for the
  authorized user's view. Verify the constructor signature still matches
  the base class.

- **`mobile/test/features/crop_listing/data/repositories/crop_listing_repository_test.dart`**
  Round-trip tests, role-visibility tests (farmer-own + buyer-all
  preserved), corrupted-blob sentinel test.

### What does NOT change

- `cropType`, `locationName`, `quantity`, `pricePerKg`, `status`,
  `farmerId` — all plaintext. `cropType` and `locationName` are
  indexed; `farmerId` is the AAD `rowKey` (must be queryable).
- Role-based visibility filtering — unchanged. Encryption happens
  underneath the filter.
- Audio note references (`audioNoteRef`) — file paths, encrypted in a
  future PII-images story (after S3-02 sets the pattern).

### Backwards compatibility

Same model as S2-01:

- New writes after this PR are encrypted
- Reads decrypt via `_hydrate`; corrupted blobs return `…` sentinel
- Pre-S2-04 plaintext rows are readable under
  `legacyPlaintextAllowed: true` until HS-3 migrates them

### Test plan

```bash
cd mobile
flutter test test/features/crop_listing/
flutter test                                  # full suite
flutter build web --no-tree-shake-icons
```

**Manual:**
1. Farmer registers, creates a listing with `farmerName = 'Abebe'`
2. Open Isar Inspector → `farmerName` is `enc:v1:...`
3. Buyer browses → sees `Abebe` (decrypt works)
4. Kill + relaunch as farmer → own crop still shows correctly

### Threat model coverage

| Threat | Pre-PR | Post-PR |
|---|---|---|
| Stolen device exposes listing creator names | Plaintext leak | `enc:v1:...` blob only |
| Cross-row AAD swap (farmer-A's name onto farmer-B's listing) | Not protected | Detected (HS-1 AAD) |

### Risk

Low. The CropListing repository is well-isolated; BLoCs consume
hydrated objects. Risk areas:

1. **Conflict resolver path for CropListing.** `mobile/lib/core/sync/conflict_resolver.dart`
   handles CropListing conflicts too. Verify the resolver routes
   through `CropListingRepository.save` so encryption happens.

2. **Browse query performance.** Encrypted columns can't be indexed.
   Confirm we don't try to filter on `farmerName` anywhere — grep
   `farmerNameEqualTo|farmerNameContains` should return zero.

### Acceptance criteria

From `specs/features/isar-hardening/sprints.md#s2-04`:

- [ ] `CropRepository.create/update` encrypts `farmerName`, optionally `description`
- [ ] Decryption on read for all role views (farmer-own, buyer-all)
- [ ] `locationName` stays plaintext — used in indexed filter
- [ ] Browse screens show plaintext after decryption; no `enc:v1:` leaks into UI

---

## 3. Reviewer guide

1. **Confirm the encrypted columns aren't indexed.** Grep
   `@Index.*farmerName` and `@Index.*description` — should be zero.
2. **Confirm no `farmerNameEqualTo` or `descriptionContains` query
   builders are used anywhere.** Both queries would silently fail on
   ciphertext.
3. **AAD `rowKey` is `CropListing.listingId`** (server UUID), not the
   Isar `id`. Same rule as S2-01.

---

## 4. Rollback plan

Same model as S2-01. Feature flag `enableCropPiiEncryption`. Default
on. Kill switch flips to off; reads fall through to legacy path.

---

## 5. Proposed diffs

### 5a. `mobile/lib/features/crop_listing/data/repositories/crop_listing_repository.dart`

```diff
 class CropListingRepository extends BaseCropListingRepository {
   final Isar _isar;
   final SyncQueue _syncQueue;
+  final FieldEncryptor _enc;
   static const _uuid = Uuid();

   CropListingRepository({
     required Isar isar,
     required SyncQueue syncQueue,
+    required FieldEncryptor encryptor,
-  })  : _isar = isar,
-        _syncQueue = syncQueue;
+  })  : _isar = isar,
+        _syncQueue = syncQueue,
+        _enc = encryptor;

   @override
   Future<CropListing> createListing({
     // ... existing params unchanged
   }) async {
     final listingId = _uuid.v4();
     // ... build the listing object ...
+
+    // Encrypt PII before put. AAD bound to (CropListing, column, listingId).
+    listing.farmerName = await _enc.encrypt(
+      listing.farmerName,
+      collection: 'CropListing', column: 'farmerName', rowKey: listingId,
+    );
+    // PO decision: encrypt description (default yes).
+    listing.description = await _enc.encrypt(
+      listing.description,
+      collection: 'CropListing', column: 'description', rowKey: listingId,
+    );

     await _isar.writeTxn(() async {
       await _isar.cropListings.put(listing);
     });
     // ... enqueue sync as before ...
     return _hydrate(listing);   // return decrypted to caller
   }

+  Future<CropListing> _hydrate(CropListing raw) async {
+    final rowKey = raw.listingId;
+    try {
+      raw.farmerName = await _enc.decrypt(raw.farmerName,
+          collection: 'CropListing', column: 'farmerName', rowKey: rowKey);
+    } on FieldDecryptionException {
+      raw.farmerName = '…';
+    }
+    try {
+      raw.description = await _enc.decrypt(raw.description,
+          collection: 'CropListing', column: 'description', rowKey: rowKey);
+    } on FieldDecryptionException {
+      raw.description = '…';
+    }
+    return raw;
+  }

   // Apply _hydrate to every read path:
   // - getById
   // - watchForRole / watchAll
   // - getByFarmerId
   // - etc.
 }
```

For watch streams, map over the stream:

```dart
Stream<List<CropListing>> watchForRole(...) {
  return _isar.cropListings.filter()...watch(fireImmediately: true)
      .asyncMap((list) async {
        return Future.wait(list.map(_hydrate));
      });
}
```

### 5b. `mobile/lib/features/crop_listing/data/repositories/web_crop_listing_repository.dart`

Just signature parity — web has no encryptor dependency. If the base
class takes `encryptor` as required, the web variant must too, but it
ignores it:

```diff
-WebCropListingRepository(this._api);
+WebCropListingRepository({required ApiClient api, FieldEncryptor? encryptor})
+    : _api = api;
+// encryptor unused on web — kept for signature parity with mobile variant
```

Or, cleaner: don't take `encryptor` on the web side at all, and only
the DI wiring differs between platforms (see S2-01 §5e for the same
pattern).

### 5c. Test additions

```dart
test('createListing encrypts farmerName before put', () async {
  final repo = CropListingRepository(isar: isar, syncQueue: syncQ, encryptor: fakeEnc);
  await repo.createListing(farmerId: 'F1', farmerName: 'Abebe', ...);

  final raw = await isar.cropListings.where().findFirst();
  expect(raw!.farmerName, startsWith('fake:'));   // FakeFieldEncryptor prefix
});

test('watchForRole emits decrypted listings', () async {
  final repo = CropListingRepository(isar: isar, syncQueue: syncQ, encryptor: fakeEnc);
  await repo.createListing(farmerId: 'F1', farmerName: 'Abebe', ...);

  final stream = repo.watchForRole(role: UserRole.buyer, currentUserId: 'B1');
  final emitted = await stream.first;
  expect(emitted.first.farmerName, equals('Abebe'));    // decrypted
});

test('hydrate returns "…" sentinel on corrupted farmerName', () async {
  final corrupt = CropListing()
    ..listingId = 'lst-A'
    ..farmerName = 'enc:v1:not-real-cipher'
    ..description = 'plain';
  await isar.writeTxn(() => isar.cropListings.put(corrupt));

  final repo = CropListingRepository(isar: isar, syncQueue: syncQ, encryptor: realEnc);
  final loaded = await repo.getById(corrupt.id);
  expect(loaded!.farmerName, equals('…'));
});

test('role-visibility filter survives encryption layer', () async {
  // Farmer F1 creates a listing. Farmer F2 cannot see it; buyer can.
  final repo = CropListingRepository(isar: isar, syncQueue: syncQ, encryptor: fakeEnc);
  await repo.createListing(farmerId: 'F1', farmerName: 'Abebe', ...);

  // Farmer F2 view: should be empty (own-only).
  final f2View = await repo.watchForRole(
      role: UserRole.farmer, currentUserId: 'F2').first;
  expect(f2View, isEmpty);

  // Buyer view: should see the listing.
  final buyerView = await repo.watchForRole(
      role: UserRole.buyer, currentUserId: 'B1').first;
  expect(buyerView.length, 1);
  expect(buyerView.first.farmerName, equals('Abebe'));
});
```

---

## 6. Open questions for the reviewer

1. **Encrypt `description` or not?** Default: yes. Counter: it kills
   any future "search by description" feature. PO call. If "no,"
   document it explicitly and add a comment in the repository.

2. **`farmerName` is denormalized — what happens if a farmer changes
   their display name?** Today the listing's `farmerName` is stale
   until the next write. That's a pre-existing issue, not new to this
   PR. Track as a follow-up.

3. **Hydrate-on-read cost.** Watch streams now decrypt every row on
   every emission. For a buyer browsing 100 listings, that's 100
   decrypts per refresh. Measure on a Tecno Spark 8; if > 100ms,
   consider memoizing hydrated objects.

---

*PR scaffold v1.0 — 2026-05-13 — depends on S2-01 merged*
