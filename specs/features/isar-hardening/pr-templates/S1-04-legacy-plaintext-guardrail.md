# PR Scaffold — S1-04: `legacyPlaintextAllowed` migration toggle guardrails

> **Status:** Design ready · **Depends on:** S1-01 + S1-02 merged
> **Sprint:** HS-1 · **Epic:** E1 — Encrypt at rest
> **Story catalog:** `specs/features/isar-hardening/sprints.md#s1-04`
> **Reviewer needed:** security + 1 mobile dev · **Story size:** 2 pts

This is a small, mostly-defensive PR that locks down the
`legacyPlaintextAllowed` migration toggle introduced in S1-02. The flag
itself already ships with S1-02 — this story adds the **guardrails** so
the flag cannot accidentally end up enabled in a production build.

---

## 0. TL;DR

The `legacyPlaintextAllowed: true` parameter is a one-shot escape hatch
intended for the HS-3 migration only. If a developer accidentally calls
`FieldEncryptor.initialize(key, legacyPlaintextAllowed: true)` outside
migration code, every read becomes a downgrade-attack vector again — the
opposite of what S1-02 just shipped.

This PR adds a CI lint that fails any PR setting the flag to `true`
outside an allow-listed file path, plus dedicated lifecycle tests proving
the flag is cleared by `reset()` and defaults to `false`.

---

## 1. How to ship this

```bash
# Prerequisite: S1-01 and S1-02 merged
git fetch origin
git checkout -b hs1/s1-04-legacy-flag-guardrails origin/main

# Apply the changes in §5:
#   - scripts/check_legacy_plaintext_flag.sh                (new)
#   - .github/workflows/lint.yml                            (extend)
#   - mobile/test/core/database/field_encryptor_test.dart   (add lifecycle tests)
#   - docs/mobile/isar-implementation.md §7                 (small clarifying edit)

cd mobile
flutter analyze
flutter test test/core/database/field_encryptor_test.dart
cd ..
bash scripts/check_legacy_plaintext_flag.sh        # should exit 0

git add scripts/check_legacy_plaintext_flag.sh \
        .github/workflows/lint.yml \
        mobile/test/core/database/field_encryptor_test.dart \
        docs/mobile/isar-implementation.md
git commit -m "$(cat <<'EOF'
ci(mobile): guardrails for legacyPlaintextAllowed migration flag

Adds a CI lint that fails any PR setting FieldEncryptor's
legacyPlaintextAllowed: true outside the migration allow-list, plus
lifecycle tests proving the flag defaults to false and is cleared by
reset(). The flag is a one-shot escape hatch for HS-3 — accidental
enablement in a production code path reopens the downgrade-attack
surface closed by S1-02.

Refs: specs/features/isar-hardening/sprints.md#s1-04
EOF
)"
git push -u origin hs1/s1-04-legacy-flag-guardrails
gh pr create --title "ci(mobile): guardrails for legacyPlaintextAllowed flag [S1-04]" \
             --body "$(cat specs/features/isar-hardening/pr-templates/S1-04-legacy-plaintext-guardrail.md | sed -n '/^## 2. PR description/,/^## 3. /p' | head -n -1)"
```

---

## 2. PR description

### Summary

Locks down `FieldEncryptor.initialize(..., legacyPlaintextAllowed: true)`
to a small allow-listed set of paths via CI lint, and adds three
lifecycle tests proving the flag's contract. No production code path
changes.

### Why

S1-02 introduced `legacyPlaintextAllowed` as a one-shot flag the HS-3
migration uses to read existing plaintext rows once before re-encrypting
them. The flag is correctly defaulted to `false` and is per-init scope
(no global state). But nothing today prevents a future contributor — or
a careless rebase — from calling it `true` in an unrelated code path.
"Flag flipped accidentally in production" is the failure mode the lint
closes.

### What changes

- **`scripts/check_legacy_plaintext_flag.sh`** (new): greps for
  `legacyPlaintextAllowed: true` (with whitespace variants) across
  `mobile/lib/` and `mobile/test/`. Exit 1 on any match outside the
  allow-list. Allow-list:
  - `mobile/lib/core/database/migrations/` — where HS-3's migration lives
  - `mobile/test/` — tests legitimately need to exercise both branches
- **`.github/workflows/lint.yml`**: extend existing lint job to run
  the new script. No new workflow; piggyback on the existing one so a
  failed lint blocks merge without adding queue time.
- **`mobile/test/core/database/field_encryptor_test.dart`**: three
  new tests in a `legacy flag lifecycle` group (see §5c).
- **`docs/mobile/isar-implementation.md`** §7 (small edit): explicit
  callout that the flag is enforced by CI lint, with a pointer to the
  allow-list.

### What does NOT change

- The flag itself, the prefix behavior, the AAD binding — all those
  already shipped in S1-01 and S1-02.
- HS-3's migration that actually uses the flag — that's S3-01.
- Any production code path.

### Backwards compatibility

No callable surface change. The CI lint catches new violations on PR
diff only; existing code is verified clean by running the script once
locally before merge.

### Test plan

```bash
bash scripts/check_legacy_plaintext_flag.sh         # main repo: exit 0
echo 'legacyPlaintextAllowed: true' > /tmp/bad.dart
cp /tmp/bad.dart mobile/lib/core/auth/                # synthetic violation
bash scripts/check_legacy_plaintext_flag.sh         # exit 1, flags the file
rm mobile/lib/core/auth/bad.dart

cd mobile && flutter test test/core/database/field_encryptor_test.dart
```

Expected: clean run → exit 0; synthetic violation → exit 1 with the
offending path printed; new lifecycle tests pass.

### Risk

Very low. The change is additive (a lint + tests) and touches no
runtime code.

The one place to look carefully: the lint regex must match all valid
Dart formatting:

- `legacyPlaintextAllowed: true`
- `legacyPlaintextAllowed:  true`
- `legacyPlaintextAllowed:true`
- `legacyPlaintextAllowed :true`
- Multi-line argument lists where `true` is on the next line

The script handles the first three via a robust regex; the multi-line
case is harder. PO call: accept that a contributor who deliberately
formats `legacyPlaintextAllowed:\n    true` to evade the lint is acting
in bad faith and is out of scope for this guardrail. The security
reviewer's eyes catch that case in code review.

### Acceptance criteria

From `specs/features/isar-hardening/sprints.md#s1-04`:

- [x] `initialize(List<int> keyBytes, {bool legacyPlaintextAllowed = false})` — already shipped in S1-02
- [x] Default `false` — production reads fail closed — already shipped
- [x] When `true`, `decrypt(value, ...)` with missing prefix returns `value` unchanged — already shipped
- [x] `reset()` clears the flag — already shipped
- [x] Doc comment: "MIGRATION ONLY — production reads must run with `false`" — already in S1-02
- [ ] **NEW:** CI lint flags violations outside the allow-list
- [ ] **NEW:** Three lifecycle tests prove the flag's contract

---

## 3. Reviewer guide

Look at the lint script carefully — it's the load-bearing piece.

1. **Allow-list discipline.** Confirm the allow-list paths exist and
   are intentional. Avoid adding broad patterns (`migrations/**` is OK;
   `**/migration*` is not — too easy to abuse).
2. **Regex robustness.** Test the script against a synthetic file
   containing all four formatting variants. The script should catch
   single-line forms cleanly; document multi-line as an explicit
   non-goal.
3. **Lifecycle tests.** Read each new test top-to-bottom; confirm they
   exercise the flag's full state space (default, set, reset, re-set).

---

## 4. Rollback plan

`git revert <merge-commit>`. The lint comes out of CI; the lifecycle
tests disappear. No production code change to unwind.

---

## 5. Proposed diffs

### 5a. `scripts/check_legacy_plaintext_flag.sh` (new file)

```bash
#!/usr/bin/env bash
# scripts/check_legacy_plaintext_flag.sh
#
# Refuses any source-tree state where FieldEncryptor's
# `legacyPlaintextAllowed: true` appears outside the migration allow-list.
#
# Exit 0: clean
# Exit 1: violations found (paths printed to stderr)
#
# Caveats:
# - Catches single-line formatting variants only. A determined contributor
#   could split `legacyPlaintextAllowed:\n    true` across lines to evade
#   detection. That requires bad-faith editing which is out of scope for
#   this guardrail — code-review catches it.
# - Skips the field_encryptor.dart source file itself, where the parameter
#   is *declared*, and the allow-listed paths below.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

# Files allowed to set the flag to true.
ALLOW_LIST=(
  "mobile/lib/core/database/migrations/"
  "mobile/test/"
)

# Build a grep --exclude-dir argument list from the allow-list.
EXCLUDE_ARGS=()
for path in "${ALLOW_LIST[@]}"; do
  EXCLUDE_ARGS+=(--exclude-dir="$(basename "$path")")
done

# Also skip the file where the parameter is declared.
EXCLUDE_ARGS+=(--exclude="field_encryptor.dart")
EXCLUDE_ARGS+=(--exclude="field_encryptor_web.dart")

# Catch:
#   legacyPlaintextAllowed: true
#   legacyPlaintextAllowed:  true
#   legacyPlaintextAllowed:true
#   legacyPlaintextAllowed :true
PATTERN='legacyPlaintextAllowed\s*:\s*true'

cd "$REPO_ROOT"

# shellcheck disable=SC2068
VIOLATIONS=$(grep -rnE \
  "${EXCLUDE_ARGS[@]}" \
  --include="*.dart" \
  "$PATTERN" \
  mobile/lib mobile/test 2>/dev/null || true)

if [[ -n "$VIOLATIONS" ]]; then
  echo "✗ legacyPlaintextAllowed: true found outside the allow-list:" >&2
  echo "" >&2
  echo "$VIOLATIONS" >&2
  echo "" >&2
  echo "Allowed paths:" >&2
  for path in "${ALLOW_LIST[@]}"; do
    echo "  - $path" >&2
  done
  echo "" >&2
  echo "If this is the HS-3 migration, place the call under" >&2
  echo "mobile/lib/core/database/migrations/. If this is a test, place" >&2
  echo "it under mobile/test/. Otherwise it's a security regression." >&2
  exit 1
fi

echo "✓ legacyPlaintextAllowed: true not used outside the allow-list."
exit 0
```

Permissions: `chmod +x scripts/check_legacy_plaintext_flag.sh`.

### 5b. `.github/workflows/lint.yml` (extend existing)

```diff
 # ... existing lint workflow content ...
     - name: Run dart analyze
       run: |
         cd mobile
         flutter analyze --fatal-infos --fatal-warnings

+    - name: Guard legacyPlaintextAllowed flag
+      run: bash scripts/check_legacy_plaintext_flag.sh
+
     - name: Aikido security scan
       # ... existing content ...
```

If the project has no `lint.yml` workflow yet, create a minimal one with
the standard `ubuntu-latest` + `subosito/flutter-action@v2` setup and
add the guard step.

### 5c. `mobile/test/core/database/field_encryptor_test.dart` (additions)

Add a new test group near the end of the file:

```dart
group('legacy flag lifecycle', () {
  test('defaults to false on initialize() without the named arg', () async {
    encryptor.initialize(generateKey());
    // Indirect proof: an unprefixed input must throw.
    expect(
      () => encryptor.decrypt('not-a-cipher-blob',
          collection: aadCollection, column: aadColumn, rowKey: aadRowKey),
      throwsA(isA<FieldDecryptionException>()),
    );
  });

  test('reset() clears the flag — subsequent initialize defaults to false',
      () async {
    encryptor.initialize(generateKey(), legacyPlaintextAllowed: true);
    // First init: legacy allowed.
    expect(
      await encryptor.decrypt('legacy-plain',
          collection: aadCollection, column: aadColumn, rowKey: aadRowKey),
      equals('legacy-plain'),
    );

    encryptor.reset();
    encryptor.initialize(generateKey());     // no flag → default false

    // Now legacy reads must throw.
    expect(
      () => encryptor.decrypt('legacy-plain',
          collection: aadCollection, column: aadColumn, rowKey: aadRowKey),
      throwsA(isA<FieldDecryptionException>()),
    );
  });

  test('flag is per-init: re-init with false closes the door', () async {
    final key = generateKey();
    encryptor.initialize(key, legacyPlaintextAllowed: true);
    encryptor.reset();
    encryptor.initialize(key, legacyPlaintextAllowed: false);

    expect(
      () => encryptor.decrypt('legacy-plain',
          collection: aadCollection, column: aadColumn, rowKey: aadRowKey),
      throwsA(isA<FieldDecryptionException>()),
    );
  });
});
```

### 5d. `docs/mobile/isar-implementation.md` §7 (small edit)

```diff
 ### Key-integrity HMAC on read-back

 [... existing content ...]

+**Enforcement.** The `legacyPlaintextAllowed: true` flag is enforced by a
+CI lint (`scripts/check_legacy_plaintext_flag.sh`). Setting it outside
+the migration allow-list (`mobile/lib/core/database/migrations/` or
+`mobile/test/`) fails the PR. Reviewers should still eyeball multi-line
+formatting variants the lint can't catch.
```

---

## 6. Open questions for the reviewer

1. **Should the allow-list include `mobile/lib/core/database/isar_service.dart`?**
   The init path that runs the HS-3 migration might want to pass the flag
   from there. Current default: no — the migration entry point should
   live under `migrations/`. Reviewer pushback welcome.

2. **Should the lint be a `pre-commit` hook too?** Adding pre-commit catches
   the violation before push at the cost of one more tool dependency. PO
   call: CI-only for now; pre-commit if violations recur.

3. **Multi-line evasion.** Documented as out-of-scope. If we want to close
   it: switch to a Dart-aware AST check via `dart_style` or `analyzer`. Cost:
   ~half a sprint of tooling work. Not worth it for a bad-faith threat model.

---

*PR scaffold v1.0 — 2026-05-13 — depends on S1-01 + S1-02 merged*
