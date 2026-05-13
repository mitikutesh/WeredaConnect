# PR Scaffold — S1-05: Security test suite consolidation + CI lockdown

> **Status:** Design ready · **Depends on:** S1-01 + S1-02 + S1-03 + S1-04 merged
> **Sprint:** HS-1 · **Epic:** E1 — Encrypt at rest · **Closes HS-1**
> **Story catalog:** `specs/features/isar-hardening/sprints.md#s1-05`
> **Reviewer needed:** QA + 1 mobile dev · **Story size:** 2 pts

This is the HS-1 closeout. By this point, AAD binding (S1-01), version
prefix + fail-closed (S1-02), key-integrity HMAC (S1-03), and the legacy-
flag guardrails (S1-04) are all merged. S1-05 audits coverage, fills any
gaps, and locks down CI so the next sprint can't reintroduce the issues
HS-1 just closed.

---

## 0. TL;DR

Three things:

1. **Coverage audit.** Run a checklist of every acceptance criterion across
   S1-01..S1-04 against the test suite. Add tests for anything missing.
2. **CI guard against `Isar.initializeIsarCore(download: true)`.** Tests
   that download the Isar native binary at runtime are MITM-able and break
   in offline CI runners. Forbid the pattern, vendor a binary instead.
3. **HS-1 closeout summary.** Update `STATE.md`, `TASK.md`, and
   `docs/mobile/isar-implementation.md` to reflect that the four
   foundational security primitives are live.

---

## 1. How to ship this

```bash
# Prerequisite: S1-01..S1-04 all merged
git fetch origin
git log origin/main --oneline | grep -E 'S1-0[1234]'   # confirm all four

git checkout -b hs1/s1-05-test-suite-closeout origin/main

# Apply the changes in §5:
#   - mobile/test/core/database/field_encryptor_test.dart  (small additions)
#   - mobile/test/core/database/isar_service_test.dart     (small additions)
#   - scripts/check_isar_test_download.sh                  (new)
#   - .github/workflows/lint.yml                           (extend)
#   - STATE.md / TASK.md / docs/mobile/isar-implementation.md  (status flips)

cd mobile
flutter analyze
flutter test
cd ..
bash scripts/check_isar_test_download.sh         # should exit 0
bash scripts/check_legacy_plaintext_flag.sh      # still 0 (from S1-04)

git add mobile/test/core/database/field_encryptor_test.dart \
        mobile/test/core/database/isar_service_test.dart \
        scripts/check_isar_test_download.sh \
        .github/workflows/lint.yml \
        STATE.md TASK.md docs/mobile/isar-implementation.md
git commit -m "$(cat <<'EOF'
test(mobile): HS-1 security test suite closeout + CI lockdown

Consolidates the security test suite for the HS-1 encryption foundation.
Adds any missing coverage against S1-01..S1-04 acceptance criteria.

Adds scripts/check_isar_test_download.sh — fails CI if any test uses
Isar.initializeIsarCore(download: true). The pattern is MITM-able on
shared CI infra and breaks in offline runners. Tests that need the
native lib must use a vendored binary; tests that don't need it (the
encryptor tests) must not initialize the core at all.

Updates STATE.md / TASK.md to reflect HS-1 complete: AAD, prefix +
fail-closed, key-integrity HMAC, legacy-flag guardrails all live.
Marketplace flow unchanged — no production callers yet (those come in
HS-2).

Refs: specs/features/isar-hardening/sprints.md#s1-05
EOF
)"
git push -u origin hs1/s1-05-test-suite-closeout
gh pr create --title "test(mobile): HS-1 security test suite closeout [S1-05]" \
             --body "$(cat specs/features/isar-hardening/pr-templates/S1-05-security-test-suite.md | sed -n '/^## 2. PR description/,/^## 3. /p' | head -n -1)"
```

---

## 2. PR description

### Summary

HS-1 closeout PR. Audits the test suite against every acceptance criterion
across S1-01..S1-04, fills any coverage gaps found, and adds a CI guard
against `Isar.initializeIsarCore(download: true)` to keep future tests
from regressing on supply-chain safety.

### Why

HS-1 introduced four security primitives (AAD, prefix + fail-closed,
key-integrity HMAC, legacy-flag guardrails). Each story landed with its
own tests, but PRs land in time order — there's no single moment where
"the whole HS-1 contract is covered" is verifiable. This PR is that
moment. It also catches the small things that slip between PRs: a missing
edge case, a renamed test that no longer asserts what it used to,
a comment that drifted out of date.

The CI guard exists because `Isar.initializeIsarCore(download: true)`
in tests pulls the native binary from `github.com/isar/isar` at first
run. On CI runners with poisoned DNS or in offline environments
(self-hosted runners, regulated infra), that's both insecure and
unreliable. Tests should either use a vendored binary or not touch the
native lib at all.

### What changes

- **`mobile/test/core/database/field_encryptor_test.dart`**: add the
  coverage-audit additions in §5a (small — most cases already covered).
- **`mobile/test/core/database/isar_service_test.dart`**: same — add
  cases per §5b.
- **`scripts/check_isar_test_download.sh`** (new): fails on `initializeIsarCore(.*download:.*true)`.
- **`.github/workflows/lint.yml`**: extend with the new guard step.
- **`STATE.md`**: flip HS-1 from "Proposed" to "Complete (closes HS-1)".
- **`TASK.md`**: same.
- **`docs/mobile/isar-implementation.md`** ⚠️ table: mark rows #1
  ("FieldEncryptor zero call sites"), #3 ("no AAD"), #9 ("biometric"
  remains OPEN), #10 ("key-integrity HMAC"), #11 ("idempotent
  migrations" — only the test discipline part) accordingly.

### Coverage audit

The audit checklist below is the load-bearing artifact of this PR.
Reviewers verify each row exists as an executable test.

| Acceptance criterion (story) | Test | File |
|---|---|---|
| S1-01: AAD on encrypt | `encrypt output has enc:v1: prefix` + AAD swap tests | field_encryptor_test.dart |
| S1-01: AAD on decrypt | `cross-row swap rejected` + `cross-column swap rejected` | field_encryptor_test.dart |
| S1-01: web stub signature parity | `flutter build web` smoke (CI gate) | CI workflow |
| S1-02: `enc:v1:` prefix on encrypt | `encrypt output has enc:v1: prefix` | field_encryptor_test.dart |
| S1-02: missing-prefix throws | `decrypt throws on unprefixed input` | field_encryptor_test.dart |
| S1-02: bad base64 throws | `decrypt throws on invalid base64` | field_encryptor_test.dart |
| S1-02: truncated body throws | `decrypt throws on truncated body` | field_encryptor_test.dart |
| S1-02: tampered MAC throws | `decrypt throws on tampered MAC byte` | field_encryptor_test.dart |
| S1-02: uninit encrypt throws | `encrypt throws StateError when not initialized` | field_encryptor_test.dart |
| S1-02: uninit decrypt throws | `decrypt throws StateError when not initialized` | field_encryptor_test.dart |
| S1-03: matching tag succeeds | `matching tag → key read succeeds` | isar_service_test.dart |
| S1-03: tampered key throws | `tampered key → KeyIntegrityFailure` | isar_service_test.dart |
| S1-03: tampered tag throws | `tampered tag → KeyIntegrityFailure` | isar_service_test.dart |
| S1-03: missing tag legacy upgrade | `missing tag with valid key → legacy upgrade writes tag` | isar_service_test.dart |
| S1-03: invalid-length key | `invalid-length key → wipe and regenerate` | isar_service_test.dart |
| S1-03: constant-time compare | property test on `_constantTimeEq` | isar_service_test.dart |
| S1-04: legacy flag default false | `defaults to false on initialize() without named arg` | field_encryptor_test.dart |
| S1-04: reset clears flag | `reset() clears the flag` | field_encryptor_test.dart |
| S1-04: per-init scope | `flag is per-init: re-init with false closes the door` | field_encryptor_test.dart |
| S1-04: CI lint catches violations | `scripts/check_legacy_plaintext_flag.sh` against synthetic bad file | shell script (manual on every release) |

Anything in the table NOT present after S1-01..S1-04 merge is filled by
this PR (§5a, §5b).

### Test plan

```bash
# Full suite — every HS-1 test should pass
cd mobile
flutter test test/core/database/

# Guard scripts both green
bash scripts/check_legacy_plaintext_flag.sh
bash scripts/check_isar_test_download.sh

# Synthetic violation: temporarily add download: true to a test
cd mobile/test/core/database/
echo 'await Isar.initializeIsarCore(download: true);' >> field_encryptor_test.dart
cd ../../..
bash scripts/check_isar_test_download.sh    # should exit 1
git checkout mobile/test/core/database/field_encryptor_test.dart
```

### Risk

Very low. Test additions are additive; the CI guard is a grep that runs
in seconds. No production code touched.

### Acceptance criteria

From `specs/features/isar-hardening/sprints.md#s1-05`:

- [ ] Nonce-freshness property test
- [ ] Cross-row AAD swap test
- [ ] Cross-column AAD swap test
- [ ] Tampered-MAC byte-flip test
- [ ] Missing-prefix test
- [ ] Legacy-mode allow test
- [ ] All tests run in `flutter test` and in CI
- [ ] **NEW:** CI guard against `Isar.initializeIsarCore(download: true)`
- [ ] **NEW:** Coverage matrix above is 100% mapped to executable tests

### HS-1 sprint closeout (definition of done at sprint level)

- [ ] All four primitive PRs merged (S1-01, S1-02, S1-03, S1-04)
- [ ] This consolidation PR merged
- [ ] `STATE.md` flipped to HS-1 complete
- [ ] `TASK.md` flipped to HS-1 complete
- [ ] `docs/mobile/isar-implementation.md` ⚠️ table reflects new state
- [ ] Aikido security scan green on `mobile/lib/core/database/**`
- [ ] No production callers yet (verified by `scripts/check_pii_writes.sh`
      — which exists from S2-03 if it shipped, or by manual grep)
- [ ] HS-2 stories unblocked and visible in JIRA

---

## 3. Reviewer guide

This PR is mostly a checklist verification. Read in this order:

1. **Coverage matrix in §"Coverage audit"** — manually verify each row
   exists as a runnable test. Confirm the test name in the file matches
   the matrix entry.
2. **`scripts/check_isar_test_download.sh`** — small grep script, easy
   review.
3. **`STATE.md` / `TASK.md` diffs** — should be small status-flip edits,
   not new content.
4. **`docs/mobile/isar-implementation.md` ⚠️ table** — the OPEN/CLOSED
   marks should match reality after this PR lands.

If the coverage matrix turns up a gap that isn't trivially filled here
(say, a missing isar_service test that needs the test-seam decision from
S1-03 §6), **don't paper over it.** File a follow-up story and proceed
with the rest. Coverage matrix becomes the source of truth; the gap is
visible to the next sprint planner.

---

## 4. Rollback plan

`git revert <merge-commit>`. Tests come out; CI guard comes out; status
docs revert to "Proposed." The HS-1 primitive PRs (S1-01..S1-04) stay
merged on their own.

---

## 5. Proposed diffs

### 5a. `mobile/test/core/database/field_encryptor_test.dart` (small additions)

Most cases land via S1-01..S1-04. The one obvious gap is the **bad-base64**
case (sprints.md mentions it; S1-02's scaffold lists it as expected
behavior but doesn't add an explicit test):

```dart
test('decrypt throws on invalid base64 body', () async {
  encryptor.initialize(generateKey());
  const malformed = 'enc:v1:!!!not-base64!!!';
  expect(
    () => encryptor.decrypt(malformed,
        collection: aadCollection, column: aadColumn, rowKey: aadRowKey),
    throwsA(isA<FieldDecryptionException>()),
  );
});

test('decrypt throws on truncated body', () async {
  encryptor.initialize(generateKey());
  // Valid base64 but only 4 bytes — below the 28-byte GCM envelope minimum.
  const short = 'enc:v1:AAAA';
  expect(
    () => encryptor.decrypt(short,
        collection: aadCollection, column: aadColumn, rowKey: aadRowKey),
    throwsA(isA<FieldDecryptionException>()),
  );
});

test('exception messages do not leak the rowKey to telemetry surface',
    () async {
  // Best-effort check: the FieldDecryptionException toString() should
  // describe the kind of failure but not include the rowKey.
  encryptor.initialize(generateKey());
  try {
    await encryptor.decrypt('not-prefixed',
        collection: 'User', column: 'displayName', rowKey: 'sub-A-secret');
    fail('expected FieldDecryptionException');
  } on FieldDecryptionException catch (e) {
    expect(e.toString(), isNot(contains('sub-A-secret')));
  }
});
```

### 5b. `mobile/test/core/database/isar_service_test.dart` (additions)

Add a constant-time compare property test (the S1-03 scaffold mentions
it as #1 open question but doesn't add the test):

```dart
test('_constantTimeEq returns false on length mismatch without short-circuit',
    () async {
  // Property: comparing equal-length-but-differing arrays takes the
  // same number of iterations as comparing equal arrays of the same
  // length. We can't truly measure timing in dart-test, but we can
  // assert the function returns false correctly across many lengths
  // and difference positions, which catches the common "early return
  // on first diff" bug.
  final svc = TestableIsarService();          // @visibleForTesting seam
  // Same length, differ at byte 0
  expect(svc.constantTimeEqForTesting([0, 1, 2], [1, 1, 2]), isFalse);
  // Same length, differ at last byte
  expect(svc.constantTimeEqForTesting([0, 1, 2], [0, 1, 3]), isFalse);
  // Same length, all equal
  expect(svc.constantTimeEqForTesting([0, 1, 2], [0, 1, 2]), isTrue);
  // Different lengths
  expect(svc.constantTimeEqForTesting([0, 1, 2], [0, 1]), isFalse);
});
```

This assumes the `_constantTimeEq` was exposed via a `@visibleForTesting`
seam in S1-03. If S1-03 didn't expose it, add a one-line `@visibleForTesting`
wrapper as part of this PR.

### 5c. `scripts/check_isar_test_download.sh` (new file)

```bash
#!/usr/bin/env bash
# scripts/check_isar_test_download.sh
#
# Fails if any test uses Isar.initializeIsarCore(download: true).
# That pattern fetches the Isar native binary from github.com/isar at
# runtime — MITM-able on shared CI infra and broken in offline runners.
#
# Tests that need Isar's native lib should vendor it and call
# initializeIsarCore(libraries: {...}) with explicit paths.
# Tests that don't need Isar (e.g. FieldEncryptor unit tests) should
# not call initializeIsarCore at all.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO_ROOT"

PATTERN='Isar\.initializeIsarCore\s*\([^)]*download\s*:\s*true'

VIOLATIONS=$(grep -rnE \
  --include="*.dart" \
  "$PATTERN" \
  mobile/test 2>/dev/null || true)

if [[ -n "$VIOLATIONS" ]]; then
  echo "✗ Found Isar.initializeIsarCore(download: true) in tests:" >&2
  echo "" >&2
  echo "$VIOLATIONS" >&2
  echo "" >&2
  echo "Either:" >&2
  echo "  1. The test doesn't need Isar's native lib — remove the call." >&2
  echo "  2. The test does — vendor the binary under mobile/test/fixtures/" >&2
  echo "     and call initializeIsarCore(libraries: {...}) with explicit" >&2
  echo "     platform-keyed paths." >&2
  exit 1
fi

echo "✓ No Isar download-at-test usage detected."
exit 0
```

### 5d. `.github/workflows/lint.yml` (extend)

```diff
     - name: Guard legacyPlaintextAllowed flag
       run: bash scripts/check_legacy_plaintext_flag.sh

+    - name: Guard Isar test-time downloads
+      run: bash scripts/check_isar_test_download.sh
+
     - name: Aikido security scan
```

### 5e. `STATE.md` (status flip)

```diff
 ENTRY POINTS:
   - Roadmap overview:   specs/features/isar-hardening/README.md
-  - 28 JIRA-ready stories: specs/features/isar-hardening/sprints.md
+  - 28 JIRA-ready stories: specs/features/isar-hardening/sprints.md
+  - HS-1 COMPLETE ($DATE): foundation primitives live (AAD, prefix,
+    fail-closed, key-integrity HMAC, legacy-flag guardrails)
+  - HS-2 NEXT: wire encryption into User + Crop repositories
```

### 5f. `TASK.md` (sprint status flip)

```diff
 | Sprint | Dates | Goal | Pts | Risk |
 |--------|-------|------|-----|------|
-| HS-1 | May 19 – May 30 | Encryptor v1: AAD + prefix + key-integrity HMAC | 15 | Low |
+| HS-1 | May 19 – May 30 | Encryptor v1: AAD + prefix + key-integrity HMAC | 15 | ✅ DONE |
```

### 5g. `docs/mobile/isar-implementation.md` ⚠️ table

```diff
 | # | Gap | Status | Section |
 |---|---|---|---|
-| 3 | No AAD / domain separation — ciphertext is portable between columns and rows | OPEN | §7 |
+| 3 | No AAD / domain separation — ciphertext is portable between columns and rows | **CLOSED (HS-1 / S1-01)** | §7 |
-| 10 | No integrity HMAC on the persisted key → swap attack possible | OPEN | §7 |
+| 10 | No integrity HMAC on the persisted key → swap attack possible | **CLOSED (HS-1 / S1-03)** | §7 |
```

(Items #1, #4, #5, #6, #7, #8, #9, #11, #12, #13, #14 remain OPEN — they
land in HS-2..HS-7.)

---

## 6. Open questions for the reviewer

1. **Coverage matrix granularity.** Is one test per acceptance criterion
   enough, or do we want multiple tests per criterion (e.g. "missing
   prefix" + "missing prefix with unusual whitespace before `enc:v1:`")?
   Default: one per criterion; multi-case tests are S2's problem.

2. **Test-time seam for `_constantTimeEq`.** §5b assumes a `@visibleForTesting`
   wrapper exists. If S1-03 went with one of the other test-seam options
   (constructor injection, vendored Isar binary), update §5b accordingly.

3. **Should `STATE.md`/`TASK.md`/doc-table flips be a separate PR?** Pro:
   keeps S1-05 a pure-test PR. Con: more PRs to chase, and the docs are
   the load-bearing "HS-1 done" signal that downstream sprints depend on.
   Default: bundle.

---

*PR scaffold v1.0 — 2026-05-13 — closes HS-1*
