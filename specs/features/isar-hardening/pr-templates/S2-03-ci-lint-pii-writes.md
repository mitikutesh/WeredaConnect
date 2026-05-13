# PR Scaffold — S2-03: CI lint — no plaintext PII writes

> **Status:** Design ready · **Depends on:** — (independent of S2-01..S2-02)
> **Sprint:** HS-2 · **Epic:** E1 — Encrypt at rest
> **Story catalog:** `specs/features/isar-hardening/sprints.md#s2-03`
> **Reviewer needed:** security + 1 mobile dev · **Story size:** 3 pts

A defensive PR. Adds a CI grep that fails any PR introducing a new
plaintext PII write — i.e., a `late String phoneNumber|faydaId|fullName`
declaration immediately followed (within a few lines) by an `.put(`
call that doesn't first encrypt the field. Doubles as a long-term
guarantee that what HS-1 + S2-01 + S2-02 close stays closed.

---

## 0. TL;DR

`scripts/check_pii_writes.sh` — coarse, false-positive-prone grep that
catches the regression pattern "added a PII field and didn't wrap the
write." Allow-list comment `// @pii-encrypted-at-write` lets reviewers
mark intentional plaintext writes (none should exist in production
code; the comment is for tests and migrations).

Wired into the same `lint.yml` workflow that runs the S1-04 flag
guardrail.

---

## 1. How to ship this

```bash
git fetch origin
git checkout -b hs2/s2-03-ci-lint-pii-writes origin/main

# Apply changes in §5:
#   - scripts/check_pii_writes.sh                 (new)
#   - .github/workflows/lint.yml                  (extend)
#   - mobile/test/fixtures/lint/bad_example.dart  (test fixture)
#   - mobile/test/fixtures/lint/good_example.dart (test fixture)

bash scripts/check_pii_writes.sh            # against the live repo: exit 0
bash scripts/check_pii_writes.sh --self-test    # against fixtures: exit 0

git add scripts/check_pii_writes.sh \
        .github/workflows/lint.yml \
        mobile/test/fixtures/lint/
git commit -m "ci(mobile): lint that catches new plaintext PII writes"
git push -u origin hs2/s2-03-ci-lint-pii-writes
gh pr create --title "ci(mobile): lint new plaintext PII writes [S2-03]"
```

---

## 2. PR description

### Summary

Adds `scripts/check_pii_writes.sh` and wires it into CI. The script
flags two patterns in `mobile/lib/`:

1. **Schema regression** — a `late String <pii_field>` declaration that
   isn't either (a) one of the known-encrypted columns from S2-01/S2-02
   or (b) annotated `// @pii-encrypted-at-write`.

2. **Caller regression** — a `.put(<user_like_obj>)` call that doesn't
   have an `await ...encrypt(...)` call on the same object within the
   preceding 20 lines.

False positives are common with grep-based linting. The escape hatch
is the inline comment annotation; the script documents it explicitly
and the failure message points to the doc.

### Why

`docs/mobile/isar-implementation.md` ⚠️ row #1 was "FieldEncryptor has
zero call sites." HS-1 + S2-01 + S2-02 close that for User and (in
S2-04) CropListing. This script is the **anti-regression** that
prevents the gap from reopening in HS-3+ work or in unrelated future
features.

### What changes

- **`scripts/check_pii_writes.sh`** (new): the grep-based checker
- **`.github/workflows/lint.yml`** (extend): one new step, runs the script
- **`mobile/test/fixtures/lint/bad_example.dart`** (new): a synthetic
  file that the script must flag — self-test
- **`mobile/test/fixtures/lint/good_example.dart`** (new): a synthetic
  file that the script must NOT flag — self-test
- A `--self-test` mode on the script that runs both fixtures and asserts
  the right exit codes

### What does NOT change

- No production code, no schema, no tests outside the lint fixtures.

### Test plan

```bash
bash scripts/check_pii_writes.sh                # main repo: exit 0
bash scripts/check_pii_writes.sh --self-test    # fixtures: exit 0 (both pass)

# Synthetic violation
mkdir -p /tmp/test-bad
cat > /tmp/test-bad/violation.dart <<'EOF'
class FakeSchema {
  late String phoneNumber;
}
Future<void> badWrite(FakeSchema s) async {
  await isar.writeTxn(() => isar.fakeSchemas.put(s));
}
EOF
bash scripts/check_pii_writes.sh /tmp/test-bad      # exit 1
```

### Risk

Low. The script is read-only, runs in seconds, and the escape-hatch
comment means no legitimate code path is permanently blocked.

The medium part: false positives. The escape-hatch comment is documented;
reviewers must use it sparingly and only on lines that genuinely need
it (test setup, migration helpers).

### Acceptance criteria

From `specs/features/isar-hardening/sprints.md#s2-03`:

- [ ] Script greps `mobile/lib/` for the offending pattern
- [ ] Exits 1 on match, prints offending file:line
- [ ] Allow-list: declarations annotated with `// @pii-encrypted-at-write` are skipped
- [ ] Runs on every PR via existing CI workflow
- [ ] False-positive escape hatch documented

---

## 3. Reviewer guide

Two things to check:

1. **The grep pattern is appropriately conservative.** The script
   should catch the obvious "new PII field added without encryption"
   case but not false-positive on every test setup. The `--self-test`
   mode exists to verify this against the two fixture files. Read the
   fixtures.

2. **The escape hatch is exactly one comment form.** `//
   @pii-encrypted-at-write` — nothing else. Resist requests for
   variants like `// pii-ok` or block comments. One form keeps the
   lint regex simple and the audit surface small.

---

## 4. Rollback plan

`git revert <merge-commit>`. The lint comes out of CI; the fixtures
disappear. No production code change.

---

## 5. Proposed diffs

### 5a. `scripts/check_pii_writes.sh` (new)

```bash
#!/usr/bin/env bash
# scripts/check_pii_writes.sh
#
# Lints for two regression patterns:
#   1. Schema:  a `late String <pii_field>` declaration without
#               // @pii-encrypted-at-write annotation
#   2. Caller:  a `.put(<obj>)` call that doesn't have an
#               `encrypt(` call on the same object within 20 lines above
#
# Allow-list:
#   - mobile/lib/core/database/schemas/         (declarations themselves;
#                                                handled by the comment annotation)
#   - mobile/lib/core/database/migrations/      (migrations may write legacy)
#   - mobile/test/                              (tests may bypass)
#
# Usage:
#   bash scripts/check_pii_writes.sh           # lint the repo
#   bash scripts/check_pii_writes.sh --self-test    # run fixture tests

set -euo pipefail

PII_FIELDS=(phoneNumber faydaId fullName displayName farmerName)

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SCAN_PATH="${1:-$REPO_ROOT/mobile/lib}"

if [[ "${1:-}" == "--self-test" ]]; then
  # Test fixtures live under mobile/test/fixtures/lint/
  echo "→ Testing good_example.dart (should pass)..."
  bash "$0" "$REPO_ROOT/mobile/test/fixtures/lint/good_example.dart" \
      && echo "  ✓ good_example.dart passed" \
      || { echo "  ✗ good_example.dart FAILED — script flagged a clean file" >&2; exit 1; }

  echo "→ Testing bad_example.dart (should fail)..."
  if bash "$0" "$REPO_ROOT/mobile/test/fixtures/lint/bad_example.dart"; then
    echo "  ✗ bad_example.dart FAILED — script missed a violation" >&2; exit 1
  else
    echo "  ✓ bad_example.dart correctly flagged"
  fi
  echo "✓ Self-test passed."
  exit 0
fi

VIOLATIONS=0

# Pattern 1: PII field declaration without annotation
for field in "${PII_FIELDS[@]}"; do
  # Only match `late String <field>` (or `String? <field>`)
  matches=$(grep -rnE \
    --include="*.dart" \
    --exclude-dir=migrations \
    "(late String|String\?)\s+${field}\s*;?" \
    "$SCAN_PATH" 2>/dev/null \
    | grep -v '@pii-encrypted-at-write' || true)

  if [[ -n "$matches" ]]; then
    # Filter: skip if the file is the schema declaration itself
    # (schemas/user.dart declares the columns; that's not a violation)
    matches=$(echo "$matches" | grep -v "/schemas/" || true)
  fi

  if [[ -n "$matches" ]]; then
    echo "✗ $field declared without @pii-encrypted-at-write:" >&2
    echo "$matches" >&2
    VIOLATIONS=$((VIOLATIONS + 1))
  fi
done

# Pattern 2: write without encrypt — best-effort. Looks for users/crops
# .put() calls and checks the preceding 20 lines for an encrypt( call.
# Skip migrations and tests.
PUT_PATTERN='\.(users|cropListings|trips|orders|deliverys|payments|paymentIntents|buyerIntents)\.put\('
matches=$(grep -rnE \
  --include="*.dart" \
  --exclude-dir=migrations \
  --exclude-dir=test \
  "$PUT_PATTERN" \
  "$SCAN_PATH" 2>/dev/null || true)

while IFS= read -r match_line; do
  [[ -z "$match_line" ]] && continue
  file=$(echo "$match_line" | cut -d: -f1)
  lineno=$(echo "$match_line" | cut -d: -f2)
  start=$((lineno - 20))
  start=$((start < 1 ? 1 : start))
  context=$(sed -n "${start},${lineno}p" "$file")
  if ! echo "$context" | grep -q 'encrypt('; then
    echo "✗ $file:$lineno — .put() without encrypt() in preceding 20 lines:" >&2
    echo "    $(sed -n "${lineno}p" "$file")" >&2
    VIOLATIONS=$((VIOLATIONS + 1))
  fi
done <<< "$matches"

if (( VIOLATIONS > 0 )); then
  echo "" >&2
  echo "$VIOLATIONS PII-write violation(s) found." >&2
  echo "" >&2
  echo "If your code is intentionally writing plaintext (test setup," >&2
  echo "migration, etc.), add the comment '// @pii-encrypted-at-write'" >&2
  echo "to the field declaration. See docs/mobile/isar-implementation.md §7." >&2
  exit 1
fi

echo "✓ No plaintext PII write violations detected."
exit 0
```

### 5b. `mobile/test/fixtures/lint/good_example.dart` (new)

```dart
// Self-test fixture for scripts/check_pii_writes.sh.
// This file MUST pass the lint.

class User {
  /// @pii-encrypted-at-write
  late String displayName;
}

Future<void> save(User u, FieldEncryptor enc, Isar isar) async {
  u.displayName = await enc.encrypt(u.displayName,
      collection: 'User', column: 'displayName', rowKey: 'sub-A');
  await isar.writeTxn(() => isar.users.put(u));
}
```

### 5c. `mobile/test/fixtures/lint/bad_example.dart` (new)

```dart
// Self-test fixture for scripts/check_pii_writes.sh.
// This file MUST be flagged by the lint.

class User {
  // INTENTIONALLY missing the @pii-encrypted-at-write annotation
  late String faydaId;
}

Future<void> save(User u, Isar isar) async {
  // INTENTIONALLY no encrypt() call before put
  await isar.writeTxn(() => isar.users.put(u));
}
```

### 5d. `.github/workflows/lint.yml` (extend)

```diff
     - name: Guard legacyPlaintextAllowed flag
       run: bash scripts/check_legacy_plaintext_flag.sh

     - name: Guard Isar test-time downloads
       run: bash scripts/check_isar_test_download.sh

+    - name: Lint plaintext PII writes
+      run: bash scripts/check_pii_writes.sh
+
+    - name: Self-test PII-writes lint
+      run: bash scripts/check_pii_writes.sh --self-test
+
     - name: Aikido security scan
```

---

## 6. Open questions for the reviewer

1. **PII_FIELDS list.** Currently: `phoneNumber faydaId fullName displayName farmerName`.
   Is `farmerName` borderline? It's user-visible-by-design when farmers
   list crops. Default: keep it on the list (mob-targeting risk in
   rural communities). Reviewer call.

2. **20-line window for the caller pattern.** Arbitrary. Smaller =
   more false negatives; larger = more false positives. 20 has worked
   for similar tools at other projects. Reviewer pushback welcome.

3. **Should the lint be enforced or advisory?** Currently enforced
   (CI fails). Alternative: warn-only for two sprints, enforce after.
   Default: enforce now — better to catch eagerly than ship a regression
   and miss it.

---

*PR scaffold v1.0 — 2026-05-13 — independent of S2-01 / S2-02*
