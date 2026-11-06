import { test, expect, type Page, type Route } from "@playwright/test";

/**
 * Admin Fraud Review (FRD-07): approve, reject and whitelist a signal, and release or refund an escrow
 * from the held queue (fraud-detection-architecture.md sections 18.3 and 19).
 *
 * The admin pool needs TOTP, so this spec does not sign in. It seeds a Cognito session for a fake admin
 * in localStorage (amazon-cognito-identity-js restores it without a network call while the tokens are
 * unexpired) and answers every API call the page makes with page.route, so it checks what the page sends
 * and shows, not the server. The server side is covered by AdminFraudReviewQueueTests and
 * FraudReviewQueueDynamoDbTests.
 *
 * The session key names the app client id baked into the build. Run it against a local build:
 *
 *   cd admin && VITE_COGNITO_ADMIN_USER_POOL_ID=eu-north-1_e2eAdmin \
 *     VITE_COGNITO_ADMIN_CLIENT_ID=e2eadminclient VITE_API_BASE_URL=https://api.e2e.invalid \
 *     npx vite build && npx vite preview --port 4173
 *   cd e2e && ADMIN_URL=http://localhost:4173 npx playwright test --project=admin admin/fraud-review.spec.ts
 *
 * Against a deployed admin, set ADMIN_COGNITO_CLIENT_ID to that build's client id; without it the
 * spec is skipped there, since the seeded session would not be found.
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

const signals = [
  {
    signalId: "sig-gps",
    userId: "farmer-1",
    signalType: "GPS_ANOMALY",
    severity: "high",
    description: "Pickup and drop-off are the same point.",
    status: "pending",
    entityType: "order",
    entityId: "ord-1",
    orderId: "ord-1",
    createdAt: "2026-11-06T09:12:00Z",
  },
  {
    signalId: "sig-dup",
    userId: "farmer-1",
    signalType: "FRAUD_DUPLICATE_LISTING",
    severity: "high",
    description: "Duplicate listing.",
    status: "pending",
    entityType: "crop",
    entityId: "crop-1",
    createdAt: "2026-11-05T14:40:00Z",
  },
];

const users = [
  {
    userId: "farmer-1",
    displayName: "Abebe Kebede",
    riskScore: 60,
    enforcementScore: 60,
    riskTier: "high",
    truncated: false,
    scoreAvailable: true,
  },
];

const heldEscrows = [
  {
    escrowId: "escrow-order-ord-9",
    orderId: "ord-9",
    deliveryId: "del-9",
    buyerId: "buyer-1",
    buyerName: "Meron Alemu",
    farmerId: "farmer-1",
    farmerName: "Abebe Kebede",
    amount: 5000,
    status: "held",
    holdReason: "fraud_confirmed",
    heldAt: "2026-11-06T10:00:00Z",
    heldBy: "system",
    heldFromStatus: "escrowed",
    heldFor: "release",
  },
  {
    escrowId: "escrow-order-ord-10",
    orderId: "ord-10",
    buyerId: "buyer-2",
    farmerId: "farmer-3",
    amount: 1200,
    status: "held",
    holdReason: "risk_critical",
    heldAt: "2026-11-06T11:00:00Z",
    heldBy: "system",
    heldFromStatus: "escrowed",
    heldFor: "refund",
  },
];

interface Captured {
  actions: { id: string; body: Record<string, unknown> }[];
  releases: Record<string, unknown>[];
  refunds: Record<string, unknown>[];
}

const json = (route: Route, body: unknown, status = 200) =>
  route.fulfill({ status, contentType: "application/json", body: JSON.stringify(body) });

async function mockApi(page: Page): Promise<Captured> {
  const captured: Captured = { actions: [], releases: [], refunds: [] };
  // Registered first, so it answers last: anything else the shell asks for gets an empty answer.
  await page.route(/\/admin\//, (route) => json(route, {}));
  await page.route(/\/admin\/fraud-signals(\?|$)/, (route) => {
    const status = new URL(route.request().url()).searchParams.get("status");
    return json(route, status === "pending" ? { signals, users, total: signals.length } : { signals: [], users: [], total: 0 });
  });
  await page.route(/\/admin\/fraud-signals\/[^/]+\/action$/, (route) => {
    const id = decodeURIComponent(route.request().url().split("/fraud-signals/")[1].split("/")[0]);
    const body = route.request().postDataJSON() as Record<string, unknown>;
    captured.actions.push({ id, body });
    const base = { signalId: id, status: "reviewed", reviewedAt: "2026-11-07T08:00:00Z", userId: "farmer-1" };
    if (body.action === "reject") {
      return json(route, {
        ...base,
        resolution: "reject",
        suspended: true,
        suspendReason: "fraud_confirmed",
        sessionsEnded: true,
        openEscrows: [{ escrowId: "escrow-order-ord-9", status: "escrowed", party: "farmer", amount: 5000 }],
        openEscrowsComplete: true,
      });
    }
    if (body.action === "whitelist") {
      return json(route, { ...base, resolution: "whitelist", whitelistUntil: "2026-12-07T08:00:00Z" });
    }
    return json(route, { ...base, resolution: "approve" });
  });
  await page.route(/\/admin\/escrows\/held(\?|$)/, (route) => json(route, { escrows: heldEscrows, total: 2 }));
  await page.route(/\/escrow\/release$/, (route) => {
    captured.releases.push(route.request().postDataJSON());
    return json(route, { escrowId: "escrow-order-ord-9", status: "completed", farmerPayout: 4875, commission: 125, releasedAt: "2026-11-07T08:00:00Z" });
  });
  await page.route(/\/escrow\/refund$/, (route) => {
    captured.refunds.push(route.request().postDataJSON());
    return json(route, { escrowId: "escrow-order-ord-10", status: "refunded", refundAmount: 1200, refundedAt: "2026-11-07T08:00:00Z" });
  });
  return captured;
}

async function openFraudReview(page: Page): Promise<Captured> {
  await seedAdminSession(page);
  const captured = await mockApi(page);
  await page.goto("/fraud-review");
  await expect(page.getByRole("heading", { name: "Fraud Review" })).toBeVisible();
  return captured;
}

function signalRow(page: Page, type: string) {
  return page.getByRole("region", { name: "Abebe Kebede" }).getByRole("row").filter({ hasText: type });
}

test.describe("Admin Fraud Review (FRD-07)", () => {
  test.beforeEach(async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
  });

  test("groups the signals by user with the risk score and the hour note", async ({ page }) => {
    await openFraudReview(page);

    const group = page.getByRole("region", { name: "Abebe Kebede" });
    await expect(group.getByText("Risk score 60 (High)")).toBeVisible();
    await expect(group.getByRole("link", { name: "Abebe Kebede" })).toHaveAttribute("href", "/users/farmer-1");
    await expect(
      page.getByText("One row stands for every refusal of its type in one hour. The time shown is the first one."),
    ).toBeVisible();
    await expect(signalRow(page, "GPS_ANOMALY").getByRole("link", { name: "Transaction timeline" })).toHaveAttribute(
      "href",
      "/transactions/ord-1",
    );
  });

  test("approve", async ({ page }) => {
    const captured = await openFraudReview(page);

    await signalRow(page, "FRAUD_DUPLICATE_LISTING").getByRole("button", { name: "Approve" }).click();

    await expect(page.getByRole("status")).toHaveText(
      "Signal approved. It no longer counts toward this user's risk score.",
    );
    expect(captured.actions).toEqual([{ id: "sig-dup", body: { action: "approve" } }]);
  });

  test("reject asks once more, then shows the suspension and the open escrows", async ({ page }) => {
    const captured = await openFraudReview(page);

    await signalRow(page, "FRAUD_DUPLICATE_LISTING").getByRole("button", { name: "Reject" }).click();
    expect(captured.actions).toEqual([]);
    await signalRow(page, "FRAUD_DUPLICATE_LISTING").getByRole("button", { name: "Reject and suspend" }).click();

    const status = page.getByRole("status");
    await expect(status.getByText("Signal rejected. User farmer-1 is suspended (fraud confirmed).")).toBeVisible();
    await expect(status.getByText("escrow-order-ord-9: Farmer, Escrowed, 5,000.00 Birr")).toBeVisible();
    expect(captured.actions).toEqual([{ id: "sig-dup", body: { action: "reject" } }]);
  });

  test("whitelist is offered on a soft signal only", async ({ page }) => {
    const captured = await openFraudReview(page);

    await expect(signalRow(page, "FRAUD_DUPLICATE_LISTING").getByRole("button", { name: "Whitelist" })).toHaveCount(0);
    await signalRow(page, "GPS_ANOMALY").getByRole("button", { name: "Whitelist" }).click();

    await expect(page.getByRole("status")).toContainText("Signal whitelisted.");
    expect(captured.actions).toEqual([{ id: "sig-gps", body: { action: "whitelist" } }]);
  });

  test("held queue: release with a reason", async ({ page }) => {
    const captured = await openFraudReview(page);
    await page.getByRole("button", { name: "Held escrows" }).click();

    const row = page.getByRole("row").filter({ hasText: "escrow-order-ord-9" });
    await expect(row.getByText("Fraud confirmed in review")).toBeVisible();
    await expect(row.getByRole("link", { name: "Transaction timeline" })).toHaveAttribute("href", "/transactions/ord-9");
    await expect(row.getByRole("button", { name: "Release" })).toBeDisabled();
    await row.getByRole("textbox", { name: "Reason (10 to 500 characters)" }).fill("Buyer confirmed the goods arrived.");
    await row.getByRole("button", { name: "Release" }).click();
    await row.getByRole("button", { name: "Yes, release" }).click();

    await expect(page.getByRole("status")).toHaveText("Escrow released.");
    expect(captured.releases).toHaveLength(1);
    expect(captured.releases[0]).toMatchObject({
      escrowId: "escrow-order-ord-9",
      trigger: "admin_override",
      deliveryId: "del-9",
      reason: "Buyer confirmed the goods arrived.",
    });
    expect(String(captured.releases[0].idempotencyKey)).toMatch(/^admin:escrow-order-ord-9:release:\d+$/);
    expect(captured.refunds).toEqual([]);
  });

  test("held queue: refund with a reason", async ({ page }) => {
    const captured = await openFraudReview(page);
    await page.getByRole("button", { name: "Held escrows" }).click();

    const row = page.getByRole("row").filter({ hasText: "escrow-order-ord-10" });
    await expect(row.getByText("Critical risk score")).toBeVisible();
    await row.getByRole("textbox", { name: "Reason (10 to 500 characters)" }).fill("Seller never shipped the teff.");
    await row.getByRole("button", { name: "Refund" }).click();
    await expect(row.getByText("Refund 1,200.00 Birr to the buyer now?")).toBeVisible();
    await row.getByRole("button", { name: "Yes, refund" }).click();

    await expect(page.getByRole("status")).toHaveText("Escrow refunded.");
    expect(captured.refunds).toHaveLength(1);
    expect(captured.refunds[0]).toMatchObject({
      escrowId: "escrow-order-ord-10",
      trigger: "admin_override",
      reason: "Seller never shipped the teff.",
    });
    expect(captured.releases).toEqual([]);
  });
});
