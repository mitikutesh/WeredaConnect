import { test, expect, type Page, type Route } from "@playwright/test";

/**
 * Manual payout claims (FUP-05, escrow-protocol.md 2.12.0): an admin claims a leg before paying it in the
 * Telebirr portal, a leg another admin claimed shows who holds it and offers nothing to pay or record, and a
 * portal reference already recorded elsewhere is refused with the escrow and leg it is on.
 *
 * Like reputation.spec.ts this spec does not sign in: it seeds a Cognito session for a fake admin in
 * localStorage and answers every API call the page makes with page.route, so it checks what the page sends
 * and shows, not the server. The mocked server keeps the claim, so the page shows it after it refetches. The
 * server side is covered by EscrowServiceManualPayoutClaimTests, AdminManualPayoutClaimRouteTests and
 * ManualPayoutClaimDynamoDbTests. Run it against a local build:
 *
 *   cd admin && VITE_COGNITO_ADMIN_USER_POOL_ID=eu-north-1_e2eAdmin \
 *     VITE_COGNITO_ADMIN_CLIENT_ID=e2eadminclient VITE_API_BASE_URL=https://api.e2e.invalid \
 *     npx vite build && npx vite preview --port 4173
 *   cd e2e && ADMIN_URL=http://localhost:4173 npx playwright test --project=admin admin/manual-payout-claims.spec.ts
 *
 * Against a deployed admin, set ADMIN_COGNITO_CLIENT_ID to that build's client id; without it the spec is
 * skipped there, since the seeded session would not be found.
 */

const LOCAL_CLIENT_ID = "e2eadminclient";
const CLIENT_ID = process.env.ADMIN_COGNITO_CLIENT_ID || LOCAL_CLIENT_ID;
const ADMIN_URL = process.env.ADMIN_URL || "";
const RUNS_HERE = Boolean(process.env.ADMIN_COGNITO_CLIENT_ID) || /localhost|127\.0\.0\.1/.test(ADMIN_URL);

test.skip(!RUNS_HERE, "Needs a local admin build (ADMIN_URL=http://localhost:...) or ADMIN_COGNITO_CLIENT_ID.");

function fakeJwt(payload: Record<string, unknown>): string {
  const encode = (value: unknown) => Buffer.from(JSON.stringify(value)).toString("base64url");
  return `${encode({ alg: "none", typ: "JWT" })}.${encode(payload)}.signature`;
}

async function seedAdminSession(page: Page) {
  const now = Math.floor(Date.now() / 1000);
  const claims = { iat: now, exp: now + 3600, sub: "admin-e2e", "cognito:groups": ["admin"] };
  const idToken = fakeJwt({ ...claims, phone_number: "+251900999999", aud: CLIENT_ID, token_use: "id" });
  const accessToken = fakeJwt({ ...claims, client_id: CLIENT_ID, token_use: "access" });
  await page.addInitScript(
    ({ clientId, idToken, accessToken }) => {
      const prefix = `CognitoIdentityServiceProvider.${clientId}`;
      window.localStorage.setItem(`${prefix}.LastAuthUser`, "admin-e2e");
      window.localStorage.setItem(`${prefix}.admin-e2e.idToken`, idToken);
      window.localStorage.setItem(`${prefix}.admin-e2e.accessToken`, accessToken);
      window.localStorage.setItem(`${prefix}.admin-e2e.refreshToken`, "refresh");
      window.localStorage.setItem(`${prefix}.admin-e2e.clockDrift`, "0");
    },
    { clientId: CLIENT_ID, idToken, accessToken },
  );
}

const json = (route: Route, body: unknown, status = 200) =>
  route.fulfill({ status, contentType: "application/json", body: JSON.stringify(body) });

interface Claim {
  claimedBy: string;
  claimExpiresAt: string;
}

interface State {
  claims: Record<string, Claim>;
}

interface Captured {
  posts: { path: string; body: unknown }[];
}

const inMinutes = (m: number) => new Date(Date.now() + m * 60_000).toISOString();

function leg(ref: string, name: string, phone: string, amount: number, claim?: Claim) {
  return {
    ref,
    payeeRole: ref.startsWith("release#trucker") ? "trucker" : "farmer",
    payeeId: `${ref}-payee`,
    payeeName: name,
    payeePhone: phone,
    payeeVerified: true,
    amount,
    status: "pending",
    ...(claim
      ? { claimedBy: claim.claimedBy, claimExpiresAt: claim.claimExpiresAt, claimedByMe: claim.claimedBy === "admin-e2e" }
      : {}),
  };
}

async function mockApi(page: Page, state: State): Promise<Captured> {
  const captured: Captured = { posts: [] };
  await page.route(/\/admin\//, (route) => json(route, {}));
  await page.route(/\/admin\/payouts\/manual(\?|$)/, (route) =>
    json(route, {
      payouts: [
        {
          escrowId: "escrow-order-ord-1",
          orderId: "ord-1",
          op: "release",
          reason: "PAYOUT_NOT_SUPPORTED",
          escrowStatus: "escrowed",
          fromStatus: "escrowed",
          queuedAt: new Date(Date.now() - 2 * 3_600_000).toISOString(),
          currency: "ETB",
          pendingAmount: 16500,
          legs: [
            leg("release#farmer", "Abebe Kebede", "+251911000001", 15000, state.claims["release#farmer"]),
            leg("release#trucker#trucker-1", "Chala Tolosa", "+251911000002", 1500, state.claims["release#trucker#trucker-1"]),
          ],
        },
      ],
      total: 1,
    }),
  );
  await page.route(/\/admin\/payouts\/escrow-order-ord-1\/[^/]+\/(claim|unclaim|complete)$/, (route) => {
    const url = new URL(route.request().url());
    const [, , , , ref, action] = url.pathname.split("/");
    const decoded = decodeURIComponent(ref);
    captured.posts.push({ path: `${decoded}/${action}`, body: route.request().postDataJSON() });
    if (action === "claim") {
      state.claims[decoded] = { claimedBy: "admin-e2e", claimExpiresAt: inMinutes(30) };
      return json(route, { escrowId: "escrow-order-ord-1", ref: decoded, claimed: true, claimedBy: "admin-e2e" });
    }

    if (action === "unclaim") {
      delete state.claims[decoded];
      return json(route, { escrowId: "escrow-order-ord-1", ref: decoded, claimed: false, released: true });
    }

    return json(
      route,
      {
        error: {
          code: "ESCROW_047",
          message: "This reference is already recorded on escrow escrow-order-ord-9, leg release#farmer.",
          field: "externalRef",
        },
      },
      409,
    );
  });
  return captured;
}

test.describe("Manual payout claims (FUP-05)", () => {
  test.beforeEach(async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
  });

  test("an admin claims a leg, sees their claim, and gives it back", async ({ page }) => {
    await seedAdminSession(page);
    const captured = await mockApi(page, { claims: {} });
    await page.goto("/payouts");

    const row = page.getByRole("row").filter({ hasText: "Abebe Kebede" });
    await row.getByRole("button", { name: "Claim" }).click();

    await expect(page.getByRole("status")).toHaveText(
      "Leg claimed for 30 minutes. Pay it in the portal, then record the transaction ID.",
    );
    await expect(row.getByText(/^You are paying this leg \(claimed until .+\)\.$/)).toBeVisible();
    await row.getByRole("button", { name: "Release claim" }).click();
    await expect(row.getByRole("button", { name: "Claim" })).toBeVisible();
    expect(captured.posts.map((p) => p.path)).toEqual(["release#farmer/claim", "release#farmer/unclaim"]);
  });

  test("a leg another admin claimed offers nothing to pay or record", async ({ page }) => {
    await seedAdminSession(page);
    await mockApi(page, {
      claims: { "release#trucker#trucker-1": { claimedBy: "admin-other", claimExpiresAt: inMinutes(20) } },
    });
    await page.goto("/payouts");

    const row = page.getByRole("row").filter({ hasText: "Chala Tolosa" });
    await expect(row.getByText("Another admin is paying this")).toBeVisible();
    await expect(row.getByText(/^Claimed by admin-other until .+\. Do not pay it in the portal\.$/)).toBeVisible();
    await expect(row.getByRole("button", { name: "Claim" })).toHaveCount(0);
    await expect(row.getByLabel("Telebirr transaction ID")).toHaveCount(0);
  });

  test("a reference already recorded elsewhere is refused with where it is used", async ({ page }) => {
    await seedAdminSession(page);
    const captured = await mockApi(page, { claims: {} });
    await page.goto("/payouts");

    const row = page.getByRole("row").filter({ hasText: "Abebe Kebede" });
    await row.getByLabel("Telebirr transaction ID").fill("TB2026111200001");
    await row.getByRole("button", { name: "Mark as paid" }).click();
    await row.getByRole("button", { name: "Yes, recorded" }).click();

    await expect(page.getByRole("alert")).toHaveText(
      "This Telebirr transaction ID is already recorded on escrow escrow-order-ord-9, leg release#farmer. Check the ID in the portal: one payment cannot be recorded twice.",
    );
    expect(captured.posts).toEqual([{ path: "release#farmer/complete", body: { externalRef: "TB2026111200001" } }]);
  });
});
