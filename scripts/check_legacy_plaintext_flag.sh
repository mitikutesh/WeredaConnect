#!/usr/bin/env bash
# scripts/check_legacy_plaintext_flag.sh
#
# Fails any source-tree state where FieldEncryptor's
# `legacyPlaintextAllowed: true` appears outside the allow-list of paths.
#
# WHY:
#   `legacyPlaintextAllowed: true` disables the S1-02 fail-closed decrypt
#   contract — `decrypt` returns the input unchanged for unprefixed values
#   instead of throwing FieldDecryptionException. The flag exists ONLY for
#   the HS-3 v1→v2 migration to read existing plaintext rows once before
#   re-encrypting them. Enabling it anywhere else reopens the downgrade-
#   attack surface S1-02 just closed.
#
# Allow-listed paths (`legacyPlaintextAllowed: true` is permitted here):
#   - mobile/lib/core/database/migrations/   (HS-3 migration code)
#   - mobile/test/                           (tests must exercise both branches)
#
# Exit codes:
#   0 — clean (no violations)
#   1 — violations found (paths printed to stderr)
#
# Usage:
#   bash scripts/check_legacy_plaintext_flag.sh           # scan mobile/lib
#   bash scripts/check_legacy_plaintext_flag.sh --self-test
#   bash scripts/check_legacy_plaintext_flag.sh <path>    # scan a specific path
#
# Caveats:
#   The grep is line-oriented. A bad-faith contributor could split
#   `legacyPlaintextAllowed:\n    true` across two lines to evade detection.
#   That's an explicit non-goal — code review catches that case.

set -euo pipefail

REPO_ROOT="$(cd "$(dirname "$0")/.." && pwd)"

PATTERN='legacyPlaintextAllowed[[:space:]]*:[[:space:]]*true'

# Self-test mode runs the script against the two fixture files and
# asserts the right exit codes.
if [[ "${1:-}" == "--self-test" ]]; then
  GOOD="$REPO_ROOT/mobile/test/fixtures/lint/good_example.dart"
  BAD="$REPO_ROOT/mobile/test/fixtures/lint/bad_example.dart"

  if [[ ! -f "$GOOD" ]] || [[ ! -f "$BAD" ]]; then
    echo "✗ Self-test fixtures missing — expected:" >&2
    echo "    $GOOD" >&2
    echo "    $BAD" >&2
    exit 1
  fi

  echo "→ Testing good_example.dart (must pass)..."
  if bash "$0" "$GOOD" >/dev/null 2>&1; then
    echo "  ✓ good_example.dart passed"
  else
    echo "  ✗ good_example.dart FAILED — script flagged a clean file" >&2
    exit 1
  fi

  echo "→ Testing bad_example.dart (must fail)..."
  if bash "$0" "$BAD" >/dev/null 2>&1; then
    echo "  ✗ bad_example.dart FAILED — script missed a violation" >&2
    exit 1
  else
    echo "  ✓ bad_example.dart correctly flagged"
  fi

  echo "✓ Self-test passed."
  exit 0
fi

# Determine the scan target. Default to mobile/lib (production source).
# Allow callers to pass an explicit path for unit-testing the lint logic.
if [[ -n "${1:-}" ]]; then
  SCAN_TARGET="$1"
  # Explicit path mode: don't apply the allow-list — the caller is
  # asking us to scan exactly this scope (e.g. a single fixture file).
  USE_ALLOWLIST=0
else
  SCAN_TARGET="$REPO_ROOT/mobile/lib"
  USE_ALLOWLIST=1
fi

if [[ ! -e "$SCAN_TARGET" ]]; then
  echo "✗ Scan target does not exist: $SCAN_TARGET" >&2
  exit 1
fi

# Find raw matches.
RAW=$(grep -rnE \
  --include="*.dart" \
  "$PATTERN" \
  "$SCAN_TARGET" 2>/dev/null || true)

# In default-scan mode, strip allow-listed paths. The allow-list is
# anchored to specific path prefixes so that a future file like
# `mobile/lib/features/auth/migrations/foo.dart` is NOT silently exempted
# by a loose `/migrations/` substring match. The full test tree
# (`mobile/test/`) is permitted because tests exercise both branches of
# the flag; only the canonical migration directory
# (`mobile/lib/core/database/migrations/`) is permitted under
# `mobile/lib/`.
if [[ "$USE_ALLOWLIST" == "1" ]]; then
  VIOLATIONS=$(echo "$RAW" \
    | grep -vE "(^|/)mobile/lib/core/database/migrations/|(^|/)mobile/test/" \
    | sed '/^$/d' || true)
else
  VIOLATIONS=$(echo "$RAW" | sed '/^$/d' || true)
fi

if [[ -n "$VIOLATIONS" ]]; then
  echo "✗ legacyPlaintextAllowed: true found outside the allow-list:" >&2
  echo "" >&2
  echo "$VIOLATIONS" >&2
  echo "" >&2
  echo "Allowed paths:" >&2
  echo "  - mobile/lib/core/database/migrations/" >&2
  echo "  - mobile/test/" >&2
  echo "" >&2
  echo "If this is the HS-3 migration, place the call under migrations/." >&2
  echo "If this is a test, place it under mobile/test/." >&2
  echo "Otherwise it's a security regression — see S1-02 fail-closed contract." >&2
  exit 1
fi

echo "✓ legacyPlaintextAllowed: true not used outside the allow-list."
exit 0
