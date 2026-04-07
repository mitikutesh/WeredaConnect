/**
 * Shared authentication helpers for E2E tests.
 *
 * Test users are seeded via scripts/seed-test-users.sh.
 * Mobile Cognito pool: phone+password auth (self-signup).
 * Admin Cognito pool: separate pool, admin group required.
 */

export const TEST_USERS = {
  farmer: {
    phone: "+251900100001",
    password: "TestPass1dev",
    name: "Abebe Kebede",
    role: "farmer",
  },
  trucker: {
    phone: "+251900100002",
    password: "TestPass1dev",
    name: "Tadesse Worku",
    role: "trucker",
  },
  buyer: {
    phone: "+251900100003",
    password: "TestPass1dev",
    name: "Meron Alemu",
    role: "buyer",
  },
} as const;

export const API_URL =
  process.env.API_URL ||
  "https://vhhm8gh9c1.execute-api.eu-north-1.amazonaws.com";

/** Login via the API and return tokens. Useful for setting auth state directly. */
export async function apiLogin(
  phone: string,
  password: string,
): Promise<{ accessToken: string; refreshToken: string; idToken: string }> {
  const res = await fetch(`${API_URL}/auth/login`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ phoneNumber: phone, password }),
  });
  if (!res.ok) {
    throw new Error(`Login failed: ${res.status} ${await res.text()}`);
  }
  const data = await res.json();
  return data.tokens;
}
