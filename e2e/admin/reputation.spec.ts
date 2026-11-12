import { test, expect, type Page, type Route } from "@playwright/test";

/**
 * Admin reputation (ESC-32, reputation-api.md 1.4.0 section 9): the reputation panel on the user
 * page (excuse an event, restrict with a reason and an end date) and the appeals queue (accept an
 * appeal, uphold a penalty).
 *
 * Like fraud-review.spec.ts this spec does not sign in: it seeds a Cognito session for a fake admin
 * in localStorage and answers every API call the page makes with page.route, so it checks what the
 * page sends and shows, not the server. The mocked server keeps a little state, so the page shows
 * the result of a decision after it refetches. The server side is covered by AdminReputationTests
 * and AdminReputationDynamoDbTests. Run it against a local build:
 *
 *   cd admin && VITE_COGNITO_ADMIN_USER_POOL_ID=eu-north-1_e2eAdmin \
 *     VITE_COGNITO_ADMIN_CLIENT_ID=e2eadminclient VITE_API_BASE_URL=https://api.e2e.invalid \
 *     npx vite build && npx vite preview --port 4173
 *   cd e2e && ADMIN_URL=http://localhost:4173 npx playwright test --project=admin admin/reputation.spec.ts
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

// A 1x1 PNG the mocked "pre-signed" photo link answers with.
const PIXEL = Buffer.from(
  "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==",
  "base64",
);
const PHOTO_URL = "https://uploads.e2e.invalid/uploads/appeals/trucker-1/photo.png?X-Amz-Signature=e2e";

interface Event {
  eventId: string;
  code: string;
  party: string;
  points: number;
  basePoints: number;
  earnedBack: number;
  remainingPoints: number;
  status: string;
  occurredAt: string;
  recordedAt: string;
  orderId: string;
  buyerId: string;
  appeal: Record<string, unknown>;
}

interface State {
  events: Event[];
  restriction: Record<string, unknown> | null;
  restrictions: Record<string, unknown>[];
}

interface Captured {
  decisions: { userId: string; eventId: string; body: Record<string, unknown> }[];
  actions: { userId: string; body: Record<string, unknown> }[];
  appealQueries: string[];
}

function initialState(): State {
  return {
    restriction: null,
    restrictions: [],
    events: [
      {
        eventId: "CANCEL#ord-7f3a",
        code: "LATE_CANCEL",
        party: "trucker",
        points: 4,
        basePoints: 4,
        earnedBack: 0,
        remainingPoints: 4,
        status: "active",
        occurredAt: "2026-11-01T06:00:00.000Z",
        recordedAt: "2026-11-01T06:00:04.000Z",
        orderId: "ord-7f3a",
        buyerId: "buyer-19",
        appeal: {
          status: "pending",
          canAppeal: false,
          text: "The road to Kombolcha was closed by the flood.",
          appealedAt: "2026-11-02T08:00:00.000Z",
          hasPhoto: true,
          appealPhotoUrl: PHOTO_URL,
        },
      },
      {
        eventId: "CANCEL#ord-2b",
        code: "CANCELLED_AFTER_ACCEPT",
        party: "trucker",
        points: 3,
        basePoints: 3,
        earnedBack: 0,
        remainingPoints: 3,
        status: "active",
        occurredAt: "2026-10-28T06:00:00.000Z",
        recordedAt: "2026-10-28T06:00:04.000Z",
        orderId: "ord-2b",
        buyerId: "buyer-20",
        appeal: { status: "none", canAppeal: true },
      },
    ],
  };
}

function reputationOf(state: State) {
  const points = state.events.reduce((sum, e) => sum + (e.status === "active" ? e.remainingPoints : 0), 0);
  const tier = state.restriction ? "restricted" : points >= 6 ? "low" : points >= 3 ? "warning" : "good";
  return {
    userId: "trucker-1",
    role: "trucker",
    tier,
    activePoints: points,
    thresholds: { warning: 3, low: 6, restricted: 10 },
    ratingAverage: 4.8,
    ratingCount: 12,
    ratingDeduction: Number((points * 0.1).toFixed(1)),
    effectiveRating: Number((4.8 - points * 0.1).toFixed(1)),
    reliabilityPct: 83,
    completedOrders90d: 10,
    faultCancels90d: 2,
    noShows90d: 0,
    penaltiesEnabled: true,
    asOf: "2026-11-12T09:00:00.000Z",
    ...(state.restriction ? { restriction: state.restriction } : {}),
  };
}

async function mockApi(page: Page, state: State): Promise<Captured> {
  const captured: Captured = { decisions: [], actions: [], appealQueries: [] };
  // Registered first, so it answers last: anything else the shell asks for gets an empty answer.
  await page.route(/\/admin\//, (route) => json(route, {}));
  await page.route(/uploads\.e2e\.invalid/, (route) => route.fulfill({ status: 200, contentType: "image/png", body: PIXEL }));
  await page.route(/\/admin\/users\/trucker-1$/, (route) =>
    json(route, {
      user: { userId: "trucker-1", displayName: "Kebede Alemu", phoneNumber: "+251900100002", role: "trucker", suspended: false },
    }),
  );
  await page.route(/\/admin\/users\/trucker-1\/reputation(\?|$)/, (route) => {
    if (route.request().method() === "POST") {
      const body = route.request().postDataJSON() as Record<string, unknown>;
      captured.actions.push({ userId: "trucker-1", body });
      if (body.action === "restrict") {
        state.restriction = { cause: "admin", since: "2026-11-12T09:00:00.000Z", until: String(body.until) };
        state.restrictions.unshift({
          eventId: "RESTRICTION#2026-11-12T09:00:00.000Z",
          status: "active",
          reason: String(body.reason),
          by: "admin-e2e",
          ...state.restriction,
        });
      }
      return json(route, { reputation: reputationOf(state), alreadyApplied: false });
    }
    return json(route, { reputation: reputationOf(state), restrictions: state.restrictions, events: state.events });
  });
  await page.route(/\/admin\/users\/[^/]+\/reputation\/events\/[^/]+\/decision$/, (route) => {
    const parts = new URL(route.request().url()).pathname.split("/");
    const userId = decodeURIComponent(parts[3]);
    const eventId = decodeURIComponent(parts[6]);
    const body = route.request().postDataJSON() as Record<string, unknown>;
    captured.decisions.push({ userId, eventId, body });
    const event = state.events.find((e) => e.eventId === eventId);
    if (!event) {
      return json(route, { error: { code: "REPUTATION_EVENT_NOT_FOUND", message: "No such event." } }, 404);
    }
    const excused = body.decision === "excuse";
    event.status = excused ? "excused" : event.status;
    event.remainingPoints = excused ? 0 : event.remainingPoints;
    event.appeal = {
      ...event.appeal,
      status: excused ? "excused" : "upheld",
      canAppeal: false,
      decidedAt: "2026-11-12T09:05:00.000Z",
      decidedBy: "admin-e2e",
      note: String(body.note),
    };
    return json(route, { event, reputation: reputationOf(state), alreadyDecided: false });
  });
  await page.route(/\/admin\/reputation\/appeals(\?|$)/, (route) => {
    const status = new URL(route.request().url()).searchParams.get("status") ?? "pending";
    captured.appealQueries.push(status);
    const open = state.events.filter((e) => e.appeal.status === status);
    return json(route, {
      appeals: open.map((e) => ({
        userId: "trucker-1",
        role: "trucker",
        displayName: "Kebede Alemu",
        eventId: e.eventId,
        code: e.code,
        points: e.points,
        occurredAt: e.occurredAt,
        orderId: e.orderId,
        appealStatus: status,
        appealedAt: e.appeal.appealedAt,
        text: e.appeal.text,
        hasPhoto: Boolean(e.appeal.hasPhoto),
        appealPhotoUrl: e.appeal.appealPhotoUrl,
      })),
    });
  });
  return captured;
}

async function open(page: Page, path: string, state = initialState()): Promise<Captured> {
  await seedAdminSession(page);
  const captured = await mockApi(page, state);
  await page.goto(path);
  return captured;
}

test.describe("Admin reputation (ESC-32)", () => {
  test.beforeEach(async ({ page }) => {
    await page.setViewportSize({ width: 1440, height: 900 });
  });

  test("the user page shows points, tier and events, and an admin excuses an event with a note", async ({ page }) => {
    const captured = await open(page, "/users/trucker-1");

    const panel = page.getByRole("region", { name: "Reputation" });
    await expect(panel.getByText("warning at 3, low at 6, restricted at 10")).toBeVisible();
    await expect(panel.getByText("4.1 (4.8 from 12 rating(s), 0.7 deducted)")).toBeVisible();
    const row = panel.getByRole("row").filter({ hasText: "Late cancel" });
    await expect(row.getByText("4 of 4")).toBeVisible();
    await expect(row.getByText("Appeal pending")).toBeVisible();
    await expect(row.getByText("The road to Kombolcha was closed by the flood.")).toBeVisible();
    await expect(row.getByRole("link", { name: "buyer-19" })).toHaveAttribute("href", "/users/buyer-19");
    // The appeal photo is shown through the short-lived signed link, not a public address.
    await expect(row.getByAltText("Photo sent with the appeal")).toHaveAttribute("src", PHOTO_URL);

    await row.getByRole("button", { name: "Excuse CANCEL#ord-7f3a" }).click();
    const dialog = page.getByRole("dialog", { name: "Excuse this event: Late cancel" });
    await expect(dialog).toBeVisible();

    // A note is required: nothing is sent without one.
    await dialog.getByRole("button", { name: "Excuse" }).click();
    await expect(dialog.getByText("Write a reason of 3 to 500 characters.")).toBeVisible();
    expect(captured.decisions).toHaveLength(0);

    await dialog.getByLabel(/Note to the account holder/).fill("Flood confirmed with the buyer.");
    await dialog.getByRole("button", { name: "Excuse" }).click();

    await expect(dialog).toBeHidden();
    await expect(panel.getByText("Event excused.")).toBeVisible();
    expect(captured.decisions).toEqual([
      {
        userId: "trucker-1",
        eventId: "CANCEL#ord-7f3a",
        body: { decision: "excuse", note: "Flood confirmed with the buyer." },
      },
    ]);

    // The panel refetches: the event is excused, its points are gone, the tier moved, no decision is offered.
    await expect(row.getByText("0 of 4")).toBeVisible();
    await expect(row.getByText("Excused").first()).toBeVisible();
    await expect(row.getByText(/by admin-e2e: Flood confirmed with the buyer\./)).toBeVisible();
    await expect(row.getByRole("button")).toHaveCount(0);
    await expect(panel.getByText("4.5 (4.8 from 12 rating(s), 0.3 deducted)")).toBeVisible();
  });

  test("an admin restricts an account with a reason and an end date, then the panel offers a lift", async ({ page }) => {
    const captured = await open(page, "/users/trucker-1");
    const panel = page.getByRole("region", { name: "Reputation" });

    await panel.getByRole("button", { name: "Restrict account" }).click();
    const dialog = page.getByRole("dialog", { name: "Restrict account" });
    await dialog.getByLabel(/Reason/).fill("Three no-shows reported by buyers.");

    // The end date is required.
    await dialog.getByRole("button", { name: "Restrict account" }).click();
    await expect(dialog.getByText("Choose a date after today and at most 365 days ahead.")).toBeVisible();
    expect(captured.actions).toHaveLength(0);

    const day = await page.evaluate(() => {
      const d = new Date();
      d.setDate(d.getDate() + 30);
      const pad = (n: number) => String(n).padStart(2, "0");
      return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
    });
    await dialog.getByLabel("Until").fill(day);
    await dialog.getByRole("button", { name: "Restrict account" }).click();

    await expect(dialog).toBeHidden();
    await expect(panel.getByText("Account restricted.")).toBeVisible();
    expect(captured.actions).toHaveLength(1);
    expect(captured.actions[0].body.action).toBe("restrict");
    expect(captured.actions[0].body.reason).toBe("Three no-shows reported by buyers.");
    // The end of the chosen day on the admin's clock, as an ISO UTC time.
    expect(String(captured.actions[0].body.until)).toMatch(/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.000Z$/);

    await expect(panel.getByText("Three no-shows reported by buyers.")).toBeVisible();
    await expect(panel.getByRole("button", { name: "Lift restriction" })).toBeVisible();
    await expect(panel.getByRole("button", { name: "Restrict account" })).toHaveCount(0);
  });

  test("the appeals queue shows the appeal with its text and photo, and accepting it excuses the event", async ({ page }) => {
    const captured = await open(page, "/reputation-appeals");

    await expect(page.getByRole("heading", { name: "Reputation Appeals" })).toBeVisible();
    const card = page.getByRole("region", { name: "Kebede Alemu CANCEL#ord-7f3a" });
    await expect(card.getByText("Late cancel")).toBeVisible();
    await expect(card.getByText("4 point(s)")).toBeVisible();
    await expect(card.getByRole("link", { name: "Kebede Alemu" })).toHaveAttribute("href", "/users/trucker-1");
    await expect(card.getByText("The road to Kombolcha was closed by the flood.")).toBeVisible();
    await expect(card.getByAltText("Photo sent with the appeal")).toHaveAttribute("src", PHOTO_URL);
    expect(captured.appealQueries[0]).toBe("pending");

    await card.getByRole("button", { name: "Accept and excuse" }).click();
    const dialog = page.getByRole("dialog", { name: "Excuse this event: Late cancel" });
    await dialog.getByLabel(/Note to the account holder/).fill("The flood was confirmed by the woreda office.");
    await dialog.getByRole("button", { name: "Accept and excuse" }).click();

    await expect(dialog).toBeHidden();
    await expect(page.getByText("Event excused.")).toBeVisible();
    expect(captured.decisions).toEqual([
      {
        userId: "trucker-1",
        eventId: "CANCEL#ord-7f3a",
        body: { decision: "excuse", note: "The flood was confirmed by the woreda office." },
      },
    ]);
    // The queue refetches: the decided appeal has left it.
    await expect(card).toHaveCount(0);
    await expect(page.getByText("No open appeals.")).toBeVisible();
  });

  test("upholding a penalty keeps the points and shows the server's refusal when another admin was first", async ({ page }) => {
    const captured = await open(page, "/reputation-appeals");
    const card = page.getByRole("region", { name: "Kebede Alemu CANCEL#ord-7f3a" });

    // Another admin decided it meanwhile: the server refuses, the dialog stays with the reason.
    await page.route(/\/admin\/users\/[^/]+\/reputation\/events\/[^/]+\/decision$/, (route) =>
      json(route, { error: { code: "REPUTATION_EVENT_DECIDED", message: "This event was already decided the other way." } }, 409),
    );
    await card.getByRole("button", { name: "Uphold the penalty" }).click();
    const dialog = page.getByRole("dialog", { name: "Uphold this event: Late cancel" });
    await dialog.getByLabel(/Note to the account holder/).fill("The buyer confirmed the cancel.");
    await dialog.getByRole("button", { name: "Uphold the penalty" }).click();

    await expect(dialog.getByText("Another admin already decided this event the other way. Reload the page.")).toBeVisible();
    await expect(dialog).toBeVisible();
    expect(captured.decisions).toHaveLength(0);

    await dialog.getByRole("button", { name: "Cancel" }).click();
    await expect(dialog).toBeHidden();

    // The overdue delivery reviews are their own tab and their own query.
    await page.getByRole("button", { name: "Overdue delivery reviews" }).click();
    await expect(page.getByText("No open reviews.")).toBeVisible();
    expect(captured.appealQueries).toContain("review");
  });
});
