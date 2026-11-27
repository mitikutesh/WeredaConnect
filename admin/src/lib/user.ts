import { t, type Locale } from "@/lib/i18n";

/**
 * ACC-05: how the admin shows accounts the server erased (ACC-02, account-deletion.md).
 *
 * The server writes the sentinel name onto a deleted party's copied name fields on orders,
 * deliveries and escrows, and the users-row tombstone keeps no phone and no name. The deletion
 * state itself is server only (UserDto marks deletionState/deletedAt [JsonIgnore]), so the admin
 * detects a deleted party two ways: the sentinel name on an embedded party, and the erased phone
 * on a users-table row.
 */

/** The display value the server writes into a deleted user's copied name fields (AccountDeletionPolicy.Sentinel). */
export const DELETED_USER_SENTINEL = "Deleted user";

type MaybeUser = Record<string, unknown> | null | undefined;

/**
 * True when a users-table row is an ACC-02 tombstone. The erasure strips the phone (a live user
 * always has one), and the deletion state is not in the admin DTO, so the empty phone is the signal.
 */
export function isDeletedUser(user: MaybeUser): boolean {
  if (!user) return false;
  const phone = typeof user.phoneNumber === "string" ? user.phoneNumber.trim() : "";
  return phone === "";
}

/**
 * The name to show for an embedded party (a buyer, farmer or trucker on an order, escrow or
 * dispute). Returns the localized label when the server wrote the sentinel, otherwise the name,
 * then the fallback id, then an empty string.
 */
export function partyName(name: unknown, fallbackId: unknown, locale: Locale): string {
  const n = typeof name === "string" ? name.trim() : "";
  if (n.toLowerCase() === DELETED_USER_SENTINEL.toLowerCase()) {
    return t("users.deleted", locale);
  }
  if (n) return n;
  return fallbackId == null ? "" : String(fallbackId);
}

/** The name to show for a users-table row: the localized label once the account was erased. */
export function userName(user: MaybeUser, locale: Locale): string {
  if (isDeletedUser(user)) return t("users.deleted", locale);
  return user && typeof user.displayName === "string" ? user.displayName : "";
}

/** The phone to show for a users-table row: "N/A" once the account was erased. */
export function userPhone(user: MaybeUser, locale: Locale): string {
  if (isDeletedUser(user)) return t("common.notAvailable", locale);
  return user && typeof user.phoneNumber === "string" ? user.phoneNumber : "";
}
