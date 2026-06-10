/**
 * Ethiopian phone number normalization and validation.
 *
 * Sibling implementations (keep semantics in sync):
 * - mobile/lib/core/utils/phone_utils.dart
 * - server/src/Validation/RequestValidator.cs (NormalizePhoneNumber)
 *
 * Accepts three input formats:
 * - `+251XXXXXXXXX` (international E.164)
 * - `00251XXXXXXXXX` (international with 00 prefix)
 * - `0XXXXXXXXX` (local Ethiopian format)
 *
 * All formats normalize to canonical E.164: `+251XXXXXXXXX`.
 */

const E164_REGEX = /^\+251\d{9}$/;

/**
 * Normalize an Ethiopian phone number to E.164 format (`+251XXXXXXXXX`).
 * Returns `null` if the input cannot be recognized as a valid Ethiopian
 * phone number.
 */
export function normalizeEthiopianPhone(input: string): string | null {
  if (!input) return null;

  const cleaned = input.trim().replace(/[\s-]/g, "");

  // Format 1: +251XXXXXXXXX (already E.164)
  if (cleaned.startsWith("+251") && cleaned.length === 13) {
    return E164_REGEX.test(cleaned) ? cleaned : null;
  }

  // Format 2: 00251XXXXXXXXX
  if (cleaned.startsWith("00251") && cleaned.length === 14) {
    const normalized = `+251${cleaned.slice(5)}`;
    return E164_REGEX.test(normalized) ? normalized : null;
  }

  // Format 3: 0XXXXXXXXX (local)
  if (cleaned.startsWith("0") && cleaned.length === 10) {
    const normalized = `+251${cleaned.slice(1)}`;
    return E164_REGEX.test(normalized) ? normalized : null;
  }

  return null;
}

/** Validate a raw phone input without normalizing. */
export function isValidEthiopianPhone(input: string): boolean {
  return normalizeEthiopianPhone(input) !== null;
}
