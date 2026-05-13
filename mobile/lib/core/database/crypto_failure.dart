/// Shared supertype for fail-closed crypto exceptions in the database
/// encryption stack.
///
/// Concrete subtypes:
/// - [FieldDecryptionException] (`field_encryptor.dart`) — AES-GCM decrypt
///   failures: missing prefix, bad base64, truncated body, MAC verify fail.
/// - [KeyIntegrityFailure] (`isar_service.dart`) — persisted AES key fails
///   its HMAC tag check on read-back.
///
/// Callers handle the two cases identically (wipe + force re-login) in
/// most contexts. A `catch (CryptoFailure)` lets the auth flow treat
/// either as a security-relevant integrity failure without enumerating
/// concrete types.
///
/// **Invariant on [reason]:** the string is always a fixed literal
/// derived from an enum on the concrete subtype — never includes byte
/// data, PII, or per-call context. Safe to log and forward to crash
/// reporting.
abstract class CryptoFailure implements Exception {
  /// Human-readable failure reason. Fixed literal; safe to log.
  String get reason;
}
