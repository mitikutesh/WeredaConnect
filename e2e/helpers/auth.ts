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

/** Login via the API and return the FULL response (tokens + user). */
export async function apiLoginFull(
  phone: string,
  password: string,
): Promise<{
  tokens: {
    accessToken: string;
    refreshToken: string;
    idToken: string;
    expiresIn: number;
  };
  user: {
    userId: string;
    phoneNumber: string;
    displayName?: string;
    role: string;
    woreda: string;
  };
}> {
  const res = await fetch(`${API_URL}/auth/login`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ phoneNumber: phone, password }),
  });
  if (!res.ok) {
    throw new Error(`Login failed: ${res.status} ${await res.text()}`);
  }
  const data = await res.json();
  return { tokens: data.tokens, user: data.user };
}

/**
 * Authenticate the Flutter web app for [role] by driving the production-gated
 * test seam (`window.__seedAuth`) the app registers in non-prod builds. It
 * writes a real session through the app's own OfflineAuthCache + session keys,
 * so the genuine startup restore path runs. Throws loudly if the seam is absent
 * (e.g. a prod build) so a broken setup FAILS the test instead of silently
 * passing — the false-green this fix removes (F-37).
 *
 * CanvasKit exposes no stable login DOM, so the only reliable signal that auth
 * succeeded is the URL leaving /login (asserted via waitForURL).
 */
export async function seedAuth(
  page: import("@playwright/test").Page,
  role: keyof typeof TEST_USERS,
): Promise<void> {
  const u = TEST_USERS[role];
  const { tokens, user } = await apiLoginFull(u.phone, u.password);

  await page.goto("/login");
  await page.waitForSelector("flutter-view", { timeout: 30_000 });
  await page.waitForTimeout(3000);

  const seeded = await page.evaluate(
    async (payloadJson) => {
      const fn = (
        window as unknown as { __seedAuth?: (json: string) => Promise<void> }
      ).__seedAuth;
      if (typeof fn !== "function") return false;
      await fn(payloadJson);
      return true;
    },
    JSON.stringify({ phone: u.phone, tokens, user }),
  );

  if (!seeded) {
    throw new Error(
      "window.__seedAuth is absent — the web build was not produced with a " +
        "non-prod ENVIRONMENT (dev/staging). The seam is correctly excluded " +
        "from production builds; e2e must run against a dev/staging build.",
    );
  }

  await page.waitForURL((url) => !url.pathname.includes("/login"), {
    timeout: 20_000,
  });
}
