// Self-test fixture for `scripts/check_legacy_plaintext_flag.sh`.
// This file MUST be flagged by the lint when scanned directly.
//
// The intentional violation lives in `main()` below — a production-shaped
// flip of the HS-3 migration escape hatch. The lint regex catches it.
//
// Because this file sits inside `mobile/test/fixtures/lint/`, the real
// lint scan (which whitelists the `mobile/test/` tree) ignores it. The
// script's `--self-test` mode invokes the lint against this file
// directly to verify detection.

class _StubEncryptor {
  void initialize(List<int> keyBytes, {bool legacyPlaintextAllowed = false}) {}
}

void main() {
  final enc = _StubEncryptor();
  // INTENTIONAL VIOLATION — the lint MUST flag the next line.
  enc.initialize(List<int>.filled(32, 0), legacyPlaintextAllowed: true);
}
