// Test matrix mirrored from mobile/test/core/validation/form_validation_test.dart
// to keep the TS, Dart, and C# phone implementations from drifting apart.
import { describe, expect, it } from "vitest";

import { isValidEthiopianPhone, normalizeEthiopianPhone } from "./phone";

describe("normalizeEthiopianPhone", () => {
  it("accepts E.164 format unchanged", () => {
    expect(normalizeEthiopianPhone("+251912345678")).toBe("+251912345678");
  });

  it("normalizes 00251 international prefix", () => {
    expect(normalizeEthiopianPhone("00251912345678")).toBe("+251912345678");
  });

  it("normalizes local 0-prefix format", () => {
    expect(normalizeEthiopianPhone("0912345678")).toBe("+251912345678");
  });

  it("strips whitespace and hyphens", () => {
    expect(normalizeEthiopianPhone(" +251 91-234-5678 ")).toBe(
      "+251912345678",
    );
    expect(normalizeEthiopianPhone("091 234 5678")).toBe("+251912345678");
  });

  it.each([
    ["empty string", ""],
    ["too short", "12345"],
    ["too long E.164", "+2519123456789"],
    ["non-Ethiopian country code", "+15551234567"],
    ["letters", "+25191234567a"],
    ["partial local input", "091-123"],
  ])("rejects %s", (_label, input) => {
    expect(normalizeEthiopianPhone(input)).toBeNull();
  });
});

describe("isValidEthiopianPhone", () => {
  it("accepts all three supported formats", () => {
    expect(isValidEthiopianPhone("+251912345678")).toBe(true);
    expect(isValidEthiopianPhone("00251912345678")).toBe(true);
    expect(isValidEthiopianPhone("0912345678")).toBe(true);
  });

  it("rejects invalid input", () => {
    expect(isValidEthiopianPhone("")).toBe(false);
    expect(isValidEthiopianPhone("12345")).toBe(false);
    expect(isValidEthiopianPhone("+15551234567")).toBe(false);
  });
});
