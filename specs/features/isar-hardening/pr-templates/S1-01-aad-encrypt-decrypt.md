# PR Scaffold — S1-01: AAD-bound encrypt/decrypt API

> **Status:** Ready to commit · **Sprint:** HS-1 · **Epic:** E1 — Encrypt at rest
> **Story catalog:** `specs/features/isar-hardening/sprints.md#s1-01`
> **Scaffold owner:** PO · **Reviewer needed:** security + 1 mobile dev

This document is the PR plan. The code changes have **already been written**
in the working tree — they are not yet committed. Follow the steps in §1 to
turn this into an actual PR.

---

## 0. TL;DR

Add `(collection, column, rowKey)` AAD parameters to `FieldEncryptor.encrypt`
and `FieldEncryptor.decrypt`. The trio is authenticated alongside the
ciphertext by AES-GCM so that a blob from one column/row cannot be planted
into another column/row without the MAC failing.

**Strictly in scope for S1-01:** signature change, AAD passthrough, web stub
parity. **NOT in this PR:** `enc:v1:` prefix (S1-02), fail-closed decrypt
(S1-02), key-integrity HMAC (S1-03), migration toggle (S1-04). Keep PRs
scoped — these land in their own stories so each can be reviewed cleanly.

---

## 1. How to ship this

```bash
# From the project root
cd "/Users/mitiku.geleta/Documents/Personal /WeredaConnect"

# 1. Verify the working tree contains only the scaffold changes for this story
git status --short
# Expect:
#   M mobile/lib/core/database/field_encryptor.dart
#   M mobile/lib/core/database/field_encryptor_web.dart
#   M mobile/test/core/database/field_encryptor_test.dart
# (Other unrelated changes? Stash them first — see §5.)

# 2. Create the feature branch from main
git fetch origin
git checkout -b hs1/s1-01-aad-encrypt-decrypt origin/main

# 3. Re-apply the working-tree changes if step 2 lost them
# (Only needed if you stashed in step 1.)
git stash pop

# 4. Run the gates
cd mobile
flutter analyze lib/core/database/field_encryptor.dart \
                lib/core/database/field_encryptor_web.dart \
                test/core/database/field_encryptor_test.dart
flutter test test/core/database/field_encryptor_test.dart
flutter build web --no-tree-shake-icons 2>&1 | tail -5   # platform-pair sanity
cd ..

# 5. Commit + push
git add mobile/lib/core/database/field_encryptor.dart \
        mobile/lib/core/database/field_encryptor_web.dart \
        mobile/test/core/database/field_encryptor_test.dart
git commit -m "$(cat <<'EOF'
feat(mobile): bind AES-GCM ciphertexts to (collection, column, rowKey) AAD

Adds required AAD parameters to FieldEncryptor.encrypt/decrypt so a blob
from one column/row cannot be planted into another without the MAC
failing. Web stub mirrors the signature (no-op).

Scoped to AAD only. Prefix/fail-closed/integrity-HMAC follow in S1-02 and
S1-03. No production callers exist yet — repository wiring lands in HS-2.

Refs: specs/features/isar-hardening/sprints.md#s1-01
EOF
)"
git push -u origin hs1/s1-01-aad-encrypt-decrypt

# 6. Open the PR
gh pr create --title "feat(mobile): AAD-bound FieldEncryptor encrypt/decrypt [S1-01]" \
             --body "$(cat specs/features/isar-hardening/pr-templates/S1-01-aad-encrypt-decrypt.md | sed -n '/^## 2. PR description/,/^## 3. /p' | head -n -1)"
```

---

## 2. PR description

### Summary

Adds required `(collection, column, rowKey)` parameters to
`FieldEncryptor.encrypt()` and `FieldEncryptor.decrypt()`. The trio is
serialized to UTF-8 and passed as AES-GCM Associated Authenticated Data.
A ciphertext bound to one column/row will fail MAC verification if read
under any other `(collection, column, rowKey)` trio.

### Why

`docs/mobile/isar-implementation.md` security review (May 2026), finding
**N-C1** in the security re-evaluation: without AAD, AES-GCM authenticates
bytes but not context. An attacker with write access to the Isar file
(rooted device, malicious backup-restore on a different device, debuggable
build via adb) can swap ciphertexts between columns or between users'
rows — and the receiving column reads it as a legitimate value, because
the MAC still verifies (same key, same plaintext, just landed in the
wrong slot).

Binding AAD to `(collection, column, rowKey)` makes the swap detectable.

### What changes

- `mobile/lib/core/database/field_encryptor.dart`
  - New private helper `_aad(collection, column, rowKey)` returns the
    canonical AAD string.
  - `encrypt()` and `decrypt()` now require three named string params.
  - AAD is passed to `AesGcm.encrypt/decrypt` via the `aad:` argument.
  - Fail-open behavior on uninitialized encryptor and on MAC failure is
    **preserved** — closing those paths is S1-02 (`enc:v1:` prefix +
    fail-closed) and out of scope here.
  - `debugPrint` on MAC failure now logs `collection:column` so the
    failure context is visible without leaking the row key.

- `mobile/lib/core/database/field_encryptor_web.dart`
  - Signature parity. The web stub accepts the same parameters but ignores
    them — there is no Isar / no on-disk PII on web, so AAD has nothing
    to protect against.

- `mobile/test/core/database/field_encryptor_test.dart`
  - All 17 pre-existing tests updated to pass an AAD trio.
  - **New** `AAD binding` test group with four cases:
    - Cross-row swap detected (row-A blob fails as a row-B read)
    - Cross-column swap detected (phone-column blob fails as displayName)
    - Cross-collection swap detected
    - Matching AAD round-trip succeeds

### What does NOT change in this PR

- `enc:v1:` ciphertext prefix → S1-02
- Fail-closed `FieldDecryptionException` on any failure → S1-02
- Key-integrity HMAC on read-back → S1-03
- `legacyPlaintextAllowed` flag → S1-04
- Any production repository caller — there are none today (verified by
  grep). Wiring repositories to use the new signature is HS-2 (S2-01..S2-04).

### Backwards compatibility

No production code currently calls `FieldEncryptor.encrypt` or `.decrypt`:

```bash
$ grep -rn "fieldEncryptor\." mobile/lib --include="*.dart" | grep -v field_encryptor
# (no matches outside the definition file itself)
```

The only callers are tests (this PR) and a couple of getter references in
`isar_service.dart` (`fieldEncryptor` getter — untouched). So the required-
parameter change is safe to land.

### Test plan

```bash
cd mobile
flutter analyze lib/core/database/field_encryptor.dart \
                lib/core/database/field_encryptor_web.dart \
                test/core/database/field_encryptor_test.dart
flutter test test/core/database/field_encryptor_test.dart
flutter build web --no-tree-shake-icons   # confirm web stub compiles
```

Expected: all tests green (existing 17 + 4 new AAD-binding tests = 21
total). Web build clean.

### Threat model coverage

| Threat | Pre-PR | Post-PR |
|---|---|---|
| Cross-column ciphertext swap | undetected | detected (MAC fail) |
| Cross-row ciphertext swap | undetected | detected (MAC fail) |
| Cross-collection ciphertext swap | undetected | detected (MAC fail) |
| Wrong key | detected (MAC fail) | detected (MAC fail) |
| Direct bit-flip in ciphertext | detected (MAC fail) | detected (MAC fail) |
| Plaintext substitution (downgrade) | **NOT detected** | **NOT detected — closed in S1-02** |

### Risk

Low. No production callers exist; the change is contained to the
encryption primitive and its tests. Reviewers should still verify:

1. `aad:` parameter name matches `cryptography: 2.7.0`'s AesGcm API
2. AAD format `${collection}:${column}:${rowKey}` does not introduce
   ambiguity if any of the three contain `:` — currently safe because:
   - `collection` is a hardcoded Dart class name
   - `column` is a hardcoded field name
   - `rowKey` is a Cognito sub UUID or server UUID (no colons)
   - Document the constraint as a comment in `_aad`
3. The web stub's required parameters don't break any web-only test (the
   web stub previously had a different signature, but no test calls it
   directly — confirmed)

### Acceptance criteria

From `specs/features/isar-hardening/sprints.md#s1-01`:

- [x] `encrypt(plaintext, {required String collection, required String column, required String rowKey})`
- [x] `decrypt(value, {required String collection, required String column, required String rowKey})`
- [x] AAD = `utf8.encode('$collection:$column:$rowKey')`, passed to `AesGcm.encrypt/decrypt`
- [x] Old single-arg signatures removed — calls without AAD fail to compile
- [x] Web stub matches signature, returns input unchanged
- [x] `mobile/lib/core/database/field_encryptor.dart` ≤ 200 lines (current: ~175)

### Story-level definition of done

- [ ] Acceptance checklist above ticked
- [ ] PR merged to `main`
- [ ] `flutter test` green on CI
- [ ] Aikido security scan passing on `mobile/lib/core/database/**`
- [ ] No new lint warnings
- [ ] Linked from `specs/features/isar-hardening/sprints.md#s1-01` (story
      catalog auto-updated by next checkpoint)

---

## 3. Reviewer guide

The diff is small. Suggested review order:

1. **Read `field_encryptor.dart` start-to-finish.** Focus on:
   - The new `_aad` helper — is the format unambiguous?
   - Encrypt: AAD is constructed once and passed verbatim to AesGcm
   - Decrypt: AAD is constructed identically; mismatch surfaces as
     `SecretBoxAuthenticationError` → existing fail-open branch
2. **Verify `field_encryptor_web.dart` signature parity.** Both functions
   take the new named params and ignore them.
3. **Spot-check the test file.** The `AAD binding` group at the bottom is
   the key acceptance evidence. Confirm each test asserts that plaintext
   does NOT leak under AAD mismatch.

Look for the things this PR is **deliberately not doing** (they belong to
S1-02..S1-04 follow-ups, in this order):

| Symptom in this PR | Why it's left for S1-02+ |
|---|---|
| MAC failure returns ciphertext as-is, not throwing | S1-02 closes this with `FieldDecryptionException` |
| No version prefix on the ciphertext | S1-02 adds `enc:v1:` |
| Encrypt-before-init returns plaintext | S1-02 changes to `StateError` throw |
| No HMAC integrity tag on the persisted AES key | S1-03 |

If a reviewer requests that any of these be brought into this PR, push
back politely and reference the story catalog. Sprint discipline is the
only reason this roadmap fits in 14 weeks.

---

## 4. Rollback plan

The change adds required parameters to a public method that **has no
production callers** today. Rollback is a single `git revert` of the
merge commit. There are no schema changes, no migration, no DB writes.

If a downstream story (S1-02, S2-01) lands on top of this and depends on
the AAD signature, the revert cascade is: `git revert <S2-01-merge>` →
`git revert <S1-02-merge>` → `git revert <S1-01-merge>`. Each story is
designed to be independently revertible.

---

## 5. If the working tree is dirty

The S1-01 changes share the working tree with the documentation work
done earlier this session. To isolate this PR cleanly:

```bash
# Stash everything else first
git stash push -m "doc-work-in-progress" -- \
  STATE.md TASK.md \
  docs/mobile/isar-implementation.md \
  specs/features/isar-hardening/ \
  .github/ISSUE_TEMPLATE/

# Check working tree — should only have field_encryptor changes
git status --short
# Expect ONLY:
#   M mobile/lib/core/database/field_encryptor.dart
#   M mobile/lib/core/database/field_encryptor_web.dart
#   M mobile/test/core/database/field_encryptor_test.dart

# Now follow §1 from "Create the feature branch from main" onwards.
# After merging the PR, restore the docs stash on a separate branch:
git stash pop   # back on main or a docs branch
```

The two ed25519 key files (`id_ed25519_2`, `id_ed25519_2.pub`) at the repo
root are also untracked — **do not commit them.** They look like a developer
SSH key that ended up in the wrong place.

---

## 6. Open questions for the reviewer

1. **AAD format collisions.** I'm using `${collection}:${column}:${rowKey}`.
   If any field name contains `:` in the future, the binding becomes
   ambiguous (`User:phone:Number:sub-A` could parse two ways). Should I
   length-prefix each field, or is the conventional-naming rule (no
   colons in collection/column names) sufficient? Adding a small assert
   in `_aad` is cheap.

2. **Should the `rowKey` parameter forbid the Isar local autoincrement `id`?**
   The doc says it must be a stable identifier. Could add a runtime assert
   that `rowKey` is not a pure digit string ≤ 10 chars (cheap heuristic
   that catches `id.toString()` callers).

3. **Web stub `required` parameters.** Forces every caller to think about
   AAD on web even though it's a no-op. Pro: consistent API, harder to
   accidentally drop. Con: unnecessary boilerplate in web-specific code
   paths. Current: required. Worth a discussion.

---

*PR scaffold v1.0 — 2026-05-13*
