import { describe, expect, it } from "vitest";

import type { Locale } from "@/lib/i18n";
import {
  DELETED_USER_SENTINEL,
  isDeletedUser,
  partyName,
  userName,
  userPhone,
} from "@/lib/user";

/* ACC-05: how the admin recognises an account the server erased (ACC-02). A users-table
 * tombstone keeps no phone, and the server writes the "Deleted user" sentinel onto a deleted
 * party's copied name fields on orders, escrows and disputes. */

const liveUser = { displayName: "Abebe Kebede", phoneNumber: "+251900100001" };
const deletedUser = { displayName: "", phoneNumber: "" };

describe("isDeletedUser", () => {
  it("is true for a tombstone (no phone)", () => {
    expect(isDeletedUser(deletedUser)).toBe(true);
    expect(isDeletedUser({ phoneNumber: "   " })).toBe(true);
    expect(isDeletedUser({})).toBe(true);
  });

  it("is false for a live user and for nullish input", () => {
    expect(isDeletedUser(liveUser)).toBe(false);
    expect(isDeletedUser(null)).toBe(false);
    expect(isDeletedUser(undefined)).toBe(false);
  });
});

describe("userName / userPhone", () => {
  const locales: Locale[] = ["en", "am", "om"];

  it("shows the live user's own name and phone", () => {
    expect(userName(liveUser, "en")).toBe("Abebe Kebede");
    expect(userPhone(liveUser, "en")).toBe("+251900100001");
  });

  it("labels a deleted user in every locale and never throws on a missing phone", () => {
    for (const locale of locales) {
      expect(() => userName(deletedUser, locale)).not.toThrow();
      expect(userName(deletedUser, locale)).not.toBe("");
      expect(userPhone(deletedUser, locale)).not.toBe("");
    }
    expect(userName(deletedUser, "en")).toBe("Deleted user");
  });
});

describe("partyName", () => {
  it("localizes the server sentinel", () => {
    expect(partyName(DELETED_USER_SENTINEL, "buyer-1", "en")).toBe("Deleted user");
    expect(partyName("deleted user", "buyer-1", "en")).toBe("Deleted user");
    expect(partyName(DELETED_USER_SENTINEL, "buyer-1", "am")).not.toBe(DELETED_USER_SENTINEL);
  });

  it("keeps a real name and falls back to the id when the name is empty", () => {
    expect(partyName("Meron Alemu", "buyer-1", "en")).toBe("Meron Alemu");
    expect(partyName("", "buyer-1", "en")).toBe("buyer-1");
    expect(partyName(undefined, undefined, "en")).toBe("");
  });
});
