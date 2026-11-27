import { test, expect, type Page, type Route } from "@playwright/test";

/**
 * Admin dispute evidence (FRD-14): open a dispute, weigh the handoffs, the timeline, the photos and
 * the parties' messages on the dispute page, and resolve it from the same screen
 * (escrow-protocol.md 1.40.0, GET /admin/disputes/{id}/evidence).
 *
 * Like fraud-review.spec.ts this spec does not sign in: it seeds a Cognito session for a fake admin
 * in localStorage and answers every API call the page makes with page.route, so it checks what the
 * page sends and shows, not the server. The server side is covered by
 * AdminFunctionDisputeEvidenceTests. Run it against a local build:
 *
 *   cd admin && VITE_COGNITO_ADMIN_USER_POOL_ID=eu-north-1_e2eAdmin \
 *     VITE_COGNITO_ADMIN_CLIENT_ID=e2eadminclient VITE_API_BASE_URL=https://api.e2e.invalid \
 *     npx vite build && npx vite preview --port 4173
 *   cd e2e && ADMIN_URL=http://localhost:4173 npx playwright test --project=admin admin/dispute-evidence.spec.ts
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

// A 1x1 PNG for the handoff photo the page shows.
const PHOTO = Buffer.from(
  "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==",
  "base64",
);

const escrow = {
  escrowId: "escrow-order-ord-1",
  orderId: "ord-1",
  deliveryId: "del-1",
  buyerId: "buyer-1",
  buyerName: "Hawi Flour Mill",
  farmerId: "farmer-1",
  farmerName: "Tolosa Gemechu",
  truckerId: "trucker-1",
  amount: 30900,
  farmerPayout: 28000,
  commission: 1400,
  status: "disputed",
  dispute: { disputeId: "dispute-1", reason: "short_delivery", createdAt: "2026-11-05T12:50:00Z" },
};

const evidence = {
  escrow,
  order: { orderId: "ord-1", buyerId: "buyer-1", farmerId: "farmer-1", truckerId: "trucker-1", truckerName: "Dawit Tesfaye" },
  delivery: {
    deliveryId: "del-1",
    buyerId: "buyer-1",
    farmerId: "farmer-1",
    truckerId: "trucker-1",
    truckerName: "Dawit Tesfaye",
    pickupPhotoUrl: "https://photos.e2e.invalid/pickup.webp",
  },
  timeline: [
    { timestamp: "2026-11-05T07:40:01Z", entity: "delivery", action: "picked_up", actor: "trucker-1" },
    { timestamp: "2026-11-05T12:15:01Z", entity: "delivery", action: "delivered", actor: "buyer-1" },
    { timestamp: "2026-11-05T12:50:00Z", entity: "delivery", action: "dispute", actor: "buyer-1" },
  ],
  handoffs: [
    {
      leg: "pickup",
      at: "2026-11-05T07:40:00Z",
      actorId: "trucker-1",
      actorRole: "trucker",
      method: "qr",
      gps: { latitude: 8.5, longitude: 38.8 },
      planned: { latitude: 8.504, longitude: 38.8 },
      distanceKm: 0.44,
    },
    {
      leg: "dropoff",
      at: "2026-11-05T12:15:00Z",
      actorId: "buyer-1",
      actorRole: "buyer",
      method: "code",
      gps: { latitude: 9.002, longitude: 38.74 },
      planned: { latitude: 9.0, longitude: 38.74 },
      distanceKm: 0.22,
    },
  ],
};

const messages = [
  {
    messageId: "m-1",
    conversationId: "order:ord-1:buyer-1:farmer-1",
    contextType: "order",
    contextId: "ord-1",
    senderId: "buyer-1",
    recipientId: "farmer-1",
    body: "Are the ten sacks ready?",
    createdAt: "2026-11-04T08:00:00Z",
  },
  {
    messageId: "m-2",
    conversationId: "delivery:del-1:buyer-1:trucker-1",
    contextType: "delivery",
    contextId: "del-1",
    senderId: "buyer-1",
    recipientId: "trucker-1",
    body: "Only 850 kg arrived.",
    createdAt: "2026-11-05T12:20:00Z",
  },
];

interface Captured {
  evidenceReads: string[];
  verdicts: Record<string, unknown>[];
}

const json = (route: Route, body: unknown, status = 200) =>
  route.fulfill({ status, contentType: "application/json", body: JSON.stringify(body) });

async function mockApi(page: Page): Promise<Captured> {
  const captured: Captured = { evidenceReads: [], verdicts: [] };
  // Registered first, so it answers last: anything else the shell asks for gets an empty answer.
  await page.route(/\/admin\//, (route) => json(route, {}));
  await page.route(/\/admin\/escrows\/escrow-order-ord-1$/, (route) => json(route, { escrow }));
  await page.route(/\/admin\/deliveries\/del-1\/trail$/, (route) =>
    json(route, {
      deliveryId: "del-1",
      tripId: null,
      status: "disputed",
      open: false,
      from: null,
      until: null,
      asOf: "2026-11-07T08:00:00Z",
      pickup: { latitude: null, longitude: null, at: null },
      dropoff: { latitude: null, longitude: null, at: null },
      points: [],
      legs: [],
      pauses: [],
      gaps: [],
      summary: {
        sharedWholeWay: false,
        declined: false,
        fixes: 0,
        coarseFixes: 0,
        mockedFixes: 0,
        lateFixes: 0,
        jumps: 0,
        gaps: 0,
        distanceKm: 0,
        durationMin: 0,
        maxKmh: null,
      },
    }),
  );
  await page.route(/\/admin\/disputes\/escrow-order-ord-1\/evidence/, (route) => {
    const url = new URL(route.request().url());
    captured.evidenceReads.push(url.search);
    return json(
      route,
      url.searchParams.get("include") === "messages" ? { ...evidence, messages, messagesTruncated: false } : evidence,
    );
  });
  await page.route(/\/admin\/disputes\/escrow-order-ord-1\/resolve$/, (route) => {
    captured.verdicts.push(route.request().postDataJSON());
    return json(route, {
      escrowId: "escrow-order-ord-1",
      status: "dispute_resolved",
      resolution: "buyer_wins",
      resolvedAt: "2026-11-07T08:00:00Z",
    });
  });
  await page.route(/photos\.e2e\.invalid/, (route) => route.fulfill({ status: 200, contentType: "image/png", body: PHOTO }));
  return captured;
}

async function openDispute(page: Page): Promise<Captured> {
  await seedAdminSession(page);
  const captured = await mockApi(page);
  await page.goto("/disputes/escrow-order-ord-1");
  await expect(page.getByRole("heading", { name: "Dispute" })).toBeVisible();
  return captured;
}

test.describe("Admin dispute evidence (FRD-14)", () => {
  test.beforeEach(async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
  });

  test("the timeline tab shows the handoffs and the audit timeline beside Decide", async ({ page }) => {
    const captured = await openDispute(page);

    await page.getByRole("tab", { name: "Timeline" }).click();
    const panel = page.getByRole("tabpanel");
    await expect(panel.getByRole("heading", { name: "Handoffs" })).toBeVisible();
    await expect(panel.getByText("QR scanned by Trucker · Dawit Tesfaye")).toBeVisible();
    await expect(panel.getByText("0.44 km from the listing")).toBeVisible();
    await expect(panel.getByText("Code typed by Buyer · Hawi Flour Mill")).toBeVisible();
    await expect(panel.getByText("0.22 km from the trip's destination")).toBeVisible();
    await expect(panel.getByText("Disputed", { exact: true })).toBeVisible();
    await expect(panel.getByRole("complementary").getByRole("heading", { name: "Decide" })).toBeVisible();

    // The evidence without the messages: nothing private was read.
    expect(captured.evidenceReads.length).toBeGreaterThan(0);
    expect(captured.evidenceReads.every((search) => search === "")).toBe(true);
  });

  test("the photos tab shows the pickup photo and says the drop-off one is missing", async ({ page }) => {
    await openDispute(page);

    await page.getByRole("tab", { name: "Photos · 1" }).click();
    await expect(page.getByRole("img", { name: "Pickup photo" })).toBeVisible();
    await expect(page.getByRole("link", { name: "Open the Pickup photo full size" })).toHaveAttribute(
      "href",
      "https://photos.e2e.invalid/pickup.webp",
    );
    await expect(page.getByText("No drop-off photo.")).toBeVisible();
  });

  test("the messages are read only on request, then the admin resolves the dispute from the same screen", async ({
    page,
  }) => {
    const captured = await openDispute(page);

    await page.getByRole("tab", { name: "Messages" }).click();
    await expect(page.getByText(/The parties' messages are private/)).toBeVisible();
    expect(captured.evidenceReads).not.toContain("?include=messages");

    await page.getByRole("button", { name: "Show the messages" }).click();
    const list = page.getByRole("list", { name: "Messages" });
    await expect(list.getByRole("listitem")).toHaveCount(2);
    await expect(list.getByText("Buyer · Hawi Flour Mill to Farmer · Tolosa Gemechu")).toBeVisible();
    await expect(list.getByText("Only 850 kg arrived.")).toBeVisible();
    await expect(page.getByText("Read-only. This read was written to the audit log.")).toBeVisible();
    expect(captured.evidenceReads.filter((search) => search === "?include=messages")).toHaveLength(1);

    await page.getByRole("complementary").getByRole("button", { name: "Buyer Wins" }).click();
    await expect(page.getByRole("status").filter({ hasText: "Verdict saved." })).toBeVisible();
    expect(captured.verdicts).toEqual([{ resolution: "buyer_wins", action: "refund" }]);
    // The verdict does not read the messages again.
    expect(captured.evidenceReads.filter((search) => search === "?include=messages")).toHaveLength(1);
  });
});
