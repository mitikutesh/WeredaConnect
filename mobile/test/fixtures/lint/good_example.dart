// Self-test fixture for `scripts/check_legacy_plaintext_flag.sh`.
// This file MUST pass the lint when scanned directly. Production-shaped
// code that does NOT flip the HS-3 migration escape hatch to true.
//
// NOTE: deliberately avoiding the pattern string in this comment — the
// grep is line-oriented and would otherwise flag the comment itself.
//
// Excluded from the real scan because the entire `mobile/test/` tree is
// allow-listed by the lint script; the self-test invokes the script
// against this file's full path directly to verify behavior.

class _StubEncryptor {
  void initialize(List<int> keyBytes, {bool legacyPlaintextAllowed = false}) {
    // Default false — clean.
  }
}

void main() {
  final enc = _StubEncryptor();
  enc.initialize(List<int>.filled(32, 0)); // No flag flip. Clean.
}
