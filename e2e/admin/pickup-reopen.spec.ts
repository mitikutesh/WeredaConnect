import { test, expect, type Page, type Route } from "@playwright/test";

/**
 * The pickup judgement on the transaction page (FUP-287, delivery-verification-api.md 2.8.0 P.2): the admin reads the
 * check-ins as evidence (times, fix, distance to the farm point, measured or not, late) and re-opens a judged pickup,
 * back to pending with a new deadline or as a confirmed handoff, after a confirm step. A refusal the server sends is
 * shown in words.
 *
 * Like manual-payout-claims.spec.ts this spec does not sign in: it seeds a Cognito session for a fake admin in
 * localStorage and answers every API call the page makes with page.route, so it checks what the page sends and
 * shows, not the server. The mocked server applies the re-open, so the page shows the order as it is after it. The
 * server side is covered by AdminPickupReopenRouteTests, PickupReopenTests and PickupReopenDynamoDbTests. Run it
 * against a local build:
 *
 *   cd admin && VITE_COGNITO_ADMIN_USER_POOL_ID=eu-north-1_e2eAdmin \
 *     VITE_COGNITO_ADMIN_CLIENT_ID=e2eadminclient VITE_API_BASE_URL=https://api.e2e.invalid \
 *     npx vite build && npx vite preview --port 4173
 *   cd e2e && ADMIN_URL=http://localhost:4173 npx playwright test --project=admin admin/pickup-reopen.spec.ts
 *
 * Against a deployed admin, set ADMIN_COGNITO_CLIENT_ID to that build's client id; without it the spec is skipped
 * there, since the seeded session would not be found.
 */

const LOCAL_CLIENT_ID = "e2eadminclient";
const CLIENT_ID = process.env.ADMIN_COGNITO_CLIENT_ID || LOCAL_CLIENT_ID;
const ADMIN_URL = process.env.ADMIN_URL || "";
const RUNS_HERE = Boolean(process.env.ADMIN_COGNITO_CLIENT_ID) || /localhost|127\.0\.0\.1/.test(ADMIN_URL);

test.skip(!RUNS_HERE, "Needs a local admin build (ADMIN_URL=http://localhost:...) or ADMIN_COGNITO_CLIENT_ID.");

const REASON = "Called both parties: the crops left the farm on the 21st.";

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

interface State {
  /** Unset until the page re-opens the pickup; then the exit it sent. */
  reopened?: "pending" | "handoff_confirmed";
  /** A refusal the mocked server answers the re-open with. */
  refuse?: { code: string; message: string };
}

interface Captured {
  posts: { path: string; body: unknown }[];
}

function detail(state: State) {
  const reopened = state.reopened;
  const status = reopened === "handoff_confirmed" ? "pickup_confirmed" : "accepted";
  return {
    order: {
      orderId: "ord-9",
      status,
      version: reopened ? 8 : 7,
      buyerId: "buyer-9",
      buyerName: "Selam Traders",
      farmerId: "farmer-9",
      farmerName: "Almaz Bekele",
      truckerId: "trucker-9",
      truckerName: "Chala Tolosa",
      cropType: "teff",
      quantity: 500,
    },
    escrow: null,
    delivery: {
      deliveryId: "del-9",
      status: reopened === "handoff_confirmed" ? "pickedUp" : "pending",
    },
    timeline: [],
    pickupEvidence: {
      judgement: reopened === "pending" ? "pending" : reopened ? "scanned" : "both_present_no_scan",
      judgedAt: reopened === "pending" ? undefined : "2026-11-22T06:01:00Z",
      deadlineAt: reopened === "pending" ? "2026-11-24T08:00:00Z" : "2026-11-22T06:00:00Z",
      selfPickup: false,
      pointLatitude: 8.7523,
      pointLongitude: 38.9785,
      checkIns: {
        arrived_pickup: {
          receivedAt: "2026-11-21T07:00:00Z",
          capturedAt: "2026-11-21T06:59:00Z",
          by: "trucker-9",
          latitude: 8.7601,
          longitude: 38.9785,
          accuracyM: 35,
          distanceKm: 0.87,
          distanceChecked: true,
          late: false,
          outOfRange: false,
        },
        crops_ready: {
          receivedAt: "2026-11-21T08:00:00Z",
          by: "farmer-9",
          distanceChecked: false,
          late: false,
          outOfRange: false,
        },
      },
      reopenExits: reopened ? [] : ["pending", "handoff_confirmed"],
      ...(reopened ? { lastReopen: { at: "2026-11-23T08:00:00Z", exit: reopened, fromJudgement: "both_present_no_scan" } } : {}),
    },
  };
}

async function mockApi(page: Page, state: State): Promise<Captured> {
  const captured: Captured = { posts: [] };
  await page.route(/\/admin\//, (route) => json(route, {}));
  await page.route(/\/admin\/transactions\/ord-9$/, (route) => json(route, detail(state)));
  await page.route(/\/admin\/orders\/ord-9\/reopen-pickup$/, (route) => {
    const body = route.request().postDataJSON() as { exit: "pending" | "handoff_confirmed" };
    captured.posts.push({ path: "ord-9/reopen-pickup", body });
    if (state.refuse) {
      return json(route, { error: state.refuse }, 409);
    }

    state.reopened = body.exit;
    return json(route, { orderId: "ord-9", exit: body.exit, alreadyApplied: false, message: "ok" });
  });
  return captured;
}

test.describe("Pickup judgement re-open (FUP-287)", () => {
  test.beforeEach(async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
  });

  test("the check-ins read as evidence on the transaction page", async ({ page }) => {
    await seedAdminSession(page);
    await mockApi(page, {});
    await page.goto("/transactions/ord-9");

    await expect(page.getByRole("heading", { name: "Pickup judgement" })).toBeVisible();
    await expect(page.getByText("Both checked in, nothing scanned")).toBeVisible();
    const arrival = page.getByRole("region", { name: "Arrival at the farm (trucker)" });
    await expect(arrival).toContainText("8.76010, 38.97850 ±35 m");
    await expect(arrival).toContainText("0.87 km");
    await expect(arrival).toContainText("Measured within 5 km");
    const ready = page.getByRole("region", { name: "Crops are ready (farmer)" });
    await expect(ready).toContainText("No GPS fix");
    await expect(ready).toContainText("Not measured");
  });

  test("an admin re-opens the pickup with a new deadline after a confirm", async ({ page }) => {
    await seedAdminSession(page);
    const captured = await mockApi(page, {});
    await page.goto("/transactions/ord-9");

    await page.getByRole("button", { name: "Re-open the pickup" }).click();
    await page.getByLabel("New deadline", { exact: true }).selectOption("48");
    await page.getByLabel("How you reconciled it").fill(REASON);
    await page.getByRole("button", { name: "Review" }).click();
    await expect(page.getByText("Re-open the pickup with a new deadline 48 hours from now?")).toBeVisible();
    expect(captured.posts).toEqual([]);
    await page.getByRole("button", { name: "Yes, re-open" }).click();

    await expect(page.getByRole("status")).toHaveText("The pickup is open again until the new deadline.");
    expect(captured.posts).toEqual([
      {
        path: "ord-9/reopen-pickup",
        body: { exit: "pending", reason: REASON, expectedVersion: 7, deadlineHours: 48 },
      },
    ]);
    // The page reloads the order: pending again, the last re-open named, no action left to take.
    await expect(page.getByText("Waiting for the pickup deadline")).toBeVisible();
    await expect(page.getByText("Back to pending with a new deadline")).toBeVisible();
    await expect(page.getByRole("button", { name: "Re-open the pickup" })).toHaveCount(0);
  });

  test("an admin confirms the handoff", async ({ page }) => {
    await seedAdminSession(page);
    const captured = await mockApi(page, {});
    await page.goto("/transactions/ord-9");

    await page.getByRole("button", { name: "Re-open the pickup" }).click();
    await page.getByRole("radio", { name: /Handoff confirmed/ }).check();
    await expect(page.getByLabel("New deadline", { exact: true })).toHaveCount(0);
    await page.getByLabel("How you reconciled it").fill(REASON);
    await page.getByRole("button", { name: "Review" }).click();
    await page.getByRole("button", { name: "Yes, re-open" }).click();

    await expect(page.getByRole("status")).toHaveText("The handoff is recorded as picked up.");
    expect(captured.posts).toEqual([
      { path: "ord-9/reopen-pickup", body: { exit: "handoff_confirmed", reason: REASON, expectedVersion: 7 } },
    ]);
    // The judgement now reads picked up (the delivery card says the same of the delivery).
    await expect(page.getByText("Judgement", { exact: true }).locator("xpath=following-sibling::dd")).toHaveText("Picked up");
  });

  test("a refusal is shown in words and the form stays", async ({ page }) => {
    await seedAdminSession(page);
    await mockApi(page, {
      refuse: { code: "PICKUP_REOPEN_MONEY_NOT_HELD", message: "The order's payment is no longer simply held." },
    });
    await page.goto("/transactions/ord-9");

    await page.getByRole("button", { name: "Re-open the pickup" }).click();
    await page.getByLabel("How you reconciled it").fill(REASON);
    await page.getByRole("button", { name: "Review" }).click();
    await page.getByRole("button", { name: "Yes, re-open" }).click();

    await expect(page.getByRole("alert")).toHaveText(
      "The payment is no longer simply held (refunding, settling, frozen for review or confirmed). Decide the money first.",
    );
    await expect(page.getByLabel("How you reconciled it")).toBeVisible();
  });
});

/**
 * FUP-330 (delivery-verification-api.md 2.11.0 P.2): a judged trucker no-show, still waiting for the buyer's choice,
 * is re-opened through its own action next to the judgement's (`reopen-no-show`): the trip booking is taken back and
 * the trucker's no-show excused, which the answer reports. The server side is covered by AdminNoShowReopenRouteTests,
 * NoShowReopenTests and NoShowReopenDynamoDbTests.
 */
function noShowDetail(state: State) {
  const reopened = state.reopened;
  const status = reopened === "handoff_confirmed" ? "pickup_confirmed" : reopened ? "accepted" : "awaiting_transport";
  return {
    order: {
      orderId: "ord-9",
      status,
      version: reopened ? 9 : 8,
      buyerId: "buyer-9",
      buyerName: "Selam Traders",
      farmerId: "farmer-9",
      farmerName: "Almaz Bekele",
      truckerId: "trucker-9",
      truckerName: "Chala Tolosa",
      cropType: "teff",
      quantity: 500,
    },
    escrow: null,
    delivery: { deliveryId: "del-9", status: reopened === "handoff_confirmed" ? "pickedUp" : "cancelled" },
    timeline: [],
    pickupEvidence: {
      judgement: reopened === "pending" ? "pending" : reopened ? "scanned" : "trucker_no_show",
      judgedAt: reopened === "pending" ? undefined : "2026-11-22T06:01:00Z",
      deadlineAt: reopened === "pending" ? "2026-11-24T08:00:00Z" : "2026-11-22T06:00:00Z",
      selfPickup: false,
      checkIns: {
        crops_ready: { receivedAt: "2026-11-21T08:00:00Z", by: "farmer-9", distanceChecked: false, late: false, outOfRange: false },
      },
      reopenExits: [],
      noShowReopenExits: reopened ? [] : ["pending", "handoff_confirmed"],
      ...(reopened ? { lastReopen: { at: "2026-11-23T08:00:00Z", exit: reopened, fromJudgement: "trucker_no_show" } } : {}),
    },
  };
}

async function mockNoShowApi(page: Page, state: State): Promise<Captured> {
  const captured: Captured = { posts: [] };
  await page.route(/\/admin\//, (route) => json(route, {}));
  await page.route(/\/admin\/transactions\/ord-9$/, (route) => json(route, noShowDetail(state)));
  await page.route(/\/admin\/orders\/ord-9\/reopen-no-show$/, (route) => {
    const body = route.request().postDataJSON() as { exit: "pending" | "handoff_confirmed" };
    captured.posts.push({ path: "ord-9/reopen-no-show", body });
    if (state.refuse) {
      return json(route, { error: state.refuse }, 409);
    }

    state.reopened = body.exit;
    return json(route, { orderId: "ord-9", exit: body.exit, alreadyApplied: false, noShowEvent: "excused", message: "ok" });
  });
  return captured;
}

test.describe("Trucker no-show re-open (FUP-330)", () => {
  test.beforeEach(async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
  });

  test("an admin re-opens a trucker no-show with a new window, and the no-show is excused", async ({ page }) => {
    await seedAdminSession(page);
    const captured = await mockNoShowApi(page, {});
    await page.goto("/transactions/ord-9");

    await expect(page.getByText("The trucker did not show up")).toBeVisible();
    await expect(page.getByRole("button", { name: "Re-open the pickup" })).toHaveCount(0);
    await page.getByRole("button", { name: "Re-open the no-show" }).click();
    await page.getByLabel("How you reconciled it").fill(REASON);
    await page.getByRole("button", { name: "Review" }).click();
    await expect(
      page.getByText(
        "Re-open the no-show with a new deadline 24 hours from now, take the trip booking back and excuse the trucker's no-show?",
      ),
    ).toBeVisible();
    expect(captured.posts).toEqual([]);
    await page.getByRole("button", { name: "Yes, re-open" }).click();

    await expect(page.getByRole("status")).toHaveText(
      "The no-show is re-opened: the trucker has a new window until the new deadline. The trucker's no-show is excused.",
    );
    expect(captured.posts).toEqual([
      {
        path: "ord-9/reopen-no-show",
        body: { exit: "pending", reason: REASON, expectedVersion: 8, deadlineHours: 24 },
      },
    ]);
    // The page reloads the order: pending again, no action left to take.
    await expect(page.getByText("Waiting for the pickup deadline")).toBeVisible();
    await expect(page.getByRole("button", { name: "Re-open the no-show" })).toHaveCount(0);
  });

  test("a trip that cannot take the order back is shown in words and the form stays", async ({ page }) => {
    await seedAdminSession(page);
    await mockNoShowApi(page, {
      refuse: { code: "PICKUP_REOPEN_TRIP_UNAVAILABLE", message: "The order's trip is full." },
    });
    await page.goto("/transactions/ord-9");

    await page.getByRole("button", { name: "Re-open the no-show" }).click();
    await page.getByRole("radio", { name: /Handoff confirmed/ }).check();
    await page.getByLabel("How you reconciled it").fill(REASON);
    await page.getByRole("button", { name: "Review" }).click();
    await page.getByRole("button", { name: "Yes, re-open" }).click();

    await expect(page.getByRole("alert")).toHaveText(
      "The trip cannot take the order back (full, departed, cancelled or gone). Reload to see it as it is now.",
    );
    await expect(page.getByLabel("How you reconciled it")).toBeVisible();
  });
});
