import { test, expect, type Page, type Route } from "@playwright/test";

/**
 * Admin legs and stuck orders (ESC-24): the per-leg dispute verdict (ESC-20's partial_refund, action
 * split), the ledger view on the escrow page, and the Stuck Orders page
 * (escrow-protocol.md 2.7.0: GET /admin/escrows/{id}/ledger, GET /admin/orders/queue).
 *
 * Like dispute-evidence.spec.ts this spec does not sign in: it seeds a Cognito session for a fake
 * admin in localStorage and answers every API call the page makes with page.route, so it checks what
 * the page sends and shows, not the server. The server side is covered by AdminFunctionLegsTests,
 * AdminFunctionSplitVerdictTests and AdminLegsDynamoDbTests. Run it against a local build:
 *
 *   cd admin && VITE_COGNITO_ADMIN_USER_POOL_ID=eu-north-1_e2eAdmin \
 *     VITE_COGNITO_ADMIN_CLIENT_ID=e2eadminclient VITE_API_BASE_URL=https://api.e2e.invalid \
 *     npx vite build && npx vite preview --port 4173
 *   cd e2e && ADMIN_URL=http://localhost:4173 npx playwright test --project=admin admin/legs-and-stuck-orders.spec.ts
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

const json = (route: Route, body: unknown, status = 200) =>
  route.fulfill({ status, contentType: "application/json", body: JSON.stringify(body) });

// A disputed escrow on the ledger: 15,000 crop, 1,500 transport, 750 commission, every leg held.
const escrow = {
  escrowId: "escrow-order-ord-1",
  orderId: "ord-1",
  deliveryId: "del-1",
  buyerId: "buyer-1",
  buyerName: "Hawi Flour Mill",
  farmerId: "farmer-1",
  farmerName: "Tolosa Gemechu",
  truckerId: "trucker-1",
  truckerName: "Dawit Tesfaye",
  amount: 17250,
  farmerPayout: 15000,
  truckerPayout: 1500,
  commission: 750,
  status: "disputed",
  legStatus: { crop: "held", transport: "held", commission: "held" },
  refundedTotal: 0,
  releasedTotal: 0,
  truckerLeg: { state: "held", pending: false, inManualQueue: false },
  dispute: { disputeId: "dispute-1", reason: "short_delivery", createdAt: "2026-11-05T12:50:00Z" },
};

const emptyTrail = {
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
    sharedWholeWay: false, declined: false, fixes: 0, coarseFixes: 0, mockedFixes: 0, lateFixes: 0,
    jumps: 0, gaps: 0, distanceKm: 0, durationMin: 0, maxKmh: null,
  },
};

test.describe("Admin per-leg verdict and ledger (ESC-24)", () => {
  test.beforeEach(async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
  });

  test("the admin splits a dispute per leg, sees the amounts before confirming, and the page sends the split", async ({
    page,
  }) => {
    const verdicts: Record<string, unknown>[] = [];
    await seedAdminSession(page);
    await page.route(/\/admin\//, (route) => json(route, {}));
    await page.route(/\/admin\/escrows\/escrow-order-ord-1$/, (route) => json(route, { escrow }));
    await page.route(/\/admin\/deliveries\/del-1\/trail$/, (route) => json(route, emptyTrail));
    await page.route(/\/admin\/disputes\/escrow-order-ord-1\/resolve$/, (route) => {
      verdicts.push(route.request().postDataJSON());
      return json(route, {
        escrowId: "escrow-order-ord-1",
        status: "dispute_resolved",
        resolution: "partial_refund",
        resolvedAt: "2026-11-07T08:00:00Z",
        verdictAmounts: { farmerAmount: 9000, truckerAmount: 1500, buyerRefund: 6000 },
      });
    });

    await page.goto("/disputes/escrow-order-ord-1");
    await expect(page.getByRole("heading", { name: "Dispute" })).toBeVisible();
    await page.getByRole("button", { name: "Decide per leg" }).click();

    const farmer = page.getByLabel("Farmer receives (crop leg), in Birr");
    await expect(farmer).toHaveValue("15000.00");
    await expect(page.getByLabel("Trucker receives (transport leg), in Birr")).toHaveValue("1500.00");
    const result = page.getByRole("group", { name: "Resulting amounts" });

    // More than the crop leg holds is refused on the page; nothing can be sent.
    await farmer.fill("15000.01");
    await expect(page.getByText("The farmer can get at most the crop leg held.")).toBeVisible();
    await expect(page.getByRole("button", { name: "Review split" })).toBeDisabled();

    await farmer.fill("9000");
    await expect(result.getByText("6,000.00 Birr")).toBeVisible();
    await expect(result.getByText("Commission kept by the platform")).toBeVisible();
    // The reason is required before the review.
    await expect(page.getByRole("button", { name: "Review split" })).toBeDisabled();
    await page.getByLabel("Reason (kept in the audit trail)").fill("Six of the fifteen quintals arrived wet.");
    await page.getByRole("button", { name: "Review split" }).click();

    await expect(page.getByText("Record this verdict? It cannot be changed afterwards.")).toBeVisible();
    await expect(result.getByText("9,000.00 Birr")).toBeVisible();
    await expect(result.getByText("1,500.00 Birr")).toBeVisible();
    await expect(result.getByText("6,000.00 Birr")).toBeVisible();
    expect(verdicts).toHaveLength(0);

    await page.getByRole("button", { name: "Confirm split verdict" }).click();
    await expect(page.getByText("Verdict saved.")).toBeVisible();
    expect(verdicts).toEqual([
      {
        resolution: "partial_refund",
        action: "split",
        farmerAmount: 9000,
        truckerAmount: 1500,
        buyerRefund: 6000,
        notes: "Six of the fifteen quintals arrived wet.",
      },
    ]);
  });

  test("a legacy escrow offers only the two single verdicts", async ({ page }) => {
    await seedAdminSession(page);
    const legacy = { ...escrow, legStatus: undefined, refundedTotal: undefined, releasedTotal: undefined };
    await page.route(/\/admin\//, (route) => json(route, {}));
    await page.route(/\/admin\/escrows\/escrow-order-ord-1$/, (route) => json(route, { escrow: legacy }));
    await page.route(/\/admin\/deliveries\/del-1\/trail$/, (route) => json(route, emptyTrail));

    await page.goto("/disputes/escrow-order-ord-1");
    await expect(page.getByRole("button", { name: "Farmer Wins" })).toBeVisible();
    await expect(page.getByRole("button", { name: "Buyer Wins" })).toBeVisible();
    await expect(page.getByRole("button", { name: "Decide per leg" })).toHaveCount(0);
  });

  test("the escrow page shows the transport leg and the ledger, read only", async ({ page }) => {
    const ledgerReads: string[] = [];
    await seedAdminSession(page);
    const released = {
      ...escrow,
      status: "released",
      dispute: undefined,
      legStatus: { crop: "released", transport: "released", commission: "kept" },
      releasedTotal: 16500,
      truckerLeg: { state: "released", pending: false, inManualQueue: false },
    };
    await page.route(/\/admin\//, (route) => json(route, {}));
    await page.route(/\/admin\/escrows\/escrow-order-ord-1$/, (route) => json(route, { escrow: released }));
    await page.route(/\/admin\/escrows\/escrow-order-ord-1\/audit$/, (route) => json(route, { auditEntries: [] }));
    await page.route(/\/admin\/escrows\/escrow-order-ord-1\/ledger/, (route) => {
      ledgerReads.push(new URL(route.request().url()).search);
      return json(route, {
        escrowId: "escrow-order-ord-1",
        settlementModel: "ledger",
        rows: [
          {
            ref: "release#farmer", kind: "release", legs: ["crop"], payeeRole: "farmer", payeeId: "farmer-1",
            payeeName: "Tolosa Gemechu", amount: 15000, status: "done", gatewayRef: "WCL0000000000000000000000001",
            telebirrRef: "TB-FARMER-1", attempts: 1, reason: "auto_release", actor: "system",
            createdAt: "2026-11-06T10:00:00Z", updatedAt: "2026-11-06T10:05:00Z", doneAt: "2026-11-06T10:05:00Z",
          },
          {
            ref: "release#trucker#trucker-1", kind: "release", legs: ["transport"], payeeRole: "trucker",
            payeeId: "trucker-1", payeeName: "Dawit Tesfaye", amount: 1500, status: "manual",
            gatewayRef: "WCL0000000000000000000000002", manualReason: "PAYOUT_NOT_SUPPORTED", attempts: 1,
            reason: "auto_release", actor: "system", createdAt: "2026-11-06T10:00:00Z", updatedAt: "2026-11-06T10:01:00Z",
          },
        ],
      });
    });

    await page.goto("/escrows/escrow-order-ord-1");
    await expect(page.getByRole("heading", { name: "Escrow Detail" })).toBeVisible();
    await expect(page.getByText("Trucker payout")).toBeVisible();
    await expect(page.getByText("Decided for the trucker (see the ledger)")).toBeVisible();

    const table = page.getByRole("table", { name: "Ledger" });
    await expect(table.getByRole("row")).toHaveCount(3);
    await expect(table.getByText("TB-FARMER-1")).toBeVisible();
    await expect(table.getByText("15,000.00 Birr")).toBeVisible();
    await expect(table.getByText("In Manual Payouts")).toBeVisible();
    await expect(table.getByText("Gateway has no payout API")).toBeVisible();
    await expect(table.getByRole("button")).toHaveCount(0);
    expect(ledgerReads[0]).toBe("?limit=50");
  });
});

test.describe("Admin stuck orders (ESC-24)", () => {
  const queued = {
    orderId: "ord-held",
    status: "accepted",
    stage: "in_progress",
    reason: "hold_cap",
    stuckSince: "2026-11-01T10:00:00Z",
    ageHours: 98,
    buyerId: "buyer-1",
    buyerName: "Hawi Flour Mill",
    farmerId: "farmer-1",
    farmerName: "Tolosa Gemechu",
    truckerId: "trucker-1",
    truckerName: "Dawit Tesfaye",
    cropType: "teff",
    orderTotal: 17250,
    currency: "ETB",
    escrowId: "escrow-order-ord-held",
  };
  const missed = {
    ...queued,
    orderId: "ord-silent",
    status: "funded",
    reason: "farmer_response",
    stuckSince: "2026-11-05T06:00:00Z",
    ageHours: 3,
    truckerId: undefined,
    truckerName: undefined,
    escrowId: undefined,
  };

  test("lists both kinds of stuck orders, pages on demand and links to the transaction", async ({ page }) => {
    const reads: string[] = [];
    await page.setViewportSize({ width: 1440, height: 900 });
    await seedAdminSession(page);
    await page.route(/\/admin\//, (route) => json(route, {}));
    await page.route(/\/admin\/orders\/queue/, (route) => {
      const url = new URL(route.request().url());
      reads.push(url.search);
      const source = url.searchParams.get("source");
      if (source === "deadline") {
        return json(route, { source, orders: [missed], total: 1, asOf: "2026-11-05T09:00:00Z" });
      }

      return url.searchParams.get("nextToken") === "page-2"
        ? json(route, { source, orders: [{ ...queued, orderId: "ord-held-2" }], total: 1, asOf: "2026-11-05T09:00:00Z" })
        : json(route, { source, orders: [queued], nextToken: "page-2", total: 1, asOf: "2026-11-05T09:00:00Z" });
    });
    await page.route(/\/admin\/transactions\/ord-silent$/, (route) =>
      json(route, {
        order: { orderId: "ord-silent", status: "funded", buyerId: "buyer-1", farmerId: "farmer-1" },
        escrow: null,
        delivery: null,
        timeline: [],
      }),
    );

    await page.goto("/");
    await page.getByRole("link", { name: "Stuck Orders" }).click();
    await expect(page.getByRole("heading", { name: "Stuck Orders" })).toBeVisible();

    const table = page.getByRole("table");
    await expect(table.getByRole("link", { name: "ord-held", exact: true })).toBeVisible();
    await expect(table.getByText("Held 21 days, an admin decides")).toBeVisible();
    await expect(table.getByText("4 d")).toBeVisible();
    await expect(table.getByText("Dawit Tesfaye")).toBeVisible();

    await page.getByRole("button", { name: "Load more" }).click();
    await expect(table.getByRole("link", { name: "ord-held-2" })).toBeVisible();
    await expect(page.getByRole("button", { name: "Load more" })).toHaveCount(0);
    expect(reads).toContain("?source=queue&limit=50&nextToken=page-2");

    await page.getByRole("button", { name: "Missed deadlines" }).click();
    await expect(table.getByText("Paid, the farmer did not answer in time")).toBeVisible();
    await expect(table.getByText("3 h")).toBeVisible();
    await expect(table.getByText("No trucker")).toBeVisible();
    expect(reads).toContain("?source=deadline&limit=50");

    await table.getByRole("link", { name: "ord-silent" }).click();
    await expect(page).toHaveURL(/\/transactions\/ord-silent$/);
    await expect(page.getByRole("heading", { name: "Transaction Detail" })).toBeVisible();
  });
});
